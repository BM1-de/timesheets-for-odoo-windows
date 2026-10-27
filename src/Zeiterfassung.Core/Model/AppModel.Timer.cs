// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Model;

public sealed partial class AppModel
{
    /// <summary>Laufende Projektzeit dieses Kontos; <c>null</c>, wenn keine läuft.</summary>
    public RunningTimer? RunningTimer =>
        Preferences.RunningTimer is { } timer && timer.Account == Session?.SecretAccount ? timer : null;

    /// <summary>Die Zeit läuft ohne Projekt; es kommt spätestens beim Stoppen dazu.</summary>
    public bool RunningTimerLacksProject => RunningTimer is { ProjectId: null };

    /// <summary>
    /// Eine Zeit ohne Projekt oder eine gestoppte von weniger als einer Minute ließe sich beim Start einer neuen nicht
    /// buchen: Solange sie steht, startet keine neue.
    /// </summary>
    public bool RunningTimerBlocksStart => RunningTimerLacksProject || StoppedTimerReason == Model.StoppedTimerReason.Short;

    public StoppedTimerReason? StoppedTimerReason
    {
        get
        {
            if (RunningTimer is not { End: { } end } timer || TimerError is not null) return null;
            if (timer.ProjectId is null) return Model.StoppedTimerReason.Project;
            return Seconds(timer, end) < 60 ? Model.StoppedTimerReason.Short : null;
        }
    }

    /// <summary>„Projekt › Aufgabe“ der laufenden Zeit, ohne Projekt „Projekt fehlt“.</summary>
    public string TimerPathText => RunningTimer is not { } timer ? "" : timer.ProjectId is null ? Loc.T("Projekt fehlt") : timer.Path;

    /// <summary>Beschreibung der laufenden Zeit; lässt sich ändern, bis sie gebucht ist.</summary>
    public string TimerDescription
    {
        get => RunningTimer?.Description ?? "";
        set
        {
            if (RunningTimer is { } timer && timer.Description != value) Preferences.RunningTimer = timer with { Description = value };
        }
    }

    /// <summary>Sekunden seit dem Start; ist die Zeit gestoppt und noch nicht gebucht, bis zum Stopp.</summary>
    public int TimerSeconds() => RunningTimer is { } timer ? Seconds(timer, clock()) : 0;

    private static int Seconds(RunningTimer timer, DateTimeOffset until) =>
        Math.Max(0, (int)((timer.End ?? until) - timer.Start).TotalSeconds);

    /// <summary>„seit 14:10“, an einem früheren Tag mit Datum.</summary>
    public string TimerStartText
    {
        get
        {
            if (RunningTimer is not { } timer) return "";
            var startDay = CalendarDay.From(timer.Start, zone);
            return startDay == Today
                ? Loc.T("seit %@", ClockTime(timer.Start))
                : Loc.T("seit %@ %@", Formatting.ShortDate(startDay), ClockTime(timer.Start));
        }
    }

    /// <summary>„Starten“ steht im Formular, solange es einen neuen Eintrag für heute anlegt.</summary>
    public bool OffersTimerStart => TimesheetsActive && EditingEntry is null && TimerEditing is null && IsShowingToday;

    /// <summary>
    /// Starten geht ohne Dauer, mit Projekt (wo nötig mit Aufgabe) oder ohne. Läuft schon eine Zeit ohne Projekt oder
    /// wartet eine kurze aufs Buchen, startet keine neue: Die laufende ließe sich nicht buchen.
    /// </summary>
    public bool CanStartTimer
    {
        get
        {
            if (!OffersTimerStart || IsSaving || IsStoppingTimer || RunningTimerBlocksStart
                || DurationResult.Kind != DurationKind.Empty) return false;
            return Draft.ProjectId is null || !TaskIsRequired || Draft.TaskId is not null;
        }
    }

