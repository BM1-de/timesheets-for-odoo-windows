// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Tests;

/// <summary>Bearbeiten und Löschen</summary>
public sealed class EditingTests
{
    private static FakeOdoo.Line? Line(FakeOdoo odoo, int id) =>
        odoo.WithLock(() => odoo.Lines.FirstOrDefault(line => line.Id == id));

    private static FakeOdoo.Attendance? Attendance(FakeOdoo odoo, int id) =>
        odoo.WithLock(() => odoo.Attendances.FirstOrDefault(attendance => attendance.Id == id));

    // Zeiteinträge

    [Theory(DisplayName = "ein Eintrag öffnet sich im Formular; danach kommt der angefangene neue zurück")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void EditsEntry(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var model = await Models.SignedIn(odoo);
        model.DraftDescription = "angefangen";

        var entry = Expect.Require(model.Entries.FirstOrDefault());
        model.BeginEditing(entry);
        await model.LoadTasksAsync(2);
        Assert.Equal(1, model.EditingEntry?.Id);
        Assert.Equal(new Draft(2, 21, "Plugin-Updates", "0:15"), model.Draft);
        Assert.Equal(new CalendarDay(2026, 10, 23), model.EditDay);

        model.DraftDescription = "Plugin-Updates und Sicherung";
        model.DraftDurationText = "0:45";
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Null(model.EditingEntry);
        Assert.Equal("angefangen", model.Draft.Description);
        Assert.Equal("Plugin-Updates und Sicherung", Line(odoo, 1)?.Name);
        Assert.Equal(0.75, Line(odoo, 1)?.Hours);
        Assert.Equal([45], model.Entries.Select(item => item.Minutes));
        Assert.Contains("account.analytic.line.write", odoo.Log);
    });

