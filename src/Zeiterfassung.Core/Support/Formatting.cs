// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace Zeiterfassung.Support;

/// <summary>Datums- und Zeitangaben in der Sprache der App, mit der Region von Windows.</summary>
public static class Formatting
{
    /// <summary>Kultur für Datum und Uhrzeit; die App setzt sie beim Start.</summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>
    /// Die Sprache kommt von der App, Region und 12- oder 24-Stunden-Uhr von Windows. Spricht Windows
    /// dieselbe Sprache, gelten auch eigene Formate aus den Windows-Einstellungen.
    /// </summary>
    public static CultureInfo MakeCulture(string language, CultureInfo current)
    {
        if (current.TwoLetterISOLanguageName == language) return current;
        var region = current.Name.Contains('-') ? current.Name[(current.Name.LastIndexOf('-') + 1)..] : null;
        if (region is not null)
        {
            try
            {
                // Auch Paare, die es so nicht gibt (Französisch in den USA): ICU setzt sie aus Sprache und Region zusammen.
                return CultureInfo.GetCultureInfo($"{language}-{region}");
            }
            catch (CultureNotFoundException)
            {
            }
        }
        return CultureInfo.GetCultureInfo(language);
    }

    /// <summary>„Freitag, 23. Oktober 2026“; im Französischen groß geschrieben, weil die Zeile allein steht.</summary>
    public static string LongDate(CalendarDay day, CultureInfo? culture = null)
    {
        culture ??= Culture;
        var text = day.ToDateOnly().ToString(culture.DateTimeFormat.LongDatePattern, culture);
        return Capitalized(text, culture);
    }

    /// <summary>
    /// „Fr. 23.10.“ – im Deutschen stehen die Kürzel fest, weil die Systemformate je nach Version mal
    /// „Fr“, mal „Fr.“ liefern. Andere Sprachen nehmen das Format ihrer Region („Fri, Oct 23“).
    /// </summary>
    public static string ShortDate(CalendarDay day, CultureInfo? culture = null)
    {
        culture ??= Culture;
        var date = day.ToDateOnly();
        if (culture.TwoLetterISOLanguageName == "de")
        {
            string[] weekdays = ["So.", "Mo.", "Di.", "Mi.", "Do.", "Fr.", "Sa."];
            return $"{weekdays[(int)date.DayOfWeek]} {day.Day}.{day.Month}.";
        }
        var monthFirst = culture.DateTimeFormat.MonthDayPattern.TrimStart('\'').StartsWith('M');
        return date.ToString(monthFirst ? "ddd, MMM d" : "ddd d MMM", culture);
    }

    /// <summary>„Freitag“ als Überschrift</summary>
    public static string Weekday(CalendarDay day, CultureInfo? culture = null)
    {
        culture ??= Culture;
        return Capitalized(day.ToDateOnly().ToString("dddd", culture), culture);
    }

    /// <summary>„08:12“ oder „8:12 AM“ in der Zeitzone der App.</summary>
    public static string Time(DateTimeOffset instant, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= Culture;
        return TimeZoneInfo.ConvertTime(instant, zone).ToString(culture.DateTimeFormat.ShortTimePattern, culture);
    }

    private static string Capitalized(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}
