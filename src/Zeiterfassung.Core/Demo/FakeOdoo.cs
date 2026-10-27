// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

// Ein simuliertes Odoo: für Unit-Tests, für Bildschirmfotos mit erfundenen Daten und für den Demo-Modus,
// in dem man die App ohne eigenes Odoo ausprobiert.

using System.Text;
using System.Text.Json.Nodes;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Demo;

/// <summary>HTTP-Client für Tests: beantwortet Anfragen über eine Funktion und merkt sie sich.</summary>
public sealed class StubHttpSender(Func<HttpRequest, HttpReply> handler) : IHttpSender
{
    private readonly List<HttpRequest> recorded = [];

    public IReadOnlyList<HttpRequest> Requests
    {
        get
        {
            lock (recorded) return recorded.ToList();
        }
    }

    public Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        lock (recorded) recorded.Add(request);
        try
        {
            return Task.FromResult(handler(request));
        }
        catch (Exception error)
        {
            return Task.FromException<HttpReply>(error);
        }
    }

    public static HttpReply Json(JsonNode? value, int status = 200) =>
        new(status, Headers("application/json; charset=utf-8"), Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null"));

    public static HttpReply Html(int status) =>
        new(status, Headers("text/html; charset=utf-8"), Encoding.UTF8.GetBytes("<html>Seite nicht gefunden</html>"));

    public static HttpReply Redirect(string location, int status = 301)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "text/html; charset=utf-8",
            ["Location"] = location,
        };
        return new HttpReply(status, headers, []);
    }

    private static Dictionary<string, string> Headers(string contentType) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType };

    public static JsonNode? Body(HttpRequest request) => request.Body is { } body ? Odoo.Json.Parse(body) : null;
}

public enum FakeOdooMode
{
    Odoo19,
    /// <summary>Ohne JSON-2: Odoo 18 leitet <c>/json/…</c> auf seine Anmeldeseite um.</summary>
    Odoo18,
    Odoo17,
}

/// <summary>
/// Ein kleines Odoo im Arbeitsspeicher. Beantwortet JSON-2 (wie Odoo 19) oder nur JSON-RPC (wie Odoo 17/18)
/// und hält Projekte, Aufgaben, Zeiteinträge und Anwesenheiten. Dient den Tests, den Bildschirmfotos und dem
/// Demo-Modus. Alle Namen sind erfunden.
/// </summary>
public sealed class FakeOdoo(FakeOdooMode mode = FakeOdooMode.Odoo19)
{
    public sealed record Line
    {
        public required int Id { get; init; }
        public required string Date { get; set; }
        public required string Name { get; set; }
        public required int ProjectId { get; set; }
        public int? TaskId { get; set; }
        public required double Hours { get; set; }
        /// <summary>Ohne Angabe die Hauptfirma und der Mitarbeiter darin.</summary>
        public int? CompanyId { get; set; }
        public int? EmployeeId { get; set; }
        /// <summary>Abgerechnet: Dauer, Datum, Projekt und Aufgabe stehen fest, löschen geht nicht.</summary>
        public bool Invoiced { get; init; }
    }

    public sealed record Attendance
    {
        public required int Id { get; init; }
        public required string CheckIn { get; set; }
        public string? CheckOut { get; set; }
        public string? InMode { get; set; }
        public string? OutMode { get; set; }
        /// <summary>Ohne Angabe der Mitarbeiter der Hauptfirma.</summary>
        public int? EmployeeId { get; init; }
    }

    private readonly object gate = new();
    private int nextId = 1000;
    private bool losesResponse;
    private readonly List<string> log = [];

