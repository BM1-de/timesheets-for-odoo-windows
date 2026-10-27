// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Tests;

/// <summary>
/// Projekt-Timer. Windows zeigt keine Zeit neben dem Symbol: Wo die Swift-Tests den Text der Menüleiste lesen, prüfen
/// diese Tests dieselbe Zahl über <see cref="AppModel.TodayProjectMinutes"/> oder eine Zeile im Tooltip; die Stoppuhr
/// der Menüleiste entspricht dem grünen Punkt (<see cref="TrayContent.ShowsDot"/>).
/// </summary>
public sealed class TimerTests
{
    /// <summary>Kundenmagazin › Layout mit Beschreibung, ohne Dauer.</summary>
    private static async Task FillDraft(AppModel model)
    {
        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        await model.LoadTasksAsync(3);
        model.SelectTask(32);
        model.DraftDescription = "Titelseite";
    }

    [Fact(DisplayName = "starten, die Menüleiste zählt mit, Stoppen bucht gerundet")]
    public void StartAndStop() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        model.Preferences.DurationRounding = DurationRounding.QuarterHour;
        await FillDraft(model);
        model.DraftDurationText = "0:30";
        Assert.False(model.CanStartTimer);
        model.DraftDurationText = "";
        Assert.True(model.CanStartTimer);
        Assert.Equal(0, model.TodayProjectMinutes);

        await model.StartTimerAsync();
        var timer = Expect.Require(model.RunningTimer);
        Assert.Equal("Kundenmagazin (Beispiel AG) › Layout", timer.Path);
        Assert.Equal("Titelseite", timer.Description);
        Assert.Equal(TestClock.Now, timer.Start);
        Assert.Empty(model.Draft.Description);
        Assert.Equal(3, model.Draft.ProjectId);
        Assert.Equal(32, model.Draft.TaskId);
        Assert.Equal(0, model.TodayProjectMinutes);
        Assert.True(model.TrayContent.ShowsDot);
        Assert.Equal("seit 14:52", model.TimerStartText);

        clock.Advance(42 * 60 + 17);
        await model.TickAsync();
        Assert.Equal(42 * 60 + 17, model.TimerSeconds());
        Assert.Equal(42, model.TodayProjectMinutes);
        Assert.StartsWith($"{AppInfo.Name}\nLäuft 0:42 seit 14:52 · Kundenmagazin › Layout\nProjektzeit heute 0:42 Std.",
                          model.TrayContent.ToolTip);