    [Fact(DisplayName = "Abbrechen lässt den Eintrag, wie er ist")]
    public void CancelsEditing() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var model = await Models.SignedIn(odoo);
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        model.DraftDescription = "verworfen";
        model.CancelEditing();
        Assert.Null(model.EditingEntry);
        Assert.Equal(new Draft(), model.Draft);
        Assert.Equal("Plugin-Updates", Line(odoo, 1)?.Name);
        Assert.DoesNotContain("account.analytic.line.write", odoo.Log);
    });

    [Fact(DisplayName = "eine unveränderte Dauer bleibt minutengenau, eine geänderte wird gerundet")]
    public void KeepsExactDuration() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Telefonat", 2, 21, 47.0 / 60)]);
        var model = await Models.SignedIn(odoo);
        model.Preferences.DurationRounding = DurationRounding.QuarterHour;
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        await model.LoadTasksAsync(2);
        Assert.Equal("0:47", model.Draft.DurationText);
        Assert.Equal(47, model.DraftMinutes);
        Assert.Null(model.DurationHint);
        model.DraftDurationText = "0:50";
        Assert.Equal(45, model.DraftMinutes);
        model.DraftDurationText = "0:47";
        await model.SaveAsync();
        Assert.True(Math.Abs((Line(odoo, 1)?.Hours ?? 0) - 47.0 / 60) < 0.0001);
    });

    [Fact(DisplayName = "mit neuem Datum wandert der Eintrag auf den anderen Tag")]
    public void MovesToAnotherDay() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var model = await Models.SignedIn(odoo);
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        await model.LoadTasksAsync(2);
        Assert.Equal(7, model.EditDayChoices.Count);
        model.EditDay = new CalendarDay(2026, 10, 22);
        Assert.Equal("Gestern, Do. 22.10.", model.DayChoiceTitle(model.EditDay));
        await model.SaveAsync();
        Assert.Equal("2026-10-22", Line(odoo, 1)?.Date);
        Assert.Empty(model.Entries);
        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        Assert.Equal([1], model.Entries.Select(entry => entry.Id));
    });

    [Fact(DisplayName = "die erledigte Aufgabe eines Eintrags bleibt wählbar; ohne Aufgabe gebucht darf er so bleiben")]
    public void KeepsClosedTaskAndMissingTask() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() =>
        {
            odoo.ClosedTasks = new() { [33] = "Korrektur" };
            odoo.Lines =
            [
                Fake.Line(1, "2026-10-23", "Satzfehler", 3, 33, 1),
                Fake.Line(2, "2026-10-23", "Abstimmung", 3, null, 0.5),
            ];
        });
        var model = await Models.SignedIn(odoo);
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault(entry => entry.Id == 1)));
        await model.LoadTasksAsync(3);
        // Swift: guard case .tasks(let tasks) = model.taskChoice else { Issue.record("keine Aufgaben"); return }
        var choice = model.TaskChoice;
        Assert.Equal(TaskChoiceKind.Tasks, choice.Kind);
        Assert.Equal(["Korrektur", "Text", "Layout"], (choice.Tasks ?? []).Select(task => task.Name));
        Assert.Equal(33, model.Draft.TaskId);
        Assert.Equal("Korrektur", model.SelectedTaskName);
        Assert.True(model.CanSave);

        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault(entry => entry.Id == 2)));
        await model.LoadTasksAsync(3);
        Assert.Null(model.Draft.TaskId);
        Assert.False(model.TaskIsRequired);
        Assert.True(model.CanSave);
        model.DraftDescription = "Abstimmung mit dem Verlag";
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Null(Line(odoo, 2)?.TaskId);
        Assert.Equal("Abstimmung mit dem Verlag", Line(odoo, 2)?.Name);
    });

    [Theory(DisplayName = "abgerechnet: nur die Beschreibung lässt sich ändern, löschen nicht")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void InvoicedEntry(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Beiträge", 2, 21, 2.5, invoiced: true)]);
        var model = await Models.SignedIn(odoo);
        var entry = Expect.Require(model.Entries.FirstOrDefault());
        Assert.Equal(EntryLock.Invoiced, entry.Lock);
        model.BeginEditing(entry);
        Assert.True(model.EditingIsLimited);
        Assert.True(model.CanSave);
        model.DraftDescription = "Beiträge für November";
        model.DraftDurationText = "1:00";
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal("Beiträge für November", Line(odoo, 1)?.Name);
        Assert.Equal(2.5, Line(odoo, 1)?.Hours);

        model.AskToDelete(new Deletion(DeletionKind.Entry, Expect.Require(model.Entries.FirstOrDefault())));
        Assert.Null(model.PendingDeletion);
    });

    [Theory(DisplayName = "Löschen erst nach der Rückfrage")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void DeletesEntry(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var model = await Models.SignedIn(odoo);
        var entry = Expect.Require(model.Entries.FirstOrDefault());
        model.BeginEditing(entry);
        model.AskToDelete(new Deletion(DeletionKind.Entry, entry));
        Assert.Equal("Eintrag löschen?", model.DeletionTitle);
        Assert.Equal("„Plugin-Updates“ (0:15 Std.) wird in Odoo gelöscht.", model.DeletionMessage);
        model.CancelDeletion();
        Assert.NotNull(Line(odoo, 1));

        model.AskToDelete(new Deletion(DeletionKind.Entry, entry));
        await model.ConfirmDeletionAsync();
        Assert.Null(model.PendingDeletion);
        Assert.Null(Line(odoo, 1));
        Assert.Empty(model.Entries);
        Assert.Null(model.EditingEntry);
    });

    [Fact(DisplayName = "ein anderer Tag beendet das Bearbeiten")]
    public void NavigationCancelsEditing() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var model = await Models.SignedIn(odoo);
        model.DraftDurationText = "1:00";
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        model.ShowPreviousDay();
        Assert.Null(model.EditingEntry);
        Assert.Equal("1:00", model.Draft.DurationText);
    });

    [Fact(DisplayName = "lehnt Odoo die Änderung ab, bleibt das Formular mit Fehler offen")]
    public void EditFailure() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Lines = [Fake.Line(1, "2026-10-23", "Plugin-Updates", 2, 21, 0.25)]);
        var model = await Models.SignedIn(odoo);
        model.BeginEditing(Expect.Require(model.Entries.FirstOrDefault()));
        await model.LoadTasksAsync(2);
        model.DraftDescription = "neu";
        odoo.WithLock(() => odoo.FailNextWrite = true);
        await model.SaveAsync();
        Assert.NotNull(model.SaveError);
        Assert.Equal(1, model.EditingEntry?.Id);
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal("neu", Line(odoo, 1)?.Name);
    });

    // Anwesenheiten

    /// <summary>Heute 08:12–12:05 und seit 12:47 (Ortszeit).</summary>
    private static void Today(FakeOdoo odoo) => odoo.WithLock(() => odoo.Attendances =
    [
        Fake.Attendance(1, "2026-10-23 06:12:00", "2026-10-23 10:05:00"),
        Fake.Attendance(2, "2026-10-23 10:47:00", null),
    ]);

    private static AttendancePeriod Period(AppModel model, int id) =>
        Expect.Require(model.ShownPeriods.FirstOrDefault(period => period.Id == id));

    [Fact(DisplayName = "Zeiten des Tages mit Pausen dazwischen")]
    public void ListsPeriodsAndGaps() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        var summary = model.AttendanceSummary;
        Assert.Equal(["08:12 – 12:05", "Pause 12:05 – 12:47", "12:47 – jetzt"], summary.Rows.Select(row => row.Range));
        Assert.Equal(["3:53", "0:42", "2:05"], summary.Rows.Select(row => row.Duration));
        Assert.True(summary.CanEdit);
        Assert.Null(summary.Attention);
        Assert.False(model.ShowsAttendanceList);
        model.SetAttendanceListExpanded(true);
        Assert.True(model.ShowsAttendanceList);
    });

    [Theory(DisplayName = "Beginn korrigieren")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void CorrectsStart(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        model.EditPeriod(Period(model, 1));
        Assert.Equal("08:12", model.AttendanceEditor?.From);
        Assert.Equal("12:05", model.AttendanceEditor?.To);
        model.SetEditorFrom("800");
        Assert.Equal("4:05 Std.", model.AttendanceEditorDuration);
        await model.SaveAttendanceAsync();
        Assert.Null(model.AttendanceEditor);
        Assert.Equal("2026-10-23 06:00:00", Attendance(odoo, 1)?.CheckIn);
        Assert.Equal("2026-10-23 10:05:00", Attendance(odoo, 1)?.CheckOut);
        Assert.Equal(new StatusNotice(NoticeLevel.Done, "Zeitraum gesichert."), model.AttendanceNotice);
        Assert.Equal("08:00 – 12:05", model.AttendanceSummary.Rows.FirstOrDefault()?.Range);
    });

    [Fact(DisplayName = "prüft Eingaben wie Odoo, bevor etwas gesendet wird")]
    public void ValidatesInput() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        (string From, string To, string Error)[] checks =
        [
            ("acht", "12:05", "Bitte Uhrzeiten wie 8:30 oder 830 eingeben."),
            ("08:12", "7:00", "Das Ende muss nach dem Beginn liegen."),
            ("08:12", "13:00", "Überschneidet sich mit 12:47 – jetzt."),
            ("08:12", "15:30", "Die Zeit liegt in der Zukunft."),
        ];
        // Wie #expect in der Schleife: alle Fälle prüfen, dann gemeinsam melden.
        var errors = new List<string?>();
        foreach (var check in checks)
        {
            model.EditPeriod(Period(model, 1));
            model.SetEditorFrom(check.From);
            model.SetEditorTo(check.To);
            await model.SaveAttendanceAsync();
            errors.Add(model.AttendanceEditor?.Error);
            model.CancelAttendanceEditing();
        }
        Assert.Equal(checks.Select(check => (string?)check.Error), errors);
        Assert.DoesNotContain("hr.attendance.write", odoo.Log);
    });

    [Fact(DisplayName = "eine Pause nach hinten schieben: erst der spätere Teil, sonst überschnitte sich das")]
    public void MovesBreakLater() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        var gap = Expect.Require(model.AttendanceSummary.Rows.FirstOrDefault(row => row.Kind == AttendanceRowKind.Gap));
        model.EditGap(Expect.Require(gap.Before), Expect.Require(gap.After));
        Assert.Equal("12:05", model.AttendanceEditor?.From);
        Assert.Equal("12:47", model.AttendanceEditor?.To);
        Assert.Equal("Sichern ändert das Ende des Zeitraums davor und den Beginn des Zeitraums danach.",
                     model.AttendanceEditorHint?.Text);
        model.SetEditorFrom("13:00");
        model.SetEditorTo("13:30");
        Assert.Equal("30 Min.", model.AttendanceEditorDuration);
        await model.SaveAttendanceAsync();
        Assert.Null(model.AttendanceEditor);
        Assert.Equal("2026-10-23 11:00:00", Attendance(odoo, 1)?.CheckOut);
        Assert.Equal("2026-10-23 11:30:00", Attendance(odoo, 2)?.CheckIn);
        Assert.Equal("Pause verschoben.", model.AttendanceNotice?.Text);
    });

    [Fact(DisplayName = "eine Pause nach vorn schieben: erst der frühere Teil")]
    public void MovesBreakEarlier() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        model.EditGap(Period(model, 1), Period(model, 2));
        model.SetEditorFrom("11:30");
        model.SetEditorTo("12:00");
        await model.SaveAttendanceAsync();
        Assert.Null(model.AttendanceEditor);
        Assert.Equal("2026-10-23 09:30:00", Attendance(odoo, 1)?.CheckOut);
        Assert.Equal("2026-10-23 10:00:00", Attendance(odoo, 2)?.CheckIn);

        model.EditGap(Period(model, 1), Period(model, 2));
        model.SetEditorFrom("7:00");
        model.SetEditorTo("12:00");
        await model.SaveAttendanceAsync();
        Assert.Equal("Die Pause muss zwischen 08:12 und jetzt liegen.", model.AttendanceEditor?.Error);
    });

    [Theory(DisplayName = "an einem Tag ohne Anwesenheit nachtragen")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void AddsMissingDay(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        var model = await Models.SignedIn(odoo);
        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        Assert.True(model.AttendanceSummary.OffersAdding);
        model.AddAttendancePeriod();
        model.SetEditorFrom("8");
        model.SetEditorTo("12:30");
        await model.SaveAttendanceAsync();
        Assert.Null(model.AttendanceEditor);
        var added = Expect.Require(odoo.WithLock(() => odoo.Attendances.FirstOrDefault()));
        Assert.Equal("2026-10-22 06:00:00", added.CheckIn);
        Assert.Equal("2026-10-22 10:30:00", added.CheckOut);
        Assert.Equal("Zeitraum nachgetragen.", model.AttendanceNotice?.Text);
        Assert.Equal("Anwesend 08:00 – 12:30", model.AttendanceSummary.Title);
    });

    [Theory(DisplayName = "Zeitraum löschen nach Rückfrage")]
    [InlineData(FakeOdooMode.Odoo19)]
    [InlineData(FakeOdooMode.Odoo18)]
    [InlineData(FakeOdooMode.Odoo17)]
    public void DeletesPeriod(FakeOdooMode mode) => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo(mode);
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        var first = Period(model, 1);
        model.EditPeriod(first);
        model.AskToDelete(new Deletion(DeletionKind.Period, Period: first));
        Assert.Equal("Zeitraum löschen?", model.DeletionTitle);
        Assert.Equal("08:12 – 12:05 wird in Odoo gelöscht.", model.DeletionMessage);
        await model.ConfirmDeletionAsync();
        Assert.Null(Attendance(odoo, 1));
        Assert.Null(model.AttendanceEditor);
        Assert.Equal("Zeitraum gelöscht.", model.AttendanceNotice?.Text);
        Assert.Equal([2], model.ShownPeriods.Select(period => period.Id));
    });

    [Fact(DisplayName = "automatisch ausgestempelt: aufgeklappt, nach dem Korrigieren bestätigt")]
    public void CorrectsAutomaticCheckOut() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() => odoo.Attendances =
            [Fake.Attendance(5, "2026-10-22 06:00:00", "2026-10-22 16:00:00", "manual", "auto_check_out")]);
        var model = await Models.SignedIn(odoo);
        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        Assert.Equal("Automatisch beendet", model.AttendanceSummary.Attention);
        Assert.True(model.ShowsAttendanceList);
        model.EditPeriod(Period(model, 5));
        model.SetEditorTo("16:30");
        await model.SaveAttendanceAsync();
        Assert.Equal("2026-10-22 14:30:00", Attendance(odoo, 5)?.CheckOut);
        Assert.Equal("manual", Attendance(odoo, 5)?.OutMode);
        Assert.Null(model.AttendanceSummary.Attention);
    });

    [Fact(DisplayName = "ohne das Recht dazu nur Anzeige, mit Hinweis")]
    public void ReadOnlyAttendance() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo { CanStamp = false };
        odoo.WithLock(() => odoo.Attendances = [Fake.Attendance(5, "2026-10-22 06:00:00", "2026-10-22 14:00:00")]);
        var model = await Models.SignedIn(odoo);
        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        var summary = model.AttendanceSummary;
        Assert.False(summary.CanEdit);
        Assert.Equal("Deine Anwesenheiten kannst du hier nicht ändern. Korrekturen trägt die für Anwesenheiten zuständige Person in Odoo ein.",
                     summary.Note);
        model.EditPeriod(Period(model, 5));
        Assert.Null(model.AttendanceEditor);
        model.AddAttendancePeriod();
        Assert.Null(model.AttendanceEditor);
    });

    [Fact(DisplayName = "lehnt Odoo die Korrektur ab, verschwindet das Bearbeiten bis zur erneuten Prüfung")]
    public void RefusedCorrection() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        Today(odoo);
        var model = await Models.SignedIn(odoo);
        odoo.WithLock(() => odoo.StampDeniedByRule = true);
        model.EditPeriod(Period(model, 1));
        model.SetEditorFrom("8:00");
        await model.SaveAttendanceAsync();
        Assert.False(model.Capabilities?.CanStamp);
        Assert.True(model.Preferences.StampingRefused);
        Assert.Null(model.AttendanceEditor);
        Assert.Equal(NoticeLevel.Warning, model.AttendanceNotice?.Level);
        Assert.False(model.CanEditAttendance);
    });

    // Modul Arbeitszeitgesetz

    [Fact(DisplayName = "Modul Arbeitszeitgesetz: eingefügte Pause erkennen, verschieben, Odoo ergänzt den Rest")]
    public void BreakModule() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        odoo.WithLock(() =>
        {
            odoo.BreakModule = true;
            // Donnerstag 07:45–12:00 und, von Odoo angelegt, 12:30–17:00
            odoo.Attendances =
            [
                Fake.Attendance(11, "2026-10-22 05:45:00", "2026-10-22 10:00:00", "manual", "manual"),
                Fake.Attendance(12, "2026-10-22 10:30:00", "2026-10-22 15:00:00", "technical", "manual"),
            ];
        });
        var model = await Models.SignedIn(odoo);
        Assert.Equal(new BreakRules(), model.Capabilities?.Schema.BreakRules);
        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        var summary = model.AttendanceSummary;
        Assert.Equal("Pause eingefügt", summary.Attention);
        var gap = Expect.Require(summary.Rows.FirstOrDefault(row => row.IsInsertedBreak));
        Assert.Equal(AttendanceRowKind.Gap, gap.Kind);

        model.EditGap(Expect.Require(gap.Before), Expect.Require(gap.After));
        Assert.Equal(new Hint("Passt: Bei 8:45 Std. Arbeit sind 30 Min. Pause nötig.", false), model.AttendanceEditorHint);
        model.SetEditorFrom("13:00");
        model.SetEditorTo("13:15");
        Assert.Equal(new Hint("Bei 9:00 Std. Arbeit sind 30 Min. Pause nötig. Odoo ergänzt beim Sichern 15 Min.", true),
                     model.AttendanceEditorHint);
        await model.SaveAttendanceAsync();
        Assert.Null(model.AttendanceEditor);
        Assert.Equal("manual", Attendance(odoo, 12)?.InMode);
        Assert.Equal(new StatusNotice(NoticeLevel.Info, "Gesichert. Odoo hat 15 Min. Pause eingefügt (10:15 – 10:30)."),
                     model.AttendanceNotice);
    });

    [Fact(DisplayName = "Uhrzeiten in den Feldern „von“ und „bis“")]
    public void ReadsClockTimes()
    {
        (string Text, int? Minutes)[] cases =
        [
            ("8:30", 510), ("08.30", 510), ("8h30", 510), ("830", 510), ("8", 480), ("17", 1020), (" 7:05 ", 425),
            ("8:30 AM", 510), ("12 pm", 720), ("12am", 0), ("5:15 p.m.", 1035),
            ("24:00", null), ("8:60", null), ("13 pm", null), ("acht", null), ("", null),
        ];
        // Wie #expect in der Schleife: jeder Fall wird geprüft und mit seinem Text gemeldet.
        Assert.Multiple(cases.Select(item => (Action)(() =>
        {
            var minutes = ClockInput.Minutes(item.Text);
            Assert.True(minutes == item.Minutes, $"„{item.Text}“: erwartet {item.Minutes?.ToString() ?? "null"}, erhalten {minutes?.ToString() ?? "null"}");
        })).ToArray());
    }
}
