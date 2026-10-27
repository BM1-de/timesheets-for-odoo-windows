// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using Zeiterfassung.Support;

namespace Zeiterfassung.Odoo;

/// <summary>
/// Lesen und Schreiben der Daten, die die App braucht. Alles läuft mit den Rechten des angemeldeten
/// Benutzers; die App fordert keine erweiterten Rechte an.
/// </summary>
public sealed class OdooService(IOdooTransport transport, int uid, IReadOnlyDictionary<string, JsonNode?>? context = null)
{
    private readonly IReadOnlyDictionary<string, JsonNode?> context = context ?? new Dictionary<string, JsonNode?>();

    public int Uid { get; } = uid;

    public Schema Schema { get; set; } = new();

    private Task<JsonNode?> CallAsync(string model, string method, IReadOnlyList<int>? ids = null,
                                      Dictionary<string, JsonNode?>? arguments = null, string[]? positional = null) =>
        transport.ExecuteAsync(new OdooCall(model, method, ids, arguments, positional), context);

    private static JsonArray Fields(params string[] names) => new(names.Select(name => (JsonNode?)name).ToArray());

    // Was gibt es?

    /// <param name="attendanceEmployeeId">In den Einstellungen gewählter Mitarbeiter zum Stempeln, falls der Benutzer
    /// in mehreren Firmen einen hat. Sonst stempelt die App mit dem der Hauptfirma.</param>
    public async Task<Capabilities> DiscoverCapabilitiesAsync(int? attendanceEmployeeId = null)
    {
        var user = await CallAsync("res.users", "read", [Uid], new() { ["fields"] = Fields("name", "company_id", "company_ids") });
        var userRecord = user.ArrayValue()?.FirstOrDefault();
        var userName = userRecord.Field("name").StringValue() ?? "";
        var mainCompanyId = userRecord.Field("company_id").Reference()?.Id;
        var companyIds = userRecord.Field("company_ids").ArrayValue()?.Select(item => item.IntValue()).OfType<int>().ToList() ?? [];

        // Mitarbeiter: Felder aus der Odoo-App „Mitarbeiter“, auf die Zeiterfassung und Anwesenheiten aufbauen.
        // `employee_id` ist der Mitarbeiter in der Hauptfirma.
        int? employeeId = null;
        var hasEmployee = false;
        try
        {
            var employee = await CallAsync("res.users", "read", [Uid], new() { ["fields"] = Fields("employee_id", "employee_ids") });
            if (employee.ArrayValue()?.FirstOrDefault() is { } record)
            {
                employeeId = record.Field("employee_id").Reference()?.Id;
                hasEmployee = employeeId is not null || record.Field("employee_ids").ArrayValue() is { Count: > 0 };
            }
        }
        catch (OdooException)
        {
        }
        Organization? organization = null;
        if (hasEmployee && companyIds.Count > 1)
        {
            organization = await OrganizationAsync(companyIds, mainCompanyId);
        }
        if (attendanceEmployeeId is { } chosen && organization?.Employees.Any(employee => employee.Id == chosen) == true)
        {
            employeeId = chosen;
        }

        var lineFields = await FieldNamesAsync("account.analytic.line");
        var timesheets = lineFields switch
        {
            { } fields when fields.Contains("project_id") => hasEmployee ? FeatureStatus.Available : FeatureStatus.NoEmployee,
            _ => FeatureStatus.NotInstalled,
        };

        var attendanceFields = await FieldNamesAsync("hr.attendance");
        var attendance = attendanceFields is null
            ? FeatureStatus.NotInstalled
            : hasEmployee ? FeatureStatus.Available : FeatureStatus.NoEmployee;
        var canStamp = attendance == FeatureStatus.Available && await CanCreateAsync("hr.attendance");

        var schema = new Schema();
        if (timesheets == FeatureStatus.Available && lineFields is not null)
        {
            schema = schema with
            {
                ProjectHasTemplates = (await FieldNamesAsync("project.project"))?.Contains("is_template") ?? false,
                TaskHasIsClosed = (await FieldNamesAsync("project.task"))?.Contains("is_closed") ?? false,
                LineHasInvoice = lineFields.Contains("timesheet_invoice_id"),
                LineHasValidated = lineFields.Contains("validated"),
            };
        }
        if (attendance == FeatureStatus.Available && attendanceFields is not null)
        {
            schema = schema with
            {
                AttendanceHasModes = attendanceFields.Contains("in_mode") && attendanceFields.Contains("out_mode"),
            };
            if (attendanceFields.Contains("arbzg_missing_break"))
            {
                // Die Regeln gelten je Firma, hier für die, in der gestempelt wird.
                var companyId = organization?.Employees.FirstOrDefault(employee => employee.Id == employeeId)?.CompanyId
                                ?? mainCompanyId;
                schema = schema with { BreakRules = await BreakRulesAsync(companyId) };
            }
        }

        return new Capabilities(userName, timesheets, attendance, canStamp, employeeId, schema, organization);
    }

