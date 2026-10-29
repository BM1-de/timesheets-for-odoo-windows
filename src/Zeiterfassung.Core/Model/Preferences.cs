// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Model;

/// <summary>Ablage für Einstellungen als Text je Schlüssel. In Tests ein Wörterbuch.</summary>
public interface ISettingsStore
{
    string? Get(string key);

    /// <summary><c>null</c> entfernt den Schlüssel.</summary>
    void Set(string key, string? value);
}

/// <summary>Einstellungen im Arbeitsspeicher, für Tests, Bildschirmfotos und Selbsttests.</summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (values) return values.GetValueOrDefault(key);
    }

    public void Set(string key, string? value)
    {
        lock (values)
        {
            if (value is null) values.Remove(key);
            else values[key] = value;
        }
    }
}

/// <summary>Ablage für den API-Schlüssel, unter Windows die Anmeldeinformationsverwaltung.</summary>
public interface ISecretStore
{
    string? Read(string account);

    void Save(string secret, string account, string label);

    void Delete(string account);
}

/// <summary>API-Schlüssel im Arbeitsspeicher statt in der Anmeldeinformationsverwaltung.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (values) return values.Count;
        }
    }

    public string? Read(string account)
    {
        lock (values) return values.GetValueOrDefault(account);
    }

    public void Save(string secret, string account, string label)
    {
        lock (values) values[account] = secret;
    }

    public void Delete(string account)
    {
        lock (values) values.Remove(account);
    }
}

/// <summary>Rundung der Dauer beim Buchen. Der Wert ist die Einheit in Minuten, 0 heißt nicht runden.</summary>
public enum DurationRounding
{
    Off = 0,
    QuarterHour = 15,
    HalfHour = 30,
    Hour = 60,
}

public static class DurationRoundingExtensions
{
    /// <summary>Einheit in Minuten; <c>null</c> ohne Rundung.</summary>
    public static int? Minutes(this DurationRounding rounding) => rounding == DurationRounding.Off ? null : (int)rounding;

    /// <summary>Schritt der Tasten − und +: die Einheit, ohne Rundung eine Viertelstunde.</summary>
    public static int Step(this DurationRounding rounding) => rounding.Minutes() ?? 15;

    public static string LessLabel(this DurationRounding rounding) => rounding.Step() switch
    {
        30 => Loc.T("30 Minuten weniger"),
        60 => Loc.T("1 Stunde weniger"),
        _ => Loc.T("15 Minuten weniger"),
    };

    public static string MoreLabel(this DurationRounding rounding) => rounding.Step() switch
    {
        30 => Loc.T("30 Minuten mehr"),
        60 => Loc.T("1 Stunde mehr"),
        _ => Loc.T("15 Minuten mehr"),
    };

    public static string Title(this DurationRounding rounding) => rounding switch
    {
        DurationRounding.QuarterHour => Loc.T("15 Minuten"),
        DurationRounding.HalfHour => Loc.T("30 Minuten"),
        DurationRounding.Hour => Loc.T("1 Stunde"),
        _ => Loc.T("Nicht runden"),
    };
}

/// <summary>Laufende Projektzeit. Sie steht in den Einstellungen, damit sie einen Neustart der App übersteht.</summary>
/// <param name="Account">Konto, zu dem die Zeit gehört (<see cref="OdooSession.SecretAccount"/>).</param>
/// <param name="ProjectId"><c>null</c>: ohne Projekt gestartet; Projekt und Aufgabe kommen beim Stoppen dazu.</param>
/// <param name="End">Gestoppt, aber noch nicht gebucht, etwa ohne Verbindung zu Odoo. Die Zeit steht dann still, bis
/// das Buchen klappt oder die Zeit verworfen wird.</param>
public sealed record RunningTimer(
    string Account,
    int? ProjectId,
    string? ProjectName,
    int? TaskId,
    string? TaskName,
    string Description,
    DateTimeOffset Start,
    DateTimeOffset? End = null)
{
    /// <summary>„Projekt › Aufgabe“</summary>
    public string Path => string.Join(" › ", new[] { ProjectName, TaskName }.OfType<string>());
}

/// <summary>Angaben der letzten Anmeldung zum Vorausfüllen.</summary>
public sealed record LoginHints(string Server, string? Login, string? Database);

