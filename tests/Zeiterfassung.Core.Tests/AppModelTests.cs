// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

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

    [Fact(DisplayName = "Projekt mit nur einer Aufgabe wählt sie vor; ohne Aufgaben aufs Projekt")]
    public void TaskPreselection() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var model = await Models.SignedIn(odoo);
        model.SelectProject(model.Projects.Single(project => project.Id == 2));
        await model.LoadTasksAsync(2);
        Assert.Equal(21, model.Draft.TaskId);

        model.SelectProject(model.Projects.Single(project => project.Id == 4));
        await model.LoadTasksAsync(4);
        Assert.Equal(TaskChoice.NoTasks, model.TaskChoice);
        Assert.False(model.TaskIsRequired);
        model.DraftDurationText = "0:30";
        Assert.True(model.CanSave);
    });

    [Fact(DisplayName = "ohne Netz bleibt das Formular stehen")]
    public void SaveOffline() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var model = await Models.SignedIn(odoo);
        model.SelectProject(model.Projects.Single(project => project.Id == 2));
        await model.LoadTasksAsync(2);
        model.DraftDescription = "Plugin-Updates";
        model.DraftDurationText = "0:15";

        odoo.WithLock(() => odoo.Offline = true);
        await model.SaveAsync();
        Assert.Equal("Keine Verbindung zu Odoo. Deine Angaben bleiben erhalten.", model.SaveError);
        Assert.Equal("Plugin-Updates", model.Draft.Description);
        Assert.Equal("0:15", model.Draft.DurationText);

        odoo.WithLock(() => odoo.Offline = false);
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Single(model.Entries);
    });

    [Fact(DisplayName = "Dauer: Rundung, Hinweise, −/+")]
    public void Duration() => MainThread.Run(async () =>
    {
        var model = await Models.SignedIn(new FakeOdoo());
        Assert.Equal(DurationRounding.Off, model.Preferences.DurationRounding);
        model.DraftDurationText = "1:20";
        Assert.Equal(80, model.DraftMinutes);
        Assert.Null(model.DurationHint);
        model.Preferences.DurationRounding = DurationRounding.QuarterHour;
        Assert.Equal(75, model.DraftMinutes);
        Assert.Equal(new Hint("Wird als 1:15 Std. gebucht.", false), model.DurationHint);
        model.Preferences.DurationRounding = DurationRounding.Hour;
        Assert.Equal(60, model.DraftMinutes);
        Assert.Equal(new Hint("Wird als 1:00 Std. gebucht.", false), model.DurationHint);

        model.DraftDurationText = "abc";
        Assert.True(model.DurationHint?.IsProblem);
        Assert.Null(model.DraftMinutes);

        model.DraftDurationText = "0:20";
        model.AdjustDuration(1);
        Assert.Equal("1:00", model.Draft.DurationText);
        model.AdjustDuration(-1);
        Assert.Equal("", model.Draft.DurationText);

        model.Preferences.DurationRounding = DurationRounding.Off;
        model.DraftDurationText = "0:20";
        model.AdjustDuration(1);
        Assert.Equal("0:30", model.Draft.DurationText);
        model.AdjustDuration(-1);
        model.AdjustDuration(-1);
        Assert.Equal("", model.Draft.DurationText);
        model.AdjustDuration(-1);
        Assert.Equal("", model.Draft.DurationText);
    });

    // Windows zeigt keine Zeit neben dem Symbol: Statt Text, Symbol und Bedienungshilfe der Menüleiste prüft der Test
    // die Zeilen im Tooltip. Die Wahl der Zeit in der Menüleiste (menuBarTime) gibt es unter Windows nicht.
    [Fact(DisplayName = "Anwesenheit: stempeln und Zusammenfassung, in der Menüleiste auf Wunsch")]
    public void Attendance() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        // Heute 08:12–12:05 (Ortszeit), seit 12:47 wieder anwesend.
        odoo.WithLock(() => odoo.Attendances =
        [
            Fake.Attendance(1, "2026-10-23 06:12:00", "2026-10-23 10:05:00"),
            Fake.Attendance(2, "2026-10-23 10:47:00", null),
        ]);
        var model = await Models.SignedIn(odoo);
        var summary = model.AttendanceSummary;
        Assert.True(summary.IsRunning);
        Assert.Equal("Anwesend seit 12:47", summary.Title);
        Assert.Equal("Heute 5:58 Std. · Pause 0:42", summary.Subtitle);
        Assert.Equal("Ausstempeln", summary.ButtonTitle);
        Assert.Equal("5:58 Std.", summary.UnbookedText);
        Assert.Equal($"{AppInfo.Name}\nProjektzeit heute 0:00 Std.\nAnwesend seit 12:47 · heute 5:58 Std.",
                     model.TrayContent.ToolTip);

        await model.ToggleAttendanceAsync();
        Assert.Null(model.StampError);
        summary = model.AttendanceSummary;
        Assert.False(summary.IsRunning);
        Assert.Equal("Ausgestempelt um 14:52", summary.Title);
        Assert.Equal("Einstempeln", summary.ButtonTitle);
        Assert.Equal($"{AppInfo.Name}\nProjektzeit heute 0:00 Std.\nAnwesend heute 5:58 Std.", model.TrayContent.ToolTip);
    });

    [Fact(DisplayName = "ohne Recht zum Stempeln nur Anzeige mit Hinweis")]
    public void AttendanceReadOnly() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo { CanStamp = false };
        var model = await Models.SignedIn(odoo);
        var summary = model.AttendanceSummary;
        Assert.Null(summary.ButtonTitle);
        Assert.Contains("„Sachbearbeiter: Alle Anwesenheiten verwalten“", summary.Note);
        Assert.False(summary.Note?.StartsWith("Odoo hat", StringComparison.Ordinal));
        Assert.Equal("Nicht eingestempelt", summary.Title);
    });

    // Statt der Zeit in der Menüleiste (effectiveMenuBarTime, Text, Symbol) der Tooltip: nur die Anwesenheit.
    [Fact(DisplayName = "nur Anwesenheit: Zeiten als Liste, in der Menüleiste die Anwesenheit")]
    public void AttendanceOnly() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Attendances = [Fake.Attendance(1, "2026-10-23 06:12:00", "2026-10-23 10:05:00")]);
        var model = await Models.SignedIn(odoo);
        Assert.True(model.SetTimesheetsEnabled(false));
        await model.RefreshAsync(force: true);
        Assert.False(model.TimesheetsActive);
        Assert.Equal($"{AppInfo.Name}\nAnwesend heute 3:53 Std.", model.TrayContent.ToolTip);
        Assert.Equal(["08:12 – 12:05"], model.AttendanceSummary.Rows.Select(row => row.Range));
        // Die letzte Funktion bleibt an.
        Assert.False(model.SetAttendanceEnabled(false));
        Assert.True(model.AttendanceActive);
    });

    [Fact(DisplayName = "ohne Odoo-App Anwesenheiten nur Zeiterfassung")]
    public void AttendanceNotInstalled() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo { AttendanceInstalled = false };
        var model = await Models.SignedIn(odoo);
        Assert.True(model.TimesheetsActive);
        Assert.False(model.AttendanceActive);
        Assert.False(model.Preferences.AttendanceEnabled);
        Assert.False(model.SetTimesheetsEnabled(false));
    });

    [Fact(DisplayName = "Tage blättern")]
    public void NavigatesDays() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-22", "Seitenstruktur abgestimmt", 1, 11, 4)]);
        var model = await Models.SignedIn(odoo);
        Assert.Equal("Heute", model.DayTitle);
        Assert.Empty(model.Entries);

        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        Assert.Equal("Gestern", model.DayTitle);
        Assert.Equal("Donnerstag, 22. Oktober 2026", model.DayLongDate);
        Assert.Equal([240], model.Entries.Select(entry => entry.Minutes));
        Assert.Equal(0, model.TodayBookedMinutes);
        Assert.Equal("Keine Anwesenheit erfasst", model.AttendanceSummary.Title);

        model.ShowToday();
        Assert.True(model.IsShowingToday);
        Assert.Empty(model.Entries);
    });

    [Fact(DisplayName = "abgelaufener Schlüssel führt zur Anmeldung")]
    public void ExpiredKey() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var secrets = new MemorySecretStore();
        var settings = new MemorySettingsStore();
        var model = Models.Make(odoo, secrets, settings: settings);
        await model.SignInAsync("odoo.example.de", odoo.ApiKey, null, null);
        model.FinishSignIn();
        // Schlüssel in Odoo widerrufen: Der gespeicherte passt nicht mehr.
        var account = Expect.Require(model.Session?.SecretAccount);
        secrets.Save("widerrufen", account, "");
        var restarted = Models.Make(odoo, secrets, settings: settings);
        await restarted.StartAsync();
        try
        {
            Assert.Equal(NoticeKind.SignedOut, restarted.Notice?.Kind);

            restarted.ShowSignIn();
            Assert.Equal(Screen.Login, restarted.Screen);
            restarted.CancelSignIn();
            Assert.Equal(Screen.Main, restarted.Screen);
        }
        finally
        {
            restarted.Stop();
        }
    });

    [Fact(DisplayName = "Abmelden löscht den Schlüssel")]
    public void SignOut() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var secrets = new MemorySecretStore();
        var model = Models.Make(odoo, secrets);
        await model.SignInAsync("odoo.example.de", odoo.ApiKey, null, null);
        model.FinishSignIn();
        model.SignOut();
        Assert.Equal(0, secrets.Count);
        Assert.Equal(Screen.Login, model.Screen);
        Assert.Null(model.Preferences.Session);
        Assert.Equal("https://odoo.example.de", model.Preferences.LoginHints?.Server);
        Assert.False(model.TrayContent.IsSignedIn);
    });

    [Fact(DisplayName = "bleibt die Antwort beim Speichern aus, legt „Erneut versuchen“ nichts doppelt an")]
    public void NoDuplicateAfterLostResponse() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var model = await Models.SignedIn(odoo);
        await Models.FillDraft(model);

        odoo.WithLock(() => odoo.LoseNextCreateResponse = true);
        await model.SaveAsync();
        Assert.Equal("Keine Verbindung zu Odoo. Deine Angaben bleiben erhalten.", model.SaveError);
        Assert.Equal("Plugin-Updates", model.Draft.Description);
        Assert.Equal(1, odoo.WithLock(() => odoo.Lines.Count));

        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal(1, odoo.WithLock(() => odoo.Lines.Count));
        Assert.Equal([15], model.Entries.Select(entry => entry.Minutes));
        Assert.Empty(model.Draft.Description);
    });

    [Fact(DisplayName = "lehnt Odoo ab, wird beim nächsten Versuch neu angelegt")]
    public void RetriesRejectedSave() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var model = await Models.SignedIn(odoo);
        await Models.FillDraft(model);
        odoo.WithLock(() => odoo.FailNextCreate = true);
        await model.SaveAsync();
        Assert.NotNull(model.SaveError);
        Assert.Equal(0, odoo.WithLock(() => odoo.Lines.Count));
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal(1, odoo.WithLock(() => odoo.Lines.Count));
    });

    [Fact(DisplayName = "was während des Speicherns getippt wird, bleibt stehen")]
    public void KeepsTypingDuringSave() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var http = new PausingHttpSender(odoo.Client(), request => request.Url.AbsolutePath.EndsWith("/create", StringComparison.Ordinal));
        var model = await Models.SignedIn(odoo, http);
        await Models.FillDraft(model);

        var saving = model.SaveAsync();
        await http.WaitUntilPaused();
        model.DraftDescription = "Nächster Eintrag";
        http.Resume();
        await saving;
        Assert.Null(model.SaveError);
        Assert.Equal("Nächster Eintrag", model.Draft.Description);
        Assert.Empty(model.Draft.DurationText);
        Assert.Equal("Plugin-Updates", odoo.WithLock(() => odoo.Lines.FirstOrDefault()?.Name));
    });

    [Fact(DisplayName = "Abmelden während des Speicherns: Was danach zurückkommt, wird verworfen")]
    public void SignOutDuringSave() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var http = new PausingHttpSender(odoo.Client(), request => request.Url.AbsolutePath.EndsWith("/create", StringComparison.Ordinal));
        var model = await Models.SignedIn(odoo, http);
        await Models.FillDraft(model);

        var saving = model.SaveAsync();
        await http.WaitUntilPaused();
        model.SignOut();
        http.Resume();
        await saving;
        Assert.Equal(Screen.Login, model.Screen);
        Assert.False(model.IsSaving);
        Assert.Null(model.SaveError);
        Assert.Empty(model.Entries);
        Assert.Empty(model.TodayEntries);
        Assert.Empty(model.RecentProjectIds);
    });

    [Fact(DisplayName = "scheitern nur die Anwesenheiten, erscheinen die Zeiteinträge trotzdem")]
    public void PartialLoadFailure() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() =>
        {
            odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.5)];
            odoo.FailAttendanceRead = true;
        });
        var model = await Models.SignedIn(odoo);
        Assert.Equal([30], model.Entries.Select(entry => entry.Minutes));
        Assert.Equal(30, model.TodayBookedMinutes);
        Assert.Equal(NoticeKind.Failure, model.Notice?.Kind);
        Assert.Equal("Anwesenheiten konnten nicht geladen werden. Kein Zugriff auf Anwesenheiten", model.Notice?.Message);

        odoo.WithLock(() => odoo.FailAttendanceRead = false);
        await model.RefreshAsync(force: true);
        Assert.Null(model.Notice);
    });

    [Fact(DisplayName = "nach einem Fehler lädt die App selbst erneut, mit wachsendem Abstand")]
    public void RetriesAfterFailure() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var time = new MovingClock();
        var secrets = new MemorySecretStore();
        var settings = new MemorySettingsStore();
        var first = Models.Make(odoo, secrets, clock: time, settings: settings);
        await first.SignInAsync("odoo.example.de", odoo.ApiKey, null, null);

        // Start ohne Netz: erster Fehler
        odoo.WithLock(() => odoo.Offline = true);
        var http = odoo.Client();
        var model = Models.Make(odoo, secrets, http, time, settings);
        await model.StartAsync();
        try
        {
            Assert.Equal(NoticeKind.Offline, model.Notice?.Kind);

            // 20 Sekunden später der zweite Versuch …
            time.Advance(20);
            var sent = http.Requests.Count;
            await model.TickAsync();
            Assert.True(http.Requests.Count > sent);

            // … danach 40 Sekunden Abstand.
            time.Advance(20);
            sent = http.Requests.Count;
            await model.TickAsync();
            Assert.Equal(sent, http.Requests.Count);

            odoo.WithLock(() => odoo.Offline = false);
            time.Advance(20);
            await model.TickAsync();
            Assert.Null(model.Notice);
            Assert.True(model.IsDayLoaded);
        }
        finally
        {
            model.Stop();
        }
    });

    [Fact(DisplayName = "eine abgelaufene Anmeldung versucht die App nicht immer wieder")]
    public void NoRetryWhenSignedOut() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var time = new MovingClock();
        var secrets = new MemorySecretStore();
        var settings = new MemorySettingsStore();
        var first = Models.Make(odoo, secrets, clock: time, settings: settings);
        await first.SignInAsync("odoo.example.de", odoo.ApiKey, null, null);
        secrets.Save("widerrufen", Expect.Require(first.Session?.SecretAccount), "");

        var http = odoo.Client();
        var model = Models.Make(odoo, secrets, http, time, settings);
        await model.StartAsync();
        try
        {
            Assert.Equal(NoticeKind.SignedOut, model.Notice?.Kind);
            var sent = http.Requests.Count;
            time.Advance(400);
            await model.TickAsync();
            Assert.Equal(sent, http.Requests.Count);
        }
        finally
        {
            model.Stop();
        }
    });

    [Fact(DisplayName = "lehnt Odoo das Stempeln trotz Recht ab, verschwindet der Knopf bis zur erneuten Prüfung")]
    public void StampRefusedByRule() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.StampDeniedByRule = true);
        var model = await Models.SignedIn(odoo);
        Assert.Equal("Einstempeln", model.AttendanceSummary.ButtonTitle);

        await model.ToggleAttendanceAsync();
        Assert.False(model.Capabilities?.CanStamp);
        Assert.False(model.Preferences.CachedCapabilities?.CanStamp);
        Assert.Null(model.StampError);
        Assert.Null(model.AttendanceSummary.ButtonTitle);
        Assert.True(model.AttendanceSummary.Note?.StartsWith("Odoo hat das Stempeln abgelehnt.", StringComparison.Ordinal));

        // Beim nächsten Start bleibt es dabei …
        await model.RefreshCapabilitiesAsync();
        Assert.False(model.Capabilities?.CanStamp);
        // … bis die Rechte in den Einstellungen erneut geprüft werden.
        odoo.WithLock(() => odoo.StampDeniedByRule = false);
        await model.RefreshCapabilitiesAsync(recheck: true);
        Assert.True(model.Capabilities?.CanStamp);
        await model.ToggleAttendanceAsync();
        Assert.NotNull(model.OpenPeriod);
    });

    [Fact(DisplayName = "Aufgaben werden nach zehn Minuten nachgeladen; erledigte fallen aus der Auswahl")]
    public void ReloadsStaleTasks() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var time = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: time);
        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        await model.LoadTasksAsync(3);
        model.SelectTask(32);
        odoo.WithLock(() => odoo.Tasks[3] = [(31, "Text"), (33, "Korrektur")]);

        time.Advance(300);
        await model.LoadTasksAsync(3);
        Assert.Equal(new TaskChoice(TaskChoiceKind.Tasks, null, [new ProjectTask(31, "Text"), new ProjectTask(32, "Layout")]),
                     model.TaskChoice);
        Assert.Equal(32, model.Draft.TaskId);

        time.Advance(301);
        await model.LoadTasksAsync(3);
        Assert.Equal(new TaskChoice(TaskChoiceKind.Tasks, null, [new ProjectTask(31, "Text"), new ProjectTask(33, "Korrektur")]),
                     model.TaskChoice);
        Assert.Null(model.Draft.TaskId);
    });

    [Fact(DisplayName = "merkt sich die Datenbank nur, wenn sie eingegeben wurde")]
    public void RemembersTypedDatabaseOnly() => MainThread.Run(async () =>
    {
        var single = new FakeOdoo(FakeOdooMode.Odoo17);
        var first = Models.Make(single);
        await first.SignInAsync("odoo.example.de", single.ApiKey, single.Login, null);
        Assert.Equal("demo", first.Session?.Database);
        Assert.Null(first.Preferences.LoginHints?.Database);

        var several = new FakeOdoo(FakeOdooMode.Odoo17) { Databases = ["demo", "test"] };
        var second = Models.Make(several);
        await second.SignInAsync("odoo.example.de", several.ApiKey, several.Login, " test ");
        Assert.Equal("test", second.Preferences.LoginHints?.Database);
    });

    [Fact(DisplayName = "Link „In Odoo öffnen“ je nach Version")]
    public void WebLink() => MainThread.Run(async () =>
    {
        var modern = await Models.SignedIn(new FakeOdoo(FakeOdooMode.Odoo19));
        Assert.Equal("https://odoo.example.de/odoo/timesheets", modern.OdooWebUrl);
        var legacy = await Models.SignedIn(new FakeOdoo(FakeOdooMode.Odoo17));
        Assert.Equal("https://odoo.example.de/web#action=hr_timesheet.act_hr_timesheet_line", legacy.OdooWebUrl);
        Assert.Equal("Odoo 17 · JSON-RPC", legacy.InterfaceDisplay);
    });
}
