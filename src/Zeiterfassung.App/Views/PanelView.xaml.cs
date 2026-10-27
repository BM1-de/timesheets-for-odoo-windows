// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.ViewManagement;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.App.Views;

/// <summary>
/// Inhalt des Fensters über dem Symbol im Infobereich. Liest alles aus dem <see cref="AppModel"/>; meldet das Modell eine
/// Änderung, liest die Ansicht alle Werte neu.
/// </summary>
public sealed partial class PanelView : UserControl, INotifyPropertyChanged
{
    private readonly DispatcherQueueTimer secondTimer;
    private readonly AttendanceEditorState editorState;
    private readonly UISettings uiSettings = new();
    private Deletion? shownDeletion;

    public PanelView()
    {
        Model = App.Current.Model;
        Updates = App.Current.Updates;
        editorState = new AttendanceEditorState(Model);
        InitializeComponent();
        Login.Initialize(Model);
        Model.PropertyChanged += (_, _) => OnModelChanged();
        Updates.Changed += () => DispatcherQueue.TryEnqueue(OnModelChanged);
        secondTimer = DispatcherQueue.CreateTimer();
        secondTimer.Interval = TimeSpan.FromSeconds(1);
        secondTimer.Tick += (_, _) => TickSecond();
        Loaded += (_, _) => secondTimer.Start();
        Unloaded += (_, _) => secondTimer.Stop();
        ActualThemeChanged += (_, _) => OnModelChanged();
        TickSecond();
    }

    public AppModel Model { get; }