/// <summary>Einstellungen der App. Der API-Schlüssel liegt nicht hier, sondern in der Anmeldeinformationsverwaltung.</summary>
public sealed class Preferences
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISettingsStore store;

    public Preferences(ISettingsStore store)
    {
        this.store = store;
        timesheetsEnabled = ReadBool(Key.Timesheets) ?? true;
        attendanceEnabled = ReadBool(Key.Attendance) ?? true;
        showsRunningDot = ReadBool(Key.RunningDot) ?? true;
        runningTimer = Load<RunningTimer>(Key.RunningTimer);
        durationRounding = ReadInt(Key.DurationRounding) is { } unit && Enum.IsDefined(typeof(DurationRounding), unit)
            ? (DurationRounding)unit
            : DurationRounding.QuarterHour;
        session = Load<OdooSession>(Key.Session);
        cachedCapabilities = Load<Capabilities>(Key.Capabilities);
        loginHints = Load<LoginHints>(Key.LoginHints);
        stampingRefused = ReadBool(Key.StampingRefused) ?? false;
        autostartDefaultApplied = ReadBool(Key.AutostartDefault) ?? false;
        attendanceEmployeeId = ReadInt(Key.AttendanceEmployee);
        checksForUpdates = ReadBool(Key.ChecksForUpdates) ?? true;
        lastUpdateCheck = store.Get(Key.LastUpdateCheck) is { } text
                          && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Meldet jede Änderung, damit die Oberfläche nachzieht.</summary>
    public event Action? Changed;

    private bool timesheetsEnabled;
    private bool attendanceEnabled;
    private bool showsRunningDot;
    private RunningTimer? runningTimer;
    private DurationRounding durationRounding;
    private OdooSession? session;
    private Capabilities? cachedCapabilities;
    private LoginHints? loginHints;
    private bool stampingRefused;
    private bool autostartDefaultApplied;
    private int? attendanceEmployeeId;
    private bool checksForUpdates;
    private DateTimeOffset? lastUpdateCheck;

    public bool TimesheetsEnabled
    {
        get => timesheetsEnabled;
        set => Store(ref timesheetsEnabled, value, Key.Timesheets, value ? "true" : "false");
    }

    public bool AttendanceEnabled
    {
        get => attendanceEnabled;
        set => Store(ref attendanceEnabled, value, Key.Attendance, value ? "true" : "false");
    }

    /// <summary>„Laufende Projektzeit am Symbol zeigen“: grüner Punkt am Symbol im Infobereich.</summary>
    public bool ShowsRunningDot
    {
        get => showsRunningDot;
        set => Store(ref showsRunningDot, value, Key.RunningDot, value ? "true" : "false");
    }

    public RunningTimer? RunningTimer
    {
        get => runningTimer;
        set => Store(ref runningTimer, value, Key.RunningTimer, Serialize(value));
    }

    public DurationRounding DurationRounding
    {
        get => durationRounding;
        set => Store(ref durationRounding, value, Key.DurationRounding, ((int)value).ToString(CultureInfo.InvariantCulture));
    }

    public OdooSession? Session
    {
        get => session;
        set => Store(ref session, value, Key.Session, Serialize(value));
    }

    public Capabilities? CachedCapabilities
    {
        get => cachedCapabilities;
        set => Store(ref cachedCapabilities, value, Key.Capabilities, Serialize(value));
    }

    public LoginHints? LoginHints
    {
        get => loginHints;
        set => Store(ref loginHints, value, Key.LoginHints, Serialize(value));
    }

    /// <summary>
    /// Odoo hat das Stempeln abgelehnt, obwohl das Recht dazu vorhanden schien. Gilt bis zur nächsten
    /// Anmeldung oder bis die Rechte in den Einstellungen erneut geprüft werden.
    /// </summary>
    public bool StampingRefused
    {
        get => stampingRefused;
        set => Store(ref stampingRefused, value, Key.StampingRefused, value ? "true" : "false");
    }

    /// <summary>„Mit Windows starten“ wurde beim ersten Start eingeschaltet.</summary>
    public bool AutostartDefaultApplied
    {
        get => autostartDefaultApplied;
        set => Store(ref autostartDefaultApplied, value, Key.AutostartDefault, value ? "true" : "false");
    }

    /// <summary>Mit Mitarbeitern in mehreren Firmen: der, mit dem die App stempelt. <c>null</c> heißt Hauptfirma.</summary>
    public int? AttendanceEmployeeId
    {
        get => attendanceEmployeeId;
        set => Store(ref attendanceEmployeeId, value, Key.AttendanceEmployee, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Download-Fassung: einmal am Tag nach Updates suchen.</summary>
    public bool ChecksForUpdates
    {
        get => checksForUpdates;
        set => Store(ref checksForUpdates, value, Key.ChecksForUpdates, value ? "true" : "false");
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => lastUpdateCheck;
        set => Store(ref lastUpdateCheck, value, Key.LastUpdateCheck, value?.ToString("O", CultureInfo.InvariantCulture));
    }

    private static class Key
    {
        public const string Timesheets = "feature.timesheets";
        public const string Attendance = "feature.attendance";
        public const string RunningDot = "tray.runningDot";
        public const string RunningTimer = "timer.running";
        public const string DurationRounding = "duration.rounding";
        public const string Session = "odoo.session";
        public const string Capabilities = "odoo.capabilities";
        public const string LoginHints = "odoo.loginHints";
        public const string StampingRefused = "odoo.stampingRefused";
        public const string AutostartDefault = "autostart.defaultApplied";
        public const string AttendanceEmployee = "attendance.employee";
        public const string ChecksForUpdates = "updates.automatic";
        public const string LastUpdateCheck = "updates.lastCheck";
    }

    private void Store<T>(ref T field, T value, string key, string? text)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        store.Set(key, text);
        Changed?.Invoke();
    }

    private bool? ReadBool(string key) => store.Get(key) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private int? ReadInt(string key) =>
        int.TryParse(store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private T? Load<T>(string key) where T : class
    {
        if (store.Get(key) is not { } text) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
