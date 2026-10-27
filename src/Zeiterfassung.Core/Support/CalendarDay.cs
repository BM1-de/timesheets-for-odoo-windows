// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace Zeiterfassung.Support;

/// <summary>Ein Kalendertag ohne Uhrzeit, wie das Feld <c>date</c> eines Zeiteintrags in Odoo.</summary>
public readonly record struct CalendarDay(int Year, int Month, int Day) : IComparable<CalendarDay>
{
    /// <summary>Der Tag, auf den ein Zeitpunkt in dieser Zeitzone fällt.</summary>
    public static CalendarDay From(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return new CalendarDay(local.Year, local.Month, local.Day);
    }

    /// <summary>Aus dem Feld <c>date</c> von Odoo: <c>2026-10-23</c></summary>
    public static CalendarDay? Parse(string text)
    {
        var parts = (text.Length > 10 ? text[..10] : text).Split('-');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return null;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month)) return null;
        return new CalendarDay(year, month, day);
    }

    /// <summary>Beginn des Tages in dieser Zeitzone.</summary>
    public DateTimeOffset Start(TimeZoneInfo zone) => At(0, 0, zone);

    /// <summary>Uhrzeit an diesem Tag in dieser Zeitzone. Fällt sie in die Zeitumstellung, die nächste gültige.</summary>
    public DateTimeOffset At(int hour, int minute, TimeZoneInfo zone)
    {
        var local = new DateTime(Year, Month, Day, 0, 0, 0, DateTimeKind.Unspecified).AddHours(hour).AddMinutes(minute);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public CalendarDay AddingDays(int days)
    {
        var date = ToDateOnly().AddDays(days);
        return new CalendarDay(date.Year, date.Month, date.Day);
    }

    public DateOnly ToDateOnly() => new(Year, Month, Day);

    /// <summary><c>2026-10-23</c></summary>
    public string OdooString => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}-{Day:D2}");

    public override string ToString() => OdooString;

    public int CompareTo(CalendarDay other) => (Year, Month, Day).CompareTo((other.Year, other.Month, other.Day));

    public static bool operator <(CalendarDay left, CalendarDay right) => left.CompareTo(right) < 0;
    public static bool operator >(CalendarDay left, CalendarDay right) => left.CompareTo(right) > 0;
    public static bool operator <=(CalendarDay left, CalendarDay right) => left.CompareTo(right) <= 0;
    public static bool operator >=(CalendarDay left, CalendarDay right) => left.CompareTo(right) >= 0;
}

/// <summary>Zeitpunkte in Odoo: <c>2026-10-23 06:12:00</c>, immer UTC.</summary>
public static class OdooDateTime
{
    public static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string text)
    {
        var parts = new List<int>();
        foreach (var piece in text.Split([' ', '-', ':', 'T'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) parts.Add(number);
        }
        if (parts.Count < 5) return null;
        try
        {
            return new DateTimeOffset(parts[0], parts[1], parts[2], parts[3], parts[4], parts.Count > 5 ? parts[5] : 0,
                                      TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