    public Updates Updates { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Einstellungen öffnen, etwa über das Zahnrad.</summary>
    public event Action? SettingsRequested;

    /// <summary>Der Inhalt ist höher oder niedriger geworden; das Fenster passt sich an.</summary>
    public event Action? ContentHeightChanged;

    private void OnModelChanged()
    {
        editorState.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        if (Model.PendingDeletion is { } deletion && deletion != shownDeletion) _ = ConfirmDeletionAsync(deletion);
        if (Model.PendingDeletion is null) shownDeletion = null;
    }

    /// <summary>Wie hoch der ganze Inhalt wäre, ohne zu scrollen.</summary>
    public double NaturalHeight => Model.Screen switch
    {
        Screen.Login => Login.ActualHeight,
        Screen.Main => Header.ActualHeight + Banners.ActualHeight + ContentStack.ActualHeight + Footer.ActualHeight,
        _ => 104,
    };

    private void Part_SizeChanged(object sender, SizeChangedEventArgs e) => ContentHeightChanged?.Invoke();

    /// <summary>Was gerade angezeigt wird, vorn: Fokus aufs Formular nach dem Öffnen.</summary>
    public void FocusFirstField()
    {
        if (Model.Screen == Screen.Login) Login.FocusFirstField();
    }

    // Kopf

    public bool IsStarting => Model.Screen == Screen.Starting;
    public bool IsLogin => Model.Screen == Screen.Login;
    public bool IsMain => Model.Screen == Screen.Main;

    public string TodayHelp => Loc.T("Zu heute (Alt+Pos1)");
    public string PreviousDayHelp => Loc.T("Vorheriger Tag (Alt+←)");
    public string NextDayHelp => Loc.T("Nächster Tag (Alt+→)");
    public string SettingsHelp => Loc.T("Einstellungen (Strg+,)");

    private void Today_Click(object sender, RoutedEventArgs e) => Model.ShowToday();
    private void PreviousDay_Click(object sender, RoutedEventArgs e) => Model.ShowPreviousDay();
    private void NextDay_Click(object sender, RoutedEventArgs e) => Model.ShowNextDay();

    private void Today_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsMain) return;
        Model.ShowToday();
        args.Handled = true;
    }

    private void PreviousDay_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsMain) return;
        Model.ShowPreviousDay();
        args.Handled = true;
    }

    private void NextDay_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsMain) return;
        Model.ShowNextDay();
        args.Handled = true;
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsMain) return;
        _ = Model.RefreshAsync(force: true);
        args.Handled = true;
    }

    // Hinweise

    public bool HasNotice => Model.Notice is not null;

    public InfoBarSeverity NoticeSeverity => Model.Notice?.Kind == NoticeKind.Failure ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    public string NoticeMessage => Model.Notice?.Message ?? "";

    public string NoticeAction => Model.Notice?.Kind == NoticeKind.SignedOut ? Loc.T("Neu anmelden") : Loc.T("Erneut versuchen");

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Notice?.Kind == NoticeKind.SignedOut) Model.ShowSignIn();
        else _ = Model.RefreshAsync(force: true);
    }

    public bool ShowsUpdate => Updates.ShowsBanner;
    public InfoBarSeverity UpdateSeverity => Updates.State == UpdateState.Installed ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
    public string UpdateText => Updates.BannerText;
    public bool UpdateClosable => Updates.State is not (UpdateState.Checking or UpdateState.Installing);
    public bool UpdateCanInstall => Updates.State == UpdateState.Available;

    private void Update_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Updates.DismissBanner();

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Version %@ installieren?", Updates.AvailableVersion ?? ""),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T("Die App startet dafür neu, das dauert nur wenige Sekunden. Deine Anwesenheit läuft in Odoo weiter."),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new HyperlinkButton
                    {
                        Content = Loc.T("Was ist neu in Version %@?", Updates.AvailableVersion ?? ""),
                        NavigateUri = new Uri(Updates.ReleaseNotesUrl),
                        Padding = new Thickness(0),
                    },
                },
            },
            PrimaryButtonText = Loc.T("Installieren"),
            CloseButtonText = Loc.T("Später"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Updates.InstallAsync();
    }

    // Laufende Projektzeit

    public bool ShowsTimerSection => Model.TimesheetsActive && (Model.RunningTimer is not null || Model.TimerNotice is not null);

    public bool HasTimer => Model.TimesheetsActive && Model.RunningTimer is not null;

    private bool TimerIsStopped => Model.RunningTimer?.End is not null;

    private bool dotDimmed;

    public Brush TimerDotBrush => TimerIsStopped ? Brushes.Caution : Brushes.Critical;

    public string TimerDescription
    {
        get => Model.TimerDescription;
        set => Model.TimerDescription = value;
    }

    public bool TimerDescriptionEditable => Model.TimerEditing is null;

    public bool TimerPathEditable => Model.TimerEditing is null && !Model.IsStoppingTimer;

    public Brush TimerPathBrush => Model.RunningTimerLacksProject ? Brushes.Accent : Brushes.Secondary;

    public string TimerElapsed { get; private set; } = "";

    public string TimerSpoken => $"{DurationInput.Spoken(Model.TimerSeconds() / 60)}, {Model.TimerStartText}";

    public bool ShowsStopButton => HasTimer && !TimerIsStopped && !Model.IsStoppingTimer;

    public bool ShowsStoppedNote =>
        HasTimer && TimerIsStopped && Model.TimerEditing is null && Model.StoppedTimerReason is not null;

    public string StoppedNoteText => Model.StoppedTimerReason == StoppedTimerReason.Project
        ? Loc.T("Zum Buchen fehlt noch das Projekt.")
        : Loc.T("Kürzer als eine Minute, noch nicht gebucht.");

    public string StoppedNoteButton => Model.StoppedTimerReason == StoppedTimerReason.Project ? Loc.T("Projekt wählen") : Loc.T("Buchen …");

    public bool ShowsTimerError => HasTimer && TimerIsStopped && Model.TimerEditing is null && Model.StoppedTimerReason is null;

    public string TimerErrorText => Model.TimerError ?? Loc.T("Die Zeit steht still, bis sie gebucht ist.");

    public bool HasTimerNotice => Model.TimerNotice is not null;

    public InfoBarSeverity TimerNoticeSeverity => Severity(Model.TimerNotice?.Level);

    public string TimerNoticeText => Model.TimerNotice?.Text ?? "";

    private static InfoBarSeverity Severity(NoticeLevel? level) => level switch
    {
        NoticeLevel.Done => InfoBarSeverity.Success,
        NoticeLevel.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    /// <summary>Jede Sekunde: laufende Zeit und der blinkende Punkt.</summary>
    private void TickSecond()
    {
        var running = HasTimer && !TimerIsStopped;
        var elapsed = HasTimer ? DurationInput.FormatSeconds(Model.TimerSeconds()) : "";
        if (elapsed != TimerElapsed)
        {
            TimerElapsed = elapsed;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimerElapsed)));
        }
        dotDimmed = running && uiSettings.AnimationsEnabled && !dotDimmed;
        if (TimerDot is not null) TimerDot.Opacity = dotDimmed ? 0.35 : 1;
    }

    private void TimerPath_Click(object sender, RoutedEventArgs e) => Model.BeginEditingTimer();

    private void DiscardTimer_Click(object sender, RoutedEventArgs e) => Model.RequestDiscardTimer();

    private async void StopTimer_Click(object sender, RoutedEventArgs e) => await Model.StopTimerAsync();

    private void BookTimer_Click(object sender, RoutedEventArgs e) => Model.BeginBookingTimer();

    private void TimerNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Model.DismissTimerNotice();

    // Anwesenheit

    public AttendanceSummary Summary => Model.AttendanceSummary;

    private bool IsAddingPeriod => Model.AttendanceEditor?.Kind == AttendanceEditorKind.Add;

    public bool OffersAddingInHeader => Summary.ButtonTitle is null && Summary.OffersAdding && !IsAddingPeriod;

    /// <summary>Ohne Zeiterfassung stehen die Zeiten immer da, sonst unter „Zeiten des Tages“.</summary>
    public bool ShowsAttendanceList => !Model.TimesheetsActive || Model.ShowsAttendanceList || IsAddingPeriod;

    public bool ShowsAttendanceToggle => Model.TimesheetsActive && Summary.Rows.Count > 0;

    public bool HasAttention => Summary.Attention is not null;

    public string PeriodsLabel => Loc.T("%lld Zeiträume", Summary.Rows.Count(row => row.Kind == AttendanceRowKind.Period));

    public double ChevronAngle => Model.ShowsAttendanceList ? 90 : 0;

    public bool ShowsAddRow => ShowsAttendanceList && !IsAddingPeriod && Summary.CanEdit && Model.Day <= Model.Today && Summary.Rows.Count > 0;

    public bool HasAttendanceNotice => Model.AttendanceNotice is not null;

    public InfoBarSeverity AttendanceNoticeSeverity => Severity(Model.AttendanceNotice?.Level);

    public string AttendanceNoticeText => Model.AttendanceNotice?.Text ?? "";

    private IReadOnlyList<AttendanceRowItem> attendanceItems = [];

    public IReadOnlyList<AttendanceRowItem> AttendanceItems
    {
        get
        {
            var items = BuildAttendanceItems();
            if (!items.SequenceEqual(attendanceItems)) attendanceItems = items;
            return attendanceItems;
        }
    }

    private List<AttendanceRowItem> BuildAttendanceItems()
    {
        var summary = Summary;
        var editor = Model.AttendanceEditor;
        var items = new List<AttendanceRowItem>();
        foreach (var row in summary.Rows)
        {
            var isEdited = editor switch
            {
                { Kind: AttendanceEditorKind.Period, Period: { } period } => row.Kind == AttendanceRowKind.Period && row.Period?.Id == period.Id,
                { Kind: AttendanceEditorKind.Gap, Before: { } before, After: { } after } =>
                    row.Kind == AttendanceRowKind.Gap && row.Before?.Id == before.Id && row.After?.Id == after.Id,
                _ => false,
            };
            if (isEdited)
            {
                items.Add(new AttendanceRowItem($"editor-{row.Id}", AttendanceTemplate.Editor, "", "", "", false, true, "", row, editorState));
                continue;
            }
            var isGap = row.Kind == AttendanceRowKind.Gap;
            var remark = row.IsAutoCheckOut ? Loc.T("Automatisch beendet") : "";
            var spoken = string.Join(", ", new[] { row.Range, row.Duration, remark }.Where(part => part.Length > 0));
            var template = row.IsInsertedBreak ? AttendanceTemplate.InsertedBreak : AttendanceTemplate.Period;
            items.Add(new AttendanceRowItem(row.Id, template, row.Range, row.Duration, remark, isGap, summary.CanEdit, spoken, row, null));
        }
        if (IsAddingPeriod)
        {
            items.Add(new AttendanceRowItem("editor-add", AttendanceTemplate.Editor, "", "", "", false, true, "", null, editorState));
        }
        return items;
    }

    private async void Stamp_Click(object sender, RoutedEventArgs e) => await Model.ToggleAttendanceAsync();

    private void AddPeriod_Click(object sender, RoutedEventArgs e) => Model.AddAttendancePeriod();

    private void ToggleAttendanceList_Click(object sender, RoutedEventArgs e) =>
        Model.SetAttendanceListExpanded(!Model.ShowsAttendanceList);

    private void AttendanceNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Model.DismissAttendanceNotice();

    private void AttendanceRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttendanceRowItem { Row: { } row }) return;
        if (row.Kind == AttendanceRowKind.Period && row.Period is { } period) Model.EditPeriod(period);
        else if (row.Before is { } before && row.After is { } after) Model.EditGap(before, after);
    }

    private void AttendanceRow_PointerEntered(object sender, PointerRoutedEventArgs e) => SetPenOpacity(sender, 1);

    private void AttendanceRow_PointerExited(object sender, PointerRoutedEventArgs e) => SetPenOpacity(sender, 0);

    private static void SetPenOpacity(object sender, double opacity)
    {
        if (sender is FrameworkElement element && FindChild(element, "Pen") is { } pen) pen.Opacity = opacity;
    }

    private void EditorFrom_Loaded(object sender, RoutedEventArgs e)
    {
        if (!editorState.FocusesEnd && sender is TextBox box) box.Focus(FocusState.Programmatic);
    }

    private void EditorTo_Loaded(object sender, RoutedEventArgs e)
    {
        if (editorState.FocusesEnd && sender is TextBox box) box.Focus(FocusState.Programmatic);
    }

    private async void EditorField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await Model.SaveAttendanceAsync();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Model.CancelAttendanceEditing();
                break;
        }
    }

    private void DeletePeriod_Click(object sender, RoutedEventArgs e)
    {
        if (Model.AttendanceEditor is { Kind: AttendanceEditorKind.Period, Period: { } period })
        {
            Model.AskToDelete(new Deletion(DeletionKind.Period, Period: period));
        }
    }

    private async void SaveAttendance_Click(object sender, RoutedEventArgs e) => await Model.SaveAttendanceAsync();

    private void CancelAttendance_Click(object sender, RoutedEventArgs e) => Model.CancelAttendanceEditing();

    // Einträge

    public bool HasEntries => Model.Entries.Count > 0;

    /// <summary>Ab sechs Zeilen scrollt die Liste, damit das Fenster auf den Bildschirm passt.</summary>
    public double EntriesMaxHeight => 6 * 52 + 26;

    public string EmptyTitle => Model.IsDayLoaded ? Loc.T("Keine Einträge") : "";

    public string EmptyText => Model.IsDayLoaded ? Model.EmptyDayMessage : Loc.T("Wird geladen …");

    public string SumText => Loc.T("%@ Std.", DurationInput.Format(Model.BookedMinutes));

    public string SumSpoken => Loc.T("Summe %@", DurationInput.Spoken(Model.BookedMinutes));

    private IReadOnlyList<EntryRowItem> entryItems = [];

    public IReadOnlyList<EntryRowItem> EntryItems
    {
        get
        {
            var items = BuildEntryItems();
            if (!items.SequenceEqual(entryItems)) entryItems = items;
            return entryItems;
        }
    }

    private List<EntryRowItem> BuildEntryItems()
    {
        var items = new List<EntryRowItem>();
        foreach (var group in Model.EntryGroups)
        {
            if (group.Title is { } title)
            {
                var minutes = DurationInput.Format(group.Minutes);
                items.Add(new EntryRowItem($"firma-{group.Id}", true, title, "", minutes, false, "", false, false, false,
                                           $"{title}, {DurationInput.Spoken(group.Minutes)}", "", null));
            }
            foreach (var entry in group.Entries)
            {
                var untitled = entry.Description.Length == 0;
                var display = untitled ? Loc.T("Ohne Beschreibung") : entry.Description;
                var lockLabel = entry.Lock switch
                {
                    EntryLock.Invoiced => Loc.T("abgerechnet"),
                    EntryLock.Validated => Loc.T("freigegeben"),
                    _ => "",
                };
                var spoken = string.Join(", ", new[] { display, entry.Path, DurationInput.Spoken(entry.Minutes), lockLabel }
                                                  .Where(part => part.Length > 0));
                var tip = string.Join("\n", new[] { entry.Description, entry.Path }.Where(part => part.Length > 0));
                items.Add(new EntryRowItem($"eintrag-{entry.Id}", false, display, entry.Path, DurationInput.Format(entry.Minutes),
                                           entry.Lock is not null, lockLabel, Model.EditingEntry?.Id == entry.Id,
                                           Model.CanStartTimerLike(entry), untitled, spoken, tip, entry));
            }
        }
        return items;
    }

    private void EntryRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntryRowItem { Entry: { } entry }) Model.BeginEditing(entry);
    }

    private async void ResumeEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntryRowItem { Entry: { } entry }) await Model.StartTimerLikeAsync(entry);
    }

    private void EntryRow_PointerEntered(object sender, PointerRoutedEventArgs e) => SetResumeOpacity(sender, 1);

    private void EntryRow_PointerExited(object sender, PointerRoutedEventArgs e) => SetResumeOpacity(sender, 0);

    private static void SetResumeOpacity(object sender, double opacity)
    {
        if (sender is FrameworkElement element && FindChild(element, "ResumeButton") is { } button) button.Opacity = opacity;
    }

    /// <summary>Rechtsklick auf einen Eintrag: Ändern, Erneut starten, Beschreibung kopieren, Löschen.</summary>
    private void EntryMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target?.DataContext is not EntryRowItem { Entry: { } entry }) return;
        menu.Items.Clear();
        var edit = new MenuFlyoutItem { Text = Loc.T("Ändern") };
        edit.Click += (_, _) => Model.BeginEditing(entry);
        menu.Items.Add(edit);
        if (Model.CanStartTimerLike(entry))
        {
            var start = new MenuFlyoutItem { Text = Loc.T("Erneut starten") };
            start.Click += async (_, _) => await Model.StartTimerLikeAsync(entry);
            menu.Items.Add(start);
        }
        var copy = new MenuFlyoutItem { Text = Loc.T("Beschreibung kopieren"), IsEnabled = entry.Description.Length > 0 };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(entry.Description);
            Clipboard.SetContent(package);
        };
        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = Loc.T("Löschen …"), IsEnabled = entry.Lock is null };
        delete.Click += (_, _) => Model.AskToDelete(new Deletion(DeletionKind.Entry, entry));
        menu.Items.Add(delete);
    }

    // Formular

    public bool IsEditing => Model.EditingEntry is not null || Model.TimerEditing is not null;

    /// <summary>Abgerechnet oder freigegeben: Projekt, Aufgabe, Dauer und Datum stehen fest.</summary>
    public bool FormFixed => Model.EditingIsLimited || Model.EditingIsLocked;

    public string FormTitle => Model.TimerEditing switch
    {
        TimerEditing.Running => Loc.T("Laufende Zeit"),
        TimerEditing.Booking => Loc.T("Zeit buchen"),
        _ => Model.EditingEntry is null ? Loc.T("Neuer Eintrag") : Loc.T("Eintrag bearbeiten"),
    };

    public string ProjectLabel => Model.SelectedProjectName ?? Loc.T("Projekt wählen");

    public Brush ProjectLabelBrush => Model.Draft.ProjectId is null ? Brushes.Secondary : Brushes.Primary;

    public bool TaskChooserEnabled => !FormFixed && Model.TaskChoice.Kind == TaskChoiceKind.Tasks;

    public string TaskLabel => Model.TaskChoice.Kind switch
    {
        TaskChoiceKind.NoProject => Loc.T("Erst Projekt wählen"),
        TaskChoiceKind.Loading => Loc.T("Aufgaben werden geladen …"),
        TaskChoiceKind.NoTasks => Loc.T("Keine offenen Aufgaben – Buchung auf das Projekt"),
        TaskChoiceKind.Failed => Model.TaskChoice.Message ?? "",
        _ => Model.SelectedTaskName ?? (Model.TaskIsRequired ? Loc.T("Aufgabe wählen") : Loc.T("Ohne Aufgabe")),
    };

    public Brush TaskLabelBrush => Model.TaskChoice.Kind switch
    {
        TaskChoiceKind.NoProject => Brushes.Tertiary,
        TaskChoiceKind.Tasks when Model.Draft.TaskId is not null => Brushes.Primary,
        _ => Brushes.Secondary,
    };

    public bool TasksLoading => Model.TaskChoice.Kind == TaskChoiceKind.Loading;

    public bool TasksFailed => Model.TaskChoice.Kind == TaskChoiceKind.Failed;

    public string DraftDescription
    {
        get => Model.DraftDescription;
        set => Model.DraftDescription = value;
    }

    public string DescriptionPlaceholder =>
        Model.TimerEditing == TimerEditing.Running ? Loc.T("Was machst du gerade?") : Loc.T("Was hast du gemacht?");

    public bool ShowsDuration => Model.TimerEditing != TimerEditing.Running;

    public string DraftDuration
    {
        get => Model.DraftDurationText;
        set => Model.DraftDurationText = value;
    }

    public string StepHelp => Model.Preferences.DurationRounding.Step() switch
    {
        30 => Loc.T("Dauer in halben Stunden ändern"),
        60 => Loc.T("Dauer in ganzen Stunden ändern"),
        _ => Loc.T("Dauer in Viertelstunden ändern"),
    };

    public string LessLabel => Model.Preferences.DurationRounding.LessLabel();

    public string MoreLabel => Model.Preferences.DurationRounding.MoreLabel();

    public string EditDayLabel => Model.DayChoiceTitle(Model.EditDay);

    public string LockNote =>
        Model.EditingIsLimited
            ? Loc.T("Schon abgerechnet: Projekt, Aufgabe, Dauer und Datum stehen fest, löschen geht nicht mehr. Die Beschreibung kannst du noch ändern.")
            : Model.EditingIsLocked
                ? Loc.T("Schon freigegeben: Ändern kann diesen Eintrag nur noch, wer in Odoo Zeiteinträge freigibt.")
                : "";

    public string DurationHintText => FormFixed ? "" : Model.DurationHint?.Text ?? "";

    public Brush DurationHintBrush => Model.DurationHint?.IsProblem == true ? Brushes.Critical : Brushes.Secondary;

    public bool HasSaveError => Model.SaveError is not null;

    public string SaveErrorTitle => Model.TimerEditing switch
    {
        TimerEditing.Booking => Loc.T("Projektzeit konnte nicht gebucht werden"),
        TimerEditing.Running => Loc.T("Änderung konnte nicht gesichert werden"),
        _ => Model.EditingEntry is null ? Loc.T("Eintrag konnte nicht gesichert werden") : Loc.T("Änderung konnte nicht gesichert werden"),
    };

    public string ForDayText => Loc.T("für %@", Model.DayShortDate);

    public string SaveTitle => Model.SaveError is null ? Loc.T("Eintragen") : Loc.T("Erneut versuchen");

    public string ConfirmTitle =>
        Model.SaveError is not null ? Loc.T("Erneut versuchen")
        : Model.TimerEditing == TimerEditing.Booking ? Loc.T("Buchen")
        : Loc.T("Sichern");

    public string StartHint
    {
        get
        {
            if (Model.OffersTimerStart && Model.RunningTimerLacksProject)
            {
                return Loc.T("Die laufende Zeit braucht erst ein Projekt, dann startet eine neue.");
            }
            if (Model.OffersTimerStart && Model.RunningTimerBlocksStart)
            {
                return Loc.T("Die gestoppte Zeit erst buchen oder verwerfen, dann startet eine neue.");
            }
            return Model.RunningTimer is not null && Model.CanStartTimer ? Loc.T("Starten bucht vorher die laufende Zeit.") : "";
        }
    }

    public bool CanDeleteEntry => Model.EditingEntry is { Lock: null };

    public string TimerForDayText => Model.TimerEditing == TimerEditing.Booking ? Loc.T("für %@", Model.TimerDayShortDate) : "";

    private async void Save_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    /// <summary>Speichert; nach einem neuen Eintrag steht der Fokus wieder in der Beschreibung.</summary>
    private async Task SubmitAsync()
    {
        if (!Model.CanSave) return;
        var wasNewEntry = Model.EditingEntry is null && Model.TimerEditing is null;
        await Model.SaveAsync();
        if (Model.SaveError is null && wasNewEntry) DescriptionBox.Focus(FocusState.Programmatic);
    }

    private async void StartTimer_Click(object sender, RoutedEventArgs e) => await Model.StartTimerAsync();

    private void CancelEditing_Click(object sender, RoutedEventArgs e) => Model.CancelEditing();

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (Model.EditingEntry is { } entry) Model.AskToDelete(new Deletion(DeletionKind.Entry, entry));
    }

    /// <summary>Eingabe speichert, Esc bricht das Bearbeiten ab.</summary>
    private async void FormField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Model.AttendanceEditor is not null || Model.PendingDeletion is not null) return;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                if (sender == DurationBox) Model.NormalizeDuration();
                await SubmitAsync();
                break;
            case VirtualKey.Escape when IsEditing:
                e.Handled = true;
                Model.CancelEditing();
                break;
        }
    }

    private void DurationBox_LostFocus(object sender, RoutedEventArgs e) => Model.NormalizeDuration();

    private void LessDuration_Click(object sender, RoutedEventArgs e) => Model.AdjustDuration(-1);

    private void MoreDuration_Click(object sender, RoutedEventArgs e) => Model.AdjustDuration(1);

    private void RetryTasks_Click(object sender, RoutedEventArgs e) => Model.RetryTasks();

    /// <summary>Aufgaben als Menü mit Häkchen.</summary>
    private void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (Model.TaskChoice is not { Kind: TaskChoiceKind.Tasks, Tasks: { } tasks }) return;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        if (Model.Draft.TaskId is null)
        {
            menu.Items.Add(new RadioMenuFlyoutItem
            {
                Text = Model.TaskIsRequired ? Loc.T("Aufgabe wählen") : Loc.T("Ohne Aufgabe"),
                IsChecked = true,
                IsEnabled = !Model.TaskIsRequired,
                GroupName = "Aufgabe",
            });
        }
        foreach (var task in tasks)
        {
            var item = new RadioMenuFlyoutItem { Text = task.Name, IsChecked = task.Id == Model.Draft.TaskId, GroupName = "Aufgabe" };
            item.Click += (_, _) => Model.SelectTask(task.Id);
            menu.Items.Add(item);
        }
        menu.ShowAt(TaskButton);
    }

    /// <summary>Datum beim Bearbeiten: die letzte Woche und der eigene Tag des Eintrags.</summary>
    private void DateButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var choice in Model.EditDayChoices)
        {
            var item = new RadioMenuFlyoutItem { Text = Model.DayChoiceTitle(choice), IsChecked = choice == Model.EditDay, GroupName = "Datum" };
            item.Click += (_, _) => Model.EditDay = choice;
            menu.Items.Add(item);
        }
        menu.ShowAt(DateButton);
    }

    // Projektauswahl

    private string pickerQuery = "";
    private int? highlightedProjectId;
    private IReadOnlyList<PickerItem> pickerItems = [];

    public string PickerQuery
    {
        get => pickerQuery;
        set
        {
            if (pickerQuery == value) return;
            pickerQuery = value;
            highlightedProjectId = IsSearching ? Matches().FirstOrDefault()?.Id : null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    private bool IsSearching => pickerQuery.Trim().Length > 0;

    private bool MultiCompany => Model.Organization is not null;

    public double PickerWidth => MultiCompany ? 420 : 340;

    public double PickerHeight => MultiCompany ? 300 : 248;

    public string PickerEmptyText =>
        PickerItems.Any(item => item.Kind == PickerItemKind.Option)
            ? ""
            : Model.IsLoading ? Loc.T("Projekte werden geladen …") : Loc.T("Kein Projekt gefunden");

    private IEnumerable<Project> Matches()
    {
        var text = pickerQuery.Trim();
        return text.Length == 0
            ? Model.Projects
            : Model.Projects.Where(project => project.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase));
    }

    public IReadOnlyList<PickerItem> PickerItems
    {
        get
        {
            var items = BuildPickerItems();
            if (!items.SequenceEqual(pickerItems)) pickerItems = items;
            return pickerItems;
        }
    }

    /// <summary>
    /// Zuletzt verwendete Projekte oben. Mit mehreren Firmen gliedert sich die Liste nach Firma; zuletzt verwendete
    /// Projekte und Suchtreffer nennen die Firma dazu.
    /// </summary>
    private List<PickerItem> BuildPickerItems()
    {
        var groups = new List<(string Key, string? Title, List<Project> Projects, bool ShowsCompany)>();
        if (IsSearching)
        {
            groups.Add(("treffer", null, Matches().ToList(), MultiCompany));
        }
        else
        {
            var recent = Model.RecentProjectIds.Select(id => Model.Projects.FirstOrDefault(project => project.Id == id))
                                               .OfType<Project>().ToList();
            if (recent.Count > 0) groups.Add(("zuletzt", Loc.T("Zuletzt verwendet"), recent, MultiCompany));
            var others = Model.Projects.Where(project => !Model.RecentProjectIds.Contains(project.Id)).ToList();
            if (Model.Organization is not { } organization)
            {
                groups.Add(("weitere", groups.Count == 0 ? null : Loc.T("Weitere Projekte"), others, false));
            }
            else
            {
                var order = organization.Companies.Select(company => company.Id).ToList();
                foreach (var companyId in order)
                {
                    var list = others.Where(project => project.CompanyId == companyId).ToList();
                    if (list.Count == 0) continue;
                    groups.Add(($"firma-{companyId}", Model.CompanyName(list[0]), list, false));
                }
                var rest = others.Where(project => project.CompanyId is not { } id || !order.Contains(id)).ToList();
                if (rest.Count > 0) groups.Add(("ohne", Loc.T("Ohne Firma"), rest, false));
            }
        }
        var items = new List<PickerItem>();
        foreach (var (key, title, projects, showsCompany) in groups)
        {
            if (items.Count > 0) items.Add(new PickerItem($"trenner-{key}", PickerItemKind.Separator, "", "", false, false, "", null));
            if (title is not null) items.Add(new PickerItem($"titel-{key}", PickerItemKind.Header, title, "", false, false, title, null));
            foreach (var project in projects)
            {
                var company = showsCompany ? Model.CompanyName(project) ?? "" : "";
                var spoken = company.Length > 0 ? $"{project.Name}, {company}" : project.Name;
                items.Add(new PickerItem($"{key}-{project.Id}", PickerItemKind.Option, project.Name, company,
                                         project.Id == Model.Draft.ProjectId, project.Id == highlightedProjectId, spoken, project));
            }
        }
        return items;
    }

    private void ProjectButton_Click(object sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(ProjectButton);

    private void ProjectFlyout_Opened(object? sender, object e)
    {
        pickerQuery = "";
        highlightedProjectId = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ProjectSearch.Focus(FocusState.Programmatic);
    }

    private void ProjectFlyout_Closed(object? sender, object e) => pickerQuery = "";

    private void PickProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PickerItem { Project: { } project }) Pick(project);
    }

    private void Pick(Project project)
    {
        Model.SelectProject(project);
        ProjectFlyout.Hide();
        DescriptionBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Tippen sucht, ↑ ↓ wählen, Eingabe übernimmt, Esc schließt.</summary>
    private void ProjectSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var navigable = PickerItems.Where(item => item.Kind == PickerItemKind.Option).Select(item => item.Project!).ToList();
        switch (e.Key)
        {
            case VirtualKey.Down or VirtualKey.Up when navigable.Count > 0:
                var step = e.Key == VirtualKey.Down ? 1 : -1;
                var index = navigable.FindIndex(project => project.Id == highlightedProjectId);
                index = index < 0 ? (step > 0 ? 0 : navigable.Count - 1) : Math.Clamp(index + step, 0, navigable.Count - 1);
                highlightedProjectId = navigable[index].Id;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PickerItems)));
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                var chosen = navigable.FirstOrDefault(project => project.Id == highlightedProjectId) ?? navigable.FirstOrDefault();
                if (chosen is not null) Pick(chosen);
                e.Handled = true;
                break;
        }
    }

    // Fuß

    private async void OpenOdoo_Click(object sender, RoutedEventArgs e)
    {
        if (Model.OdooWebUrl is { } url) await Launcher.LaunchUriAsync(new Uri(url));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    // Löschen und Verwerfen

    /// <summary>Rückfrage im Fenster selbst. Eingabe bricht ab, wie bei Rückfragen, die etwas Unwiderrufliches tun.</summary>
    private async Task ConfirmDeletionAsync(Deletion deletion)
    {
        shownDeletion = deletion;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Model.DeletionTitle,
            Content = new TextBlock { Text = Model.DeletionMessage, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Model.DeletionConfirmTitle,
            CloseButtonText = Loc.T("Abbrechen"),
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        if (Model.PendingDeletion != deletion) return;
        if (result == ContentDialogResult.Primary) await Model.ConfirmDeletionAsync();
        else Model.CancelDeletion();
    }

    // Hilfen

    private static FrameworkElement? FindChild(DependencyObject parent, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindChild(child, name) is { } found) return found;
        }
        return null;
    }
}
