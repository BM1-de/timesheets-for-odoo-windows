// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Serialization;
using Zeiterfassung.Support;

namespace Zeiterfassung.Odoo;

/// <param name="CompanyId">Firma des Projekts; <c>null</c>, wenn es allen Firmen gehört.</param>
public sealed record Project(int Id, string Name, int? CompanyId = null);

public sealed record ProjectTask(int Id, string Name);

/// <summary>Warum Odoo einen Eintrag nicht mehr ändern lässt.</summary>
public enum EntryLock
{
    /// <summary>
    /// Abgerechnet (<c>timesheet_invoice_id</c>): Dauer, Datum, Projekt und Aufgabe stehen fest,
    /// löschen geht nicht, die Beschreibung bleibt änderbar.
    /// </summary>
    Invoiced,
    /// <summary>Freigegeben (<c>validated</c>, Odoo Enterprise): nur noch die Freigebenden dürfen ändern.</summary>
    Validated,
}

/// <summary>Ein Zeiteintrag (<c>account.analytic.line</c> mit Projekt).</summary>
/// <param name="CompanyId">Firma, in der Odoo den Eintrag führt.</param>
/// <param name="EmployeeId">Mitarbeiter, auf den der Eintrag gebucht ist.</param>
public sealed record TimesheetEntry(
    int Id,
    string Description,
    Reference? Project,
    Reference? Task,
    int Minutes,
    CalendarDay? Day = null,
    EntryLock? Lock = null,
    int? CompanyId = null,
    int? EmployeeId = null)
{
    /// <summary>„Projekt › Aufgabe“ für die Liste.</summary>
    public string Path => string.Join(" › ", new[] { Project?.Name, Task?.Name }.OfType<string>());
}

/// <summary>Neuer Zeiteintrag, wie er an Odoo geht.</summary>
public sealed record TimesheetDraft(int ProjectId, int? TaskId, string Description, int Minutes, CalendarDay Day);

/// <summary>Eine Anwesenheit (<c>hr.attendance</c>): eingestempelt bis ausgestempelt.</summary>
/// <param name="InMode">Wie eingestempelt wurde (<c>in_mode</c>, ab Odoo 17), etwa <c>manual</c>.</param>
/// <param name="OutMode">Wie ausgestempelt wurde (<c>out_mode</c>).</param>
public sealed record AttendancePeriod(
    int Id,
    DateTimeOffset CheckIn,
    DateTimeOffset? CheckOut,
    string? InMode = null,
    string? OutMode = null)
{
    public bool IsOpen => CheckOut is null;

    /// <summary>Odoo hat selbst ausgestempelt (ab Odoo 18, „Automatisch abmelden“ in den Einstellungen).</summary>
    public bool IsAutoCheckOut => OutMode == "auto_check_out";

    /// <summary>
    /// Von Odoo angelegt statt gestempelt. Mit dem Modul Arbeitszeitgesetz ist das der zweite Teil einer
    /// Anwesenheit, in die Odoo die gesetzliche Pause eingefügt hat.
    /// </summary>
    public bool IsTechnical => InMode == "technical";

    /// <summary>Dauer in Minuten; läuft die Anwesenheit noch, bis <paramref name="now"/>.</summary>
    public int Minutes(DateTimeOffset now) => Math.Max(0, (int)Math.Floor(((CheckOut ?? now) - CheckIn).TotalMinutes));
}

[JsonConverter(typeof(JsonStringEnumConverter<FeatureStatus>))]
public enum FeatureStatus
{
    Available,
    /// <summary>Die Odoo-App ist nicht installiert.</summary>
    NotInstalled,
    /// <summary>Der Benutzer ist mit keinem Mitarbeiter verknüpft.</summary>
    NoEmployee,
    /// <summary>Kein Lesezugriff.</summary>
    NoAccess,
}

/// <summary>Was die App in dieser Odoo-Datenbank kann.</summary>
/// <param name="CanStamp">Darf Anwesenheiten anlegen, ändern und löschen, also stempeln und korrigieren; in Odoo
/// mit „Sachbearbeiter: Alle Anwesenheiten verwalten“ oder als eigener Anwesenheitsgenehmiger.</param>
/// <param name="EmployeeId">Mitarbeiter, mit dem die App stempelt: der des Benutzers in seiner Hauptfirma oder
/// der in den Einstellungen gewählte.</param>
/// <param name="Organization">Firmen und Mitarbeiter, wenn der Benutzer mehr als eine Firma sehen darf.</param>
public sealed record Capabilities(
    string UserName,
    FeatureStatus Timesheets,
    FeatureStatus Attendance,
    bool CanStamp,
    int? EmployeeId,
    Schema Schema,
    Organization? Organization);

