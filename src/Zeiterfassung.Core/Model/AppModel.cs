// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.Text.Json.Nodes;
using Zeiterfassung.Demo;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Model;

/// <summary>
/// Zustand und Abläufe der App: anmelden, Tag laden, buchen, stempeln. Läuft im Thread der Oberfläche; alle
/// Änderungen melden sich gesammelt über <see cref="PropertyChanged"/> mit leerem Namen („alles neu lesen“).
/// </summary>
public sealed partial class AppModel : INotifyPropertyChanged
{
    // Abhängigkeiten

    public Preferences Preferences { get; }
    private readonly IHttpSender http;
    private readonly ISecretStore secrets;
    private readonly TimeZoneInfo zone;
    private readonly Func<DateTimeOffset> clock;
    private OdooService? service;
    /// <summary>Steigt mit jeder An- und Abmeldung. Was danach aus einer älteren Anfrage zurückkommt, wird verworfen.</summary>
    private int sessionToken;
    private DateTimeOffset? lastRefresh;
    private DateTimeOffset? lastRefreshAttempt;
    private int failedRefreshes;
    private DateTimeOffset? lastProjectLoad;
    private DateTimeOffset? lastPanelClose;
    private int refreshGeneration;
    private int appliedGeneration;
    private CancellationTokenSource? tickCancellation;
    private Dictionary<CalendarDay, List<TimesheetEntry>> entryCache = [];
    private Dictionary<CalendarDay, List<AttendancePeriod>> periodCache = [];
    private Dictionary<int, DateTimeOffset> taskLoadDates = [];
    private List<UncertainSave> uncertainSaves = [];
    /// <summary>Was im Formular für einen neuen Eintrag stand, bevor ein bestehender zum Bearbeiten geöffnet wurde.</summary>
    private Draft? draftBeforeEditing;

    /// <summary>Ein Eintrag, bei dem die Antwort von Odoo ausblieb: Vielleicht wurde er trotzdem angelegt.</summary>
    /// <param name="MatchesBefore">Gleiche Einträge an diesem Tag vor dem Speichern.</param>
    private sealed record UncertainSave(TimesheetDraft Request, int MatchesBefore);

    public AppModel(Preferences preferences, IHttpSender http, ISecretStore secrets, TimeZoneInfo? zone = null,
                    Func<DateTimeOffset>? clock = null)
    {
        Preferences = preferences;
        this.http = http;
        this.secrets = secrets;
        this.zone = zone ?? TimeZoneInfo.Local;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        now = this.clock();
        day = CalendarDay.From(now, this.zone);
        editDay = day;
        Preferences.Changed += Changed;
    }

    public TimeZoneInfo Zone => zone;

    // Änderungen melden

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sammelt Meldungen: Die Oberfläche setzt hier <c>DispatcherQueue.TryEnqueue</c> ein, dann kommt je
    /// Durchlauf nur eine Meldung an. Ohne (in Tests) meldet jede Änderung sofort.
    /// </summary>
    public Action<Action>? Post { get; set; }

    private bool notificationPending;

