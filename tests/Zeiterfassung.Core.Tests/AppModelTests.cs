// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;

namespace Zeiterfassung.Tests;

/// <summary>Abläufe der App</summary>
public sealed class AppModelTests
{
    [Fact(DisplayName = "Anmeldung legt den Schlüssel ab und zeigt erst nach „Fertig“ den Tag")]
    public void SignIn() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var secrets = new MemorySecretStore();
        var model = Models.Make(odoo, secrets);
        await model.StartAsync();
        try
        {
            Assert.Equal(Screen.Login, model.Screen);

            var found = await model.SignInAsync("odoo.example.de", $"  {odoo.ApiKey}  ", null, null);
            Assert.Equal(FeatureStatus.Available, found.Timesheets);
            Assert.Equal(Screen.Login, model.Screen);
            Assert.Equal(1, secrets.Count);
            Assert.Equal("https://odoo.example.de", model.Preferences.Session?.ServerUrl);
            Assert.True(model.Preferences.TimesheetsEnabled && model.Preferences.AttendanceEnabled);

            model.FinishSignIn();
            Assert.Equal(Screen.Main, model.Screen);
            Assert.True(model.TrayContent.IsSignedIn);
        }
        finally
        {
            model.Stop();
        }
    });

    [Fact(DisplayName = "unverschlüsselte Adresse wird abgelehnt")]
    public void InsecureServer() => MainThread.Run(async () =>
    {
        var model = Models.Make(new FakeOdoo());
        var error = await Assert.ThrowsAsync<OdooException>(() => model.SignInAsync("http://odoo.example.de", "x", null, null));
        Assert.Equal(OdooErrorKind.InsecureServerAddress, error.Kind);
    });

    [Fact(DisplayName = "Neustart: Sitzung aus den Einstellungen, Schlüssel aus der Anmeldeinformationsverwaltung")]
    public void RestoresSession() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var secrets = new MemorySecretStore();
        var settings = new MemorySettingsStore();
        var first = Models.Make(odoo, secrets, settings: settings);
        await first.SignInAsync("odoo.example.de", odoo.ApiKey, null, null);

        var second = Models.Make(odoo, secrets, settings: settings);
        await second.StartAsync();
        try
        {
            Assert.Equal(Screen.Main, second.Screen);
            Assert.Equal("Erika Mustermann", second.Capabilities?.UserName);
        }
        finally
        {
            second.Stop();
        }
    });

    [Theory(DisplayName = "bucht einen Eintrag: Projekt, Aufgabe, Dauer, Summe")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void BooksEntry(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        var model = await Models.SignedIn(odoo);
        Assert.Empty(model.Entries);
        Assert.True(model.IsDayLoaded);
        Assert.False(model.CanSave);

        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        await model.LoadTasksAsync(3);
        Assert.Equal(new TaskChoice(TaskChoiceKind.Tasks, null, [new ProjectTask(31, "Text"), new ProjectTask(32, "Layout")]),
                     model.TaskChoice);
        Assert.Null(model.Draft.TaskId);
        model.SelectTask(32);
        model.DraftDescription = "Korrekturen eingearbeitet";
        model.DraftDurationText = "1,5";
        Assert.True(model.CanSave);

        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.True(model.Draft.Description.Length == 0 && model.Draft.DurationText.Length == 0);
        Assert.True(model.Draft.ProjectId == 3 && model.Draft.TaskId == 32);
        Assert.Equal([90], model.Entries.Select(entry => entry.Minutes));
        Assert.Equal(90, model.BookedMinutes);
        Assert.Contains("Projektzeit heute 1:30 Std.", model.TrayContent.ToolTip, StringComparison.Ordinal);
        Assert.Equal(3, model.RecentProjectIds[0]);
        Assert.Equal("2026-10-23", odoo.WithLock(() => odoo.Lines[0].Date));
    });
}