/// <summary>
/// Mehrere Firmen in einer Odoo-Datenbank. Odoo bucht jeden Eintrag in der Firma seines Projekts, mit dem
/// Mitarbeiter des Benutzers in dieser Firma. Eine Anwesenheit gehört zu einem Mitarbeiter und damit zu einer Firma.
/// </summary>
/// <param name="Companies">Die Firmen des Benutzers, die Hauptfirma zuerst.</param>
/// <param name="Employees">Die Mitarbeiter des Benutzers, höchstens einer je Firma.</param>
public sealed record Organization(IReadOnlyList<Company> Companies, IReadOnlyList<Employee> Employees)
{
    [JsonIgnore]
    public int? MainCompanyId => Companies.Count > 0 ? Companies[0].Id : null;

    public Company? FindCompany(int? id) => Companies.FirstOrDefault(company => company.Id == id);

    public Employee? EmployeeIn(int? companyId) => Employees.FirstOrDefault(employee => employee.CompanyId == companyId);

    /// <summary>
    /// Lehnt Odoo einen Eintrag auf diesem Projekt ab? Das tut es, wenn der Benutzer mehrere Mitarbeiter hat,
    /// aber keinen in der Firma des Projekts. Mit nur einem bucht Odoo auf diesen.
    /// </summary>
    public bool LacksEmployee(Project project) =>
        Employees.Count > 1 && project.CompanyId is { } companyId && EmployeeIn(companyId) is null;

    public bool Equals(Organization? other) =>
        other is not null && Companies.SequenceEqual(other.Companies) && Employees.SequenceEqual(other.Employees);

    public override int GetHashCode() => HashCode.Combine(Companies.Count, Employees.Count);
}

public sealed record Company(int Id, string Name);

public sealed record Employee(int Id, int CompanyId);

/// <summary>Felder, die es nicht in jeder Odoo-Version gibt.</summary>
public sealed record Schema
{
    /// <summary><c>project.project.is_template</c> (ab Odoo 18)</summary>
    public bool ProjectHasTemplates { get; init; }
    /// <summary><c>project.task.is_closed</c></summary>
    public bool TaskHasIsClosed { get; init; }
    /// <summary><c>account.analytic.line.timesheet_invoice_id</c> (mit der Odoo-App Verkauf)</summary>
    public bool LineHasInvoice { get; init; }
    /// <summary><c>account.analytic.line.validated</c> (Odoo Enterprise)</summary>
    public bool LineHasValidated { get; init; }
    /// <summary><c>hr.attendance.in_mode</c> und <c>out_mode</c> (ab Odoo 17)</summary>
    public bool AttendanceHasModes { get; init; }
    /// <summary>Pausenregeln des Moduls Arbeitszeitgesetz (<c>l10n_de_hr_attendance_arbzg</c>), falls installiert.</summary>
    public BreakRules? BreakRules { get; init; }
}

/// <summary>
/// Pausen und Höchstarbeitszeit nach dem Arbeitszeitgesetz, wie sie das Modul je Firma einstellt.
/// Die App rechnet damit nur vor; was gilt, entscheidet Odoo beim Speichern.
/// </summary>
public sealed record BreakRules
{
    /// <summary>Ab so viel Arbeit (Minuten) …</summary>
    public int FirstThreshold { get; init; } = 360;
    /// <summary>… so viel Pause (Minuten).</summary>
    public int FirstBreak { get; init; } = 30;
    public int SecondThreshold { get; init; } = 540;
    public int SecondBreak { get; init; } = 45;
    /// <summary>Lücken ab dieser Länge zählen als Pause.</summary>
    public int MinimumGap { get; init; } = 15;
    /// <summary>Höchstarbeitszeit am Tag</summary>
    public int MaximumWork { get; init; } = 600;

    /// <summary>
    /// Fehlende Pause für <paramref name="work"/> Minuten Arbeit mit <paramref name="breaks"/> Minuten Pause, wie das
    /// Modul sie rechnet: Verlangt wird nur die Zeit über der Grenze; wer 6:10 Stunden arbeitet, braucht 10 Minuten.
    /// </summary>
    public (int Missing, int Required) MissingBreak(int work, int breaks)
    {
        var missing = 0;
        var required = 0;
        foreach (var (threshold, needed) in new[] { (FirstThreshold, FirstBreak), (SecondThreshold, SecondBreak) })
        {
            if (work <= threshold) continue;
            required = needed;
            if (breaks < needed)
            {
                var part = Math.Min(needed - breaks, work - threshold);
                missing += part;
                work -= part;
                breaks += part;
            }
        }
        return (missing, required);
    }
}
