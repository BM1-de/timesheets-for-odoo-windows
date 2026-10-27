// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Model;

public sealed partial class AppModel
{
    /// <summary>Heutige Anwesenheiten samt einer offenen vom Vortag.</summary>
    public IReadOnlyList<AttendancePeriod> TodayPeriodsIncludingOpen
    {
        get
        {
            if (OpenPeriod is not { } open || TodayPeriods.Any(period => period.Id == open.Id)) return TodayPeriods;
            return [open, .. TodayPeriods];
        }
    }

    public IReadOnlyList<AttendancePeriod> ShownPeriods => IsShowingToday ? TodayPeriodsIncludingOpen : Periods;

    public int AttendanceMinutes(IEnumerable<AttendancePeriod> list) => list.Sum(period => period.Minutes(Now));

    public int TodayAttendanceMinutes => AttendanceMinutes(TodayPeriodsIncludingOpen);

    public AttendanceSummary AttendanceSummary
    {
        get
        {
            var list = ShownPeriods;
            var total = AttendanceMinutes(list);
            var canEdit = CanEditAttendance;
            string title;
            string subtitle;
            var isRunning = false;
            string? buttonTitle = null;
            string? note = null;
            var offersAdding = false;
            if (IsShowingToday)
            {
                if (OpenPeriod is { } open)
                {
                    isRunning = true;
                    title = Loc.T("Anwesend seit %@", ClockTime(open.CheckIn));
                    var pause = PauseMinutes(list);
                    var totalText = DurationInput.Format(total);
                    subtitle = pause > 0
                        ? Loc.T("Heute %@ Std. · Pause %@", totalText, DurationInput.Format(pause))
                        : Loc.T("Heute %@ Std.", totalText);
                }
                else if (list.Count > 0 && list[^1].CheckOut is { } checkOut)
                {
                    title = Loc.T("Ausgestempelt um %@", ClockTime(checkOut));
                    subtitle = Loc.T("Heute %@ Std.", DurationInput.Format(total));
                }
                else
                {
                    title = Loc.T("Nicht eingestempelt");
                    subtitle = Loc.T("Heute noch keine Anwesenheit");
                }
                if (Capabilities?.CanStamp == true)
                {
                    buttonTitle = OpenPeriod is null ? Loc.T("Einstempeln") : Loc.T("Ausstempeln");
                }
                else
                {
                    var rights = Loc.T("Stempeln geht hier nur mit dem Odoo-Recht „Sachbearbeiter: Alle Anwesenheiten verwalten“ oder als eigener Anwesenheitsgenehmiger. Bis dahin in Odoo stempeln; Korrekturen trägt die für Anwesenheiten zuständige Person ein.");
                    note = Preferences.StampingRefused ? Loc.T("Odoo hat das Stempeln abgelehnt.") + " " + rights : rights;
                }
            }
            else if (list.Count > 0)
            {
                var end = list[^1].CheckOut is { } last ? ClockTime(last) : Loc.T("jetzt");
                title = Loc.T("Anwesend %@ – %@", ClockTime(list[0].CheckIn), end);
                subtitle = Loc.T("%@ Std.", DurationInput.Format(total));
                if (!canEdit)
                {
                    note = Loc.T("Deine Anwesenheiten kannst du hier nicht ändern. Korrekturen trägt die für Anwesenheiten zuständige Person in Odoo ein.");
                }
            }
            else
            {
                title = Loc.T("Keine Anwesenheit erfasst");
                subtitle = Day > Today ? Loc.T("Dieser Tag liegt in der Zukunft.") : Loc.T("An diesem Tag wurde nicht gestempelt.");
                offersAdding = canEdit && Day < Today;
            }
            var rows = AttendanceRows(list);
            string? attention = null;
            if (rows.Any(row => row.IsAutoCheckOut)) attention = Loc.T("Automatisch beendet");
            else if (rows.Any(row => row.IsInsertedBreak)) attention = Loc.T("Pause eingefügt");
            string? unbooked = null;
            if (TimesheetsActive && total > BookedMinutes) unbooked = Loc.T("%@ Std.", DurationInput.Format(total - BookedMinutes));
            return new AttendanceSummary
            {
                Title = title,
                Subtitle = subtitle,
                IsRunning = isRunning,
                ButtonTitle = buttonTitle,
                Rows = rows,
                UnbookedText = unbooked,
                Note = note,
                Attention = attention,
                CanEdit = canEdit,
                OffersAdding = offersAdding,
            };
        }
    }