    /// <summary>Startet die Zeit für Projekt, Aufgabe und Beschreibung im Formular, auch ohne Projekt.</summary>
    public async Task StartTimerAsync()
    {
        if (!CanStartTimer) return;
        Project? project = null;
        if (Draft.ProjectId is { } projectId)
        {
            if (Projects.FirstOrDefault(candidate => candidate.Id == projectId) is not { } chosen) return;
            if (Organization is { } organization && organization.LacksEmployee(chosen))
            {
                SaveError = MissingEmployeeMessage(chosen);
                return;
            }
            project = chosen;
        }
        SaveError = null;
        var submitted = Draft;
        var taskId = project is not null && TaskIsRequired ? Draft.TaskId : null;
        if (!await BeginTimerAsync(project, taskId, taskId is null ? null : SelectedTaskName, Draft.Description.Trim())) return;
        // Was während des Startens schon getippt wurde, bleibt stehen.
        if (Draft.Description == submitted.Description) Draft = Draft with { Description = "" };
    }

    /// <summary>
    /// Erneut starten geht, solange das Projekt des Eintrags Buchungen annimmt und nicht genau diese Zeit schon läuft.
    /// </summary>
    public bool CanStartTimerLike(TimesheetEntry entry)
    {
        if (!TimesheetsActive || IsStoppingTimer || RunningTimerBlocksStart || entry.Project?.Id is not { } projectId
            || Projects.All(project => project.Id != projectId)) return false;
        if (RunningTimer is not { End: null } timer) return true;
        return !(timer.ProjectId == projectId && timer.TaskId == entry.Task?.Id && timer.Description == entry.Description);
    }

    /// <summary>Startet die Zeit mit Projekt, Aufgabe und Beschreibung eines Eintrags.</summary>
    public async Task StartTimerLikeAsync(TimesheetEntry entry)
    {
        if (!CanStartTimerLike(entry) || Projects.FirstOrDefault(candidate => candidate.Id == entry.Project?.Id) is not { } project) return;
        if (Organization is { } organization && organization.LacksEmployee(project))
        {
            TimerNotice = new StatusNotice(NoticeLevel.Warning, MissingEmployeeMessage(project));
            return;
        }
        await BeginTimerAsync(project, entry.Task?.Id, entry.Task?.Name, entry.Description);
    }

    /// <summary>
    /// Ein Timer zur Zeit: Eine laufende Zeit wird bis zum neuen Start gebucht. Scheitert das oder fehlen ihr Angaben,
    /// startet nichts.
    /// </summary>
    private async Task<bool> BeginTimerAsync(Project? project, int? taskId, string? taskName, string description)
    {
        if (Session?.SecretAccount is not { } account) return false;
        var start = clock();
        TimerNotice = null;
        var booking = TimerBooking.Nothing;
        if (RunningTimer is not null) booking = await BookTimerAsync(start);
        switch (booking)
        {
            case TimerBooking.Failed:
                return false;
            case TimerBooking.NeedsDetails:
                BeginBookingTimer();
                return false;
        }
        Preferences.RunningTimer = new RunningTimer(account, project?.Id, project?.Name, taskId, taskName, description, start);
        if (project is not null) SetRecentProjectIds([project.Id, .. RecentProjectIds.Where(other => other != project.Id)]);
        if (booking == TimerBooking.Booked) await RefreshAsync(force: true);
        return true;
    }

    /// <summary>
    /// Stoppt die Zeit und bucht sie. Fehlen Projekt oder Aufgabe oder lief sie keine Minute, kommt sie dafür ins
    /// Formular; verloren geht nichts.
    /// </summary>
    public async Task StopTimerAsync()
    {
        switch (await BookTimerAsync(clock(), keepsShort: true))
        {
            case TimerBooking.Booked:
                await RefreshAsync(force: true);
                break;
            case TimerBooking.NeedsDetails:
                BeginBookingTimer();
                break;
        }
    }

    private enum TimerBooking
    {
        Booked,
        Nothing,
        Failed,
        /// <summary>
        /// Projekt oder Aufgabe fehlen oder die Zeit lief keine Minute; sie steht gestoppt, bis sie im Formular gebucht
        /// wird.
        /// </summary>
        NeedsDetails,
    }

