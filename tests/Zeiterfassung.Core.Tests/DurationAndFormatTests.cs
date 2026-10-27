// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;
using OdooTime = Zeiterfassung.Support.OdooDateTime;

namespace Zeiterfassung.Tests;

/// <summary>Dauer eingeben</summary>
public sealed class DurationInputTests
{
    [Theory(DisplayName = "liest übliche Schreibweisen")]
    [InlineData("1:30", 90)]
    [InlineData("0:15", 15)]
    [InlineData("1,5", 90)]
    [InlineData("1.5", 90)]
    [InlineData("2", 120)]
    [InlineData("0,25", 15)]
    [InlineData("90m", 90)]
    [InlineData("90 min", 90)]
    [InlineData("45min", 45)]
    [InlineData("2h", 120)]
    [InlineData("1h30", 90)]
    [InlineData("1 h 30 min", 90)]
    [InlineData("1,5h", 90)]
    [InlineData("8 Std", 480)]
    [InlineData(" 1:05 ", 65)]
    [InlineData("2 hours", 120)]
    [InlineData("1 hr 30 min", 90)]
    [InlineData("1.5 hrs", 90)]
    [InlineData("45 mins", 45)]
    [InlineData("90 minutes", 90)]
    [InlineData("2 heures", 120)]
    [InlineData("1 heure 30", 90)]
    [InlineData("30 mn", 30)]
    public void Parses(string text, int minutes) =>
        Assert.Equal(new DurationResult(DurationKind.Minutes, minutes), DurationInput.Parse(text));

    [Fact(DisplayName = "erkennt Leeres, Unlesbares und zu Langes")]
    public void Rejects()
    {
        Assert.Equal(DurationResult.Empty, DurationInput.Parse(""));
        Assert.Equal(DurationResult.Empty, DurationInput.Parse("   "));
        Assert.Equal(DurationResult.Unreadable, DurationInput.Parse("abc"));
        Assert.Equal(DurationResult.Unreadable, DurationInput.Parse("1:75"));
        Assert.Equal(DurationResult.Unreadable, DurationInput.Parse("1;30"));
        Assert.Equal(DurationResult.TooLong, DurationInput.Parse("25"));
        Assert.Equal(DurationResult.TooLong, DurationInput.Parse("24:01"));
        Assert.Equal(new DurationResult(DurationKind.Minutes, 1440), DurationInput.Parse("24:00"));
    }

    /// <summary>400 Neunen lassen sich nicht als Konstante in <c>InlineData</c> schreiben.</summary>
    public static TheoryData<string> VeryLongNumber => new() { new string('9', 400) };

    [Theory(DisplayName = "sehr lange Zahlen sind zu lang, ohne dass die App abstürzt")]
    [InlineData("99999999999999999999")]
    [InlineData("99999999999999999999999999999999999999,5")]
    [InlineData("1h99999999999999999999")]
    [InlineData("99999999999999999999h")]
    [InlineData("9999999999999999999999999 min")]
    [MemberData(nameof(VeryLongNumber))]
    public void HugeNumbers(string text) => Assert.Equal(DurationResult.TooLong, DurationInput.Parse(text));

    [Fact(DisplayName = "rundet zur nächsten Einheit, aber nie auf null")]
    public void Rounds()
    {
        Assert.Equal(0, DurationInput.Rounded(0, 15));
        Assert.Equal(15, DurationInput.Rounded(5, 15));
        Assert.Equal(15, DurationInput.Rounded(7, 15));
        Assert.Equal(15, DurationInput.Rounded(22, 15));
        Assert.Equal(30, DurationInput.Rounded(23, 15));
        Assert.Equal(75, DurationInput.Rounded(80, 15));
        Assert.Equal(90, DurationInput.Rounded(90, 15));
        Assert.Equal(30, DurationInput.Rounded(10, 30));
        Assert.Equal(30, DurationInput.Rounded(44, 30));
        Assert.Equal(60, DurationInput.Rounded(45, 30));
        Assert.Equal(60, DurationInput.Rounded(20, 60));
        Assert.Equal(60, DurationInput.Rounded(89, 60));
        Assert.Equal(120, DurationInput.Rounded(90, 60));
    }