    /// <summary>Anwesenheiten ändern, nachtragen und löschen darf, wer auch stempeln darf.</summary>
    public bool CanEditAttendance => AttendanceActive && Capabilities?.CanStamp == true;

    /// <summary>„Zeiten des Tages“: aufgeklappt, wenn gewünscht oder wenn etwas zu prüfen ist.</summary>
    public bool ShowsAttendanceList => IsAttendanceListExpanded ?? AttendanceSummary.Attention is not null;

    public void SetAttendanceListExpanded(bool expanded)
    {
        IsAttendanceListExpanded = expanded;
        if (!expanded) AttendanceEditor = null;
    }

    /// <summary>Die Anwesenheiten des Tages und dazwischen die Pausen.</summary>
    private List<AttendanceRow> AttendanceRows(IReadOnlyList<AttendancePeriod> list)
    {
        var hasBreakModule = Capabilities?.Schema.BreakRules is not null;
        var rows = new List<AttendanceRow>();
        for (var index = 0; index < list.Count; index++)
        {
            var period = list[index];
            if (index > 0 && list[index - 1].CheckOut is { } checkOut)
            {
                var minutes = (int)(period.CheckIn - checkOut).TotalMinutes;
                if (minutes > 0)
                {
                    rows.Add(new AttendanceRow(
                        $"pause-{list[index - 1].Id}-{period.Id}", AttendanceRowKind.Gap, null, list[index - 1], period,
                        Loc.T("Pause %@ – %@", ClockTime(checkOut), ClockTime(period.CheckIn)),
                        DurationInput.Format(minutes),
                        IsInsertedBreak: hasBreakModule && period.IsTechnical));
                }
            }
            rows.Add(new AttendanceRow(
                $"zeit-{period.Id}", AttendanceRowKind.Period, period, null, null, RangeText(period),
                DurationInput.Format(period.Minutes(Now)), IsAutoCheckOut: period.IsAutoCheckOut));
        }
        return rows;
    }

    /// <summary>„08:10 – 12:05“, bei einer laufenden Anwesenheit „08:10 – jetzt“.</summary>
    private string RangeText(AttendancePeriod period) =>
        $"{ClockTime(period.CheckIn)} – {(period.CheckOut is { } end ? ClockTime(end) : Loc.T("jetzt"))}";

    /// <summary>Uhrzeit in der Zeitzone, mit der die App auch die Tage rechnet.</summary>
    public string ClockTime(DateTimeOffset instant) => Formatting.Time(instant, zone);

    private static int PauseMinutes(IReadOnlyList<AttendancePeriod> list)
    {
        var sum = 0;
        for (var index = 1; index < list.Count; index++)
        {
            if (list[index - 1].CheckOut is not { } checkOut) continue;
            sum += Math.Max(0, (int)(list[index].CheckIn - checkOut).TotalMinutes);
        }
        return sum;
    }