    public FakeOdooMode Mode { get; set; } = mode;
    public string ApiKey { get; set; } = "test-schluessel-123";
    public string Login { get; } = "erika@example.com";
    public List<string> Databases { get; set; } = ["demo"];
    public int Uid { get; set; } = 7;
    public string UserName { get; set; } = "Erika Mustermann";
    public int? EmployeeId { get; set; } = 3;
    public bool AttendanceInstalled { get; set; } = true;
    public bool CanStamp { get; set; } = true;
    /// <summary>
    /// Das Recht zu stempeln scheint da, eine Datensatzregel lehnt das Anlegen dann aber ab („Sachbearbeiter:
    /// Anwesenheiten verwalten“, ohne eigener Anwesenheitsgenehmiger zu sein).
    /// </summary>
    public bool StampDeniedByRule { get; set; }
    public bool Offline { get; set; }
    public bool FailNextCreate { get; set; }
    /// <summary>Legt den nächsten Zeiteintrag an, aber die Antwort geht unterwegs verloren.</summary>
    public bool LoseNextCreateResponse { get; set; }
    public bool FailAttendanceRead { get; set; }
    public bool FailNextWrite { get; set; }
    /// <summary>Firmen, die der Benutzer sehen darf; die erste ist seine Hauptfirma.</summary>
    public List<(int Id, string Name)> Companies { get; set; } = [(1, "Muster Agentur GmbH")];
    /// <summary>Mitarbeiter des Benutzers in weiteren Firmen; <see cref="EmployeeId"/> ist der in der Hauptfirma.</summary>
    public List<(int Id, int CompanyId)> OtherEmployees { get; set; } = [];
    /// <summary>Firma je Projekt; Projekte ohne Eintrag gehören allen Firmen.</summary>
    public Dictionary<int, int> ProjectCompanies { get; set; } = [];
    /// <summary>Wie das Modul Arbeitszeitgesetz: Fehlt Pause, fügt Odoo sie beim Sichern ein.</summary>
    public bool BreakModule { get; set; }
    public BreakRules BreakRules { get; set; } = new();
    public List<(int Id, string Name)> Projects { get; set; } =
    [
        (1, "Interne Arbeiten"),
        (2, "Website-Betreuung (Muster GmbH)"),
        (3, "Kundenmagazin (Beispiel AG)"),
        (4, "Online-Shop (Demo KG)"),
    ];
    public Dictionary<int, List<(int Id, string Name)>> Tasks { get; set; } = new()
    {
        [1] = [(11, "Verwaltung"), (12, "Weiterbildung")],
        [2] = [(21, "Wartung")],
        [3] = [(31, "Text"), (32, "Layout")],
        [4] = [],
    };
    /// <summary>Erledigte Aufgaben: Sie stehen nicht mehr zur Auswahl, aber noch in alten Einträgen.</summary>
    public Dictionary<int, string> ClosedTasks { get; set; } = [];
    public List<Line> Lines { get; set; } = [];
    public List<Attendance> Attendances { get; set; } = [];
    /// <summary>Die Tests prüfen anhand des Protokolls, was die App aufgerufen hat; im Demo-Modus würde es nur wachsen.</summary>
    public bool LogsCalls { get; set; } = true;

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (gate) return log.ToList();
        }
    }

    public StubHttpSender Client() => new(Reply);

    public T WithLock<T>(Func<T> body)
    {
        lock (gate) return body();
    }

    public void WithLock(Action body)
    {
        lock (gate) body();
    }

    // Anfragen

    public HttpReply Reply(HttpRequest request)
    {
        lock (gate)
        {
            if (Offline) throw OdooException.Offline("Die Netzwerkverbindung wurde unterbrochen.");
            var reply = Route(request);
            if (losesResponse)
            {
                losesResponse = false;
                throw OdooException.Offline("Zeitüberschreitung bei der Anfrage.");
            }
            return reply;
        }
    }

    private HttpReply Route(HttpRequest request)
    {
        var path = request.Url.AbsolutePath;
        if (Mode == FakeOdooMode.Odoo18 && path.StartsWith("/json/", StringComparison.Ordinal))
        {
            return StubHttpSender.Redirect("/web/login?redirect=" + Uri.EscapeDataString(path), 303);
        }
        if (path == "/json/version")
        {
            return Mode == FakeOdooMode.Odoo19
                ? StubHttpSender.Json(new JsonObject { ["version_info"] = new JsonArray(19, 0, 0, "final", 0, ""), ["version"] = "19.0" })
                : StubHttpSender.Html(404);
        }
        if (path.StartsWith("/json/2/", StringComparison.Ordinal))
        {
            if (Mode != FakeOdooMode.Odoo19) return StubHttpSender.Html(404);
            // Bei mehreren Datenbanken gibt es JSON-2 nur mit Angabe der Datenbank.
            var database = request.Header("X-Odoo-Database");
            if (Databases.Count != 1 && !Databases.Contains(database ?? "")) return StubHttpSender.Html(404);
            return Json2(request, path);
        }
        if (path == "/jsonrpc") return JsonRpc(request);
        return StubHttpSender.Html(404);
    }

    private HttpReply Json2(HttpRequest request, string path)
    {
        if (request.Header("Authorization") != $"bearer {ApiKey}")
        {
            return StubHttpSender.Json(new JsonObject
            {
                ["name"] = "werkzeug.exceptions.Unauthorized",
                ["message"] = "Invalid apikey",
                ["arguments"] = new JsonArray("Invalid apikey", 401),
            }, 401);
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var model = parts[2];
        var method = parts[3];
        var kwargs = StubHttpSender.Body(request) as JsonObject ?? new JsonObject();
        var ids = kwargs["ids"].ArrayValue()?.Select(item => item.IntValue()).OfType<int>().ToList();
        kwargs.Remove("ids");
        kwargs.Remove("context");
        var result = Dispatch(model, method, ids, kwargs);
        return result.Failure is { } failure
            ? StubHttpSender.Json(new JsonObject { ["name"] = failure.Name, ["message"] = failure.Message }, failure.Status)
            : StubHttpSender.Json(result.Value);
    }

    private HttpReply JsonRpc(HttpRequest request)
    {
        var parameters = StubHttpSender.Body(request).Field("params");
        var service = parameters.Field("service").StringValue() ?? "";
        var method = parameters.Field("method").StringValue() ?? "";
        var args = parameters.Field("args").ArrayValue()?.Select(item => item?.DeepClone()).ToList() ?? [];

        static HttpReply Result(JsonNode? value) =>
            StubHttpSender.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = value });

        static HttpReply Error(string name, string message) =>
            StubHttpSender.Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["error"] = new JsonObject
                {
                    ["code"] = 200,
                    ["message"] = "Odoo Server Error",
                    ["data"] = new JsonObject { ["name"] = name, ["message"] = message },
                },
            });

        switch (service, method)
        {
            case ("common", "version"):
                var major = MajorVersion;
                return Result(new JsonObject
                {
                    ["server_version"] = $"{major}.0",
                    ["server_version_info"] = new JsonArray(major, 0, 0, "final", 0, ""),
                });
            case ("db", "list"):
                return Databases.Count == 1
                    ? Result(new JsonArray(Databases.Select(name => (JsonNode?)name).ToArray()))
                    : Error("odoo.exceptions.AccessDenied", "Access Denied");
            case ("common", "authenticate"):
                var ok = args.Count >= 3 && Databases.Contains(args[0].StringValue() ?? "")
                         && args[1].StringValue() == Login && args[2].StringValue() == ApiKey;
                return Result(ok ? JsonValue.Create(Uid) : JsonValue.Create(false));
            case ("object", "execute_kw"):
                if (args.Count < 7 || args[1].IntValue() != Uid || args[2].StringValue() != ApiKey)
                {
                    return Error("odoo.exceptions.AccessDenied", "Access Denied");
                }
                var model = args[3].StringValue() ?? "";
                var objectMethod = args[4].StringValue() ?? "";
                var positional = args[5].ArrayValue()?.Select(item => item?.DeepClone()).ToList() ?? [];
                var kwargs = args[6] as JsonObject ?? new JsonObject();
                kwargs.Remove("context");
                List<int>? ids = null;
                if (objectMethod is "read" or "write" or "unlink" or "has_access" && positional.Count > 0)
                {
                    ids = positional[0].ArrayValue()?.Select(item => item.IntValue()).OfType<int>().ToList();
                    positional.RemoveAt(0);
                }
                if (objectMethod == "create")
                {
                    if (positional.Count == 0) return Error("builtins.IndexError", "tuple index out of range");
                    kwargs["vals_list"] = positional[0];
                }
                // Bis Odoo 18 heißt der Parameter von `write` bei Zeiteinträgen `values`; nur als Position passt er.
                if (objectMethod == "write" && model == "account.analytic.line" && Mode != FakeOdooMode.Odoo19
                    && kwargs.ContainsKey("vals"))
                {
                    return Error("builtins.TypeError", "AccountAnalyticLine.write() got an unexpected keyword argument 'vals'");
                }
                if (objectMethod == "write" && positional.Count > 0) kwargs["vals"] = positional[0];
                var result = Dispatch(model, objectMethod, ids, kwargs);
                return result.Failure is { } failure ? Error(failure.Name, failure.Message) : Result(result.Value);
            default:
                return Error("builtins.KeyError", service);
        }
    }

    private int MajorVersion => Mode switch
    {
        FakeOdooMode.Odoo19 => 19,
        FakeOdooMode.Odoo18 => 18,
        _ => 17,
    };

    // Modelle

    private sealed record Failure(string Name, string Message, int Status);

    private readonly record struct Outcome(JsonNode? Value, Failure? Failure)
    {
        public static Outcome Ok(JsonNode? value) => new(value, null);
        public static Outcome Fail(string name, string message, int status) => new(null, new Failure(name, message, status));
    }

    private Outcome Dispatch(string model, string method, List<int>? ids, JsonObject kwargs)
    {
        if (LogsCalls) log.Add($"{model}.{method}");
        var domain = kwargs["domain"].ArrayValue()?.ToList() ?? [];
        switch (model, method)
        {
            case ("res.users", "context_get"):
                return Outcome.Ok(new JsonObject { ["lang"] = "de_DE", ["tz"] = "Europe/Berlin", ["uid"] = Uid });
            case ("res.users", "read"):
                var record = new JsonObject
                {
                    ["id"] = Uid,
                    ["name"] = UserName,
                    ["company_id"] = CompanyReference(MainCompanyId),
                    ["company_ids"] = Odoo.Json.Ints(Companies.Select(company => company.Id)),
                };
                // Wie Odoo: `employee_ids` nennt nur den Mitarbeiter der Hauptfirma.
                if (EmployeeId is { } employeeId)
                {
                    record["employee_id"] = new JsonArray(employeeId, UserName);
                    record["employee_ids"] = new JsonArray(employeeId);
                }
                else
                {
                    record["employee_id"] = false;
                    record["employee_ids"] = new JsonArray();
                }
                return Outcome.Ok(new JsonArray(record));
            case ("res.company", "read"):
                var wanted = ids ?? [];
                return Outcome.Ok(new JsonArray(Companies.Where(company => wanted.Contains(company.Id)).Select(company =>
                {
                    var row = new JsonObject { ["id"] = company.Id, ["name"] = company.Name };
                    if (BreakModule)
                    {
                        row["arbzg_break_hours_1"] = BreakRules.FirstThreshold / 60.0;
                        row["arbzg_break_minutes_1"] = BreakRules.FirstBreak;
                        row["arbzg_break_hours_2"] = BreakRules.SecondThreshold / 60.0;
                        row["arbzg_break_minutes_2"] = BreakRules.SecondBreak;
                        row["arbzg_min_break_minutes"] = BreakRules.MinimumGap;
                        row["arbzg_max_hours"] = BreakRules.MaximumWork / 60.0;
                    }
                    return (JsonNode?)row;
                }).ToArray()));
            case ("hr.employee", "search_read"):
                return Outcome.Ok(new JsonArray(AllEmployees
                    .Select(employee => (JsonNode?)new JsonObject { ["id"] = employee.Id, ["company_id"] = CompanyReference(employee.CompanyId) })
                    .ToArray()));
            case ("account.analytic.line", "fields_get"):
                return Outcome.Ok(TypeMap(("project_id", "many2one"), ("task_id", "many2one"), ("unit_amount", "float"),
                                          ("company_id", "many2one"), ("employee_id", "many2one"),
                                          ("timesheet_invoice_id", "many2one")));
            case ("project.project", "fields_get"):
                return Outcome.Ok(Mode != FakeOdooMode.Odoo17
                    ? TypeMap(("allow_timesheets", "boolean"), ("is_template", "boolean"))
                    : TypeMap(("allow_timesheets", "boolean")));
            case ("project.task", "fields_get"):
                return Outcome.Ok(TypeMap(("is_closed", "boolean")));
            case ("hr.attendance", "fields_get"):
                // Fehlt die App, meldet JSON-2 „nicht gefunden“, JSON-RPC einen UserError (wie Odoo 17 bis 19).
                if (!AttendanceInstalled)
                {
                    return Mode == FakeOdooMode.Odoo19
                        ? Outcome.Fail("werkzeug.exceptions.NotFound", "the model 'hr.attendance' does not exist", 404)
                        : Outcome.Fail("odoo.exceptions.UserError", "Object hr.attendance doesn't exist", 200);
                }
                var fields = TypeMap(("check_in", "datetime"), ("check_out", "datetime"), ("in_mode", "selection"),
                                     ("out_mode", "selection"));
                if (BreakModule) fields["arbzg_missing_break"] = new JsonObject { ["type"] = "float" };
                return Outcome.Ok(fields);
            case ("hr.attendance", "has_access"):
                return Mode != FakeOdooMode.Odoo17
                    ? Outcome.Ok(CanStamp)
                    : Outcome.Fail("builtins.AttributeError", "has_access", 404);
            case ("hr.attendance", "check_access_rights"):
                return Outcome.Ok(CanStamp);
            case ("project.project", "search_read"):
                return Outcome.Ok(new JsonArray(Projects.Select(project => (JsonNode?)new JsonObject
                {
                    ["id"] = project.Id,
                    ["display_name"] = project.Name,
                    ["company_id"] = ProjectCompanies.TryGetValue(project.Id, out var company) ? CompanyReference(company) : false,
                }).ToArray()));
            case ("project.task", "search_read"):
                var projectId = Condition(domain, "project_id", "=").IntValue() ?? 0;
                return Outcome.Ok(new JsonArray((Tasks.GetValueOrDefault(projectId) ?? [])
                    .Select(task => (JsonNode?)new JsonObject { ["id"] = task.Id, ["name"] = task.Name }).ToArray()));
            case ("account.analytic.line", "search_read"):
                IEnumerable<Line> lines = Lines;
                if (Condition(domain, "date", "=").StringValue() is { } day) lines = lines.Where(line => line.Date == day);
                if (Condition(domain, "date", ">=").StringValue() is { } since)
                {
                    lines = lines.Where(line => string.CompareOrdinal(line.Date, since) >= 0).Reverse();
                }
                return Outcome.Ok(new JsonArray(lines.Select(line => (JsonNode?)LineRecord(line)).ToArray()));
            case ("account.analytic.line", "create"):
                return CreateLine(kwargs);
            case ("account.analytic.line", "write"):
                return WriteLine(ids, kwargs);
            case ("account.analytic.line", "unlink"):
                var removed = ids ?? [];
                if (Lines.Any(line => removed.Contains(line.Id) && line.Invoiced))
                {
                    return Outcome.Fail("odoo.exceptions.UserError", "You cannot remove a timesheet that has already been invoiced.", 422);
                }
                Lines.RemoveAll(line => removed.Contains(line.Id));
                return Outcome.Ok(true);
            case ("hr.attendance", "search_read"):
                return ReadAttendances(domain);
            case ("hr.attendance", "create"):
                return CreateAttendance(kwargs);
            case ("hr.attendance", "write"):
                return WriteAttendance(ids, kwargs);
            case ("hr.attendance", "unlink"):
                if (!CanStamp || StampDeniedByRule) return Outcome.Fail("odoo.exceptions.AccessError", "Keine Berechtigung", 403);
                var gone = ids ?? [];
                Attendances.RemoveAll(attendance => gone.Contains(attendance.Id));
                return Outcome.Ok(true);
            default:
                return Outcome.Fail("werkzeug.exceptions.NotFound", $"{model}.{method} gibt es nicht", 404);
        }
    }

    private Outcome CreateLine(JsonObject kwargs)
    {
        if (FailNextCreate)
        {
            FailNextCreate = false;
            return Outcome.Fail("werkzeug.exceptions.ServiceUnavailable", "Wartung", 503);
        }
        if (kwargs["vals_list"].ArrayValue()?.FirstOrDefault() is not JsonObject vals)
        {
            return Outcome.Fail("builtins.TypeError", "vals_list fehlt", 422);
        }
        // Wie Odoo: die Firma des Projekts und der Mitarbeiter darin; mit nur einem Mitarbeiter dieser.
        var projectId = vals["project_id"].IntValue() ?? 0;
        int? projectCompany = ProjectCompanies.TryGetValue(projectId, out var company) ? company : null;
        var employees = AllEmployees;
        var employee = employees.Count == 1
            ? employees[0]
            : employees.FirstOrDefault(candidate => candidate.CompanyId == (projectCompany ?? MainCompanyId));
        if (employee == default)
        {
            return Outcome.Fail("odoo.exceptions.ValidationError",
                                "Timesheets must be created with an active employee in the selected companies.", 422);
        }
        nextId += 1;
        Lines.Add(new Line
        {
            Id = nextId,
            Date = vals["date"].StringValue() ?? "",
            Name = vals["name"].StringValue() ?? "",
            ProjectId = projectId,
            TaskId = vals["task_id"].IntValue(),
            Hours = vals["unit_amount"].DoubleValue() ?? 0,
            CompanyId = projectCompany ?? employee.CompanyId,
            EmployeeId = employee.Id,
        });
        if (LoseNextCreateResponse)
        {
            LoseNextCreateResponse = false;
            losesResponse = true;
        }
        return Outcome.Ok(new JsonArray(nextId));
    }

    private Outcome WriteLine(List<int>? ids, JsonObject kwargs)
    {
        if (TakeWriteFailure() is { } failure) return new Outcome(null, failure);
        var line = ids is [var id, ..] ? Lines.FirstOrDefault(candidate => candidate.Id == id) : null;
        if (line is null) return Outcome.Fail("odoo.exceptions.MissingError", "Datensatz fehlt", 422);
        var vals = kwargs["vals"] as JsonObject ?? new JsonObject();
        if (line.Invoiced && vals.Any(pair => pair.Key is "unit_amount" or "employee_id" or "project_id" or "task_id" or "date"))
        {
            return Outcome.Fail("odoo.exceptions.UserError", "You cannot modify timesheets that are already invoiced.", 422);
        }
        if (vals["name"].StringValue() is { } name) line.Name = name;
        // Wie Odoo: Das Projekt bringt seine Firma mit, der Mitarbeiter bleibt, wenn er nicht mitkommt.
        if (vals["project_id"].IntValue() is { } projectId)
        {
            line.ProjectId = projectId;
            if (ProjectCompanies.TryGetValue(projectId, out var company)) line.CompanyId = company;
        }
        if (vals.ContainsKey("task_id")) line.TaskId = vals["task_id"].IntValue();
        if (vals["unit_amount"].DoubleValue() is { } hours) line.Hours = hours;
        if (vals["date"].StringValue() is { } date) line.Date = date;
        if (vals["employee_id"].IntValue() is { } employeeId) line.EmployeeId = employeeId;
        return Outcome.Ok(true);
    }

    private Outcome ReadAttendances(List<JsonNode?> domain)
    {
        if (FailAttendanceRead) return Outcome.Fail("odoo.exceptions.AccessError", "Kein Zugriff auf Anwesenheiten", 403);
        IEnumerable<Attendance> result = Attendances;
        if (Condition(domain, "employee_id", "=").IntValue() is { } employee)
        {
            result = result.Where(attendance => Owner(attendance) == employee);
        }
        if (domain.Any(item => item.ArrayValue()?.FirstOrDefault().StringValue() == "check_out"))
        {
            result = result.Where(attendance => attendance.CheckOut is null);
        }
        if (Condition(domain, "check_in", ">=").StringValue() is { } from && Condition(domain, "check_in", "<").StringValue() is { } to)
        {
            result = result.Where(attendance =>
                string.CompareOrdinal(attendance.CheckIn, from) >= 0 && string.CompareOrdinal(attendance.CheckIn, to) < 0);
        }
        return Outcome.Ok(new JsonArray(result.OrderBy(attendance => attendance.CheckIn, StringComparer.Ordinal)
            .Select(attendance => (JsonNode?)new JsonObject
            {
                ["id"] = attendance.Id,
                ["check_in"] = attendance.CheckIn,
                ["check_out"] = attendance.CheckOut is { } checkOut ? JsonValue.Create(checkOut) : JsonValue.Create(false),
                ["in_mode"] = attendance.InMode is { } inMode ? JsonValue.Create(inMode) : JsonValue.Create(false),
                ["out_mode"] = attendance.OutMode is { } outMode ? JsonValue.Create(outMode) : JsonValue.Create(false),
            }).ToArray()));
    }

    private Outcome CreateAttendance(JsonObject kwargs)
    {
        if (!CanStamp || StampDeniedByRule) return Outcome.Fail("odoo.exceptions.AccessError", "Keine Berechtigung", 403);
        if (kwargs["vals_list"].ArrayValue()?.FirstOrDefault() is not JsonObject vals)
        {
            return Outcome.Fail("builtins.TypeError", "vals_list fehlt", 422);
        }
        var employee = vals["employee_id"].IntValue();
        var checkOut = vals["check_out"].StringValue();
        var record = new Attendance
        {
            Id = nextId + 1,
            CheckIn = vals["check_in"].StringValue() ?? "",
            CheckOut = checkOut,
            InMode = "manual",
            OutMode = checkOut is null ? null : "manual",
            EmployeeId = employee == EmployeeId ? null : employee,
        };
        if (AttendanceProblem(record) is { } failure) return new Outcome(null, failure);
        nextId += 1;
        Attendances.Add(record);
        InsertMissingBreak(record);
        return Outcome.Ok(new JsonArray(nextId));
    }

    private Outcome WriteAttendance(List<int>? ids, JsonObject kwargs)
    {
        if (!CanStamp || StampDeniedByRule) return Outcome.Fail("odoo.exceptions.AccessError", "Keine Berechtigung", 403);
        if (TakeWriteFailure() is { } writeFailure) return new Outcome(null, writeFailure);
        var index = ids is [var id, ..] ? Attendances.FindIndex(candidate => candidate.Id == id) : -1;
        if (index < 0) return Outcome.Fail("odoo.exceptions.MissingError", "Datensatz fehlt", 422);
        var vals = kwargs["vals"] as JsonObject ?? new JsonObject();
        var record = Attendances[index] with { };
        if (vals["check_in"].StringValue() is { } checkIn) record.CheckIn = checkIn;
        if (vals.ContainsKey("check_out")) record.CheckOut = vals["check_out"].StringValue();
        if (vals["in_mode"].StringValue() is { } inMode) record.InMode = inMode;
        if (vals["out_mode"].StringValue() is { } outMode) record.OutMode = outMode;
        if (AttendanceProblem(record) is { } failure) return new Outcome(null, failure);
        Attendances[index] = record;
        InsertMissingBreak(record);
        return Outcome.Ok(true);
    }

    private static JsonNode? Condition(List<JsonNode?> domain, string field, string op)
    {
        foreach (var item in domain)
        {
            if (item is JsonArray { Count: 3 } triple && triple[0].StringValue() == field && triple[1].StringValue() == op)
            {
                return triple[2];
            }
        }
        return null;
    }

    private static JsonObject TypeMap(params (string Field, string Type)[] fields)
    {
        var result = new JsonObject();
        foreach (var (field, type) in fields) result[field] = new JsonObject { ["type"] = type };
        return result;
    }

    private JsonObject LineRecord(Line line)
    {
        var projectName = Projects.FirstOrDefault(project => project.Id == line.ProjectId).Name ?? "";
        var taskName = (Tasks.GetValueOrDefault(line.ProjectId) ?? []).FirstOrDefault(task => task.Id == line.TaskId).Name
                       ?? (line.TaskId is { } closed ? ClosedTasks.GetValueOrDefault(closed) : null) ?? "";
        var employee = line.EmployeeId ?? EmployeeId;
        return new JsonObject
        {
            ["id"] = line.Id,
            ["name"] = line.Name,
            ["date"] = line.Date,
            ["project_id"] = new JsonArray(line.ProjectId, projectName),
            ["task_id"] = line.TaskId is { } taskId ? new JsonArray(taskId, taskName) : JsonValue.Create(false),
            ["unit_amount"] = line.Hours,
            ["company_id"] = CompanyReference(line.CompanyId ?? MainCompanyId),
            ["employee_id"] = employee is { } id ? new JsonArray(id, UserName) : JsonValue.Create(false),
            ["timesheet_invoice_id"] = line.Invoiced ? new JsonArray(9001, "RE/2026/0042") : JsonValue.Create(false),
        };
    }

    // Firmen und Mitarbeiter

    private int MainCompanyId => Companies.Count > 0 ? Companies[0].Id : 1;

    private JsonArray CompanyReference(int id) =>
        new(id, Companies.FirstOrDefault(company => company.Id == id).Name ?? $"Firma {id}");

    /// <summary>Alle Mitarbeiter des Benutzers, der der Hauptfirma zuerst.</summary>
    private List<(int Id, int CompanyId)> AllEmployees
    {
        get
        {
            var all = new List<(int Id, int CompanyId)>();
            if (EmployeeId is { } id) all.Add((id, MainCompanyId));
            all.AddRange(OtherEmployees);
            return all;
        }
    }

    private int? Owner(Attendance attendance) => attendance.EmployeeId ?? EmployeeId;

    private Failure? TakeWriteFailure()
    {
        if (!FailNextWrite) return null;
        FailNextWrite = false;
        return new Failure("werkzeug.exceptions.ServiceUnavailable", "Wartung", 503);
    }

    // Anwesenheiten prüfen wie Odoo

    private const string Forever = "9999-12-31 23:59:59";

    /// <summary>Ende vor Beginn oder Überschneidung mit einer anderen Anwesenheit desselben Mitarbeiters.</summary>
    private Failure? AttendanceProblem(Attendance record)
    {
        if (record.CheckOut is { } checkOut && string.CompareOrdinal(checkOut, record.CheckIn) < 0)
        {
            return new Failure("odoo.exceptions.ValidationError", "\"Check Out\" time cannot be earlier than \"Check In\" time.", 422);
        }
        var end = record.CheckOut ?? Forever;
        var clash = Attendances.FirstOrDefault(other =>
            other.Id != record.Id && Owner(other) == Owner(record)
            && string.CompareOrdinal(record.CheckIn, other.CheckOut ?? Forever) < 0
            && string.CompareOrdinal(end, other.CheckIn) > 0);
        if (clash is null) return null;
        return new Failure("odoo.exceptions.ValidationError",
                           $"Cannot create new attendance record for {UserName}, the employee was already checked in on {clash.CheckIn}",
                           422);
    }

    /// <summary>
    /// Wie das Modul Arbeitszeitgesetz: Fehlt an einem Tag Pause, teilt Odoo die längste Anwesenheit und legt die
    /// Pause in ihre Mitte; der zweite Teil ist „technical“ angelegt.
    /// </summary>
    private void InsertMissingBreak(Attendance changed)
    {
        if (!BreakModule) return;
        var day = changed.CheckIn.Length >= 10 ? changed.CheckIn[..10] : changed.CheckIn;
        var list = Attendances.Where(attendance => Owner(attendance) == Owner(changed)
                                                   && attendance.CheckIn.StartsWith(day, StringComparison.Ordinal))
                              .OrderBy(attendance => attendance.CheckIn, StringComparer.Ordinal)
                              .ToList();
        if (list.Count == 0 || list.Any(attendance => attendance.CheckOut is null)) return;
        static DateTimeOffset Date(string? text) => text is not null && OdooDateTime.Parse(text) is { } date ? date : DateTimeOffset.MinValue;
        static int Minutes(Attendance item) => (int)((Date(item.CheckOut) - Date(item.CheckIn)).TotalMinutes);
        var work = list.Sum(Minutes);
        var breaks = 0;
        for (var i = 1; i < list.Count; i++)
        {
            var gap = (int)((Date(list[i].CheckIn) - Date(list[i - 1].CheckOut)).TotalMinutes);
            if (gap >= BreakRules.MinimumGap || list[i].InMode == "technical") breaks += Math.Max(0, gap);
        }
        var missing = BreakRules.MissingBreak(work, breaks).Missing;
        var longest = list.Aggregate((best, item) => Minutes(item) > Minutes(best) ? item : best);
        var index = Attendances.FindIndex(attendance => attendance.Id == longest.Id);
        if (missing <= 0 || Minutes(longest) <= missing + 2 || index < 0) return;
        var start = Date(longest.CheckIn).AddMinutes((Minutes(longest) - missing) / 2);
        var end = start.AddMinutes(missing);
        var originalCheckOut = Attendances[index].CheckOut;
        Attendances[index] = Attendances[index] with { CheckOut = OdooDateTime.Format(start) };
        nextId += 1;
        Attendances.Add(new Attendance
        {
            Id = nextId,
            CheckIn = OdooDateTime.Format(end),
            CheckOut = originalCheckOut,
            InMode = "technical",
            OutMode = longest.OutMode,
            EmployeeId = longest.EmployeeId,
        });
    }
}
