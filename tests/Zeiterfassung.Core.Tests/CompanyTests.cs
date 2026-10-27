// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Tests;

/// <summary>Mehrere Firmen</summary>
public sealed class CompanyTests
{
    /// <summary>Zwei Firmen mit je einem Mitarbeiter. Projekt 3 gehört der zweiten Firma, Projekt 4 allen.</summary>
    private static FakeOdoo TwoCompanies(FakeOdooMode mode = FakeOdooMode.Odoo19)
    {
        var odoo = new FakeOdoo(mode);
        odoo.WithLock(() =>
        {
            odoo.Companies = [(1, "Muster Agentur GmbH"), (2, "Muster Fotostudio")];
            odoo.OtherEmployees = [(4, 2)];
            odoo.ProjectCompanies = new() { [1] = 1, [2] = 1, [3] = 2 };
        });
        return odoo;
    }

    private static FakeOdoo.Line? Line(FakeOdoo odoo, int id) =>
        odoo.WithLock(() => odoo.Lines.FirstOrDefault(line => line.Id == id));

    [Theory(DisplayName = "erkennt Firmen und Mitarbeiter je Firma, die Hauptfirma zuerst")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void DiscoversOrganization(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = TwoCompanies(mode);
        var found = await Services.For(odoo).DiscoverCapabilitiesAsync();
        var organization = Expect.Require(found.Organization);
        Assert.Equal([1, 2], organization.Companies.Select(company => company.Id));
        Assert.Equal([new Employee(3, 1), new Employee(4, 2)], organization.Employees);
        Assert.Equal(3, found.EmployeeId);

        found = await Services.For(odoo).DiscoverCapabilitiesAsync(attendanceEmployeeId: 4);
        Assert.Equal(4, found.EmployeeId);
        found = await Services.For(odoo).DiscoverCapabilitiesAsync(attendanceEmployeeId: 99);
        Assert.Equal(3, found.EmployeeId);
    });

