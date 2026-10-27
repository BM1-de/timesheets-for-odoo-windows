// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Odoo;

namespace Zeiterfassung.Model;

public enum Screen
{
    Starting,
    Login,
    Main,
}

/// <summary>Was im Formular steht.</summary>
public sealed record Draft(int? ProjectId = null, int? TaskId = null, string Description = "", string DurationText = "");

public enum NoticeKind
{
    Offline,
    SignedOut,
    Failure,
}

public sealed record Notice(NoticeKind Kind, string Message);

public sealed record Hint(string Text, bool IsProblem);

public enum TaskChoiceKind
{
    NoProject,
    Loading,
    NoTasks,
    Failed,
    Tasks,
}

public sealed record TaskChoice(TaskChoiceKind Kind, string? Message = null, IReadOnlyList<ProjectTask>? Tasks = null)
{
    public static readonly TaskChoice NoProject = new(TaskChoiceKind.NoProject);
    public static readonly TaskChoice Loading = new(TaskChoiceKind.Loading);
    public static readonly TaskChoice NoTasks = new(TaskChoiceKind.NoTasks);

    public bool Equals(TaskChoice? other) =>
        other is not null && Kind == other.Kind && Message == other.Message
        && (Tasks ?? []).SequenceEqual(other.Tasks ?? []);

    public override int GetHashCode() => HashCode.Combine(Kind, Message, Tasks?.Count);
}

/// <summary>Was das Symbol im Infobereich zeigt.</summary>
/// <param name="ShowsDot">Grüner Punkt: Eine Projektzeit läuft.</param>
/// <param name="ToolTip">Alle Zeiten von heute, je eine Zeile.</param>
public sealed record TrayContent(bool IsSignedIn, bool ShowsDot, string ToolTip);

public enum AttendanceRowKind
{
    Period,
    Gap,
}

/// <summary>Eine Anwesenheit oder die Pause zwischen zwei Anwesenheiten.</summary>
/// <param name="IsAutoCheckOut">Odoo hat selbst ausgestempelt; die echte Zeit sollte jemand eintragen.</param>
/// <param name="IsInsertedBreak">Pause, die das Modul Arbeitszeitgesetz eingefügt hat.</param>
public sealed record AttendanceRow(
    string Id,
    AttendanceRowKind Kind,
    AttendancePeriod? Period,
    AttendancePeriod? Before,
    AttendancePeriod? After,
    string Range,
    string Duration,
    bool IsAutoCheckOut = false,
    bool IsInsertedBreak = false);

public sealed record AttendanceSummary
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool IsRunning { get; init; }
    public string? ButtonTitle { get; init; }
    public IReadOnlyList<AttendanceRow> Rows { get; init; } = [];
    public string? UnbookedText { get; init; }
    public string? Note { get; init; }
    /// <summary>Was in den Zeiten des Tages Aufmerksamkeit braucht, für die zugeklappte Liste.</summary>
    public string? Attention { get; init; }
    /// <summary>Zeiten lassen sich korrigieren, nachtragen und löschen.</summary>
    public bool CanEdit { get; init; }
    /// <summary>Vergangener Tag ohne Anwesenheit: „Nachtragen …“ im Kopf.</summary>
    public bool OffersAdding { get; init; }

    public bool Equals(AttendanceSummary? other) =>
        other is not null && Title == other.Title && Subtitle == other.Subtitle && IsRunning == other.IsRunning
        && ButtonTitle == other.ButtonTitle && Rows.SequenceEqual(other.Rows) && UnbookedText == other.UnbookedText
        && Note == other.Note && Attention == other.Attention && CanEdit == other.CanEdit && OffersAdding == other.OffersAdding;

    public override int GetHashCode() => HashCode.Combine(Title, Subtitle, Rows.Count);
}

public enum AttendanceEditorKind
{
    /// <summary>Beginn und Ende einer Anwesenheit</summary>
    Period,
    /// <summary>Pause zwischen zwei Anwesenheiten: das Ende der ersten und der Beginn der zweiten</summary>
    Gap,
    /// <summary>Vergessene Anwesenheit nachtragen</summary>
    Add,
}

/// <summary>Bearbeiten einer Anwesenheit direkt in der Liste.</summary>
public sealed record AttendanceEditor(
    AttendanceEditorKind Kind,
    string From,
    string To,
    AttendancePeriod? Period = null,
    AttendancePeriod? Before = null,
    AttendancePeriod? After = null,
    string? Error = null);

public enum NoticeLevel
{
    Done,
    /// <summary>Odoo hat danach selbst etwas geändert, etwa eine Pause eingefügt.</summary>
    Info,
    Warning,
}

/// <summary>Ergebnis nach dem Sichern einer Anwesenheit oder dem Stoppen der Projektzeit.</summary>
public sealed record StatusNotice(NoticeLevel Level, string Text);

public enum DeletionKind
{
    Entry,
    Period,
    /// <summary>Laufende Projektzeit verwerfen, ohne sie zu buchen</summary>
    RunningTimer,
}

/// <summary>Wartet auf die Bestätigung im Fenster.</summary>
public sealed record Deletion(DeletionKind Kind, TimesheetEntry? Entry = null, AttendancePeriod? Period = null)
{
    public static readonly Deletion RunningTimer = new(DeletionKind.RunningTimer);
}

/// <summary>Wofür das Formular gerade die Projektzeit zeigt.</summary>
public enum TimerEditing
{
    /// <summary>Projekt, Aufgabe und Beschreibung der laufenden Zeit ändern</summary>
    Running,
    /// <summary>Gestoppt, aber Projekt oder Aufgabe fehlen: mit Dauer buchen</summary>
    Booking,
}

/// <summary>Warum eine gestoppte Zeit noch nicht gebucht ist, solange kein Fehler dahintersteckt.</summary>
public enum StoppedTimerReason
{
    /// <summary>Ohne Projekt gestartet.</summary>
    Project,
    /// <summary>Lief keine Minute.</summary>
    Short,
}

/// <summary>Einträge eines Tages, nach Firma geordnet, wenn sie aus mehreren Firmen stammen.</summary>
/// <param name="Title">Name der Firma; <c>null</c>, wenn alle Einträge aus einer Firma stammen.</param>
public sealed record EntryGroup(int Id, string? Title, int Minutes, IReadOnlyList<TimesheetEntry> Entries)
{
    public bool Equals(EntryGroup? other) =>
        other is not null && Id == other.Id && Title == other.Title && Minutes == other.Minutes && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Minutes, Entries.Count);
}
