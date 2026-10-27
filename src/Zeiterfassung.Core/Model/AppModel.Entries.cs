// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Model;

public sealed partial class AppModel
{
    // Zeiteinträge

    public int BookedMinutes => Entries.Sum(entry => entry.Minutes);
    public int TodayBookedMinutes => TodayEntries.Sum(entry => entry.Minutes);

    public string EntryCountText => Loc.T("%lld Einträge", Entries.Count);

    public void SelectProject(Project project)
    {
        if (Draft.ProjectId == project.Id) return;
        Draft = Draft with { ProjectId = project.Id, TaskId = null };
        SaveError = null;
        TaskLoadError = null;
        Fire(LoadTasksAsync(project.Id));
    }

    public void SelectTask(int? taskId) => Draft = Draft with { TaskId = taskId };

    /// <summary>Offene Aufgaben eines Projekts. Geladene gelten zehn Minuten, danach wird nachgeladen.</summary>
    public async Task LoadTasksAsync(int projectId)
    {
        if (tasksByProject.TryGetValue(projectId, out var cached))
        {
            Preselect(cached, projectId);
            if (taskLoadDates.TryGetValue(projectId, out var loaded) && (clock() - loaded).TotalSeconds < 600) return;
        }
        if (service is not { } current) return;
        var token = sessionToken;
        try
        {
            var tasks = await current.TasksAsync(projectId);
            if (token != sessionToken) return;
            tasksByProject[projectId] = tasks;
            taskLoadDates[projectId] = clock();
            Changed();
            // Eine inzwischen erledigte Aufgabe nicht weiter vorschlagen, außer beim Bearbeiten des Eintrags, der auf
            // ihr steht.
            if (Draft.ProjectId == projectId && Draft.TaskId is { } taskId && tasks.All(task => task.Id != taskId)
                && !(EditingEntry?.Project?.Id == projectId && EditingEntry?.Task?.Id == taskId))
            {
                Draft = Draft with { TaskId = null };
            }
            Preselect(tasks, projectId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return;
            // Schon geladene Aufgaben bleiben stehen, wenn nur das Nachladen scheitert.
            if (Draft.ProjectId == projectId && !tasksByProject.ContainsKey(projectId)) TaskLoadError = MessageFor(error);
        }
    }

    public void RetryTasks()
    {
        if (Draft.ProjectId is not { } projectId) return;
        TaskLoadError = null;
        Fire(LoadTasksAsync(projectId));
    }

    /// <summary>Die einzige Aufgabe gleich wählen. Ein bearbeiteter Eintrag behält, was er hat.</summary>
    private void Preselect(List<ProjectTask> tasks, int projectId)
    {
        if (Draft.ProjectId != projectId || Draft.TaskId is not null || tasks.Count != 1
            || EditingEntry?.Project?.Id == projectId) return;
        Draft = Draft with { TaskId = tasks[0].Id };
    }

    public TaskChoice TaskChoice
    {
        get
        {
            if (Draft.ProjectId is not { } projectId) return TaskChoice.NoProject;
            if (tasksByProject.TryGetValue(projectId, out var loaded))
            {
                var tasks = loaded.ToList();
                // Beim Bearbeiten bleibt die Aufgabe des Eintrags wählbar, auch wenn sie inzwischen erledigt ist.
                if (EditingEntry is { } entry && entry.Project?.Id == projectId && entry.Task is { } task
                    && tasks.All(candidate => candidate.Id != task.Id))
                {
                    tasks.Insert(0, new ProjectTask(task.Id, task.Name));
                }
                return tasks.Count == 0 ? TaskChoice.NoTasks : new TaskChoice(TaskChoiceKind.Tasks, null, tasks);
            }
            if (TaskLoadError is { } message) return new TaskChoice(TaskChoiceKind.Failed, message);
            return TaskChoice.Loading;
        }
    }

    /// <summary>
    /// Name des gewählten Projekts. Beim Bearbeiten auch dann, wenn das Projekt des Eintrags nicht mehr zum Buchen
    /// angeboten wird, etwa weil es archiviert ist.
    /// </summary>
    public string? SelectedProjectName
    {
        get
        {
            if (Draft.ProjectId is not { } projectId) return null;
            if (Projects.FirstOrDefault(project => project.Id == projectId) is { } found) return found.Name;
            if (EditingEntry is { } entry && entry.Project?.Id == projectId) return entry.Project?.Name;
            return null;
        }
    }

    public string? SelectedTaskName
    {
        get
        {
            if (Draft.ProjectId is not { } projectId || Draft.TaskId is not { } taskId) return null;
            if (tasksByProject.GetValueOrDefault(projectId)?.FirstOrDefault(task => task.Id == taskId) is { } found) return found.Name;
            if (EditingEntry is { } entry && entry.Task?.Id == taskId) return entry.Task?.Name;
            return null;
        }
    }

    public DurationResult DurationResult => DurationInput.Parse(Draft.DurationText);

    /// <summary>
    /// Minuten, die gebucht würden (ggf. gerundet, siehe Einstellungen). Beim Bearbeiten bleibt eine unveränderte
    /// Dauer minutengenau, wie sie in Odoo steht.
    /// </summary>
    public int? DraftMinutes
    {
        get
        {
            if (DurationResult is not { Kind: DurationKind.Minutes, Minutes: > 0 and var minutes }) return null;
            if (EditingEntry is { } entry && Draft.DurationText == DurationInput.Format(entry.Minutes)) return entry.Minutes;
            return Preferences.DurationRounding.Minutes() is { } unit ? DurationInput.Rounded(minutes, unit) : minutes;
        }
    }

    public Hint? DurationHint
    {
        get
        {
            var result = DurationResult;
            switch (result.Kind)
            {
                case DurationKind.Unreadable:
                    return new Hint(Loc.T("Bitte als 1:30, 1,5 oder 90 min eingeben."), true);
                case DurationKind.TooLong:
                    return new Hint(Loc.T("Höchstens 24 Stunden je Eintrag."), true);
                case DurationKind.Minutes:
                    if (DraftMinutes is not { } booked || booked == result.Minutes) return null;
                    return new Hint(Loc.T("Wird als %@ Std. gebucht.", DurationInput.Format(booked)), false);
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// −/+: zur nächsten Stufe in diese Richtung; die Stufe ist die Einheit der Rundung, ohne Rundung eine
    /// Viertelstunde.
    /// </summary>
    public void AdjustDuration(int direction)
    {
        var current = DurationResult is { Kind: DurationKind.Minutes } result ? result.Minutes : 0;
        var minutes = DurationInput.Stepped(current, direction, Preferences.DurationRounding.Step());
        DraftDurationText = minutes > 0 ? DurationInput.Format(minutes) : "";
    }

    public void NormalizeDuration()
    {
        if (DraftMinutes is { } minutes) DraftDurationText = DurationInput.Format(minutes);
    }

    /// <summary>
    /// Ohne offene Aufgaben im Projekt darf ohne Aufgabe gebucht werden, wie in Odoo. Ein Eintrag, der schon ohne
    /// Aufgabe gebucht ist, darf so bleiben.
    /// </summary>
    public bool TaskIsRequired
    {
        get
        {
            if (EditingEntry is { } entry && entry.Task is null && Draft.ProjectId == entry.Project?.Id) return false;
            return TaskChoice.Kind != TaskChoiceKind.NoTasks;
        }
    }

    public bool CanSave
    {
        get
        {
            if (!TimesheetsActive || IsSaving) return false;
            var taskComplete = !TaskIsRequired || Draft.TaskId is not null;
            switch (TimerEditing)
            {
                case Model.TimerEditing.Running:
                    return RunningTimer is not null && (Draft.ProjectId is null || taskComplete);
                case Model.TimerEditing.Booking:
                    return RunningTimer is not null && !IsStoppingTimer && Draft.ProjectId is not null && taskComplete
                           && DraftMinutes is not null;
            }
            switch (EditingEntry?.Lock)
            {
                case EntryLock.Validated: return false;
                case EntryLock.Invoiced: return true;
            }
            return Draft.ProjectId is not null && taskComplete && DraftMinutes is not null;
        }
    }

    public async Task SaveAsync()
    {
        switch (TimerEditing)
        {
            case Model.TimerEditing.Running:
                ApplyTimerEditing();
                return;
            case Model.TimerEditing.Booking:
                await BookTimerFromFormAsync();
                return;
        }
        if (EditingEntry is not null)
        {
            await SaveEditingAsync();
            return;
        }
        if (!CanSave || service is not { } current || Draft.ProjectId is not { } projectId || DraftMinutes is not { } minutes) return;
        if (Organization is { } organization && Projects.FirstOrDefault(project => project.Id == projectId) is { } chosen
            && organization.LacksEmployee(chosen))
        {
            SaveError = MissingEmployeeMessage(chosen);
            return;
        }
        var submitted = Draft;
        var request = new TimesheetDraft(projectId, TaskIsRequired ? Draft.TaskId : null, Draft.Description.Trim(), minutes, Day);
        var token = sessionToken;
        IsSaving = true;
        SaveError = null;
        try
        {
            var createdId = await CreateAsync(request, current);
            if (token != sessionToken) return;
            if (createdId is { } id) InsertSaved(id, request);
            // Was während des Speicherns schon für den nächsten Eintrag getippt wurde, bleibt stehen.
            if (Draft.Description == submitted.Description) Draft = Draft with { Description = "" };
            if (Draft.DurationText == submitted.DurationText) Draft = Draft with { DurationText = "" };
            SetRecentProjectIds([projectId, .. RecentProjectIds.Where(id => id != projectId)]);
            IsSaving = false;
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

    /// <summary>
    /// Legt einen Eintrag in Odoo an. Blieb beim letzten Versuch mit genau diesem Eintrag die Antwort aus und kam er
    /// doch an, wird er nicht ein zweites Mal angelegt; das Ergebnis ist dann <c>null</c>.
    /// </summary>
    private async Task<int?> CreateAsync(TimesheetDraft request, OdooService current)
    {
        var (count, arrivedEarlier) = await CountMatchesAsync(request, current);
        uncertainSaves.RemoveAll(save => save.Request == request);
        if (arrivedEarlier) return null;
        uncertainSaves.Add(new UncertainSave(request, count));
        try
        {
            var id = await current.CreateTimesheetAsync(request);
            uncertainSaves.RemoveAll(save => save.Request == request);
            return id;
        }
        catch (Exception error)
        {
            if (!MayHaveArrived(error)) uncertainSaves.RemoveAll(save => save.Request == request);
            throw;
        }
    }

    /// <summary>
    /// Wie oft der Eintrag an seinem Tag schon vorkommt. Blieb beim letzten Versuch die Antwort von Odoo aus, wird
    /// frisch gezählt: Ist es einer mehr als vorher, kam der Eintrag doch an und wird nicht ein zweites Mal angelegt.
    /// </summary>
    private async Task<(int Count, bool ArrivedEarlier)> CountMatchesAsync(TimesheetDraft request, OdooService current)
    {
        if (uncertainSaves.FirstOrDefault(save => save.Request == request) is { } pending)
        {
            var now = Matches(request, await current.TimesheetsAsync(request.Day));
            return (now, now > pending.MatchesBefore);
        }
        if (entryCache.TryGetValue(request.Day, out var cached)) return (Matches(request, cached), false);
        return (Matches(request, await current.TimesheetsAsync(request.Day)), false);
    }

    private static int Matches(TimesheetDraft request, IEnumerable<TimesheetEntry> list) =>
        list.Count(entry => entry.Project?.Id == request.ProjectId && entry.Task?.Id == request.TaskId
                            && entry.Description == request.Description && Math.Abs(entry.Minutes - request.Minutes) <= 1);

    /// <summary>Fehler, nach denen offen ist, ob Odoo die Anfrage noch ausgeführt hat.</summary>
    private static bool MayHaveArrived(Exception error) =>
        error is OdooException { Kind: OdooErrorKind.Offline or OdooErrorKind.Server or OdooErrorKind.UnexpectedResponse };

    /// <summary>Zeigt einen gerade angelegten Eintrag sofort, bis das Neuladen die Liste aus Odoo bringt.</summary>
    private void InsertSaved(int id, TimesheetDraft request, string? taskName = null)
    {
        if (id <= 0 || !entryCache.TryGetValue(request.Day, out var cached)) return;
        var list = cached.ToList();
        var project = Projects.FirstOrDefault(candidate => candidate.Id == request.ProjectId);
        Reference? task = null;
        if (request.TaskId is { } taskId)
        {
            var name = tasksByProject.GetValueOrDefault(request.ProjectId)?.FirstOrDefault(candidate => candidate.Id == taskId)?.Name
                       ?? taskName;
            if (name is not null) task = new Reference(taskId, name);
        }
        list.Add(new TimesheetEntry(id, request.Description, project is null ? null : new Reference(project.Id, project.Name), task,
                                    request.Minutes, request.Day, null, project?.CompanyId ?? Organization?.MainCompanyId));
        entryCache[request.Day] = list;
        if (request.Day == Day) SetEntries(list);
        if (request.Day == Today) SetTodayEntries(list);
    }

    // Bearbeiten

    /// <summary>Öffnet einen Eintrag im Formular. Was dort für einen neuen Eintrag stand, kommt danach zurück.</summary>
    public void BeginEditing(TimesheetEntry entry)
    {
        if (!TimesheetsActive || IsSaving || EditingEntry?.Id == entry.Id) return;
        if (EditingEntry is null && TimerEditing is null) draftBeforeEditing = Draft;
        EditingEntry = entry;
        TimerEditing = null;
        EditDay = entry.Day ?? Day;
        Draft = new Draft(entry.Project?.Id, entry.Task?.Id, entry.Description, DurationInput.Format(entry.Minutes));
        SaveError = null;
        TaskLoadError = null;
        if (entry.Project?.Id is { } projectId) Fire(LoadTasksAsync(projectId));
    }

    public void CancelEditing()
    {
        if (EditingEntry is null && TimerEditing is null) return;
        FinishEditing();
    }

    private void FinishEditing()
    {
        EditingEntry = null;
        TimerEditing = null;
        Draft = draftBeforeEditing ?? new Draft();
        draftBeforeEditing = null;
        EditDay = Day;
        SaveError = null;
        TaskLoadError = null;
    }

    /// <summary>Abgerechnet: Nur die Beschreibung lässt sich noch ändern.</summary>
    public bool EditingIsLimited => EditingEntry?.Lock == EntryLock.Invoiced;

    /// <summary>Freigegeben: Nichts lässt sich mehr ändern.</summary>
    public bool EditingIsLocked => EditingEntry?.Lock == EntryLock.Validated;

    /// <summary>Tage, auf die sich ein Eintrag legen lässt: die letzte Woche und sein eigener Tag.</summary>
    public IReadOnlyList<CalendarDay> EditDayChoices
    {
        get
        {
            var days = Enumerable.Range(0, 7).Select(offset => Today.AddingDays(-offset)).ToList();
            foreach (var extra in new[] { EditingEntry?.Day, (CalendarDay?)EditDay })
            {
                if (extra is { } value && !days.Contains(value)) days.Add(value);
            }
            days.Sort((left, right) => right.CompareTo(left));
            return days;
        }
    }

    public string DayChoiceTitle(CalendarDay choice)
    {
        if (choice == Today) return Loc.T("Heute, %@", Formatting.ShortDate(choice));
        if (choice == Today.AddingDays(-1)) return Loc.T("Gestern, %@", Formatting.ShortDate(choice));
        return Formatting.ShortDate(choice);
    }

    private async Task SaveEditingAsync()
    {
        if (EditingEntry is not { } entry || service is not { } current || !CanSave) return;
        var limited = EditingIsLimited;
        var projectId = limited ? entry.Project?.Id ?? 0 : Draft.ProjectId ?? 0;
        var request = new TimesheetDraft(
            projectId,
            limited ? entry.Task?.Id : TaskChoice.Kind == TaskChoiceKind.NoTasks ? null : Draft.TaskId,
            Draft.Description.Trim(),
            limited ? entry.Minutes : DraftMinutes ?? entry.Minutes,
            limited ? entry.Day ?? EditDay : EditDay);
        int? employeeId = null;
        if (!limited && Organization is { } organization && projectId != entry.Project?.Id
            && Projects.FirstOrDefault(project => project.Id == projectId) is { } chosen)
        {
            if (organization.LacksEmployee(chosen))
            {
                SaveError = MissingEmployeeMessage(chosen);
                return;
            }
            // Wechselt das Projekt die Firma, gehört der Eintrag dem Mitarbeiter der neuen Firma, wie beim Anlegen.
            // Bei einem Projekt ohne Firma bleibt der Eintrag in seiner Firma.
            if (organization.EmployeeIn(chosen.CompanyId ?? entry.CompanyId) is { } employee && employee.Id != entry.EmployeeId)
            {
                employeeId = employee.Id;
            }
        }
        var token = sessionToken;
        IsSaving = true;
        SaveError = null;
        try
        {
            await current.UpdateTimesheetAsync(entry.Id, request, limited, employeeId);
            if (token != sessionToken) return;
            IsSaving = false;
            ReplaceEntry(entry, request);
            if (request.ProjectId != entry.Project?.Id)
            {
                SetRecentProjectIds([request.ProjectId, .. RecentProjectIds.Where(id => id != request.ProjectId)]);
            }
            FinishEditing();
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

    /// <summary>
    /// Zeigt eine Änderung sofort, bis das Neuladen den Stand aus Odoo bringt. Mit neuem Datum wandert der Eintrag
    /// auf den anderen Tag.
    /// </summary>
    private void ReplaceEntry(TimesheetEntry entry, TimesheetDraft request)
    {
        var project = Projects.FirstOrDefault(candidate => candidate.Id == request.ProjectId);
        var projectReference = project is null ? entry.Project : new Reference(project.Id, project.Name);
        Reference? task = null;
        if (request.TaskId is { } taskId)
        {
            task = tasksByProject.GetValueOrDefault(request.ProjectId)?.FirstOrDefault(candidate => candidate.Id == taskId) is { } found
                ? new Reference(found.Id, found.Name)
                : entry.Task?.Id == taskId ? entry.Task : null;
        }
        var companyId = request.ProjectId == entry.Project?.Id ? entry.CompanyId : project?.CompanyId ?? entry.CompanyId;
        var updated = new TimesheetEntry(entry.Id, request.Description, projectReference, task, request.Minutes, request.Day,
                                         entry.Lock, companyId, entry.EmployeeId);
        foreach (var (cachedDay, list) in entryCache.ToList())
        {
            var index = list.FindIndex(candidate => candidate.Id == entry.Id);
            if (index < 0) continue;
            var changed = list.ToList();
            if (cachedDay == request.Day) changed[index] = updated;
            else changed.RemoveAt(index);
            entryCache[cachedDay] = changed;
        }
        if (entryCache.TryGetValue(request.Day, out var target) && target.All(candidate => candidate.Id != entry.Id))
        {
            entryCache[request.Day] = [.. target, updated];
        }
        SetEntries(entryCache.GetValueOrDefault(Day) ?? entries);
        SetTodayEntries(entryCache.GetValueOrDefault(Today) ?? todayEntries);
    }

    private void RemoveEntry(int id)
    {
        foreach (var (cachedDay, list) in entryCache.ToList()) entryCache[cachedDay] = list.Where(entry => entry.Id != id).ToList();
        SetEntries(entries.Where(entry => entry.Id != id).ToList());
        SetTodayEntries(todayEntries.Where(entry => entry.Id != id).ToList());
    }

    // Löschen

    public void AskToDelete(Deletion deletion)
    {
        if (deletion.Kind == DeletionKind.Entry && deletion.Entry?.Lock is not null) return;
        PendingDeletion = deletion;
    }

    public void CancelDeletion() => PendingDeletion = null;

    public string DeletionTitle => PendingDeletion?.Kind switch
    {
        DeletionKind.Period => Loc.T("Zeitraum löschen?"),
        DeletionKind.RunningTimer => Loc.T("Laufende Zeit verwerfen?"),
        _ => Loc.T("Eintrag löschen?"),
    };

    public string DeletionConfirmTitle =>
        PendingDeletion?.Kind == DeletionKind.RunningTimer ? Loc.T("Verwerfen") : Loc.T("Löschen");

    public string DeletionMessage
    {
        get
        {
            switch (PendingDeletion)
            {
                case { Kind: DeletionKind.Entry, Entry: { } entry }:
                    var duration = DurationInput.Format(entry.Minutes);
                    return entry.Description.Length == 0
                        ? Loc.T("Der Eintrag über %@ Std. wird in Odoo gelöscht.", duration)
                        : Loc.T("„%@“ (%@ Std.) wird in Odoo gelöscht.", entry.Description, duration);
                case { Kind: DeletionKind.Period, Period: { } period }:
                    return Loc.T("%@ wird in Odoo gelöscht.", RangeText(period));
                case { Kind: DeletionKind.RunningTimer }:
                    var timerDuration = DurationInput.Format(TimerSeconds() / 60);
                    var description = RunningTimer?.Description.Trim() ?? "";
                    var title = description.Length == 0 ? Loc.T("Ohne Beschreibung") : description;
                    return Loc.T("%@ Std. für „%@“ werden nicht gebucht.", timerDuration, title);
                default:
                    return "";
            }
        }
    }

    public async Task ConfirmDeletionAsync()
    {
        if (PendingDeletion is not { } deletion || IsDeleting) return;
        if (deletion.Kind == DeletionKind.RunningTimer)
        {
            PendingDeletion = null;
            DiscardTimer();
            return;
        }
        if (service is not { } current) return;
        var token = sessionToken;
        IsDeleting = true;
        try
        {
            if (deletion is { Kind: DeletionKind.Entry, Entry: { } entry }) await current.DeleteTimesheetAsync(entry.Id);
            else if (deletion is { Kind: DeletionKind.Period, Period: { } period }) await current.DeleteAttendanceAsync(period.Id);
            if (token != sessionToken) return;
            IsDeleting = false;
            PendingDeletion = null;
            if (deletion is { Kind: DeletionKind.Entry, Entry: { } removed })
            {
                if (EditingEntry?.Id == removed.Id) FinishEditing();
                RemoveEntry(removed.Id);
            }
            else if (deletion.Kind == DeletionKind.Period)
            {
                AttendanceEditor = null;
                AttendanceNotice = new StatusNotice(NoticeLevel.Done, Loc.T("Zeitraum gelöscht."));
            }
            await RefreshAsync(force: true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return;
            IsDeleting = false;
            PendingDeletion = null;
            var message = MessageFor(error);
            if (deletion.Kind == DeletionKind.Entry)
            {
                SaveError = message;
            }
            else if (deletion.Kind == DeletionKind.Period)
            {
                if (IsKind(error, OdooErrorKind.AccessDenied)) MarkStampingRefused();
                if (AttendanceEditor is { } editor) AttendanceEditor = editor with { Error = message };
                else AttendanceNotice = new StatusNotice(NoticeLevel.Warning, message);
            }
            if (IsKind(error, OdooErrorKind.Unauthorized)) Handle(error);
        }
    }
}
