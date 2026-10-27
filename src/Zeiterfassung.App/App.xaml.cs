// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Zeiterfassung.App.Platform;
using Zeiterfassung.App.Views;
using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.App;

/// <summary>
/// Die App lebt im Infobereich der Taskleiste: Ein Klick auf die Uhr öffnet das Fenster darüber, ein Rechtsklick das
/// Menü. Ein normales Hauptfenster gibt es nicht; die Einstellungen öffnen sich bei Bedarf.
/// </summary>
public sealed partial class App : Application
{
    private readonly string[] arguments;
    private DispatcherQueue? dispatcher;
    private TrayIcon? tray;
    private FlyoutWindow? flyout;
    private SettingsWindow? settings;
    private DispatcherQueueTimer? hourly;

    public App(string[] arguments)
    {
        this.arguments = arguments;
        ConfigureLanguage();
        AppInfo.Version = PackageInfo.Version;
        AppInfo.IsStore = PackageInfo.IsStore;
        InitializeComponent();
        // Die App lebt im Infobereich: Schließt man das letzte Fenster, läuft sie weiter, bis „Beenden“ sie beendet.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => ErrorLog.Write(e.Exception, e.Message);
        // Fehlt eine Ressource im XAML, meldet WinUI beim Laden nur „XAML parsing failed“; das Ereignis nennt den Namen.
        DebugSettings.IsXamlResourceReferenceTracingEnabled = ScreenshotMode.IsRequested(arguments);
        DebugSettings.XamlResourceReferenceFailed += (_, e) => ErrorLog.Write(e.Message, "XAML-Ressource");
    }

    public static new App Current => (App)Application.Current;

    public AppModel Model { get; private set; } = null!;

    public Updates Updates { get; private set; } = null!;

    /// <summary>Sprache der App aus der Liste der Windows-Sprachen, Datums- und Zeitformate aus der Region.</summary>
    private static void ConfigureLanguage()
    {
        IEnumerable<string> preferred;
        try
        {
            preferred = Windows.System.UserProfile.GlobalizationPreferences.Languages;
        }
        catch (Exception)
        {
            preferred = [CultureInfo.CurrentUICulture.Name];
        }
        Loc.Language = Loc.Choose(preferred);
        Formatting.Culture = Formatting.MakeCulture(Loc.Language, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Loc.Language);
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Loc.Language switch
            {
                "en" => "en-US",
                "fr" => "fr-FR",
                _ => "de-DE",
            };
        }
        catch (Exception)
        {
            // Ohne Paket gibt es keine Sprachauswahl für die eingebauten Texte von WinUI.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        if (ScreenshotMode.IsRequested(arguments))
        {
            await ScreenshotMode.RunAsync(arguments, model => Model = model, updates => Updates = updates);
            Exit();
            return;
        }

        var preferences = new Preferences(new WindowsSettingsStore());
        var http = new DemoRoutingHttpSender(new NetHttpSender(AppInfo.UserAgent));
        Model = new AppModel(preferences, http, new CredentialSecretStore())
        {
            Post = action => dispatcher.TryEnqueue(() => action()),
        };
        Updates = new Updates(preferences);

        tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tray"));
        tray.Selected += ToggleFlyout;
        tray.MenuItems = MenuItems;
        Model.PropertyChanged += (_, _) => UpdateTray();
        Updates.Changed += () => dispatcher.TryEnqueue(UpdateTray);
        UpdateTray();

        flyout = new FlyoutWindow();
        flyout.SettingsRequested += OpenSettings;

        AppInstance.GetCurrent().Activated += (_, activation) => dispatcher.TryEnqueue(() => OnActivated(activation));

        await Model.StartAsync();
        await ApplyAutostartDefaultAsync();

        // Aus dem Autostart beim Anmelden bei Windows bleibt das Fenster zu; sonst geht es auf, auch zur Anmeldung.
        var launch = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (launch.Kind != ExtendedActivationKind.StartupTask || Model.Screen == Screen.Login) ShowFlyout();

        hourly = dispatcher.CreateTimer();
        hourly.Interval = TimeSpan.FromHours(1);
        hourly.Tick += async (_, _) => await Updates.CheckIfDueAsync();
        hourly.Start();
        await Updates.CheckIfDueAsync();
    }