    [Fact(DisplayName = "mit nur einer Firma bleibt alles wie bisher")]
    public void SingleCompany() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var found = await Services.For(odoo).DiscoverCapabilitiesAsync();
        Assert.Null(found.Organization);
        var model = await Models.SignedIn(odoo);
        Assert.Single(model.EntryGroups);
        Assert.Null(model.EntryGroups.FirstOrDefault()?.Title);
        model.SelectProject(model.Projects.Single(project => project.Id == 2));
        Assert.Null(model.SelectedProjectCompanyText);
        Assert.Empty(model.AttendanceCompanies);
    });

    [Theory(DisplayName = "Odoo bucht in der Firma des Projekts, mit dem Mitarbeiter dieser Firma")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void BooksInProjectCompany(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = TwoCompanies(mode);
        var service = Services.For(odoo);
        var projects = await service.ProjectsAsync();
        Assert.Equal(2, projects.FirstOrDefault(project => project.Id == 3)?.CompanyId);
        Assert.Null(projects.FirstOrDefault(project => project.Id == 4)?.CompanyId);

        var day = new CalendarDay(2026, 10, 23);
        await service.CreateTimesheetAsync(new TimesheetDraft(3, 31, "Bildauswahl", 60, day));
        var entry = Expect.Require((await service.TimesheetsAsync(day)).FirstOrDefault());
        Assert.Equal(2, entry.CompanyId);
        Assert.Equal(4, entry.EmployeeId);
    });

    [Fact(DisplayName = "Einträge eines Tages aus zwei Firmen stehen in Gruppen mit Zwischensumme")]
    public void GroupsEntries() => MainThread.Run(async () =>
    {
        var odoo = TwoCompanies();
        odoo.WithLock(() => odoo.Lines =
        [
            Fake.Line(1, "2026-10-23", "Bildauswahl", 3, 31, 1, companyId: 2, employeeId: 4),
            Fake.Line(2, "2026-10-23", "Plugin-Updates", 2, 21, 0.25),
            Fake.Line(3, "2026-10-23", "Angebot", 1, 11, 2),
        ]);
        var model = await Models.SignedIn(odoo);
        var groups = model.EntryGroups;
        Assert.Equal(["Muster Agentur GmbH", "Muster Fotostudio"], groups.Select(group => group.Title));
        Assert.Equal([135, 60], groups.Select(group => group.Minutes));
        Assert.Equal([[2, 3], [1]], groups.Select(group => group.Entries.Select(entry => entry.Id).ToArray()));
    });

    [Fact(DisplayName = "im Formular steht, in welcher Firma Odoo bucht")]
    public void ShowsBookingCompany() => MainThread.Run(async () =>
    {
        var model = await Models.SignedIn(TwoCompanies());
        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        Assert.Equal("Muster Fotostudio", model.SelectedProjectCompanyText);
        model.SelectProject(model.Projects.Single(project => project.Id == 4));
        Assert.Equal("Ohne Firma · gebucht in Muster Agentur GmbH", model.SelectedProjectCompanyText);
        Assert.Equal("Ohne Firma", model.CompanyName(model.Projects.Single(project => project.Id == 4)));
    });

    [Theory(DisplayName = "wechselt ein Eintrag die Firma, wechselt die App auch den Mitarbeiter")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void SwitchesEmployee(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = TwoCompanies(mode);
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Retusche", 2, 21, 1, companyId: 1, employeeId: 3)]);
        var model = await Models.SignedIn(odoo);
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        await model.LoadTasksAsync(3);
        model.SelectTask(31);
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal(2, Line(odoo, 1)?.CompanyId);
        Assert.Equal(4, Line(odoo, 1)?.EmployeeId);

        // Zu einem Projekt ohne Firma: Der Eintrag bleibt in seiner Firma und bei seinem Mitarbeiter.
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        model.SelectProject(model.Projects.Single(project => project.Id == 4));
        await model.LoadTasksAsync(4);
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal(4, Line(odoo, 1)?.ProjectId);
        Assert.Equal(2, Line(odoo, 1)?.CompanyId);
        Assert.Equal(4, Line(odoo, 1)?.EmployeeId);
    });

    [Fact(DisplayName = "ohne Mitarbeiter in der Firma des Projekts sagt die App, warum es nicht geht")]
    public void MissingEmployee() => MainThread.Run(async () =>
    {
        var odoo = TwoCompanies();
        odoo.WithLock(() =>
        {
            odoo.Companies.Add((5, "Muster Druck"));
            odoo.ProjectCompanies[1] = 5;
        });
        var model = await Models.SignedIn(odoo);
        model.SelectProject(model.Projects.Single(project => project.Id == 1));
        await model.LoadTasksAsync(1);
        model.SelectTask(11);
        model.DraftDurationText = "1:00";
        await model.SaveAsync();
        Assert.Equal("Für „Muster Druck“ gibt es keinen Mitarbeiter zu deinem Odoo-Benutzer. Wähle ein anderes Projekt oder lass dir in Odoo einen Mitarbeiter anlegen.",
                     model.SaveError);
        Assert.True(odoo.WithLock(() => odoo.Lines.Count == 0));
        Assert.DoesNotContain("account.analytic.line.create", odoo.Log);
    });

    [Fact(DisplayName = "Stempeln für eine andere Firma: deren Mitarbeiter, deren Anwesenheiten")]
    public void AttendanceCompany() => MainThread.Run(async () =>
    {
        var odoo = TwoCompanies();
        odoo.WithLock(() => odoo.Attendances = [Fake.Attendance(1, "2026-10-23 06:12:00", "2026-10-23 10:05:00")]);
        var settings = new MemorySettingsStore();
        var model = await Models.SignedIn(odoo, settings: settings);
        Assert.Equal([1, 2], model.AttendanceCompanies.Select(company => company.Id));
        Assert.Equal(1, model.AttendanceCompanyId);
        Assert.Equal([1], model.ShownPeriods.Select(period => period.Id));

        await model.SetAttendanceCompanyAsync(2);
        Assert.Equal(4, model.Capabilities?.EmployeeId);
        Assert.Equal(2, model.AttendanceCompanyId);
        Assert.Equal(4, model.Preferences.AttendanceEmployeeId);
        Assert.Empty(model.ShownPeriods);
        await model.ToggleAttendanceAsync();
        Assert.Equal(4, odoo.WithLock(() => odoo.Attendances.LastOrDefault()?.EmployeeId));

        // Nach einem Neustart gilt die Wahl weiter.
        var restarted = Models.Make(odoo, settings: settings);
        Assert.Equal(4, restarted.Preferences.AttendanceEmployeeId);

        await model.SetAttendanceCompanyAsync(1);
        Assert.Equal(3, model.Capabilities?.EmployeeId);
        Assert.Null(model.Preferences.AttendanceEmployeeId);
    });
}