    private void Changed()
    {
        if (Post is not { } post)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            return;
        }
        if (notificationPending) return;
        notificationPending = true;
        post(() =>
        {
            notificationPending = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        });
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed();
    }

    /// <summary>Startet eine Aufgabe, ohne auf sie zu warten; Fehler darin gehen nicht verloren, sondern ins Protokoll.</summary>
    private static void Fire(Task task)
    {
        if (task.IsCompleted) return;
        task.ContinueWith(t => System.Diagnostics.Debug.WriteLine($"Zeiterfassung: {t.Exception}"),
                          TaskContinuationOptions.OnlyOnFaulted);
    }

    // Zustand

    private Screen screen = Screen.Starting;
    private OdooSession? session;
    private Capabilities? capabilities;
    private CalendarDay day;
    private DateTimeOffset now;
    private List<TimesheetEntry> entries = [];
    private List<TimesheetEntry> todayEntries = [];
    private List<AttendancePeriod> periods = [];
    private List<AttendancePeriod> todayPeriods = [];
    private AttendancePeriod? openPeriod;
    private HashSet<CalendarDay> loadedDays = [];
    private bool isLoading;
    private Notice? notice;
    private List<Project> projects = [];
    private List<int> recentProjectIds = [];
    private Dictionary<int, List<ProjectTask>> tasksByProject = [];
    private string? taskLoadError;
    private Draft draft = new();
    private bool isSigningIn;
    private bool isSaving;
    private string? saveError;
    private bool isStamping;
    private string? stampError;
    private bool isCheckingCapabilities;
    private TimesheetEntry? editingEntry;
    private CalendarDay editDay;
    private AttendanceEditor? attendanceEditor;
    private bool isSavingAttendance;
    private StatusNotice? attendanceNotice;
    private bool? isAttendanceListExpanded;
    private Deletion? pendingDeletion;
    private bool isDeleting;
    private bool isStoppingTimer;
    private string? timerError;
    private StatusNotice? timerNotice;
    private TimerEditing? timerEditing;

    public Screen Screen { get => screen; private set => Set(ref screen, value); }
    public OdooSession? Session { get => session; private set => Set(ref session, value); }
    public Capabilities? Capabilities { get => capabilities; private set => Set(ref capabilities, value); }
    public CalendarDay Day { get => day; private set => Set(ref day, value); }
    public DateTimeOffset Now { get => now; private set => Set(ref now, value); }
    public IReadOnlyList<TimesheetEntry> Entries => entries;
    public IReadOnlyList<TimesheetEntry> TodayEntries => todayEntries;
    public IReadOnlyList<AttendancePeriod> Periods => periods;
    public IReadOnlyList<AttendancePeriod> TodayPeriods => todayPeriods;
    public AttendancePeriod? OpenPeriod { get => openPeriod; private set => Set(ref openPeriod, value); }
    public IReadOnlySet<CalendarDay> LoadedDays => loadedDays;
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public Notice? Notice { get => notice; private set => Set(ref notice, value); }
    public IReadOnlyList<Project> Projects => projects;
    public IReadOnlyList<int> RecentProjectIds => recentProjectIds;
    public IReadOnlyDictionary<int, List<ProjectTask>> TasksByProject => tasksByProject;
    public string? TaskLoadError { get => taskLoadError; private set => Set(ref taskLoadError, value); }
    public bool IsSigningIn { get => isSigningIn; private set => Set(ref isSigningIn, value); }
    public bool IsSaving { get => isSaving; private set => Set(ref isSaving, value); }
    public string? SaveError { get => saveError; private set => Set(ref saveError, value); }
    public bool IsStamping { get => isStamping; private set => Set(ref isStamping, value); }
    public string? StampError { get => stampError; private set => Set(ref stampError, value); }
    public bool IsCheckingCapabilities { get => isCheckingCapabilities; private set => Set(ref isCheckingCapabilities, value); }
    /// <summary>Eintrag, der gerade im Formular bearbeitet wird; <c>null</c> beim Anlegen eines neuen.</summary>
    public TimesheetEntry? EditingEntry { get => editingEntry; private set => Set(ref editingEntry, value); }
    /// <summary>Tag, auf den der bearbeitete Eintrag gebucht ist; lässt sich ändern.</summary>
    public CalendarDay EditDay { get => editDay; set => Set(ref editDay, value); }
    public AttendanceEditor? AttendanceEditor { get => attendanceEditor; private set => Set(ref attendanceEditor, value); }
    public bool IsSavingAttendance { get => isSavingAttendance; private set => Set(ref isSavingAttendance, value); }
    public StatusNotice? AttendanceNotice { get => attendanceNotice; private set => Set(ref attendanceNotice, value); }
    /// <summary>Auf- oder zugeklappt von Hand; <c>null</c> heißt: aufklappen, wenn etwas zu prüfen ist.</summary>
    public bool? IsAttendanceListExpanded { get => isAttendanceListExpanded; private set => Set(ref isAttendanceListExpanded, value); }
    public Deletion? PendingDeletion { get => pendingDeletion; private set => Set(ref pendingDeletion, value); }
    public bool IsDeleting { get => isDeleting; private set => Set(ref isDeleting, value); }
    public bool IsStoppingTimer { get => isStoppingTimer; private set => Set(ref isStoppingTimer, value); }
    /// <summary>Warum die gestoppte Projektzeit nicht gebucht werden konnte.</summary>
    public string? TimerError { get => timerError; private set => Set(ref timerError, value); }
    public StatusNotice? TimerNotice { get => timerNotice; private set => Set(ref timerNotice, value); }
    /// <summary>Die Projektzeit steht im Formular; <c>null</c> für einen neuen oder bearbeiteten Eintrag.</summary>
    public TimerEditing? TimerEditing { get => timerEditing; private set => Set(ref timerEditing, value); }

    private void SetEntries(List<TimesheetEntry> value) { entries = value; Changed(); }
    private void SetTodayEntries(List<TimesheetEntry> value) { todayEntries = value; Changed(); }
    private void SetPeriods(List<AttendancePeriod> value) { periods = value; Changed(); }
    private void SetTodayPeriods(List<AttendancePeriod> value) { todayPeriods = value; Changed(); }
    private void SetProjects(List<Project> value) { projects = value; Changed(); }
    private void SetRecentProjectIds(List<int> value) { recentProjectIds = value; Changed(); }

    // Formular

    public Draft Draft { get => draft; set => Set(ref draft, value); }

    public string DraftDescription
    {
        get => draft.Description;
        set => Draft = draft with { Description = value };
    }

    public string DraftDurationText
    {
        get => draft.DurationText;
        set => Draft = draft with { DurationText = value };
    }

    // Start, Anmeldung, Abmeldung

    public async Task StartAsync()
    {
        StartTicking();
        if (Preferences.Session is not { } stored || secrets.Read(stored.SecretAccount) is not { } apiKey)
        {
            Screen = Screen.Login;
            return;
        }
        Session = stored;
        Capabilities = Preferences.CachedCapabilities;
        service = MakeService(stored, apiKey, Capabilities?.Schema ?? new Schema());
        Screen = Screen.Main;
        await RefreshCapabilitiesAsync();
        await RefreshAsync(force: true);
    }

    public void Stop()
    {
        tickCancellation?.Cancel();
        tickCancellation = null;
    }

    /// <summary>
    /// Meldet an, prüft die Funktionen und legt den API-Schlüssel in der Anmeldeinformationsverwaltung ab.
    /// Die Hauptansicht erscheint erst mit <see cref="FinishSignIn"/>.
    /// </summary>
    public async Task<Capabilities> SignInAsync(string server, string apiKey, string? login, string? database)
    {
        // „demo“ führt zum Demo-Server im Arbeitsspeicher, mit dessen festem Schlüssel.
        var isDemo = DemoServer.IsRequested(server);
        var url = isDemo ? DemoServer.Url : ServerAddress.Normalize(server);
        if (url is null) throw new OdooException(OdooErrorKind.InvalidServerAddress);
        if (!ServerAddress.IsSecure(url)) throw new OdooException(OdooErrorKind.InsecureServerAddress);
        var key = isDemo ? DemoServer.ApiKey : apiKey.Trim();
        if (key.Length == 0) throw OdooException.Unauthorized();
        if (isDemo) database = null;
        IsSigningIn = true;
        try
        {
            var newSession = await new Connector(http).ConnectAsync(url, key, isDemo ? null : login, database);
            var newService = MakeService(newSession, key, new Schema());
            var accountChanged = Session?.SecretAccount != newSession.SecretAccount;
            var found = await newService.DiscoverCapabilitiesAsync(accountChanged ? null : Preferences.AttendanceEmployeeId);
            newService.Schema = found.Schema;

            secrets.Save(key, newSession.SecretAccount, $"{AppInfo.Name} – {ServerAddress.Host(url)}");
            if (accountChanged) Preferences.AttendanceEmployeeId = null;
            if (Session is { } previous && accountChanged) secrets.Delete(previous.SecretAccount);
            Preferences.Session = newSession;
            Preferences.CachedCapabilities = found;
            if (Preferences.RunningTimer?.Account != newSession.SecretAccount) Preferences.RunningTimer = null;
            // Die Datenbank nur merken, wenn sie eingegeben wurde. Sonst findet die App sie selbst wieder.
            Preferences.LoginHints = new LoginHints(isDemo ? "demo" : url, newSession.Login,
                                                    string.IsNullOrWhiteSpace(database) ? null : database.Trim());
            Preferences.StampingRefused = false;
            Preferences.TimesheetsEnabled = found.Timesheets == FeatureStatus.Available || found.Attendance != FeatureStatus.Available;
            Preferences.AttendanceEnabled = found.Attendance == FeatureStatus.Available;

            Session = newSession;
            Capabilities = found;
            service = newService;
            ResetLoadedData();
            if (accountChanged) Draft = new Draft();
            return found;
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    /// <summary>Nach „Fertig“ in der Anmeldung.</summary>
    public void FinishSignIn()
    {
        Screen = Screen.Main;
        Notice = null;
        Fire(RefreshAsync(force: true));
    }

    /// <summary>Zur Anmeldung, etwa wenn der API-Schlüssel abgelaufen ist. Die Angaben bleiben vorausgefüllt.</summary>
    public void ShowSignIn() => Screen = Screen.Login;

    /// <summary>Aus der Anmeldung zurück geht es nur, wenn noch eine Verbindung besteht und gerade keine neue entsteht.</summary>
    public bool CanCancelSignIn => Session is not null && !IsSigningIn;

    public void CancelSignIn()
    {
        if (!CanCancelSignIn) return;
        Screen = Screen.Main;
    }

    public void SignOut()
    {
        if (Session is { } current) secrets.Delete(current.SecretAccount);
        Preferences.Session = null;
        Preferences.CachedCapabilities = null;
        Preferences.StampingRefused = false;
        Preferences.AttendanceEmployeeId = null;
        Preferences.RunningTimer = null;
        Session = null;
        Capabilities = null;
        service = null;
        ResetLoadedData();
        Draft = new Draft();
        Notice = null;
        Screen = Screen.Login;
    }

    // Laden

    public async Task PanelDidOpenAsync()
    {
        Now = clock();
        // Nach längerer Pause wieder mit heute beginnen.
        if (lastPanelClose is { } closed && (Now - closed).TotalSeconds > 600 && Day != Today) Show(Today, refreshing: false);
        // Aufgaben des gewählten Projekts auffrischen, falls sie schon älter sind.
        if (Draft.ProjectId is { } projectId) Fire(LoadTasksAsync(projectId));
        await RefreshAsync(force: false);
    }

    public void PanelDidClose()
    {
        lastPanelClose = clock();
        TimerNotice = null;
    }

    /// <summary>
    /// Prüft, welche Funktionen Odoo bietet. Mit <paramref name="recheck"/> (auf Wunsch in den Einstellungen) wird
    /// auch ein Stempeln wieder angeboten, das Odoo früher abgelehnt hat.
    /// </summary>
    public async Task RefreshCapabilitiesAsync(bool recheck = false)
    {
        if (service is not { } current) return;
        var token = sessionToken;
        IsCheckingCapabilities = true;
        try
        {
            var found = await current.DiscoverCapabilitiesAsync(Preferences.AttendanceEmployeeId);
            if (token != sessionToken) return;
            if (recheck) Preferences.StampingRefused = false;
            if (Preferences.StampingRefused) found = found with { CanStamp = false };
            Capabilities = found;
            Preferences.CachedCapabilities = found;
            if (service is { } active) active.Schema = found.Schema;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return;
            Handle(error);
        }
        finally
        {
            IsCheckingCapabilities = false;
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (Screen != Screen.Main || service is not { } current) return;
        Now = clock();
        if (!force && lastRefresh is { } last && (Now - last).TotalSeconds < 15) return;

        refreshGeneration += 1;
        var generation = refreshGeneration;
        var request = new LoadRequest(
            Today, Day, TimesheetsActive, AttendanceActive,
            TimesheetsActive && (Projects.Count == 0 || lastProjectLoad is not { } loaded || (Now - loaded).TotalSeconds > 600),
            Capabilities?.EmployeeId);
        IsLoading = true;
        lastRefreshAttempt = Now;
        var snapshot = await LoadAsync(request, current, zone);
        if (generation == refreshGeneration) IsLoading = false;
        // Ein älteres Ergebnis, das nach einem neueren eintrifft, wird verworfen, ebenso alles von vor einer
        // An- oder Abmeldung.
        if (generation <= appliedGeneration) return;
        appliedGeneration = generation;
        var failures = Apply(snapshot, request);
        if (failures.Count > 0)
        {
            var failure = failures.MinBy(item => Weight(item.Error))!;
            failedRefreshes += 1;
            Handle(failure.Error, failures.Count < request.PartCount ? failure.Problem : null);
        }
        else
        {
            failedRefreshes = 0;
            lastRefresh = clock();
            if (Notice?.Kind != NoticeKind.SignedOut) Notice = null;
        }
    }

    /// <param name="AttendanceEmployeeId">Mit Mitarbeitern in mehreren Firmen nur die Anwesenheiten dessen, mit dem
    /// gestempelt wird.</param>
    private sealed record LoadRequest(CalendarDay Today, CalendarDay Viewed, bool Timesheets, bool Attendance, bool Projects,
                                      int? AttendanceEmployeeId)
    {
        public int PartCount => new[] { Timesheets, Attendance, Projects }.Count(wanted => wanted);
    }

    private sealed record TimesheetPart(List<TimesheetEntry> Today, List<TimesheetEntry> Viewed);

    private sealed record AttendancePart(List<AttendancePeriod> Today, List<AttendancePeriod> Viewed, AttendancePeriod? Open);

    private sealed record ProjectPart(List<Project> List, List<int> RecentIds);

    /// <summary>Ein geladener Teil oder der Fehler beim Laden; <c>null</c>, wenn nicht angefragt.</summary>
    private sealed record Part<T>(T? Value, Exception? Error) where T : class;

    private sealed record Snapshot(Part<TimesheetPart>? Timesheets, Part<AttendancePart>? Attendance, Part<ProjectPart>? Projects);

    /// <param name="Problem">Was nicht geladen werden konnte, als ganzer Satz: „Anwesenheiten konnten nicht geladen
    /// werden.“</param>
    private sealed record LoadFailure(string Problem, Exception Error);

    /// <summary>
    /// Lädt Zeiteinträge, Anwesenheiten und Projekte gleichzeitig, aber getrennt: Scheitert ein Teil, erscheint der
    /// Rest trotzdem.
    /// </summary>
    private static async Task<Snapshot> LoadAsync(LoadRequest request, OdooService service, TimeZoneInfo zone)
    {
        var separateDay = request.Viewed != request.Today;

        async Task<TimesheetPart> TimesheetsAsync()
        {
            var today = service.TimesheetsAsync(request.Today);
            var viewed = separateDay ? service.TimesheetsAsync(request.Viewed) : Task.FromResult(new List<TimesheetEntry>());
            var todayEntries = await today;
            var viewedEntries = await viewed;
            return new TimesheetPart(todayEntries, separateDay ? viewedEntries : todayEntries);
        }

        async Task<AttendancePart> AttendanceAsync()
        {
            var today = service.AttendancesAsync(request.Today, zone, request.AttendanceEmployeeId);
            var viewed = separateDay
                ? service.AttendancesAsync(request.Viewed, zone, request.AttendanceEmployeeId)
                : Task.FromResult(new List<AttendancePeriod>());
            var open = service.OpenAttendanceAsync(request.AttendanceEmployeeId);
            var todayPeriods = await today;
            var viewedPeriods = await viewed;
            var openPeriod = await open;
            return new AttendancePart(todayPeriods, separateDay ? viewedPeriods : todayPeriods, openPeriod);
        }

        async Task<ProjectPart> ProjectsAsync()
        {
            var list = service.ProjectsAsync();
            var recent = service.RecentProjectIdsAsync(request.Today.AddingDays(-30));
            return new ProjectPart(await list, await recent);
        }

        var timesheets = CaptureAsync(request.Timesheets, TimesheetsAsync);
        var attendance = CaptureAsync(request.Attendance, AttendanceAsync);
        var projectList = CaptureAsync(request.Projects, ProjectsAsync);
        return new Snapshot(await timesheets, await attendance, await projectList);
    }

    private static async Task<Part<T>?> CaptureAsync<T>(bool wanted, Func<Task<T>> work) where T : class
    {
        if (!wanted) return null;
        try
        {
            return new Part<T>(await work(), null);
        }
        catch (Exception error)
        {
            return new Part<T>(null, error);
        }
    }

    /// <summary>Übernimmt, was geladen wurde, und meldet, was nicht.</summary>
    private List<LoadFailure> Apply(Snapshot snapshot, LoadRequest request)
    {
        var failures = new List<LoadFailure>();
        var days = new[] { request.Today, request.Viewed };
        switch (snapshot.Timesheets)
        {
            case { Value: { } part }:
                SetTodayEntries(part.Today);
                entryCache[request.Today] = part.Today;
                entryCache[request.Viewed] = part.Viewed;
                if (request.Viewed == Day) SetEntries(part.Viewed);
                loadedDays.UnionWith(days);
                Changed();
                break;
            case { Error: { } error }:
                failures.Add(new LoadFailure(Loc.T("Zeiteinträge konnten nicht geladen werden."), error));
                break;
        }
        switch (snapshot.Attendance)
        {
            case { Value: { } part }:
                SetTodayPeriods(part.Today);
                OpenPeriod = part.Open;
                periodCache[request.Today] = part.Today;
                periodCache[request.Viewed] = part.Viewed;
                if (request.Viewed == Day) SetPeriods(part.Viewed);
                if (!request.Timesheets)
                {
                    loadedDays.UnionWith(days);
                    Changed();
                }
                break;
            case { Error: { } error }:
                failures.Add(new LoadFailure(Loc.T("Anwesenheiten konnten nicht geladen werden."), error));
                break;
        }
        switch (snapshot.Projects)
        {
            case { Value: { } part }:
                SetProjects(part.List);
                SetRecentProjectIds(part.RecentIds);
                lastProjectLoad = clock();
                // Ein inzwischen geschlossenes Projekt nicht weiter zum Buchen anbieten. Ein bearbeiteter Eintrag
                // behält sein Projekt.
                if (Draft.ProjectId is { } projectId && part.List.All(project => project.Id != projectId)
                    && EditingEntry?.Project?.Id != projectId)
                {
                    Draft = Draft with { ProjectId = null, TaskId = null };
                }
                break;
            case { Error: { } error }:
                failures.Add(new LoadFailure(Loc.T("Projekte konnten nicht geladen werden."), error));
                break;
        }
        return failures;
    }

    /// <summary>Vergisst alles Geladene, etwa nach einer An- oder Abmeldung. Was noch unterwegs ist, läuft ins Leere.</summary>
    private void ResetLoadedData()
    {
        sessionToken += 1;
        refreshGeneration += 1;
        appliedGeneration = refreshGeneration;
        IsLoading = false;
        IsSaving = false;
        IsStamping = false;
        SetEntries([]);
        SetTodayEntries([]);
        SetPeriods([]);
        SetTodayPeriods([]);
        OpenPeriod = null;
        loadedDays = [];
        entryCache = [];
        periodCache = [];
        SetProjects([]);
        SetRecentProjectIds([]);
        tasksByProject = [];
        taskLoadDates = [];
        TaskLoadError = null;
        uncertainSaves = [];
        lastRefresh = null;
        lastRefreshAttempt = null;
        failedRefreshes = 0;
        lastProjectLoad = null;
        SaveError = null;
        StampError = null;
        if (EditingEntry is not null || TimerEditing is not null) Draft = draftBeforeEditing ?? new Draft();
        EditingEntry = null;
        TimerEditing = null;
        draftBeforeEditing = null;
        AttendanceEditor = null;
        AttendanceNotice = null;
        IsAttendanceListExpanded = null;
        IsSavingAttendance = false;
        PendingDeletion = null;
        IsDeleting = false;
        IsStoppingTimer = false;
        TimerError = null;
        TimerNotice = null;
        Day = Today;
        EditDay = Today;
        Changed();
    }

    /// <summary>Welcher Fehler zählt, wenn mehrere Teile scheitern: abgemeldet vor offline vor dem Rest.</summary>
    private static int Weight(Exception error) => error is OdooException odoo
        ? odoo.Kind switch
        {
            OdooErrorKind.Unauthorized => 0,
            OdooErrorKind.Offline => 1,
            _ => 2,
        }
        : 2;

    /// <param name="problem">Was nicht geladen werden konnte, wenn der Rest geladen wurde.</param>
    private void Handle(Exception error, string? problem = null)
    {
        if (error is OperationCanceledException) return;
        System.Diagnostics.Debug.WriteLine($"Odoo: {error.Message}");
        Notice = (error as OdooException)?.Kind switch
        {
            OdooErrorKind.Unauthorized => new Notice(NoticeKind.SignedOut, Loc.T("Die Anmeldung bei Odoo ist abgelaufen oder wurde widerrufen.")),
            OdooErrorKind.Offline => new Notice(NoticeKind.Offline, Loc.T("Odoo ist gerade nicht erreichbar.")),
            _ => new Notice(NoticeKind.Failure, problem is null ? error.Message : $"{problem} {error.Message}"),
        };
    }

    public static string MessageFor(Exception error) => error is OdooException { Kind: OdooErrorKind.Offline }
        ? Loc.T("Keine Verbindung zu Odoo. Deine Angaben bleiben erhalten.")
        : error.Message;

    private static bool IsKind(Exception error, OdooErrorKind kind) => error is OdooException odoo && odoo.Kind == kind;

    // Uhr

    private void StartTicking()
    {
        tickCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        tickCancellation = cancellation;
        Fire(TickLoopAsync(cancellation.Token));
    }

    private async Task TickLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickDelay, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await TickAsync();
        }
    }

    /// <summary>Alle 20 Sekunden; läuft eine Projektzeit, auch zu jeder ihrer vollen Minuten.</summary>
    private TimeSpan TickDelay
    {
        get
        {
            if (CountingStart is not { } start) return TimeSpan.FromSeconds(20);
            var intoMinute = (clock() - start).TotalSeconds % 60;
            return TimeSpan.FromSeconds(Math.Clamp(60 - intoMinute + 0.05, 0.05, 20));
        }
    }

    /// <summary>Tageswechsel, regelmäßig neu laden, nach Fehlern erneut versuchen.</summary>
    public async Task TickAsync()
    {
        var previousToday = Today;
        Now = clock();
        if (Today != previousToday)
        {
            if (Day == previousToday) Show(Today, refreshing: false);
            await RefreshAsync(force: true);
        }
        else if (IsRefreshDue)
        {
            await RefreshAsync(force: true);
        }
    }

    /// <summary>
    /// Alle fünf Minuten; nach einem Fehler früher, mit wachsendem Abstand (20 s, 40 s, 80 s … bis 5 min). Eine
    /// abgelaufene Anmeldung wird nicht wiederholt, sie braucht einen neuen Schlüssel.
    /// </summary>
    private bool IsRefreshDue
    {
        get
        {
            if (IsLoading || Notice?.Kind == NoticeKind.SignedOut) return false;
            if (failedRefreshes == 0 && lastRefresh is { } last) return (Now - last).TotalSeconds >= 300;
            if (lastRefreshAttempt is not { } attempt) return true;
            var wait = Math.Min(300, 20 << Math.Min(Math.Max(failedRefreshes - 1, 0), 4));
            return (Now - attempt).TotalSeconds >= wait;
        }
    }

    // Tage

    public CalendarDay Today => CalendarDay.From(Now, zone);
    public bool IsShowingToday => Day == Today;
    public bool IsDayLoaded => loadedDays.Contains(Day);

    public void ShowPreviousDay() => Show(Day.AddingDays(-1));
    public void ShowNextDay() => Show(Day.AddingDays(1));
    public void ShowToday() => Show(Today);

    /// <summary>Ein anderer Tag beendet, was gerade bearbeitet wird.</summary>
    private void Show(CalendarDay newDay, bool refreshing = true)
    {
        if (newDay == Day) return;
        CancelEditing();
        AttendanceEditor = null;
        AttendanceNotice = null;
        IsAttendanceListExpanded = null;
        PendingDeletion = null;
        Day = newDay;
        EditDay = newDay;
        SetEntries(entryCache.GetValueOrDefault(newDay) ?? []);
        SetPeriods(periodCache.GetValueOrDefault(newDay) ?? []);
        SaveError = null;
        StampError = null;
        if (refreshing) Fire(RefreshAsync(force: true));
    }

    public string DayTitle
    {
        get
        {
            if (Day == Today) return Loc.T("Heute");
            if (Day == Today.AddingDays(-1)) return Loc.T("Gestern");
            if (Day == Today.AddingDays(1)) return Loc.T("Morgen");
            return Formatting.Weekday(Day);
        }
    }

    public string DayLongDate => Formatting.LongDate(Day);
    public string DayShortDate => Formatting.ShortDate(Day);

    /// <summary>„Für heute ist noch nichts erfasst.“ – als ganzer Satz, weil sich die Sätze je Sprache anders bilden.</summary>
    public string EmptyDayMessage
    {
        get
        {
            if (Day == Today) return Loc.T("Für heute ist noch nichts erfasst.");
            if (Day == Today.AddingDays(-1)) return Loc.T("Für gestern ist noch nichts erfasst.");
            return Loc.T("Für %@ ist noch nichts erfasst.", Formatting.ShortDate(Day));
        }
    }

    // Funktionen

    public bool TimesheetsActive => Preferences.TimesheetsEnabled && Capabilities?.Timesheets == FeatureStatus.Available;
    public bool AttendanceActive => Preferences.AttendanceEnabled && Capabilities?.Attendance == FeatureStatus.Available;
    public bool NoFeatureActive => !TimesheetsActive && !AttendanceActive;

    /// <summary>Schaltet die Zeiterfassung. Die letzte aktive Funktion lässt sich nicht ausschalten.</summary>
    public bool SetTimesheetsEnabled(bool enabled)
    {
        if (!enabled && TimesheetsActive && !AttendanceActive) return false;
        Preferences.TimesheetsEnabled = enabled;
        Fire(RefreshAsync(force: true));
        return true;
    }

    /// <summary>Schaltet die Anwesenheit. Die letzte aktive Funktion lässt sich nicht ausschalten.</summary>
    public bool SetAttendanceEnabled(bool enabled)
    {
        if (!enabled && AttendanceActive && !TimesheetsActive) return false;
        Preferences.AttendanceEnabled = enabled;
        Fire(RefreshAsync(force: true));
        return true;
    }

    // Konto

    public string UserName => Capabilities?.UserName ?? "";

    public string ServerDisplay
    {
        get
        {
            if (Session?.ServerUrl is not { } url) return "";
            return DemoServer.IsDemo(url) ? Loc.T("Demo") : ServerAddress.Host(url);
        }
    }

    public string InterfaceDisplay
    {
        get
        {
            if (Session is not { } current) return "";
            var version = current.ServerMajorVersion is { } major ? $"Odoo {major}" : "Odoo";
            return current.Interface == OdooInterface.Json2 ? $"{version} · JSON-2" : $"{version} · JSON-RPC";
        }
    }

    /// <summary>Zeiterfassung oder Anwesenheiten im Browser.</summary>
    public string? OdooWebUrl
    {
        get
        {
            if (Session is not { } current || DemoServer.IsDemo(current.ServerUrl)) return null;
            var attendanceOnly = AttendanceActive && !TimesheetsActive;
            if ((current.ServerMajorVersion ?? 19) >= 18)
            {
                return $"{current.ServerUrl}/{(attendanceOnly ? "odoo/attendances" : "odoo/timesheets")}";
            }
            var action = attendanceOnly ? "hr_attendance.hr_attendance_action" : "hr_timesheet.act_hr_timesheet_line";
            return $"{current.ServerUrl}/web#action={action}";
        }
    }

    private OdooService MakeService(OdooSession current, string apiKey, Schema schema)
    {
        var context = new Dictionary<string, JsonNode?>();
        if (current.Language is { } language) context["lang"] = language;
        return new OdooService(current.MakeTransport(apiKey, http), current.Uid, context) { Schema = schema };
    }
}