    /// <summary>
    /// Bucht die Zeit auf den Tag, an dem sie begann, gerundet wie in den Einstellungen. Unter einer Minute bucht die
    /// App nichts: Mit <paramref name="keepsShort"/> (Stoppen) bleibt die Zeit für das Formular stehen, sonst (ein neuer
    /// Start) wird sie verworfen. Scheitert das Buchen, bleibt die Zeit gestoppt stehen, bis ein neuer Versuch klappt.
    /// </summary>
    private async Task<TimerBooking> BookTimerAsync(DateTimeOffset stop, bool keepsShort = false)
    {
        if (RunningTimer is not { } timer || service is not { } current || IsStoppingTimer) return TimerBooking.Failed;
        TimerNotice = null;
        if (timer.End is null)
        {
            timer = timer with { End = stop };
            Preferences.RunningTimer = timer;
        }
        var seconds = Seconds(timer, stop);
        if (seconds < 60)
        {
            if (keepsShort)
            {
                TimerError = null;
                return TimerBooking.NeedsDetails;
            }
            Preferences.RunningTimer = null;
            TimerError = null;
            if (TimerEditing is not null) FinishEditing();
            TimerNotice = new StatusNotice(NoticeLevel.Info, Loc.T("Weniger als eine Minute, nichts gebucht."));
            return TimerBooking.Nothing;
        }
        if (timer.ProjectId is not { } projectId)
        {
            TimerError = null;
            return TimerBooking.NeedsDetails;
        }
        var measured = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        var minutes = Preferences.DurationRounding.Minutes() is { } unit ? DurationInput.Rounded(measured, unit) : measured;
        var request = new TimesheetDraft(projectId, timer.TaskId, timer.Description.Trim(), minutes, CalendarDay.From(timer.Start, zone));
        var token = sessionToken;
        IsStoppingTimer = true;
        TimerError = null;
        try
        {
            // Ohne Aufgabe bucht die App nur, wenn das Projekt keine offenen Aufgaben hat; sonst fehlt sie noch.
            if (timer.TaskId is null && (await OpenTasksAsync(projectId, current)).Count > 0)
            {
                if (token != sessionToken) return TimerBooking.Failed;
                IsStoppingTimer = false;
                return TimerBooking.NeedsDetails;
            }
            var createdId = await CreateAsync(request, current);
            if (token != sessionToken) return TimerBooking.Failed;
            IsStoppingTimer = false;
            Preferences.RunningTimer = null;
            if (TimerEditing is not null) FinishEditing();
            if (createdId is { } created) InsertSaved(created, request, timer.TaskName);
            var duration = DurationInput.Format(minutes);
            TimerNotice = new StatusNotice(NoticeLevel.Done, minutes == measured
                ? Loc.T("%@ Std. gebucht auf %@.", duration, timer.Path)
                : Loc.T("%@ Std. gebucht auf %@, aus %@ gerundet.", duration, timer.Path, DurationInput.Format(measured)));
            return TimerBooking.Booked;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return TimerBooking.Failed;
            IsStoppingTimer = false;
            TimerError = MessageFor(error);
            if (IsKind(error, OdooErrorKind.Unauthorized)) Handle(error);
            return TimerBooking.Failed;
        }
    }

    /// <summary>Papierkorb an der Zeit: unter einer Minute gleich weg, sonst nach einer Rückfrage.</summary>
    public void RequestDiscardTimer()
    {
        if (RunningTimer is null || IsStoppingTimer) return;
        if (TimerSeconds() < 60) DiscardTimer();
        else PendingDeletion = Deletion.RunningTimer;
    }

    private void DiscardTimer()
    {
        Preferences.RunningTimer = null;
        TimerError = null;
        if (TimerEditing is not null) FinishEditing();
        TimerNotice = new StatusNotice(NoticeLevel.Info, Loc.T("Laufende Zeit verworfen, nichts gebucht."));
    }

    public void DismissTimerNotice() => TimerNotice = null;