    public async Task ToggleAttendanceAsync()
    {
        if (service is not { } current || Capabilities is not { } found || !found.CanStamp || !IsShowingToday || IsStamping) return;
        var token = sessionToken;
        IsStamping = true;
        StampError = null;
        try
        {
            if (OpenPeriod is { } open)
            {
                await current.CheckOutAsync(open.Id, clock());
            }
            else if (found.EmployeeId is { } employeeId)
            {
                await current.CheckInAsync(employeeId, clock());
            }
            else
            {
                throw OdooException.Rejected(Loc.T("Dein Odoo-Benutzer ist in deiner Hauptfirma mit keinem Mitarbeiter verknüpft."));
            }
            if (token != sessionToken) return;
            IsStamping = false;
            await RefreshAsync(force: true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return;
            IsStamping = false;
            switch ((error as OdooException)?.Kind)
            {
                case OdooErrorKind.AccessDenied:
                    MarkStampingRefused();
                    break;
                case OdooErrorKind.Unauthorized:
                    StampError = error.Message;
                    Handle(error);
                    break;
                case OdooErrorKind.Offline:
                    StampError = error.Message;
                    break;
                default:
                    StampError = error.Message;
                    // Odoo hat abgelehnt, etwa weil die Anzeige veraltet war: den Stand aus Odoo holen.
                    await RefreshAsync(force: true);
                    break;
            }
        }
    }

    /// <summary>
    /// Manche Rechte prüft Odoo erst beim Speichern. Stempeln und Korrigieren bleiben dann weg, bis die Rechte in den
    /// Einstellungen erneut geprüft werden; der Hinweis erklärt, warum.
    /// </summary>
    private void MarkStampingRefused()
    {
        Preferences.StampingRefused = true;
        if (Capabilities is { } found) Capabilities = found with { CanStamp = false };
        Preferences.CachedCapabilities = Capabilities;
        AttendanceEditor = null;
    }

    // Anwesenheit korrigieren

    public void EditPeriod(AttendancePeriod period)
    {
        if (!CanEditAttendance) return;
        OpenAttendanceEditor(new AttendanceEditor(AttendanceEditorKind.Period, ClockTime(period.CheckIn),
                                                  period.CheckOut is { } end ? ClockTime(end) : "", Period: period));
    }

    public void EditGap(AttendancePeriod before, AttendancePeriod after)
    {
        if (!CanEditAttendance || before.CheckOut is not { } checkOut) return;
        OpenAttendanceEditor(new AttendanceEditor(AttendanceEditorKind.Gap, ClockTime(checkOut), ClockTime(after.CheckIn),
                                                  Before: before, After: after));
    }

    /// <summary>Eine vergessene Anwesenheit nachtragen, auch an Tagen ganz ohne.</summary>
    public void AddAttendancePeriod()
    {
        if (!CanEditAttendance || Day > Today) return;
        OpenAttendanceEditor(new AttendanceEditor(AttendanceEditorKind.Add, "", ""));
    }

    private void OpenAttendanceEditor(AttendanceEditor editor)
    {
        AttendanceEditor = editor;
        AttendanceNotice = null;
        IsAttendanceListExpanded = true;
    }

    /// <summary>Text in den Feldern „von“ und „bis“.</summary>
    public void SetAttendanceEditorTimes(string from, string to)
    {
        if (AttendanceEditor is { } editor && (editor.From != from || editor.To != to))
        {
            AttendanceEditor = editor with { From = from, To = to, Error = null };
        }
    }

    public void CancelAttendanceEditing() => AttendanceEditor = null;

    public void DismissAttendanceNotice() => AttendanceNotice = null;

    /// <summary>Was Sichern in Odoo schreibt, in dieser Reihenfolge.</summary>
    private abstract record AttendanceWrite;

    private sealed record UpdateWrite(int Id, DateTimeOffset? CheckIn, DateTimeOffset? CheckOut, bool ConfirmsCheckIn,
                                      bool ConfirmsCheckOut) : AttendanceWrite;

    private sealed record AddWrite(DateTimeOffset From, DateTimeOffset To) : AttendanceWrite;

    /// <summary>
    /// Prüft die Eingaben wie Odoo und plant das Schreiben. Eine Pause zu verschieben ändert zwei Anwesenheiten;
    /// zuerst die, die Platz macht, sonst lehnt Odoo die Überschneidung ab.
    /// </summary>
    private (List<AttendanceWrite>? Plan, string? Problem) AttendancePlan(AttendanceEditor editor)
    {
        var unreadable = Loc.T("Bitte Uhrzeiten wie 8:30 oder 830 eingeben.");
        switch (editor.Kind)
        {
            case AttendanceEditorKind.Period when editor.Period is { } period:
            {
                if (ResolveTime(editor.From, period.CheckIn) is not { } from) return (null, unreadable);
                DateTimeOffset? to = null;
                if (!period.IsOpen)
                {
                    if (ResolveTime(editor.To, period.CheckOut) is not { } end) return (null, unreadable);
                    to = end;
                }
                if (Problem(from, to, period.Id) is { } problem) return (null, problem);
                var movesCheckIn = from != period.CheckIn;
                var movesCheckOut = to != period.CheckOut;
                if (!movesCheckIn && !movesCheckOut && !period.IsAutoCheckOut) return ([], null);
                return ([new UpdateWrite(period.Id, movesCheckIn ? from : null, movesCheckOut ? to : null,
                                         movesCheckIn && period.IsTechnical, period.IsAutoCheckOut)], null);
            }
            case AttendanceEditorKind.Gap when editor is { Before: { } before, After: { } after }:
            {
                if (before.CheckOut is not { } start || ResolveTime(editor.From, start) is not { } from
                    || ResolveTime(editor.To, after.CheckIn) is not { } to) return (null, unreadable);
                if (to <= from) return (null, Loc.T("Das Ende der Pause muss nach dem Beginn liegen."));
                if (!(from > before.CheckIn && to < (after.CheckOut ?? Now)))
                {
                    var end = after.CheckOut is { } afterEnd ? ClockTime(afterEnd) : Loc.T("jetzt");
                    return (null, Loc.T("Die Pause muss zwischen %@ und %@ liegen.", ClockTime(before.CheckIn), end));
                }
                var first = new List<AttendanceWrite>();
                var second = new List<AttendanceWrite>();
                if (from != start) first.Add(new UpdateWrite(before.Id, null, from, false, before.OutMode == "technical"));
                if (to != after.CheckIn) second.Add(new UpdateWrite(after.Id, to, null, after.IsTechnical, false));
                return (to > after.CheckIn ? [.. second, .. first] : [.. first, .. second], null);
            }
            case AttendanceEditorKind.Add:
            {
                if (ResolveTime(editor.From, null) is not { } from || ResolveTime(editor.To, null) is not { } to)
                {
                    return (null, unreadable);
                }
                if (Problem(from, to, null) is { } problem) return (null, problem);
                return ([new AddWrite(from, to)], null);
            }
            default:
                return (null, unreadable);
        }
    }

    /// <summary>Reihenfolge, Zukunft und Überschneidung mit den anderen Anwesenheiten des Tages.</summary>
    private string? Problem(DateTimeOffset from, DateTimeOffset? to, int? excluding)
    {
        if (to is { } end && end <= from) return Loc.T("Das Ende muss nach dem Beginn liegen.");
        if (from > Now || (to ?? from) > Now) return Loc.T("Die Zeit liegt in der Zukunft.");
        var until = to ?? Now;
        var other = ShownPeriods.FirstOrDefault(period => period.Id != excluding && from < (period.CheckOut ?? Now)
                                                          && until > period.CheckIn);
        return other is null ? null : Loc.T("Überschneidet sich mit %@.", RangeText(other));
    }

    /// <summary>
    /// Uhrzeit aus dem Feld am angezeigten Tag. Unverändert bleibt die Zeit aus Odoo, sekundengenau und auch dann,
    /// wenn sie an einem anderen Tag liegt, etwa nach Mitternacht.
    /// </summary>
    private DateTimeOffset? ResolveTime(string text, DateTimeOffset? original)
    {
        if (original is { } kept && text.Trim() == ClockTime(kept)) return kept;
        if (ClockInput.Minutes(text) is not { } minutes) return null;
        return Day.At(minutes / 60, minutes % 60, zone);
    }

    /// <summary>
    /// Unter den Feldern einer Pause: was Sichern bewirkt, mit dem Modul Arbeitszeitgesetz auch, ob die Pause reicht.
    /// Was gilt, entscheidet Odoo beim Speichern.
    /// </summary>
    public Hint? AttendanceEditorHint
    {
        get
        {
            if (AttendanceEditor is not { Kind: AttendanceEditorKind.Gap, Before: { } before, After: { } after } editor) return null;
            if (Capabilities?.Schema.BreakRules is not { } rules)
            {
                return new Hint(Loc.T("Sichern ändert das Ende des Zeitraums davor und den Beginn des Zeitraums danach."), false);
            }
            if (AttendancePlan(editor).Plan is null || before.CheckOut is not { } start
                || ResolveTime(editor.From, start) is not { } from || ResolveTime(editor.To, after.CheckIn) is not { } to)
            {
                return new Hint(Loc.T("Nötig sind %lld Min. bei über %@ Std. und %lld Min. bei über %@ Std. Arbeit.",
                                      rules.FirstBreak, Hours(rules.FirstThreshold), rules.SecondBreak, Hours(rules.SecondThreshold)),
                                false);
            }
            var list = ShownPeriods.Select(period =>
                period.Id == before.Id ? period with { CheckOut = from }
                : period.Id == after.Id ? period with { CheckIn = to }
                : period).ToList();
            var work = AttendanceMinutes(list);
            var breaks = 0;
            for (var index = 1; index < list.Count; index++)
            {
                if (list[index - 1].CheckOut is not { } checkOut) continue;
                var gap = Math.Max(0, (int)(list[index].CheckIn - checkOut).TotalMinutes);
                if (gap >= rules.MinimumGap || list[index].IsTechnical) breaks += gap;
            }
            var (missing, required) = rules.MissingBreak(work, breaks);
            var workText = DurationInput.Format(work);
            if (missing > 0)
            {
                return new Hint(Loc.T("Bei %@ Std. Arbeit sind %lld Min. Pause nötig. Odoo ergänzt beim Sichern %lld Min.",
                                      workText, required, missing), true);
            }
            if (required > 0)
            {
                return new Hint(Loc.T("Passt: Bei %@ Std. Arbeit sind %lld Min. Pause nötig.", workText, required), false);
            }
            return new Hint(Loc.T("Unter %@ Std. Arbeit ist keine Pause vorgeschrieben.", Hours(rules.FirstThreshold)), false);
        }
    }

    /// <summary>360 → „6“, 390 → „6:30“</summary>
    private static string Hours(int minutes) =>
        minutes % 60 == 0 ? (minutes / 60).ToString(System.Globalization.CultureInfo.InvariantCulture) : DurationInput.Format(minutes);

    /// <summary>Dauer, die gerade im Bearbeiten steht, etwa „4:15 Std.“ oder „30 Min.“ bei einer Pause.</summary>
    public string? AttendanceEditorDuration
    {
        get
        {
            switch (AttendanceEditor)
            {
                case { Kind: AttendanceEditorKind.Period, Period: { } period } editor:
                {
                    if (ResolveTime(editor.From, period.CheckIn) is not { } from) return null;
                    var to = period.IsOpen ? Now : ResolveTime(editor.To, period.CheckOut);
                    if (to is not { } end || end <= from) return null;
                    return Loc.T("%@ Std.", DurationInput.Format((int)(end - from).TotalMinutes));
                }
                case { Kind: AttendanceEditorKind.Gap, Before: { } before, After: { } after } editor:
                {
                    if (before.CheckOut is not { } start || ResolveTime(editor.From, start) is not { } from
                        || ResolveTime(editor.To, after.CheckIn) is not { } to || to <= from) return null;
                    return Loc.T("%lld Min.", (int)(to - from).TotalMinutes);
                }
                case { Kind: AttendanceEditorKind.Add } editor:
                {
                    if (ResolveTime(editor.From, null) is not { } from || ResolveTime(editor.To, null) is not { } to
                        || to <= from) return null;
                    return Loc.T("%@ Std.", DurationInput.Format((int)(to - from).TotalMinutes));
                }
                default:
                    return null;
            }
        }
    }

    public async Task SaveAttendanceAsync()
    {
        if (AttendanceEditor is not { } editor || service is not { } current || IsSavingAttendance) return;
        var (writes, problem) = AttendancePlan(editor);
        if (writes is null)
        {
            AttendanceEditor = editor with { Error = problem };
            return;
        }
        if (writes.Count == 0)
        {
            AttendanceEditor = null;
            return;
        }
        var token = sessionToken;
        var insertedBefore = ShownPeriods.Where(period => period.IsTechnical).Select(period => period.Id).ToHashSet();
        IsSavingAttendance = true;
        AttendanceEditor = editor with { Error = null };
        try
        {
            foreach (var write in writes)
            {
                switch (write)
                {
                    case UpdateWrite update:
                        await current.UpdateAttendanceAsync(update.Id, update.CheckIn, update.CheckOut, update.ConfirmsCheckIn,
                                                            update.ConfirmsCheckOut);
                        break;
                    case AddWrite add:
                        if (Capabilities?.EmployeeId is not { } employeeId)
                        {
                            throw OdooException.Rejected(Loc.T("Dein Odoo-Benutzer ist in deiner Hauptfirma mit keinem Mitarbeiter verknüpft."));
                        }
                        await current.AddAttendanceAsync(employeeId, add.From, add.To);
                        break;
                }
            }
            if (token != sessionToken) return;
            await RefreshAsync(force: true);
            if (token != sessionToken) return;
            IsSavingAttendance = false;
            AttendanceEditor = null;
            AttendanceNotice = NoticeAfterSaving(editor.Kind, insertedBefore);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token != sessionToken) return;
            IsSavingAttendance = false;
            if (IsKind(error, OdooErrorKind.AccessDenied))
            {
                MarkStampingRefused();
                AttendanceNotice = new StatusNotice(NoticeLevel.Warning, MessageFor(error));
            }
            else
            {
                if (AttendanceEditor is { } open) AttendanceEditor = open with { Error = MessageFor(error) };
                if (IsKind(error, OdooErrorKind.Unauthorized)) Handle(error);
            }
            // Bei einer Pause kann die erste Hälfte schon gesichert sein: den Stand aus Odoo zeigen.
            if (writes.Count > 1) await RefreshAsync(force: true);
        }
    }