    /// <summary>
    /// Firmen und Mitarbeiter des Benutzers. Die Mitarbeiter anderer Firmen stehen nicht in
    /// <c>res.users.employee_ids</c>, deshalb die Suche über <c>hr.employee</c>; deren Felder <c>company_id</c> und
    /// <c>user_id</c> darf auch lesen, wer keine Rechte für die Personalverwaltung hat.
    /// </summary>
    private async Task<Organization?> OrganizationAsync(List<int> companyIds, int? mainCompanyId)
    {
        JsonNode? companyRows;
        JsonNode? employeeRows;
        try
        {
            companyRows = await CallAsync("res.company", "read", companyIds, new() { ["fields"] = Fields("name") });
            employeeRows = await CallAsync("hr.employee", "search_read", null, new()
            {
                ["domain"] = new JsonArray(Json.Condition("user_id", "=", Uid)),
                ["fields"] = Fields("company_id"),
                ["order"] = "id asc",
            });
        }
        catch (OdooException)
        {
            return null;
        }
        var companies = companyRows.ArrayValue()?
            .Select(row => row.Field("id").IntValue() is { } id ? new Company(id, row.Field("name").StringValue() ?? "") : null)
            .OfType<Company>()
            .ToList() ?? [];
        var mainIndex = companies.FindIndex(company => company.Id == mainCompanyId);
        if (mainIndex > 0)
        {
            var main = companies[mainIndex];
            companies.RemoveAt(mainIndex);
            companies.Insert(0, main);
        }
        var employees = new List<Employee>();
        foreach (var row in employeeRows.ArrayValue() ?? [])
        {
            if (row.Field("id").IntValue() is not { } id || row.Field("company_id").Reference()?.Id is not { } companyId
                || employees.Any(employee => employee.CompanyId == companyId)) continue;
            employees.Add(new Employee(id, companyId));
        }
        return companies.Count > 1 ? new Organization(companies, employees) : null;
    }