    [Fact(DisplayName = "−/+ geht zur nächsten Stufe, nie unter null")]
    public void Steps()
    {
        Assert.Equal(30, DurationInput.Stepped(20, 1, 15));
        Assert.Equal(45, DurationInput.Stepped(30, 1, 15));
        Assert.Equal(15, DurationInput.Stepped(20, -1, 15));
        Assert.Equal(0, DurationInput.Stepped(15, -1, 15));
        Assert.Equal(0, DurationInput.Stepped(0, -1, 15));
        Assert.Equal(60, DurationInput.Stepped(50, 1, 30));
        Assert.Equal(60, DurationInput.Stepped(20, 1, 60));
        Assert.Equal(60, DurationInput.Stepped(80, -1, 60));
    }

    [Fact(DisplayName = "schreibt Stunden:Minuten und gesprochenen Text")]
    public void Formats()
    {
        Assert.Equal("0:00", DurationInput.Format(0));
        Assert.Equal("1:05", DurationInput.Format(65));
        Assert.Equal("10:00", DurationInput.Format(600));
        Assert.Equal("0 Minuten", DurationInput.Spoken(0));
        Assert.Equal("1 Stunde", DurationInput.Spoken(60));
        Assert.Equal("1 Stunde 1 Minute", DurationInput.Spoken(61));
        Assert.Equal("5 Stunden 15 Minuten", DurationInput.Spoken(315));
    }
}

/// <summary>Tage und Zeitpunkte</summary>
public sealed class CalendarTests
{
    [Fact(DisplayName = "rechnet Tage über Monats- und Jahresgrenzen")]
    public void AddsDays()
    {
        var day = new CalendarDay(2026, 12, 31);
        Assert.Equal(new CalendarDay(2027, 1, 1), day.AddingDays(1));
        Assert.Equal(new CalendarDay(2026, 2, 28), new CalendarDay(2026, 3, 1).AddingDays(-1));
        Assert.Equal("2026-12-31", day.OdooString);
        Assert.True(new CalendarDay(2026, 1, 2) < new CalendarDay(2026, 1, 10));
    }

    [Fact(DisplayName = "Tagesbeginn und Sommerzeit in Berlin")]
    public void StartOfDay()
    {
        // Am 25.10.2026 endet die Sommerzeit; der Tag hat 25 Stunden.
        var day = new CalendarDay(2026, 10, 25);
        var start = day.Start(TestClock.Zone);
        var next = day.AddingDays(1).Start(TestClock.Zone);
        Assert.Equal(25 * 3600, (next - start).TotalSeconds);
        Assert.Equal("2026-10-24 22:00:00", OdooTime.Format(start));
    }

    [Fact(DisplayName = "Odoo-Zeitpunkte sind UTC")]
    public void OdooDateTime()
    {
        var date = OdooTime.Parse("2026-10-23 06:12:00");
        Assert.NotNull(date);
        Assert.Equal("2026-10-23 06:12:00", OdooTime.Format(date.Value));
        Assert.Equal(date.Value.AddSeconds(30), OdooTime.Parse("2026-10-23T06:12:30"));
        Assert.Null(OdooTime.Parse("kein Datum"));
    }

    [Theory(DisplayName = "Datum und Uhrzeit je Sprache und Region")]
    [InlineData("de-DE", "Freitag, 23. Oktober 2026", "Fr. 23.10.", "Freitag", "20:12", "08:05")]
    [InlineData("de-AT", "Freitag, 23. Oktober 2026", "Fr. 23.10.", "Freitag", "20:12", "08:05")]
    [InlineData("en-US", "Friday, October 23, 2026", "Fri, Oct 23", "Friday", "8:12 PM", "8:05 AM")]
    [InlineData("en-GB", "Friday, 23 October 2026", "Fri 23 Oct", "Friday", "20:12", "08:05")]
    [InlineData("fr-FR", "Vendredi 23 octobre 2026", "ven. 23 oct.", "Vendredi", "20:12", "08:05")]
    public void FormatsDates(string identifier, string longDate, string shortDate, string weekday, string evening, string morning)
    {
        var culture = CultureInfo.GetCultureInfo(identifier);
        var day = new CalendarDay(2026, 10, 23);
        Assert.Equal(longDate, Formatting.LongDate(day, culture));
        Assert.Equal(shortDate, Formatting.ShortDate(day, culture));
        Assert.Equal(weekday, Formatting.Weekday(day, culture));
        Assert.Equal(evening, Formatting.Time(TestClock.At(20, 12), TestClock.Zone, culture));
        Assert.Equal(morning, Formatting.Time(TestClock.At(8, 5), TestClock.Zone, culture));
    }

