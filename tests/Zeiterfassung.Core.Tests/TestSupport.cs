// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
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

    public static async Task<AppModel> SignedIn(FakeOdoo odoo, IHttpSender? http = null, MovingClock? clock = null)
    {
        var model = Make(odoo, http: http, clock: clock);
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
}
