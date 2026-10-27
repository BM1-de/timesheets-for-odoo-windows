// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Tests;

/// <summary>Datenzugriff</summary>
public sealed class OdooServiceTests
{
    [Theory(DisplayName = "erkennt beide Funktionen und das Recht zu stempeln")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public async Task DiscoversEverything(FakeOdooMode mode)
    {
        var odoo = new FakeOdoo(mode);
        var found = await Services.For(odoo).DiscoverCapabilitiesAsync();
        Assert.Equal("Erika Mustermann", found.UserName);
        Assert.Equal(FeatureStatus.Available, found.Timesheets);
        Assert.Equal(FeatureStatus.Available, found.Attendance);
        Assert.True(found.CanStamp);
        Assert.Equal(3, found.EmployeeId);
        Assert.True(found.Schema.TaskHasIsClosed);
        Assert.Equal(mode != FakeOdooMode.Odoo17, found.Schema.ProjectHasTemplates);
    }

    [Theory(DisplayName = "ohne Odoo-App Anwesenheiten")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public async Task AttendanceMissing(FakeOdooMode mode)
    {
        var odoo = new FakeOdoo(mode) { AttendanceInstalled = false };
        var found = await Services.For(odoo).DiscoverCapabilitiesAsync();
        Assert.Equal(FeatureStatus.Available, found.Timesheets);
        Assert.Equal(FeatureStatus.NotInstalled, found.Attendance);
        Assert.False(found.CanStamp);
    }

    [Fact(DisplayName = "ohne verknüpften Mitarbeiter und ohne Recht zu stempeln")]
    public async Task NoEmployeeNoStamp()
    {
        var odoo = new FakeOdoo { EmployeeId = null };
        var found = await Services.For(odoo).DiscoverCapabilitiesAsync();
        Assert.Equal(FeatureStatus.NoEmployee, found.Timesheets);
        Assert.Equal(FeatureStatus.NoEmployee, found.Attendance);

        odoo.EmployeeId = 3;
        odoo.CanStamp = false;
        found = await Services.For(odoo).DiscoverCapabilitiesAsync();
        Assert.Equal(FeatureStatus.Available, found.Attendance);
        Assert.False(found.CanStamp);
    }

    [Theory(DisplayName = "legt einen Zeiteintrag an und liest ihn wieder")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public async Task CreatesAndReadsTimesheet(FakeOdooMode mode)
    {
        var odoo = new FakeOdoo(mode);
        var service = Services.For(odoo);
        var day = new CalendarDay(2026, 10, 23);
        var id = await service.CreateTimesheetAsync(new TimesheetDraft(2, 21, "Plugin-Updates", 45, day));
        Assert.True(id > 0);
        var line = Expect.Require(odoo.WithLock(() => odoo.Lines.FirstOrDefault()));
        Assert.Equal(0.75, line.Hours);
        Assert.Equal("2026-10-23", line.Date);
        Assert.Equal(21, line.TaskId);

        var entries = await service.TimesheetsAsync(day);
        Assert.Single(entries);
        Assert.Equal(45, entries[0].Minutes);
        Assert.Equal("Plugin-Updates", entries[0].Description);
        Assert.Equal("Website-Betreuung (Muster GmbH) › Wartung", entries[0].Path);
        Assert.Empty(await service.TimesheetsAsync(day.AddingDays(1)));
    }

    [Fact(DisplayName = "leere Beschreibung wird zu „/“ und zurück")]
    public async Task EmptyDescription()
    {
        var odoo = new FakeOdoo();
        var service = Services.For(odoo);
        var day = new CalendarDay(2026, 10, 23);
        await service.CreateTimesheetAsync(new TimesheetDraft(4, null, "", 15, day));
        Assert.Equal("/", odoo.WithLock(() => odoo.Lines.FirstOrDefault()?.Name));
        Assert.Null(odoo.WithLock(() => odoo.Lines.FirstOrDefault()?.TaskId));
        Assert.Equal("", (await service.TimesheetsAsync(day)).FirstOrDefault()?.Description);
    }

    [Theory(DisplayName = "stempelt ein und aus")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public async Task Stamps(FakeOdooMode mode)
    {
        var odoo = new FakeOdoo(mode);
        var service = Services.For(odoo);
        var checkIn = TestClock.Now.AddSeconds(-3600);
        await service.CheckInAsync(3, checkIn);
        var open = Expect.Require(await service.OpenAttendanceAsync());
        Assert.Equal(checkIn, open.CheckIn);
        Assert.True(open.IsOpen);

        await service.CheckOutAsync(open.Id, TestClock.Now);
        Assert.Null(await service.OpenAttendanceAsync());
        var today = await service.AttendancesAsync(CalendarDay.From(TestClock.Now, TestClock.Zone), TestClock.Zone);
        Assert.Single(today);
        Assert.Equal(60, today[0].Minutes(TestClock.Now));
    }

    [Fact(DisplayName = "Odoos eigene Einträge von einer Sekunde zählen nicht als Anwesenheit")]
    public async Task SkipsTechnicalEntries()
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Attendances =
        [
            Fake.Attendance(1, "2026-10-23 06:00:00", "2026-10-23 06:00:01", "technical", "technical"),
            Fake.Attendance(2, "2026-10-23 07:00:00", "2026-10-23 11:00:00", "technical", "manual"),
        ]);
        var periods = await Services.For(odoo).AttendancesAsync(new CalendarDay(2026, 10, 23), TestClock.Zone);
        Assert.Equal([2], periods.Select(period => period.Id));
        Assert.True(periods.FirstOrDefault()?.IsTechnical);
    }

    // „gemerkte Funktionen der Version 0.1.0 lassen sich weiter lesen“ (decodesOldCapabilities) fehlt hier: Es liest das
    // gespeicherte Format älterer Mac-Versionen. Die Windows-App hat keine älteren Versionen, aus denen sie übernimmt.

    [Fact(DisplayName = "Projekte, Aufgaben und zuletzt verwendete")]
    public async Task ProjectsAndTasks()
    {
        var odoo = new FakeOdoo();
        var service = Services.For(odoo);
        Assert.Contains("Interne Arbeiten", (await service.ProjectsAsync()).Select(project => project.Name));
        Assert.Equal(["Text", "Layout"], (await service.TasksAsync(3)).Select(task => task.Name));
        Assert.Empty(await service.TasksAsync(4));

        var today = new CalendarDay(2026, 10, 23);
        foreach (var (project, day) in new[] { (3, 20), (1, 21), (3, 22), (2, 23) })
        {
            await service.CreateTimesheetAsync(new TimesheetDraft(project, null, "x", 15, new CalendarDay(2026, 10, day)));
        }
        Assert.Equal([2, 3, 1], await service.RecentProjectIdsAsync(today.AddingDays(-30)));
    }
}