    /// <summary>Ein zweiter Start, etwa aus dem Startmenü, öffnet das Fenster der laufenden App.</summary>
    private void OnActivated(AppActivationArguments activation)
    {
        if (activation.Kind == ExtendedActivationKind.StartupTask) return;
        ShowFlyout();
    }

    /// <summary>
    /// „Mit Windows starten“ ist bei der Download-Fassung nach dem ersten Start eingeschaltet. Danach entscheidet nur der
    /// Benutzer. Aus dem Microsoft Store bleibt es aus, bis man es in den Einstellungen einschaltet.
    /// </summary>
    private async Task ApplyAutostartDefaultAsync()
    {
        if (AppInfo.IsStore || !PackageInfo.IsPackaged || Model.Preferences.AutostartDefaultApplied) return;
        Model.Preferences.AutostartDefaultApplied = true;
        try
        {
            await Autostart.SetEnabledAsync(true);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Autostart: {error.Message}");
        }
    }

    // Infobereich

    private void UpdateTray()
    {
        if (tray is null) return;
        var content = Model.TrayContent;
        var state = !content.IsSignedIn ? TrayState.SignedOut : content.ShowsDot ? TrayState.Running : TrayState.Normal;
        tray.Update(state, content.ToolTip);
    }

    private void ToggleFlyout()
    {
        if (flyout is null) return;
        if (flyout.IsShown) flyout.HideFlyout();
        else if (!flyout.ClosedJustNow) ShowFlyout();
    }

    private void ShowFlyout() => flyout?.ShowNear(tray);

    /// <summary>Rechtsklick auf die Uhr.</summary>
    private IReadOnlyList<TrayMenuItem> MenuItems()
    {
        flyout?.HideFlyout();
        var items = new List<TrayMenuItem>();
        if (Model.TimesheetsActive && Model.RunningTimer is { End: null })
        {
            var elapsed = DurationInput.Format(Model.TimerSeconds() / 60);
            items.Add(new TrayMenuItem($"{Loc.T("Stoppen und buchen")}\t{elapsed}", () => _ = StopFromMenuAsync()));
            items.Add(TrayMenuItem.Separator);
        }
        items.Add(new TrayMenuItem(Loc.T("Zeiterfassung öffnen"), ShowFlyout, IsDefault: true));
        items.Add(new TrayMenuItem(Loc.T("Aktualisieren"), () => _ = Model.RefreshAsync(force: true), IsEnabled: Model.Screen == Screen.Main));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Loc.T("Einstellungen …"), OpenSettings));
        if (Updates.IsSupported)
        {
            items.Add(new TrayMenuItem(Loc.T("Nach Updates suchen …"), () =>
            {
                ShowFlyout();
                _ = Updates.CheckAsync(manual: true);
            }));
        }
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Loc.T("Beenden"), Quit));
        return items;
    }

    /// <summary>Stoppt aus dem Menü; braucht die Zeit noch Angaben oder scheitert das Buchen, geht das Fenster auf.</summary>
    private async Task StopFromMenuAsync()
    {
        await Model.StopTimerAsync();
        if (Model.TimerEditing is not null || Model.TimerError is not null || Model.RunningTimer is not null) ShowFlyout();
    }

    // Fenster

    private void OpenSettings()
    {
        flyout?.HideFlyout();
        if (settings is null)
        {
            settings = new SettingsWindow();
            settings.Closed += (_, _) => settings = null;
        }
        settings.Activate();
    }

    private void Quit()
    {
        hourly?.Stop();
        Model.Stop();
        tray?.Dispose();
        tray = null;
        settings?.Close();
        flyout?.Close();
        Exit();
    }
}