        model.TimerDescription = "Titelseite und Inhalt";
        await model.StopTimerAsync();
        Assert.Null(model.RunningTimer);
        Assert.Null(model.TimerError);
        Assert.Equal("0:45 Std. gebucht auf Kundenmagazin (Beispiel AG) › Layout, aus 0:42 gerundet.", model.TimerNotice?.Text);
        var line = Expect.Require(odoo.WithLock(() => odoo.Lines.FirstOrDefault()));
        Assert.Equal("Titelseite und Inhalt", line.Name);
        Assert.Equal(3, line.ProjectId);
        Assert.Equal(32, line.TaskId);
        Assert.Equal(0.75, line.Hours);
        Assert.Equal("2026-10-23", line.Date);
        Assert.Equal([45], model.Entries.Select(entry => entry.Minutes));
        // Ohne laufende Zeit zählt die Projektzeit von heute.
        Assert.Equal(45, model.TodayProjectMinutes);
        Assert.False(model.TrayContent.ShowsDot);
    });

    [Fact(DisplayName = "unter einer Minute: Stoppen behält die Zeit fürs Formular, ein neuer Start verwirft sie; Verwerfen fragt erst ab einer Minute")]
    public void ShortAndDiscarded() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        await FillDraft(model);

        await model.StartTimerAsync();
        clock.Advance(50);
        await model.StopTimerAsync();
        Assert.Equal(TestClock.Now.AddSeconds(50), model.RunningTimer?.End);
        Assert.Null(model.TimerNotice);
        Assert.Equal(TimerEditing.Booking, model.TimerEditing);
        Assert.Equal("0:01", model.Draft.DurationText);
        Assert.Equal(3, model.Draft.ProjectId);
        Assert.Equal(32, model.Draft.TaskId);
        model.CancelEditing();
        Assert.Equal(StoppedTimerReason.Short, model.StoppedTimerReason);
        Assert.False(model.CanStartTimer);
        model.RequestDiscardTimer();
        Assert.Null(model.PendingDeletion);
        Assert.Null(model.RunningTimer);
        Assert.Equal("Laufende Zeit verworfen, nichts gebucht.", model.TimerNotice?.Text);

        await model.StartTimerAsync();
        clock.Advance(30);
        await model.StartTimerAsync();
        Assert.Equal("Weniger als eine Minute, nichts gebucht.", model.TimerNotice?.Text);
        Assert.Equal(TestClock.Now.AddSeconds(80), model.RunningTimer?.Start);
        model.RequestDiscardTimer();
        Assert.Null(model.RunningTimer);

        model.DraftDescription = "Titelseite";
        await model.StartTimerAsync();
        clock.Advance(5 * 60);
        model.RequestDiscardTimer();
        Assert.Equal(Deletion.RunningTimer, model.PendingDeletion);
        Assert.Equal("Laufende Zeit verwerfen?", model.DeletionTitle);
        Assert.Equal("0:05 Std. für „Titelseite“ werden nicht gebucht.", model.DeletionMessage);
        Assert.Equal("Verwerfen", model.DeletionConfirmTitle);
        model.CancelDeletion();
        Assert.NotNull(model.RunningTimer);
        model.RequestDiscardTimer();
        await model.ConfirmDeletionAsync();
        Assert.Null(model.RunningTimer);
        Assert.True(odoo.WithLock(() => odoo.Lines.Count == 0));
    });

    [Fact(DisplayName = "ein Timer zur Zeit: erneut starten bucht die laufende Zeit bis zum neuen Start")]
    public void SwitchesTimer() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.5)]);
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        await FillDraft(model);
        await model.StartTimerAsync();
        clock.Advance(10 * 60);

        var entry = Expect.Require(model.Entries.FirstOrDefault());
        Assert.True(model.CanStartTimerLike(entry));
        await model.StartTimerLikeAsync(entry);
        var timer = Expect.Require(model.RunningTimer);
        Assert.Equal(2, timer.ProjectId);
        Assert.Equal(21, timer.TaskId);
        Assert.Equal("Plugin-Updates", timer.Description);
        Assert.Equal(clock.Now, timer.Start);
        Assert.Equal("0:10 Std. gebucht auf Kundenmagazin (Beispiel AG) › Layout.", model.TimerNotice?.Text);
        Assert.Equal([0.5, 10.0 / 60], odoo.WithLock(() => odoo.Lines.Select(line => line.Hours).ToList()));
        // Genau diese Zeit läuft schon.
        Assert.False(model.CanStartTimerLike(entry));
        Assert.Equal(2, model.RecentProjectIds.FirstOrDefault());
    });

    [Fact(DisplayName = "ohne Netz bleibt die Zeit gestoppt stehen; erneut versuchen bucht sie einmal")]
    public void StopOffline() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        await FillDraft(model);
        await model.StartTimerAsync();
        clock.Advance(20 * 60);

        odoo.WithLock(() => odoo.Offline = true);
        await model.StopTimerAsync();
        Assert.Equal("Keine Verbindung zu Odoo. Deine Angaben bleiben erhalten.", model.TimerError);
        Assert.Equal(clock.Now, model.RunningTimer?.End);
        clock.Advance(5 * 60);
        await model.TickAsync();
        Assert.Equal(20 * 60, model.TimerSeconds());
        // Swift: Bedienungshilfe „Projektzeit 20 Minuten noch nicht gebucht“; unter Windows die Zeile im Tooltip.
        Assert.Contains("\nGestoppt bei 0:20 · Kundenmagazin › Layout\n", model.TrayContent.ToolTip);
        // Starten bucht erst die gestoppte Zeit; ohne Netz startet nichts Neues.
        var entry = new TimesheetEntry(9, "Wartung", new Reference(2, "Website-Betreuung"), new Reference(21, "Wartung"), 15);
        await model.StartTimerLikeAsync(entry);
        Assert.Equal(3, model.RunningTimer?.ProjectId);

        odoo.WithLock(() =>
        {
            odoo.Offline = false;
            odoo.LoseNextCreateResponse = true;
        });
        await model.StopTimerAsync();
        Assert.NotNull(model.TimerError);
        Assert.Equal(1, odoo.WithLock(() => odoo.Lines.Count));
        await model.StopTimerAsync();
        Assert.Null(model.TimerError);
        Assert.Null(model.RunningTimer);
        Assert.Equal([20.0 / 60], odoo.WithLock(() => odoo.Lines.Select(line => line.Hours).ToList()));
    });

    [Fact(DisplayName = "über Mitternacht bucht auf den Tag, an dem die Zeit begann")]
    public void OverMidnight() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        await FillDraft(model);
        await model.StartTimerAsync();
        clock.Advance(10 * 3600);
        await model.TickAsync();
        Assert.Equal("Heute", model.DayTitle);
        Assert.Equal("seit Fr. 23.10. 14:52", model.TimerStartText);
        await model.StopTimerAsync();
        Assert.Equal(["2026-10-23"], odoo.WithLock(() => odoo.Lines.Select(line => line.Date).ToList()));
        Assert.Equal([10.0], odoo.WithLock(() => odoo.Lines.Select(line => line.Hours).ToList()));
    });

    [Fact(DisplayName = "Starten im Formular nur heute und nicht beim Bearbeiten")]
    public void StartOnlyToday() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.5)]);
        var model = await Models.SignedIn(odoo, clock: new MovingClock());
        await FillDraft(model);
        Assert.True(model.OffersTimerStart);
        model.ShowPreviousDay();
        Assert.False(model.OffersTimerStart);
        Assert.False(model.CanStartTimer);
        model.ShowToday();
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        Assert.False(model.OffersTimerStart);
    });

    [Fact(DisplayName = "ohne Projekt starten; beim Stoppen im Formular zuordnen und buchen")]
    public void StartWithoutProject() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        model.Preferences.DurationRounding = DurationRounding.QuarterHour;
        // Im leeren Formular startet „Starten“ ohne Projekt.
        Assert.Null(model.Draft.ProjectId);
        Assert.True(model.CanStartTimer);

        await model.StartTimerAsync();
        var timer = Expect.Require(model.RunningTimer);
        Assert.Null(timer.ProjectId);
        Assert.Null(timer.TaskId);
        Assert.Empty(timer.Description);
        Assert.Equal("Projekt fehlt", model.TimerPathText);
        // Solange sie ohne Projekt läuft, startet keine neue, auch nicht mit einem Eintrag im Formular.
        await FillDraft(model);
        Assert.False(model.CanStartTimer);

        clock.Advance(42 * 60);
        await model.StopTimerAsync();
        Assert.True(odoo.WithLock(() => odoo.Lines.Count == 0));
        Assert.Equal(TimerEditing.Booking, model.TimerEditing);
        Assert.Equal(clock.Now, model.RunningTimer?.End);
        Assert.Null(model.Draft.ProjectId);
        Assert.Equal("0:42", model.Draft.DurationText);
        Assert.False(model.CanSave);
        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        await model.LoadTasksAsync(3);
        model.SelectTask(32);
        model.DraftDescription = "Satz";
        Assert.True(model.CanSave);
        await model.SaveAsync();
        Assert.Null(model.RunningTimer);
        Assert.Null(model.TimerEditing);
        var line = Expect.Require(odoo.WithLock(() => odoo.Lines.FirstOrDefault()));
        Assert.Equal(3, line.ProjectId);
        Assert.Equal(32, line.TaskId);
        Assert.Equal("Satz", line.Name);
        Assert.Equal(0.75, line.Hours);
        Assert.Equal("2026-10-23", line.Date);
        Assert.Equal("0:45 Std. gebucht auf Kundenmagazin (Beispiel AG) › Layout.", model.TimerNotice?.Text);
        // Was im Formular für einen neuen Eintrag stand, ist wieder da.
        Assert.Equal("Titelseite", model.Draft.Description);
        Assert.Equal(3, model.Draft.ProjectId);
    });

    [Fact(DisplayName = "Projekt, Aufgabe und Beschreibung lassen sich ändern, solange die Zeit läuft")]
    public void EditWhileRunning() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        await model.StartTimerAsync();
        await FillDraft(model);

        model.BeginEditingTimer();
        Assert.Equal(TimerEditing.Running, model.TimerEditing);
        Assert.Null(model.Draft.ProjectId);
        Assert.Empty(model.Draft.Description);
        Assert.False(model.OffersTimerStart);
        Assert.True(model.CanSave);
        model.SelectProject(model.Projects.Single(project => project.Id == 3));
        await model.LoadTasksAsync(3);
        Assert.False(model.CanSave);
        model.SelectTask(32);
        model.DraftDescription = " Satz ";
        await model.SaveAsync();
        Assert.Null(model.TimerEditing);
        var timer = Expect.Require(model.RunningTimer);
        Assert.Equal(3, timer.ProjectId);
        Assert.Equal(32, timer.TaskId);
        Assert.Equal("Satz", timer.Description);
        Assert.Null(timer.End);
        Assert.Equal("Kundenmagazin (Beispiel AG) › Layout", model.TimerPathText);
        Assert.Equal("Titelseite", model.Draft.Description);

        clock.Advance(30 * 60);
        await model.StopTimerAsync();
        Assert.Null(model.RunningTimer);
        Assert.Equal([0.5], odoo.WithLock(() => odoo.Lines.Select(line => line.Hours).ToList()));
    });

    [Fact(DisplayName = "ohne Projekt gestoppt: Abbrechen lässt die Zeit stehen, Verwerfen räumt auf")]
    public void StoppedWithoutProject() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.5)]);
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        await model.StartTimerAsync();
        await FillDraft(model);
        var entry = Expect.Require(model.Entries.FirstOrDefault());
        Assert.False(model.CanStartTimerLike(entry));
        clock.Advance(20 * 60);
        await model.StopTimerAsync();
        Assert.Equal(TimerEditing.Booking, model.TimerEditing);
        model.CancelEditing();
        Assert.Null(model.TimerEditing);
        Assert.NotNull(model.RunningTimer?.End);
        Assert.Equal("Titelseite", model.Draft.Description);

        // Ein Klick auf die Zeit öffnet sie wieder zum Buchen, mit der Dauer bis zum Stopp.
        clock.Advance(5 * 60);
        model.BeginEditingTimer();
        Assert.Equal(TimerEditing.Booking, model.TimerEditing);
        Assert.Equal("0:20", model.Draft.DurationText);
        model.RequestDiscardTimer();
        await model.ConfirmDeletionAsync();
        Assert.Null(model.RunningTimer);
        Assert.Null(model.TimerEditing);
        Assert.Equal("Titelseite", model.Draft.Description);
        Assert.Equal(1, odoo.WithLock(() => odoo.Lines.Count));
    });

    [Fact(DisplayName = "fehlt die Aufgabe, obwohl das Projekt welche hat, kommt die Zeit beim Stoppen ins Formular")]
    public void StopWithoutRequiredTask() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var model = await Models.SignedIn(odoo, clock: clock);
        var account = Expect.Require(model.Session?.SecretAccount);
        model.Preferences.RunningTimer = new RunningTimer(account, 3, "Kundenmagazin (Beispiel AG)", null, null, "Satz", clock.Now);
        clock.Advance(15 * 60);
        await model.StopTimerAsync();
        Assert.True(odoo.WithLock(() => odoo.Lines.Count == 0));
        Assert.Equal(TimerEditing.Booking, model.TimerEditing);
        Assert.Equal(3, model.Draft.ProjectId);
        Assert.Equal("0:15", model.Draft.DurationText);
        model.SelectTask(32);
        await model.SaveAsync();
        Assert.Equal([32], odoo.WithLock(() => odoo.Lines.Select(line => line.TaskId).ToList()));
        Assert.Null(model.RunningTimer);
    });

    [Fact(DisplayName = "übersteht einen Neustart; Abmelden verwirft die Zeit")]
    public void SurvivesRestart() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var clock = new MovingClock();
        var settings = new MemorySettingsStore();
        var secrets = new MemorySecretStore();
        var first = await Models.SignedIn(odoo, clock: clock, settings: settings, secrets: secrets);
        await FillDraft(first);
        await first.StartTimerAsync();
        clock.Advance(3 * 60);

        var second = Models.Make(odoo, secrets, clock: clock, settings: settings);
        await second.StartAsync();
        try
        {
            Assert.Equal("Titelseite", second.RunningTimer?.Description);
            Assert.Equal(3 * 60, second.TimerSeconds());
            Assert.Equal(3, second.TodayProjectMinutes);

            second.SignOut();
            Assert.Null(second.Preferences.RunningTimer);
        }
        finally
        {
            second.Stop();
        }
    });
}