    /// <summary>Feldnamen eines Modells; <c>null</c>, wenn es das Modell nicht gibt (App nicht installiert).</summary>
    public async Task<HashSet<string>?> FieldNamesAsync(string model)
    {
        try
        {
            var fields = await CallAsync(model, "fields_get", null, new() { ["attributes"] = Fields("type") });
            return fields.ObjectValue()?.Select(pair => pair.Key).ToHashSet() ?? [];
        }
        catch (OdooException error) when (error.Kind == OdooErrorKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Pausenregeln einer Firma aus dem Modul Arbeitszeitgesetz. Lassen sie sich nicht lesen, gelten die
    /// gesetzlichen Werte, mit denen das Modul ausgeliefert wird.
    /// </summary>
    private async Task<BreakRules> BreakRulesAsync(int? companyId)
    {
        var rules = new BreakRules();
        if (companyId is not { } id) return rules;
        JsonNode? company;
        try
        {
            var rows = await CallAsync("res.company", "read", [id], new()
            {
                ["fields"] = Fields("arbzg_break_hours_1", "arbzg_break_minutes_1", "arbzg_break_hours_2",
                                    "arbzg_break_minutes_2", "arbzg_min_break_minutes", "arbzg_max_hours"),
            });
            company = rows.ArrayValue()?.FirstOrDefault();
        }
        catch (OdooException)
        {
            return rules;
        }
        if (company is null) return rules;
        int? FromHours(string key) =>
            company.Field(key).DoubleValue() is { } hours ? (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero) : null;
        return rules with
        {
            FirstThreshold = FromHours("arbzg_break_hours_1") ?? rules.FirstThreshold,
            FirstBreak = company.Field("arbzg_break_minutes_1").IntValue() ?? rules.FirstBreak,
            SecondThreshold = FromHours("arbzg_break_hours_2") ?? rules.SecondThreshold,
            SecondBreak = company.Field("arbzg_break_minutes_2").IntValue() ?? rules.SecondBreak,
            MinimumGap = company.Field("arbzg_min_break_minutes").IntValue() ?? rules.MinimumGap,
            MaximumWork = FromHours("arbzg_max_hours") ?? rules.MaximumWork,
        };
    }

    /// <summary>
    /// Darf der Benutzer Datensätze dieses Modells anlegen? <c>has_access</c> gibt es ab Odoo 18, davor
    /// <c>check_access_rights</c>.
    /// </summary>
    private async Task<bool> CanCreateAsync(string model)
    {
        try
        {
            if ((await CallAsync(model, "has_access", [], new() { ["operation"] = "create" })).BoolValue() is { } allowed)
            {
                return allowed;
            }
        }
        catch (OdooException)
        {
        }
        try
        {
            var result = await CallAsync(model, "check_access_rights", null,
                                         new() { ["operation"] = "create", ["raise_exception"] = false });
            return result.BoolValue() ?? false;
        }
        catch (OdooException)
        {
            return false;
        }
    }

    // Projekte und Aufgaben

    public async Task<List<Project>> ProjectsAsync()
    {
        var domain = new JsonArray(Json.Condition("allow_timesheets", "=", true));
        if (Schema.ProjectHasTemplates) domain.Add(Json.Condition("is_template", "=", false));
        var rows = await CallAsync("project.project", "search_read", null, new()
        {
            ["domain"] = domain,
            ["fields"] = Fields("display_name", "company_id"),
            ["order"] = "name asc, id asc",
        });
        return rows.ArrayValue()?
            .Select(row => row.Field("id").IntValue() is { } id
                ? new Project(id, row.Field("display_name").StringValue() ?? "", row.Field("company_id").Reference()?.Id)
                : null)
            .OfType<Project>()
            .ToList() ?? [];
    }

    /// <summary>Offene Aufgaben eines Projekts.</summary>
    public async Task<List<ProjectTask>> TasksAsync(int projectId)
    {
        var domain = new JsonArray(Json.Condition("project_id", "=", projectId));
        if (Schema.TaskHasIsClosed) domain.Add(Json.Condition("is_closed", "=", false));
        var rows = await CallAsync("project.task", "search_read", null, new()
        {
            ["domain"] = domain,
            ["fields"] = Fields("name"),
            ["order"] = "sequence asc, id asc",
        });
        return rows.ArrayValue()?
            .Select(row => row.Field("id").IntValue() is { } id ? new ProjectTask(id, row.Field("name").StringValue() ?? "") : null)
            .OfType<ProjectTask>()
            .ToList() ?? [];
    }

    /// <summary>Projekte der eigenen Zeiteinträge seit <paramref name="since"/>, zuletzt verwendete zuerst.</summary>
    public async Task<List<int>> RecentProjectIdsAsync(CalendarDay since, int limit = 5)
    {
        var rows = await CallAsync("account.analytic.line", "search_read", null, new()
        {
            ["domain"] = new JsonArray(Json.Condition("user_id", "=", Uid), Json.Condition("project_id", "!=", false),
                                       Json.Condition("date", ">=", since.OdooString)),
            ["fields"] = Fields("project_id"),
            ["order"] = "date desc, id desc",
            ["limit"] = 200,
        });
        var seen = new HashSet<int>();
        var ids = new List<int>();
        foreach (var row in rows.ArrayValue() ?? [])
        {
            if (row.Field("project_id").Reference()?.Id is not { } id || !seen.Add(id)) continue;
            ids.Add(id);
            if (ids.Count == limit) break;
        }
        return ids;
    }

    // Zeiteinträge

    public async Task<List<TimesheetEntry>> TimesheetsAsync(CalendarDay day)
    {
        var fields = Fields("name", "project_id", "task_id", "unit_amount", "date", "company_id", "employee_id");
        if (Schema.LineHasInvoice) fields.Add("timesheet_invoice_id");
        if (Schema.LineHasValidated) fields.Add("validated");
        var rows = await CallAsync("account.analytic.line", "search_read", null, new()
        {
            ["domain"] = new JsonArray(Json.Condition("user_id", "=", Uid), Json.Condition("project_id", "!=", false),
                                       Json.Condition("date", "=", day.OdooString)),
            ["fields"] = fields,
            ["order"] = "id asc",
        });
        var entries = new List<TimesheetEntry>();
        foreach (var row in rows.ArrayValue() ?? [])
        {
            if (row.Field("id").IntValue() is not { } id) continue;
            var name = row.Field("name").StringValue() ?? "";
            EntryLock? entryLock = null;
            if (row.Field("validated").BoolValue() == true) entryLock = EntryLock.Validated;
            else if (row.Field("timesheet_invoice_id").Reference() is not null) entryLock = EntryLock.Invoiced;
            entries.Add(new TimesheetEntry(
                id,
                name == "/" ? "" : name,
                row.Field("project_id").Reference(),
                row.Field("task_id").Reference(),
                (int)Math.Round((row.Field("unit_amount").DoubleValue() ?? 0) * 60, MidpointRounding.AwayFromZero),
                row.Field("date").StringValue() is { } date ? CalendarDay.Parse(date) ?? day : day,
                entryLock,
                row.Field("company_id").Reference()?.Id,
                row.Field("employee_id").Reference()?.Id));
        }
        return entries;
    }

    /// <summary>Legt einen Zeiteintrag an. Den Mitarbeiter wählt Odoo selbst passend zur Firma des Projekts.</summary>
    public async Task<int> CreateTimesheetAsync(TimesheetDraft draft)
    {
        var values = new JsonObject
        {
            ["project_id"] = draft.ProjectId,
            ["name"] = draft.Description.Length == 0 ? "/" : draft.Description,
            ["unit_amount"] = draft.Minutes / 60.0,
            ["date"] = draft.Day.OdooString,
        };
        if (draft.TaskId is { } taskId) values["task_id"] = taskId;
        var result = await CallAsync("account.analytic.line", "create", null, new() { ["vals_list"] = new JsonArray(values) },
                                     ["vals_list"]);
        return result.ArrayValue()?.FirstOrDefault().IntValue() ?? result.IntValue() ?? 0;
    }

    /// <summary>Ändert einen Zeiteintrag. Bei einem abgerechneten nur die Beschreibung; den Rest lehnt Odoo ab.</summary>
    /// <param name="employeeId">Neuer Mitarbeiter, wenn das Projekt in eine andere Firma wechselt. Odoo übernimmt dann
    /// die Firma des Projekts, behält aber sonst den Mitarbeiter der alten Firma.</param>
    public async Task UpdateTimesheetAsync(int id, TimesheetDraft draft, bool descriptionOnly = false, int? employeeId = null)
    {
        var values = new JsonObject { ["name"] = draft.Description.Length == 0 ? "/" : draft.Description };
        if (!descriptionOnly)
        {
            values["project_id"] = draft.ProjectId;
            values["task_id"] = draft.TaskId is { } taskId ? JsonValue.Create(taskId) : JsonValue.Create(false);
            values["unit_amount"] = draft.Minutes / 60.0;
            values["date"] = draft.Day.OdooString;
            if (employeeId is { } employee) values["employee_id"] = employee;
        }
        await WriteAsync("account.analytic.line", id, values);
    }

    /// <summary>
    /// <c>write</c> mit den Werten als Position: Bis Odoo 18 heißt der Parameter in manchen Modellen nicht
    /// <c>vals</c>, sondern <c>values</c> (etwa bei Zeiteinträgen), über JSON-2 dagegen immer <c>vals</c>.
    /// </summary>
    private Task<JsonNode?> WriteAsync(string model, int id, JsonObject values) =>
        CallAsync(model, "write", [id], new() { ["vals"] = values }, ["vals"]);

    public Task DeleteTimesheetAsync(int id) => CallAsync("account.analytic.line", "unlink", [id]);

    // Anwesenheit

    /// <summary>Eigene Anwesenheiten, die an diesem Tag (Ortszeit) begonnen haben.</summary>
    /// <param name="employeeId">Der Mitarbeiter, mit dem gestempelt wird; ohne ihn alle des Benutzers.</param>
    public async Task<List<AttendancePeriod>> AttendancesAsync(CalendarDay day, TimeZoneInfo zone, int? employeeId = null)
    {
        var start = day.Start(zone);
        var end = day.AddingDays(1).Start(zone);
        var rows = await CallAsync("hr.attendance", "search_read", null, new()
        {
            ["domain"] = new JsonArray(AttendanceOwner(employeeId),
                                       Json.Condition("check_in", ">=", OdooDateTime.Format(start)),
                                       Json.Condition("check_in", "<", OdooDateTime.Format(end))),
            ["fields"] = AttendanceFields(),
            ["order"] = "check_in asc",
        });
        return Periods(rows);
    }

    /// <summary>Die laufende Anwesenheit, falls eingestempelt.</summary>
    public async Task<AttendancePeriod?> OpenAttendanceAsync(int? employeeId = null)
    {
        var rows = await CallAsync("hr.attendance", "search_read", null, new()
        {
            ["domain"] = new JsonArray(AttendanceOwner(employeeId), Json.Condition("check_out", "=", false)),
            ["fields"] = AttendanceFields(),
            ["order"] = "check_in desc",
            ["limit"] = 1,
        });
        return Periods(rows).FirstOrDefault();
    }

    private JsonArray AttendanceOwner(int? employeeId) =>
        employeeId is { } id ? Json.Condition("employee_id", "=", id) : Json.Condition("employee_id.user_id", "=", Uid);

    private JsonArray AttendanceFields() =>
        Schema.AttendanceHasModes ? Fields("check_in", "check_out", "in_mode", "out_mode") : Fields("check_in", "check_out");

    public Task CheckInAsync(int employeeId, DateTimeOffset at) =>
        CallAsync("hr.attendance", "create", null, new()
        {
            ["vals_list"] = new JsonArray(new JsonObject { ["employee_id"] = employeeId, ["check_in"] = OdooDateTime.Format(at) }),
        }, ["vals_list"]);

    public Task CheckOutAsync(int attendanceId, DateTimeOffset at) =>
        WriteAsync("hr.attendance", attendanceId, new JsonObject { ["check_out"] = OdooDateTime.Format(at) });

    /// <summary>Trägt eine vergessene Anwesenheit nach.</summary>
    public Task AddAttendanceAsync(int employeeId, DateTimeOffset checkIn, DateTimeOffset checkOut) =>
        CallAsync("hr.attendance", "create", null, new()
        {
            ["vals_list"] = new JsonArray(new JsonObject
            {
                ["employee_id"] = employeeId,
                ["check_in"] = OdooDateTime.Format(checkIn),
                ["check_out"] = OdooDateTime.Format(checkOut),
            }),
        }, ["vals_list"]);

    /// <summary>
    /// Korrigiert eine Anwesenheit. Was nicht angegeben ist, bleibt. Wer Zeiten selbst korrigiert, hat sie
    /// bestätigt: <c>manual</c> statt „automatisch ausgestempelt“ oder „von Odoo eingefügt“.
    /// </summary>
    public async Task UpdateAttendanceAsync(int id, DateTimeOffset? checkIn = null, DateTimeOffset? checkOut = null,
                                            bool confirmsCheckIn = false, bool confirmsCheckOut = false)
    {
        var values = new JsonObject();
        if (checkIn is { } start) values["check_in"] = OdooDateTime.Format(start);
        if (checkOut is { } end) values["check_out"] = OdooDateTime.Format(end);
        if (Schema.AttendanceHasModes)
        {
            if (confirmsCheckIn) values["in_mode"] = "manual";
            if (confirmsCheckOut) values["out_mode"] = "manual";
        }
        if (values.Count == 0) return;
        await WriteAsync("hr.attendance", id, values);
    }

    public Task DeleteAttendanceAsync(int id) => CallAsync("hr.attendance", "unlink", [id]);

    private static List<AttendancePeriod> Periods(JsonNode? rows)
    {
        var periods = new List<AttendancePeriod>();
        foreach (var row in rows.ArrayValue() ?? [])
        {
            if (row.Field("id").IntValue() is not { } id
                || row.Field("check_in").StringValue() is not { } checkInText
                || OdooDateTime.Parse(checkInText) is not { } checkIn) continue;
            var checkOut = row.Field("check_out").StringValue() is { } checkOutText ? OdooDateTime.Parse(checkOutText) : null;
            var period = new AttendancePeriod(id, checkIn, checkOut, row.Field("in_mode").StringValue(),
                                              row.Field("out_mode").StringValue());
            // Die Abwesenheitserkennung von Odoo legt für Tage ohne Anwesenheit Einträge von einer Sekunde an
            // (beide Seiten „technical“). Das ist keine Anwesenheit.
            if (period.IsTechnical && period.OutMode == "technical" && checkOut is { } end && (end - checkIn).TotalSeconds < 60)
            {
                continue;
            }
            periods.Add(period);
        }
        return periods;
    }
}
