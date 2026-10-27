// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.Management.Deployment;
using Zeiterfassung.Model;
using Zeiterfassung.Support;

namespace Zeiterfassung.App;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Installing,
    Installed,
    Failed,
}

/// <summary>
/// Updates der Download-Fassung über die App-Installer-Datei auf GitHub. Windows installiert nur Pakete mit derselben
/// Signatur wie das installierte. Die Fassung aus dem Microsoft Store aktualisiert der Store; dort ist hier alles aus.
/// </summary>
public sealed class Updates
{
    public const string AppInstallerUrl =
        "https://github.com/BM1-de/timesheets-for-odoo-windows/releases/latest/download/Zeiterfassung.appinstaller";

    public const string ReleaseNotesUrl = "https://github.com/BM1-de/timesheets-for-odoo-windows/releases/latest";

    private readonly Preferences preferences;
    private readonly HttpClient http;
    private bool manualCheck;
    private bool bannerDismissed;

    public Updates(Preferences preferences)
    {
        this.preferences = preferences;
        // GitHub leitet „latest/download“ auf den Speicher der Dateien um; hier geht es um keine Zugangsdaten.
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    /// <summary>Nur die Download-Fassung sucht selbst nach Updates.</summary>
    public static bool IsSupported => Platform.PackageInfo.IsPackaged && !Platform.PackageInfo.IsStore;

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public string? AvailableVersion { get; private set; }

    public event Action? Changed;

    /// <summary>Hinweis im Fenster: eine neue Version, und nach „Nach Updates suchen …“ auch das Ergebnis.</summary>
    public bool ShowsBanner => IsSupported && !bannerDismissed && State switch
    {
        UpdateState.Available or UpdateState.Installing or UpdateState.Installed => true,
        UpdateState.Checking or UpdateState.UpToDate or UpdateState.Failed => manualCheck,
        _ => false,
    };

    public string BannerText => State switch
    {
        UpdateState.Checking => Loc.T("Suche nach Updates …"),
        UpdateState.UpToDate => Loc.T("Du hast die neueste Version."),
        UpdateState.Installing => Loc.T("Version %@ wird installiert …", AvailableVersion ?? ""),
        UpdateState.Installed => Loc.T("Version %@ ist installiert.", AvailableVersion ?? ""),
        UpdateState.Failed => Loc.T("Die Suche nach Updates ist gescheitert."),
        _ => Loc.T("Version %@ ist verfügbar.", AvailableVersion ?? ""),
    };

    /// <summary>„Zuletzt gesucht“ in den Einstellungen.</summary>
    public string LastCheckText
    {
        get
        {
            if (manualCheck && State == UpdateState.UpToDate) return Loc.T("Gerade eben: Du hast die neueste Version.");
            if (preferences.LastUpdateCheck is not { } last) return Loc.T("noch nie");
            var local = last.ToLocalTime();
            return local.ToString(Formatting.Culture.DateTimeFormat.ShortDatePattern + " " + Formatting.Culture.DateTimeFormat.ShortTimePattern,
                                  Formatting.Culture);
        }
    }

    public void DismissBanner()
    {
        bannerDismissed = true;
        Changed?.Invoke();
    }

    /// <summary>Einmal am Tag, solange es in den Einstellungen eingeschaltet ist.</summary>
    public async Task CheckIfDueAsync()
    {
        if (!IsSupported || !preferences.ChecksForUpdates) return;
        if (preferences.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(23)) return;
        await CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (!IsSupported || State is UpdateState.Checking or UpdateState.Installing) return;
        manualCheck = manual;
        bannerDismissed = false;
        State = UpdateState.Checking;
        Changed?.Invoke();
        try
        {
            var text = await http.GetStringAsync(AppInstallerUrl);
            var document = XDocument.Parse(text);
            var main = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "MainPackage");
            var offered = Version.TryParse(main?.Attribute("Version")?.Value, out var version) ? version : null;
            var installed = Windows.ApplicationModel.Package.Current.Id.Version;
            var current = new Version(installed.Major, installed.Minor, installed.Build, installed.Revision);
            preferences.LastUpdateCheck = DateTimeOffset.Now;
            if (offered is not null && offered > current)
            {
                AvailableVersion = $"{offered.Major}.{offered.Minor}.{offered.Build}";
                State = UpdateState.Available;
            }
            else
            {
                State = UpdateState.UpToDate;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            State = UpdateState.Failed;
        }
        Changed?.Invoke();
    }

    /// <summary>Installiert über den App Installer; Windows beendet die App dafür und startet sie danach neu.</summary>
    public async Task InstallAsync()
    {
        if (State != UpdateState.Available) return;
        State = UpdateState.Installing;
        Changed?.Invoke();
        try
        {
            RegisterApplicationRestart(null, 0);
            var manager = new PackageManager();
            var result = await manager.AddPackageByAppInstallerFileAsync(new Uri(AppInstallerUrl),
                                                                         AddPackageByAppInstallerOptions.ForceTargetAppShutdown,
                                                                         manager.GetDefaultPackageVolume());
            State = result.IsRegistered ? UpdateState.Installed : UpdateState.Failed;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            State = UpdateState.Failed;
        }
        Changed?.Invoke();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);
}
