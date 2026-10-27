// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Tests;

/// <summary>Feste Zeitzone und feste Uhrzeit für Tests: Freitag, 23. Oktober 2026, 14:52 in Berlin.</summary>
public static class TestClock
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 52, 0, TimeSpan.FromHours(2));

    /// <summary>Uhrzeit am Testtag in Berlin.</summary>
    public static DateTimeOffset At(int hour, int minute, int dayOffset = 0) =>
        CalendarDay.From(Now, Zone).AddingDays(dayOffset).At(hour, minute, Zone);
}

/// <summary>Uhr, die ein Test vorstellen kann, ab Freitag, 23. Oktober 2026, 14:52.</summary>
public sealed class MovingClock
{
    private readonly object gate = new();
    private DateTimeOffset current = TestClock.Now;

    public DateTimeOffset Now
    {
        get
        {
            lock (gate) return current;
        }
    }

    public void Advance(double seconds)
    {
        lock (gate) current = current.AddSeconds(seconds);
    }
}

/// <summary>
/// Ein Thread mit eigener Warteschlange, wie der Hauptthread der App: Was die App mit „Fire“ startet, läuft dort,
/// sobald der Test wartet. So verhalten sich die Abläufe wie in der Oberfläche.
/// </summary>
public static class MainThread
{
    private sealed class Context : SynchronizationContext
    {
        public readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue = [];

        public override void Post(SendOrPostCallback d, object? state) => Queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    public static void Run(Func<Task> test)
    {
        var previous = SynchronizationContext.Current;
        var context = new Context();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context.Queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context.Queue.GetConsumingEnumerable()) callback(state);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Lässt alles laufen, was gerade in der Warteschlange steht.</summary>
    public static async Task Settle()
    {
        for (var round = 0; round < 5; round++) await Task.Yield();
    }
}

/// <summary>
/// Hält die erste passende Anfrage an, bis der Test sie weiterlaufen lässt. So lässt sich prüfen, was passiert,
/// während die App auf Odoo wartet.
/// </summary>
public sealed class PausingHttpSender(IHttpSender inner, Func<HttpRequest, bool> pausesOn) : IHttpSender
{
    private readonly TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int paused;

    public async Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (pausesOn(request) && Interlocked.Exchange(ref paused, 1) == 0)
        {
            arrived.TrySetResult();
            await released.Task;
        }
        return await inner.SendAsync(request, cancellationToken);
    }

    /// <summary>Wartet, bis die Anfrage angehalten ist.</summary>
    public Task WaitUntilPaused() => arrived.Task;

    public void Resume() => released.TrySetResult();
}

/// <summary>Bausteine für Tests mit dem Modell.</summary>
public static class Models
{
    public static AppModel Make(FakeOdoo odoo, MemorySecretStore? secrets = null, IHttpSender? http = null,
                                MovingClock? clock = null, ISettingsStore? settings = null) =>
        new(new Preferences(settings ?? new MemorySettingsStore()), http ?? odoo.Client(), secrets ?? new MemorySecretStore(),
            TestClock.Zone, () => clock?.Now ?? TestClock.Now);

    public static async Task<AppModel> SignedIn(FakeOdoo odoo, IHttpSender? http = null, MovingClock? clock = null,
                                                ISettingsStore? settings = null, MemorySecretStore? secrets = null)
    {
        var model = Make(odoo, secrets, http, clock, settings);
        await model.SignInAsync("odoo.example.de", odoo.ApiKey, odoo.Mode == FakeOdooMode.Odoo19 ? null : odoo.Login, null);
        model.FinishSignIn();
        await model.RefreshAsync(force: true);
        return model;
    }

    /// <summary>Projekt „Website-Betreuung“ mit seiner einzigen Aufgabe, Beschreibung und Dauer.</summary>
    public static async Task FillDraft(AppModel model)
    {
        model.SelectProject(model.Projects.Single(project => project.Id == 2));
        await model.LoadTasksAsync(2);
        model.DraftDescription = "Plugin-Updates";
        model.DraftDurationText = "0:15";
    }

    /// <summary>Feld „von“ beim Bearbeiten einer Anwesenheit, wie <c>model.attendanceEditor?.from = text</c>.</summary>
    public static void SetEditorFrom(this AppModel model, string text) =>
        model.SetAttendanceEditorTimes(text, model.AttendanceEditor?.To ?? "");

    /// <summary>Feld „bis“ beim Bearbeiten einer Anwesenheit, wie <c>model.attendanceEditor?.to = text</c>.</summary>
    public static void SetEditorTo(this AppModel model, string text) =>
        model.SetAttendanceEditorTimes(model.AttendanceEditor?.From ?? "", text);
}

/// <summary>Datenzugriff ohne Modell: JSON-2 wie Odoo 19, sonst JSON-RPC mit der Datenbank „demo“.</summary>
public static class Services
{
    public const string Server = "https://odoo.example.de";

    public static OdooService For(FakeOdoo odoo)
    {
        IOdooTransport transport = odoo.Mode == FakeOdooMode.Odoo19
            ? new Json2Transport(Server, odoo.ApiKey, null, odoo.Client())
            : new JsonRpcTransport(Server, "demo", odoo.Uid, odoo.ApiKey, odoo.Client());
        return new OdooService(transport, odoo.Uid);
    }
}

/// <summary>Datensätze für das Odoo im Arbeitsspeicher, so knapp wie in den Swift-Tests.</summary>
public static class Fake
{
    public static FakeOdoo.Line Line(int id, string date, string name, int projectId, int? taskId, double hours,
                                     int? companyId = null, int? employeeId = null, bool invoiced = false) =>
        new()
        {
            Id = id, Date = date, Name = name, ProjectId = projectId, TaskId = taskId, Hours = hours,
            CompanyId = companyId, EmployeeId = employeeId, Invoiced = invoiced,
        };

    public static FakeOdoo.Attendance Attendance(int id, string checkIn, string? checkOut, string? inMode = null,
                                                 string? outMode = null) =>
        new() { Id = id, CheckIn = checkIn, CheckOut = checkOut, InMode = inMode, OutMode = outMode };
}

/// <summary>Prüfungen, die die Swift-Tests mit <c>#expect</c> schreiben und xUnit so nicht mitbringt.</summary>
public static class Expect
{
    /// <summary>Wie <c>try #require(x)</c>: Fehlt der Wert, bricht der Test ab, sonst geht es mit ihm weiter.</summary>
    public static T Require<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    /// <summary>Wie <c>#expect(throws: OdooError.x(detail))</c>: Art und Meldung des Fehlers müssen stimmen.</summary>
    public static async Task OdooError(OdooErrorKind kind, string detail, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<OdooException>(action);
        Assert.Equal((kind, detail), (error.Kind, error.Detail));
    }

    /// <summary>JSON gleichen Inhalts, wie <c>==</c> auf <c>JSONValue</c>.</summary>
    public static void Json(JsonNode? expected, JsonNode? actual)
    {
        if (JsonNode.DeepEquals(expected, actual)) return;
        Assert.Fail($"JSON verschieden.\nErwartet: {expected?.ToJsonString() ?? "null"}\nErhalten: {actual?.ToJsonString() ?? "null"}");
    }
}