    /// <summary>Öffnet Projekt, Aufgabe und Beschreibung der Zeit im Formular; ist sie schon gestoppt, mit Dauer zum Buchen.</summary>
    public void BeginEditingTimer()
    {
        if (RunningTimer is not { } timer || IsStoppingTimer) return;
        if (timer.End is not null) BeginBookingTimer();
        else OpenTimerInForm(Model.TimerEditing.Running, timer, "");
    }

    /// <summary>
    /// Gestoppte Zeit, der Projekt oder Aufgabe fehlen oder die keine Minute lief: ins Formular, mit der gemessenen
    /// Dauer, mindestens einer Minute.
    /// </summary>
    public void BeginBookingTimer()
    {
        if (RunningTimer is not { End: not null } timer) return;
        var measured = Math.Max(1, (int)Math.Round(Seconds(timer, clock()) / 60.0, MidpointRounding.AwayFromZero));
        OpenTimerInForm(Model.TimerEditing.Booking, timer, DurationInput.Format(measured));
    }

    /// <summary>Was dort für einen neuen Eintrag stand, kommt danach zurück, wie beim Bearbeiten eines Eintrags.</summary>
    private void OpenTimerInForm(TimerEditing mode, RunningTimer timer, string durationText)
    {
        if (!TimesheetsActive || IsSaving) return;
        if (EditingEntry is null && TimerEditing is null) draftBeforeEditing = Draft;
        EditingEntry = null;
        TimerEditing = mode;
        Draft = new Draft(timer.ProjectId, timer.TaskId, timer.Description, durationText);
        SaveError = null;
        TaskLoadError = null;
        if (timer.ProjectId is { } projectId) Fire(LoadTasksAsync(projectId));
    }

    /// <summary>Übernimmt Projekt, Aufgabe und Beschreibung aus dem Formular in die laufende Zeit.</summary>
    private void ApplyTimerEditing()
    {
        if (RunningTimer is not { } timer || !CanSave) return;
        var project = Draft.ProjectId is { } id ? Projects.FirstOrDefault(candidate => candidate.Id == id) : null;
        if (project is not null && Organization is { } organization && organization.LacksEmployee(project))
        {
            SaveError = MissingEmployeeMessage(project);
            return;
        }
        var taskId = project is not null && TaskIsRequired ? Draft.TaskId : null;
        Preferences.RunningTimer = timer with
        {
            ProjectId = project?.Id,
            ProjectName = project?.Name,
            TaskId = taskId,
            TaskName = taskId is null ? null : SelectedTaskName,
            Description = Draft.Description.Trim(),
        };
        FinishEditing();
    }

