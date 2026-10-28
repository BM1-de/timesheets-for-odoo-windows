// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Zeiterfassung.App.Platform;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.App.Views;

public sealed partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private bool showsFeatureWarning;
    private AutostartState autostart = AutostartState.Unavailable;
    private bool updatingControls;

    public SettingsWindow()
    {
        Model = App.Current.Model;
        Updates = App.Current.Updates;
        InitializeComponent();
        Title = WindowTitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        // Mittig auf dem Bildschirm, auf dem geklickt wurde, und nie größer als dessen Arbeitsfläche.
        var scale = Native.ScaleFor(this);
        NativeMethods.GetCursorPos(out var cursor);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        var size = new SizeInt32(Math.Min((int)(620 * scale), work.Width), Math.Min((int)(780 * scale), work.Height));
        AppWindow.Resize(size);
        AppWindow.Move(new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        RoundingBox.ItemsSource = new[] { DurationRounding.Off, DurationRounding.QuarterHour, DurationRounding.HalfHour, DurationRounding.Hour }
            .Select(rounding => new ComboBoxItem { Content = rounding.Title(), Tag = rounding }).ToList();

        Model.PropertyChanged += OnModelChanged;
        Updates.Changed += OnUpdatesChanged;
        Activated += async (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) await RefreshAutostartAsync();
        };
        Closed += (_, _) =>
        {
            Model.PropertyChanged -= OnModelChanged;
            Updates.Changed -= OnUpdatesChanged;
        };
        UpdateControls();
        _ = RefreshAutostartAsync();
    }

    public AppModel Model { get; }

    public Updates Updates { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnUpdatesChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        UpdateControls();
    }

    /// <summary>Auswahlfelder, die nicht über x:Bind laufen.</summary>
    private void UpdateControls()
    {
        updatingControls = true;
        try
        {
            foreach (var item in RoundingBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is DurationRounding rounding && rounding == Model.Preferences.DurationRounding) RoundingBox.SelectedItem = item;
            }
            var companies = Model.AttendanceCompanies;
            var current = AttendanceCompanyBox.Items.OfType<ComboBoxItem>().Select(item => item.Tag).OfType<int>();
            if (!current.SequenceEqual(companies.Select(company => company.Id)))
            {
                AttendanceCompanyBox.ItemsSource = companies.Select(company => new ComboBoxItem { Content = company.Name, Tag = company.Id }).ToList();
            }
            foreach (var item in AttendanceCompanyBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is int id && id == Model.AttendanceCompanyId) AttendanceCompanyBox.SelectedItem = item;
            }
        }
        finally
        {
            updatingControls = false;
        }
    }

    public string WindowTitle => Loc.T("%@ – Einstellungen", AppInfo.Name);

    // Funktionen

    public FeatureText TimesheetsStatus => FeatureText.Timesheets(Model.Capabilities);

    public FeatureText AttendanceStatus => FeatureText.Attendance(Model.Capabilities);

    public bool TimesheetsAvailable => Model.Capabilities?.Timesheets == FeatureStatus.Available;

    public bool AttendanceAvailable => Model.Capabilities?.Attendance == FeatureStatus.Available;

    public bool TimesheetsOn => Model.TimesheetsActive;

    public bool AttendanceOn => Model.AttendanceActive;

    public bool ShowsAttendanceCompany => Model.Organization is not null && Model.AttendanceActive && Model.AttendanceCompanies.Count > 1;

    public string FeatureHint => showsFeatureWarning
        ? Loc.T("Mindestens eine Funktion muss eingeschaltet bleiben.")
        : Loc.T("Beim Verbinden prüft die App, welche Odoo-Apps installiert sind. Was fehlt, bleibt ausgeschaltet.");

    public Brush FeatureHintBrush => showsFeatureWarning ? Brushes.Critical : Brushes.Secondary;

    public string RecheckTitle => Model.IsCheckingCapabilities ? Loc.T("Prüfe …") : Loc.T("Erneut prüfen");

    public bool CanRecheck => Model.Session is not null && !Model.IsCheckingCapabilities;

    private void Timesheets_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.IsOn == Model.TimesheetsActive) return;
        showsFeatureWarning = !Model.SetTimesheetsEnabled(toggle.IsOn);
        if (showsFeatureWarning) toggle.IsOn = Model.TimesheetsActive;
        Refresh();
    }

    private void Attendance_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.IsOn == Model.AttendanceActive) return;
        showsFeatureWarning = !Model.SetAttendanceEnabled(toggle.IsOn);
        if (showsFeatureWarning) toggle.IsOn = Model.AttendanceActive;
        Refresh();
    }

    private async void AttendanceCompany_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || AttendanceCompanyBox.SelectedItem is not ComboBoxItem { Tag: int companyId }) return;
        await Model.SetAttendanceCompanyAsync(companyId);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        showsFeatureWarning = false;
        await Model.RefreshCapabilitiesAsync(recheck: true);
        await Model.RefreshAsync(force: true);
    }

    // Infobereich

    public bool DotOn => Model.Preferences.ShowsRunningDot && Model.TimesheetsActive;

    public Brush DotLabelBrush => Model.TimesheetsActive ? Brushes.Primary : Brushes.Secondary;

    private void Dot_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && Model.TimesheetsActive) Model.Preferences.ShowsRunningDot = toggle.IsOn;
    }

    // Konto

    public bool IsSignedIn => Model.Session is not null;

    public string CompaniesText => Model.Organization is { } organization
        ? string.Join(", ", organization.Companies.Select((company, index) => index == 0 ? Loc.T("%@ (Hauptfirma)", company.Name) : company.Name))
        : "";

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var message = Model.RunningTimer is not null
            ? Loc.T("Der API-Schlüssel wird aus der Windows-Anmeldeinformationsverwaltung gelöscht. Deine Einträge in Odoo bleiben erhalten, die laufende Projektzeit wird nicht gebucht.")
            : Loc.T("Der API-Schlüssel wird aus der Windows-Anmeldeinformationsverwaltung gelöscht. Deine Einträge in Odoo bleiben erhalten.");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Loc.T("Von Odoo abmelden?"),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("Abmelden"),
            CloseButtonText = Loc.T("Abbrechen"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Model.SignOut();
    }

    // Allgemein

    public bool AutostartOn => autostart is AutostartState.Enabled;

    public bool AutostartBlocked => autostart is AutostartState.DisabledByUser or AutostartState.ByPolicy;

    public bool AutostartChangeable => autostart is AutostartState.Enabled or AutostartState.Disabled;

    public Brush AutostartLabelBrush => AutostartChangeable ? Brushes.Primary : Brushes.Secondary;

    public string AutostartBlockedText => autostart == AutostartState.ByPolicy
        ? Loc.T("Deine Organisation legt fest, ob die App mit Windows startet.")
        : Loc.T("In den Windows-Einstellungen unter Apps › Autostart ausgeschaltet. Nur dort lässt es sich wieder einschalten.");

    private async Task RefreshAutostartAsync()
    {
        try
        {
            autostart = await Autostart.StateAsync();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            autostart = AutostartState.Unavailable;
        }
        Refresh();
    }

    private async void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.IsOn == AutostartOn || !AutostartChangeable) return;
        autostart = await Autostart.SetEnabledAsync(toggle.IsOn);
        Refresh();
    }

    private async void OpenAutostartSettings_Click(object sender, RoutedEventArgs e) => await Autostart.OpenSettingsAsync();

    public string RoundingExplanation =>
        Loc.T("Rundet die eingetragene Dauer zur nächsten Einheit, bei 15 Minuten zum Beispiel 1:20 auf 1:15 und 0:05 auf 0:15.");

    private void Rounding_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || RoundingBox.SelectedItem is not ComboBoxItem { Tag: DurationRounding rounding }) return;
        Model.Preferences.DurationRounding = rounding;
    }

    // Updates

    public bool ShowsUpdates => Updates.IsSupported;

    public string CheckTitle => Updates.State == UpdateState.Checking ? Loc.T("Suche …") : Loc.T("Jetzt suchen");

    public bool CanCheck => Updates.State is not (UpdateState.Checking or UpdateState.Installing);

    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) Model.Preferences.ChecksForUpdates = toggle.IsOn;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckAsync(manual: true);

    // Über

    public string AboutTitle => Loc.T("Über %@", AppInfo.Name);

    public string VersionText => AppInfo.IsStore ? Loc.T("%@ aus dem Microsoft Store", AppInfo.Version) : AppInfo.Version;

    public string AboutFooter => (AppInfo.IsStore ? Loc.T("Updates kommen über den Microsoft Store.") + " " : "") + AppInfo.LegalNotice;
}
