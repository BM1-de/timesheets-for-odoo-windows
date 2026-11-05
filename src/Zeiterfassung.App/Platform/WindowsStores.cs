// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Zeiterfassung.Model;
using Zeiterfassung.Support;
using static Zeiterfassung.App.Platform.NativeMethods;

namespace Zeiterfassung.App.Platform;

/// <summary>Läuft die App als MSIX-Paket (installiert) oder als lose Programmdatei (Entwicklung, Bildschirmfotos)?</summary>
public static class PackageInfo
{
    public static readonly bool IsPackaged = Detect();

    private static bool Detect()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>Version des Pakets; ohne Paket die der Programmdatei.</summary>
    public static string Version
    {
        get
        {
            if (IsPackaged)
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            var assembly = typeof(PackageInfo).Assembly.GetName().Version;
            return assembly is null ? "0.0.0" : $"{assembly.Major}.{assembly.Minor}.{assembly.Build}";
        }
    }

    /// <summary>Aus dem Microsoft Store installiert: Microsoft hat das Paket signiert.</summary>
    public static bool IsStore
    {
        get
        {
            if (!IsPackaged) return false;
            try
            {
                return Windows.ApplicationModel.Package.Current.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// Einstellungen der App: im Paket in den lokalen App-Einstellungen von Windows, ohne Paket in einer Datei unter
/// %LOCALAPPDATA%\Zeiterfassung.
/// </summary>
public sealed class WindowsSettingsStore : ISettingsStore
{
    private readonly Windows.Storage.ApplicationDataContainer? container;
    private readonly string? file;
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public WindowsSettingsStore()
    {
        if (PackageInfo.IsPackaged)
        {
            container = Windows.Storage.ApplicationData.Current.LocalSettings;
            return;
        }
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zeiterfassung");
        Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "settings.json");
        if (File.Exists(file))
        {
            try
            {
                values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? values;
            }
            catch (JsonException)
            {
            }
        }
    }

    public string? Get(string key)
    {
        if (container is not null) return container.Values.TryGetValue(key, out var value) ? value as string : null;
        return values.GetValueOrDefault(key);
    }

    public void Set(string key, string? value)
    {
        if (container is not null)
        {
            if (value is null) container.Values.Remove(key);
            else container.Values[key] = value;
            return;
        }
        if (value is null) values.Remove(key);
        else values[key] = value;
        File.WriteAllText(file!, JsonSerializer.Serialize(values));
    }
}

/// <summary>
/// Der API-Schlüssel liegt in der Windows-Anmeldeinformationsverwaltung (Systemsteuerung › Anmeldeinformationsverwaltung ›
/// Windows-Anmeldeinformationen), nie in den Einstellungen.
/// </summary>
public sealed class CredentialSecretStore : ISecretStore
{
    private const string Prefix = "ZeiterfassungFuerOdoo:";

    public string? Read(string account)
    {
        if (!CredReadW(Prefix + account, CRED_TYPE_GENERIC, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIALW>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Save(string secret, string account, string label)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Prefix + account,
                Comment = label,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = label,
            };
            if (!CredWriteW(ref credential, 0))
            {
                var code = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    Loc.T("Der API-Schlüssel konnte nicht in der Windows-Anmeldeinformationsverwaltung gespeichert werden (Fehler %lld).", code));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string account) => CredDeleteW(Prefix + account, CRED_TYPE_GENERIC, 0);
}

public enum AutostartState
{
    Enabled,
    Disabled,
    /// <summary>In den Windows-Einstellungen unter Apps › Autostart ausgeschaltet; nur dort wieder einzuschalten.</summary>
    DisabledByUser,
    /// <summary>Von der Organisation vorgegeben.</summary>
    ByPolicy,
    /// <summary>Weder Startaufgabe noch Eintrag in der Registrierung.</summary>
    Unavailable,
}

/// <summary>
/// „Mit Windows starten“: im Paket über die Startaufgabe von Windows, ohne Paket über den Run-Schlüssel der
/// Registrierung. Beide Wege liegen hinter derselben Schnittstelle, die Oberfläche unterscheidet sie nicht.
/// </summary>
public static class Autostart
{
    private const string TaskId = "ZeiterfassungAutostart";

    /// <summary>
    /// Hängt am Eintrag in der Registrierung. Daran erkennt die App, dass Windows sie beim Anmelden gestartet hat:
    /// Ohne Paket gibt es keine Startaufgabe, deren Aktivierungsart das verriete.
    /// </summary>
    public const string LoginArgument = "--autostart";

    public static async Task<AutostartState> StateAsync()
    {
        if (!PackageInfo.IsPackaged) return RunKey.State();
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        return Map(task.State);
    }

    /// <summary>Einschalten geht nur, solange der Benutzer es nicht in Windows ausgeschaltet hat.</summary>
    public static async Task<AutostartState> SetEnabledAsync(bool enabled)
    {
        if (!PackageInfo.IsPackaged) return RunKey.SetEnabled(enabled);
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        if (enabled) return Map(await task.RequestEnableAsync());
        task.Disable();
        return Map(task.State);
    }

    /// <summary>
    /// Ohne Paket zeigt der Eintrag auf die Programmdatei. Verschiebt der Benutzer den Ordner der entpackten
    /// Fassung, zeigt er ins Leere; dann wird er beim Start auf den neuen Pfad gesetzt.
    /// </summary>
    public static void RepairIfMoved()
    {
        if (!PackageInfo.IsPackaged) RunKey.RepairIfMoved();
    }

    private static AutostartState Map(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled => AutostartState.Enabled,
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => AutostartState.DisabledByUser,
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy
            => AutostartState.ByPolicy,
        _ => AutostartState.Disabled,
    };

    /// <summary>Öffnet Einstellungen › Apps › Autostart.</summary>
    public static Task OpenSettingsAsync()
    {
        const string uri = "ms-settings:startupapps";
        if (PackageInfo.IsPackaged) return Windows.System.Launcher.LaunchUriAsync(new Uri(uri)).AsTask();
        // Ohne Paketidentität bleibt der Weg über die Shell, die die Adresse an die Einstellungen weitergibt.
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write(error, "Autostart-Einstellungen");
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Der Eintrag unter <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>: Windows startet die Programmdatei
/// beim Anmelden. Ausschalten kann der Benutzer das auch in den Windows-Einstellungen unter Apps › Autostart; Windows
/// merkt sich das daneben unter <c>Explorer\StartupApproved\Run</c> und lässt den Run-Eintrag dabei stehen.
/// </summary>
internal static class RunKey
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ZeiterfassungFuerOdoo";

    /// <summary>Der Aufruf, den Windows beim Anmelden startet.</summary>
    private static string? Command =>
        Environment.ProcessPath is { Length: > 0 } path ? $"\"{path}\" {Autostart.LoginArgument}" : null;

    public static AutostartState State()
    {
        try
        {
            if (Command is null) return AutostartState.Unavailable;
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunPath);
            if (run?.GetValue(ValueName) is not string) return AutostartState.Disabled;
            return IsApproved() ? AutostartState.Enabled : AutostartState.DisabledByUser;
        }
        catch (Exception error) when (IsRegistryError(error))
        {
            return AutostartState.Unavailable;
        }
    }

    public static AutostartState SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
                // Der Vermerk von Windows gilt nur für vorhandene Einträge; bliebe er stehen, wäre ein späteres
                // Einschalten in der App sofort wieder „in Windows ausgeschaltet“.
                using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                return AutostartState.Disabled;
            }
            if (Command is not { } command) return AutostartState.Unavailable;
            if (!IsApproved()) return AutostartState.DisabledByUser;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
            if (key is null) return AutostartState.Unavailable;
            key.SetValue(ValueName, command, Microsoft.Win32.RegistryValueKind.String);
            return AutostartState.Enabled;
        }
        catch (Exception error) when (IsRegistryError(error))
        {
            ErrorLog.Write(error, "Autostart");
            return AutostartState.Unavailable;
        }
    }

    /// <summary>Steht im Eintrag ein anderer Pfad, wird er auf die laufende Programmdatei gesetzt.</summary>
    public static void RepairIfMoved()
    {
        try
        {
            if (Command is not { } command) return;
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            if (run?.GetValue(ValueName) is not string current || current == command) return;
            run.SetValue(ValueName, command, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception error) when (IsRegistryError(error))
        {
            ErrorLog.Write(error, "Autostart");
        }
    }

    /// <summary>
    /// Windows legt hier für jeden Autostart-Eintrag zwölf Bytes ab; im ersten steht, ob er eingeschaltet ist:
    /// gerade Werte (2, 6) ja, ungerade (3, 7) nein. Microsoft beschreibt das Format nicht. Fehlt der Wert, gilt der
    /// Eintrag als eingeschaltet — so sieht es auch nach einem frisch geschriebenen Run-Eintrag aus.
    /// </summary>
    private static bool IsApproved()
    {
        using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedPath);
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || flags[0] % 2 == 0;
    }

    private static bool IsRegistryError(Exception error) =>
        error is System.Security.SecurityException or UnauthorizedAccessException or IOException;
}
