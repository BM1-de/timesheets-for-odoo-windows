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
    /// <summary>Ohne Paket gibt es keine Startaufgabe.</summary>
    Unavailable,
}

/// <summary>„Mit Windows starten“ über die Startaufgabe des Pakets.</summary>
public static class Autostart
{
    private const string TaskId = "ZeiterfassungAutostart";

    public static async Task<AutostartState> StateAsync()
    {
        if (!PackageInfo.IsPackaged) return AutostartState.Unavailable;
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        return Map(task.State);
    }

    /// <summary>Einschalten geht nur, solange der Benutzer es nicht in Windows ausgeschaltet hat.</summary>
    public static async Task<AutostartState> SetEnabledAsync(bool enabled)
    {
        if (!PackageInfo.IsPackaged) return AutostartState.Unavailable;
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        if (enabled) return Map(await task.RequestEnableAsync());
        task.Disable();
        return Map(task.State);
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
    public static Task OpenSettingsAsync() =>
        Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")).AsTask();
}