    /// <summary>
    /// Hat Odoo nach dem Sichern selbst etwas geändert? Das Modul Arbeitszeitgesetz fügt fehlende Pausen ein und
    /// meldet zu lange Tage.
    /// </summary>
    private StatusNotice NoticeAfterSaving(AttendanceEditorKind kind, HashSet<int> insertedBefore)
    {
        var list = ShownPeriods;
        if (Capabilities?.Schema.BreakRules is { } rules)
        {
            var index = list.ToList().FindIndex(period => period.IsTechnical && !insertedBefore.Contains(period.Id));
            if (index > 0 && list[index - 1].CheckOut is { } start)
            {
                var minutes = (int)(list[index].CheckIn - start).TotalMinutes;
                return new StatusNotice(NoticeLevel.Info, Loc.T("Gesichert. Odoo hat %lld Min. Pause eingefügt (%@ – %@).", minutes,
                                                                ClockTime(start), ClockTime(list[index].CheckIn)));
            }
            if (AttendanceMinutes(list) > rules.MaximumWork)
            {
                return new StatusNotice(NoticeLevel.Warning,
                                        Loc.T("Gesichert. Mehr als %@ Std. Arbeit am Tag erlaubt das Arbeitszeitgesetz nicht.",
                                              Hours(rules.MaximumWork)));
            }
        }
        return kind switch
        {
            AttendanceEditorKind.Period => new StatusNotice(NoticeLevel.Done, Loc.T("Zeitraum gesichert.")),
            AttendanceEditorKind.Gap => new StatusNotice(NoticeLevel.Done, Loc.T("Pause verschoben.")),
            _ => new StatusNotice(NoticeLevel.Done, Loc.T("Zeitraum nachgetragen.")),
        };
    }
}