    [Fact(DisplayName = "Sprache von der App, Region vom Mac")]
    public void AppLocale()
    {
        var english = Formatting.MakeCulture("en", CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("en", english.TwoLetterISOLanguageName);
        Assert.Equal("DE", Region(english));
        var french = Formatting.MakeCulture("fr", CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("fr", french.TwoLetterISOLanguageName);
        Assert.Equal("US", Region(french));
        Assert.Equal("de-AT", Formatting.MakeCulture("de", CultureInfo.GetCultureInfo("de-AT")).Name);
        // Swift: Formatting.locale spricht die Sprache, in der die App läuft.
        Assert.Equal(Loc.Language, Formatting.Culture.TwoLetterISOLanguageName);
    }

    private static string? Region(CultureInfo culture) =>
        culture.IsNeutralCulture ? null : new RegionInfo(culture.Name).TwoLetterISORegionName;
}

/// <summary>Serveradresse</summary>
public sealed class ServerAddressTests
{
    [Theory(DisplayName = "ergänzt https und kürzt auf den Server")]
    [InlineData("odoo.example.de", "https://odoo.example.de")]
    [InlineData("https://Odoo.Example.de/", "https://odoo.example.de")]
    [InlineData("https://odoo.example.de/odoo/timesheets?debug=1#x", "https://odoo.example.de")]
    [InlineData("https://odoo.example.de:8443/web", "https://odoo.example.de:8443")]
    [InlineData("http://localhost:8069", "http://localhost:8069")]
    [InlineData("  https://firma.odoo.com  ", "https://firma.odoo.com")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, ServerAddress.Normalize(input));

    [Fact(DisplayName = "lehnt Unsinn ab")]
    public void Rejects()
    {
        Assert.Null(ServerAddress.Normalize(""));
        Assert.Null(ServerAddress.Normalize("ftp://odoo.example.de"));
        Assert.Null(ServerAddress.Normalize("https://"));
    }

    [Fact(DisplayName = "http nur auf dem eigenen Rechner")]
    public void Security()
    {
        Assert.True(ServerAddress.IsSecure("https://odoo.example.de"));
        Assert.True(ServerAddress.IsSecure("http://localhost:8069"));
        Assert.True(ServerAddress.IsSecure("http://127.0.0.1:8069"));
        Assert.False(ServerAddress.IsSecure("http://odoo.example.de"));
        Assert.False(ServerAddress.IsSecure("http://192.168.1.10:8069"));
    }
}

/// <summary>JSON-Werte</summary>
public sealed class JsonValueTests
{
    [Fact(DisplayName = "liest Verweise, false und Zahlen")]
    public void Reads()
    {
        var data = Encoding.UTF8.GetBytes("""{"project_id":[7,"Interne Arbeiten"],"task_id":false,"unit_amount":1.5,"id":42,"name":"x"}""");
        var value = Expect.Require(Json.Parse(data));
        Assert.Equal(new Reference(7, "Interne Arbeiten"), value.Field("project_id").Reference());
        Assert.Null(value.Field("task_id").Reference());
        Assert.False(value.Field("task_id").BoolValue());
        Assert.Equal(1.5, value.Field("unit_amount").DoubleValue());
        Assert.Equal(42, value.Field("id").IntValue());
        Assert.Null(value.Field("unit_amount").IntValue());
    }

    [Fact(DisplayName = "schreibt IDs als Ganzzahl")]
    public void WritesIntegers()
    {
        var value = new JsonObject { ["ids"] = Json.Ints([1, 2]), ["amount"] = 0.25, ["flag"] = false, ["none"] = null };
        var text = Json.Serialize(value);
        Assert.Contains("\"ids\":[1,2]", text);
        Assert.Contains("\"amount\":0.25", text);
        Assert.Contains("\"flag\":false", text);
        Assert.Contains("\"none\":null", text);
    }
}
