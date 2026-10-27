// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace Zeiterfassung.Support;

public enum DurationKind
{
    Empty,
    Minutes,
    Unreadable,
    TooLong,
}

/// <summary>Ergebnis beim Lesen des Felds „Dauer“.</summary>
public readonly record struct DurationResult(DurationKind Kind, int Minutes = 0)
{
    public static readonly DurationResult Empty = new(DurationKind.Empty);
    public static readonly DurationResult Unreadable = new(DurationKind.Unreadable);
    public static readonly DurationResult TooLong = new(DurationKind.TooLong);
}

/// <summary>
/// Liest und schreibt Dauern im Feld „Dauer“.
/// Erlaubt sind <c>1:30</c>, <c>1,5</c>, <c>1.5</c>, <c>90 min</c>, <c>90m</c>, <c>2h</c>, <c>1h30</c> und
/// <c>1 h 30 min</c>, die Einheiten auch englisch und französisch (<c>2 hours</c>, <c>1 heure 30</c>).
/// Eine Zahl ohne Einheit sind Stunden, wie in Odoo selbst.
/// </summary>
public static partial class DurationInput
{
    public const int MaximumMinutes = 24 * 60;

    /// <summary>Obergrenze beim Umrechnen: Sehr lange Zahlen würden sonst überlaufen.</summary>
    private const int Cap = MaximumMinutes + 1;

    public static DurationResult Parse(string text)
    {
        var compact = text.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (compact.Length == 0) return DurationResult.Empty;
        var minutes = MinutesIn(compact);
        if (minutes is null) return DurationResult.Unreadable;
        return minutes > MaximumMinutes ? DurationResult.TooLong : new DurationResult(DurationKind.Minutes, minutes.Value);
    }

    /// <summary>Rundet zur nächsten Einheit von <paramref name="unit"/> Minuten, aber nie auf null.</summary>
    public static int Rounded(int minutes, int unit)
    {
        if (minutes <= 0) return 0;
        if (unit <= 1) return minutes;
        return Math.Max(unit, (int)Math.Round((double)minutes / unit, MidpointRounding.AwayFromZero) * unit);
    }

    /// <summary>−/+: von <paramref name="minutes"/> zur nächsten Stufe in diese Richtung, nie unter null.</summary>
    public static int Stepped(int minutes, int direction, int unit)
    {
        var steps = direction > 0 ? minutes / unit + 1 : (minutes + unit - 1) / unit - 1;
        return Math.Max(0, steps * unit);
    }

    /// <summary><c>1:05</c></summary>
    public static string Format(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}:{minutes % 60:D2}");

    /// <summary><c>1:05:09</c> für die laufende Projektzeit</summary>
    public static string FormatSeconds(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:D2}:{seconds % 60:D2}");

    /// <summary>„1 Stunde 5 Minuten“ für die Sprachausgabe.</summary>
    public static string Spoken(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add(Loc.T("%lld Stunden", hours));
        if (rest > 0 || hours == 0) parts.Add(Loc.T("%lld Minuten", rest));
        return string.Join(" ", parts);
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{1,2})$")]
    private static partial Regex ClockPattern();

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)$")]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^(?:(\d+(?:[.,]\d+)?)(?:h|std|stunden?|hrs?|hours?|heures?))?(?:(\d+)(?:m|mn|mins?|minuten?|minutes?)?)?$")]
    private static partial Regex UnitPattern();

    private static int? MinutesIn(string text)
    {
        var clock = ClockPattern().Match(text);
        if (clock.Success)
        {
            var hours = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            return minutes < 60 ? hours * 60 + minutes : null;
        }
        var number = DecimalPattern().Match(text);
        if (number.Success)
        {
            return DecimalHours(number.Groups[1].Value) is { } value ? MinutesFromHours(value) : null;
        }
        var units = UnitPattern().Match(text);
        if (units.Success && (units.Groups[1].Success || units.Groups[2].Success))
        {
            // „1h30“ ohne Einheit hinter der 30 sind Minuten; „30“ allein wurde oben als Stunden gelesen.
            var hours = units.Groups[1].Success ? DecimalHours(units.Groups[1].Value) ?? 0 : 0;
            var extra = units.Groups[2].Success
                ? int.TryParse(units.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : Cap
                : 0;
            return Math.Min(MinutesFromHours(hours) + Math.Min(extra, Cap), Cap);
        }
        return null;
    }

    private static double? DecimalHours(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int MinutesFromHours(double hours)
    {
        if (!double.IsFinite(hours)) return Cap;
        return (int)Math.Round(Math.Min(hours * 60, Cap), MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Liest Uhrzeiten in den Feldern „von“ und „bis“: <c>8:30</c>, <c>08.30</c>, <c>8h30</c>, <c>830</c>, <c>8</c>,
/// auch mit <c>AM</c> und <c>PM</c>, so wie die App sie in Regionen mit 12-Stunden-Uhr vorausfüllt.
/// </summary>
public static partial class ClockInput
{
    [GeneratedRegex(@"^(\d{1,2})[:.h](\d{2})$")]
    private static partial Regex Separated();

    [GeneratedRegex(@"^(\d{1,2})(\d{2})$")]
    private static partial Regex Compact();

    [GeneratedRegex(@"^(\d{1,2})h?$")]
    private static partial Regex HourOnly();

    /// <summary>Minuten seit Mitternacht; <c>null</c>, wenn sich keine Uhrzeit lesen lässt.</summary>
    public static int? Minutes(string text)
    {
        var compact = new string(text.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
        char? meridiem = null;
        foreach (var (suffix, mark) in new[] { ("a.m.", 'a'), ("p.m.", 'p'), ("am", 'a'), ("pm", 'p') })
        {
            if (!compact.EndsWith(suffix, StringComparison.Ordinal)) continue;
            compact = compact[..^suffix.Length];
            meridiem = mark;
            break;
        }
        int hour;
        int minute;
        Match match;
        if ((match = Separated().Match(compact)).Success || (match = Compact().Match(compact)).Success)
        {
            hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if ((match = HourOnly().Match(compact)).Success)
        {
            hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            minute = 0;
        }
        else
        {
            return null;
        }
        if (minute is < 0 or >= 60) return null;
        switch (meridiem)
        {
            case 'a':
                if (hour is < 1 or > 12) return null;
                return hour % 12 * 60 + minute;
            case 'p':
                if (hour is < 1 or > 12) return null;
                return (hour % 12 + 12) * 60 + minute;
            default:
                if (hour is < 0 or >= 24) return null;
                return hour * 60 + minute;
        }
    }
}