    /// <summary>Bucht die gestoppte Zeit mit den Angaben aus dem Formular auf den Tag, an dem sie begann.</summary>
    private async Task BookTimerFromFormAsync()
    {
        if (RunningTimer is not { } timer || service is not { } current || !CanSave || Draft.ProjectId is not { } projectId
            || DraftMinutes is not { } minutes) return;
        if (Organization is { } organization && Projects.FirstOrDefault(project => project.Id == projectId) is { } chosen
            && organization.LacksEmployee(chosen))
        {
            SaveError = MissingEmployeeMessage(chosen);
            return;
        }
        var taskId = TaskIsRequired ? Draft.TaskId : null;
        var taskName = taskId is null ? null : SelectedTaskName;
        var path = string.Join(" › ", new[] { SelectedProjectName, taskName }.OfType<string>());
        var request = new TimesheetDraft(projectId, taskId, Draft.Description.Trim(), minutes, CalendarDay.From(timer.Start, zone));
        var token = sessionToken;
        IsSaving = true;
        SaveError = null;
        try
        {
            var createdId = await CreateAsync(request, current);
            if (token != sessionToken) return;
            IsSaving = false;
            Preferences.RunningTimer = null;
            TimerError = null;
            if (createdId is { } created) InsertSaved(created, request, taskName);
            SetRecentProjectIds([projectId, .. RecentProjectIds.Where(other => other != projectId)]);
            FinishEditing();
            TimerNotice = new StatusNotice(NoticeLevel.Done, Loc.T("%@ Std. gebucht auf %@.", DurationInput.Format(minutes), path));
            await RefreshAsync(force: true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return;
            IsSaving = false;
            SaveError = MessageFor(error);
            if (IsKind(error, OdooErrorKind.Unauthorized)) Handle(error);
        }
    }

    /// <summary>Tag, auf den die Zeit gebucht wird: der, an dem sie begann.</summary>
    public string TimerDayShortDate =>
        RunningTimer is { } timer ? Formatting.ShortDate(CalendarDay.From(timer.Start, zone)) : DayShortDate;

    /// <summary>Offene Aufgaben eines Projekts, aus dem Zwischenspeicher, solange er frisch ist, sonst aus Odoo.</summary>
    private async Task<List<ProjectTask>> OpenTasksAsync(int projectId, OdooService current)
    {
        if (tasksByProject.TryGetValue(projectId, out var cached) && taskLoadDates.TryGetValue(projectId, out var loaded)
            && (clock() - loaded).TotalSeconds < 600) return cached;
        var tasks = await current.TasksAsync(projectId);
        tasksByProject[projectId] = tasks;
        taskLoadDates[projectId] = clock();
        Changed();
        return tasks;
    }

    // Mehrere Firmen

    public Organization? Organization => Capabilities?.Organization;

    public IReadOnlyList<EntryGroup> EntryGroups
    {
        get
        {
            var single = new List<EntryGroup> { new(0, null, BookedMinutes, Entries) };
            if (Organization is not { MainCompanyId: { } mainId } organization) return single;
            var byCompany = Entries.GroupBy(entry => entry.CompanyId ?? mainId).ToDictionary(group => group.Key, group => group.ToList());
            if (byCompany.Count <= 1) return single;
            var known = organization.Companies.Select(company => company.Id).ToList();
            var order = known.Concat(byCompany.Keys.Where(id => !known.Contains(id)).Order());
            return order.Where(byCompany.ContainsKey)
                        .Select(id => new EntryGroup(id, organization.FindCompany(id)?.Name ?? Loc.T("Andere Firma"),
                                                     byCompany[id].Sum(entry => entry.Minutes), byCompany[id]))
                        .ToList();
        }
    }

    /// <summary>Unter dem Projekt im Formular: in welcher Firma Odoo bucht.</summary>
    public string? SelectedProjectCompanyText
    {
        get
        {
            if (Organization is not { } organization || Draft.ProjectId is not { } projectId) return null;
            if (EditingEntry is { } entry && entry.Project?.Id == projectId && organization.FindCompany(entry.CompanyId) is { } company)
            {
                return company.Name;
            }
            return Projects.FirstOrDefault(project => project.Id == projectId) is { } chosen ? CompanyText(chosen, organization) : null;
        }
    }

    /// <summary>Firma eines Projekts für die Projektauswahl.</summary>
    public string? CompanyName(Project project) =>
        Organization is { } organization ? organization.FindCompany(project.CompanyId)?.Name ?? Loc.T("Ohne Firma") : null;

    private static string? CompanyText(Project project, Organization organization)
    {
        if (organization.FindCompany(project.CompanyId) is { } company) return company.Name;
        if (organization.FindCompany(organization.MainCompanyId) is not { } main) return null;
        return Loc.T("Ohne Firma · gebucht in %@", main.Name);
    }

    private string MissingEmployeeMessage(Project project)
    {
        var company = Organization?.FindCompany(project.CompanyId)?.Name ?? "";
        return Loc.T("Für „%@“ gibt es keinen Mitarbeiter zu deinem Odoo-Benutzer. Wähle ein anderes Projekt oder lass dir in Odoo einen Mitarbeiter anlegen.", company);
    }

    /// <summary>Firmen, in denen der Benutzer einen Mitarbeiter hat und damit stempeln kann.</summary>
    public IReadOnlyList<Company> AttendanceCompanies =>
        Organization is { } organization
            ? organization.Companies.Where(company => organization.EmployeeIn(company.Id) is not null).ToList()
            : [];

    /// <summary>Firma des Mitarbeiters, mit dem die App stempelt.</summary>
    public int? AttendanceCompanyId =>
        Organization is { } organization
            ? organization.Employees.FirstOrDefault(employee => employee.Id == Capabilities?.EmployeeId)?.CompanyId
              ?? organization.MainCompanyId
            : null;

    public async Task SetAttendanceCompanyAsync(int companyId)
    {
        if (Organization is not { } organization || organization.EmployeeIn(companyId) is not { } employee
            || employee.Id == Capabilities?.EmployeeId) return;
        Preferences.AttendanceEmployeeId = companyId == organization.MainCompanyId ? null : employee.Id;
        AttendanceEditor = null;
        AttendanceNotice = null;
        periodCache = [];
        await RefreshCapabilitiesAsync();
        await RefreshAsync(force: true);
    }

    // Infobereich der Taskleiste

    /// <summary>Heute auf Projekte gebucht; eine heute begonnene Projektzeit zählt mit, bis sie gebucht ist.</summary>
    public int TodayProjectMinutes
    {
        get
        {
            if (RunningTimer is not { } timer || CalendarDay.From(timer.Start, zone) != Today) return TodayBookedMinutes;
            return TodayBookedMinutes + Seconds(timer, Now) / 60;
        }
    }

    /// <summary>
    /// Symbol und Tooltip im Infobereich: Neben dem Symbol kann unter Windows keine Zeit stehen. Ein grüner Punkt zeigt
    /// die laufende Projektzeit, der Tooltip nennt alle Zeiten von heute.
    /// </summary>
    public TrayContent TrayContent
    {
        get
        {
            if (Screen != Screen.Main || Session is null)
            {
                return new TrayContent(false, false, Loc.T("%@, nicht angemeldet", AppInfo.Name));
            }
            var showsDot = Preferences.ShowsRunningDot && TimesheetsActive && RunningTimer is not null;
            return new TrayContent(true, showsDot, TrayToolTip);
        }
    }

    /// <summary>Alle Zeiten von heute für den Tooltip am Symbol, je eine Zeile, mit dem Namen der App darüber.</summary>
    private string TrayToolTip
    {
        get
        {
            var lines = new List<string> { AppInfo.Name };
            if (TimesheetsActive)
            {
                if (RunningTimer is { } timer)
                {
                    var duration = DurationInput.Format(Seconds(timer, Now) / 60);
                    var path = timer.ProjectId is null ? Loc.T("Projekt fehlt") : ShortPath(timer);
                    lines.Add(timer.End is null
                        ? Loc.T("Läuft %@ seit %@ · %@", duration, ClockTime(timer.Start), path)
                        : Loc.T("Gestoppt bei %@ · %@", duration, path));
                }
                lines.Add(Loc.T("Projektzeit heute %@ Std.", DurationInput.Format(TodayProjectMinutes)));
            }
            if (AttendanceActive)
            {
                var total = DurationInput.Format(TodayAttendanceMinutes);
                if (OpenPeriod is { } open) lines.Add(Loc.T("Anwesend seit %@ · heute %@ Std.", ClockTime(open.CheckIn), total));
                else if (TodayPeriodsIncludingOpen.Count == 0) lines.Add(Loc.T("Heute nicht eingestempelt"));
                else lines.Add(Loc.T("Anwesend heute %@ Std.", total));
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>„Website-Relaunch › Design“: Der Kunde in Klammern hinter dem Projekt fällt weg, damit die Zeile kurz bleibt.</summary>
    public static string ShortPath(RunningTimer timer) =>
        string.Join(" › ", new[] { ShortProjectName(timer.ProjectName), timer.TaskName }.OfType<string>());

    public static string? ShortProjectName(string? name)
    {
        if (name is null) return null;
        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && name.EndsWith(')') ? name[..open] : name;
    }

    /// <summary>Beginn der Zeit, die mitzählt: die laufende Projektzeit, sonst die laufende Anwesenheit.</summary>
    private DateTimeOffset? CountingStart =>
        TimesheetsActive && RunningTimer is { End: null } timer ? timer.Start
        : AttendanceActive ? OpenPeriod?.CheckIn
        : null;
}
