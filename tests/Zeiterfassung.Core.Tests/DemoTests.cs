// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;

namespace Zeiterfassung.Tests;

/// <summary>
/// Netz, das im Demo-Modus keine Anfrage bekommen darf. Wie <c>Issue.record</c> merkt es sich jede Anfrage, damit der
/// Test sie am Ende meldet; eine Ausnahme hier finge das Modell ab.
/// </summary>
file sealed class NoNetwork : IHttpSender
{
    private readonly List<string> requests = [];

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (requests) return requests.ToList();
        }
    }

    public Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        lock (requests) requests.Add($"Anfrage ins Netz: {request.Url.AbsoluteUri}");
        return Task.FromException<HttpReply>(OdooException.Offline("Kein Netz im Test"));
    }
}

/// <summary>Demo-Modus</summary>
public sealed class DemoTests
{
    private static AppModel MakeModel(IHttpSender network)
    {
        var demo = DemoData.MakeOdoo(TestClock.Now, TestClock.Zone);
        return new AppModel(new Preferences(new MemorySettingsStore()), new DemoRoutingHttpSender(network, demo),
                            new MemorySecretStore(), TestClock.Zone, () => TestClock.Now);
    }

    [Fact(DisplayName = "„demo“ als Server: anmelden, buchen und stempeln, ohne dass etwas ins Netz geht")]
    public void Demo() => MainThread.Run(async () =>
    {
        var network = new NoNetwork();
        var model = MakeModel(network);
        var found = await model.SignInAsync(" Demo ", "beliebig", null, null);
        Assert.Equal(FeatureStatus.Available, found.Timesheets);
        Assert.Equal(FeatureStatus.Available, found.Attendance);
        Assert.True(found.CanStamp);
        Assert.Equal("Demo", model.ServerDisplay);
        Assert.Null(model.OdooWebUrl);
        Assert.Equal("demo", model.Preferences.LoginHints?.Server);

        model.FinishSignIn();
        await model.RefreshAsync(force: true);
        Assert.Equal(2, model.Entries.Count);
        Assert.Equal(135, model.BookedMinutes);

        // Das Online-Shop-Projekt hat keine Aufgaben, gebucht wird aufs Projekt.
        model.SelectProject(model.Projects.Single(project => project.Id == 4));
        await model.LoadTasksAsync(4);
        model.DraftDescription = "Produktfotos";
        model.DraftDurationText = "1:30";
        await model.SaveAsync();
        Assert.Null(model.SaveError);
        Assert.Equal(3, model.Entries.Count);

        await model.ToggleAttendanceAsync();
        Assert.Null(model.StampError);
        Assert.NotNull(model.OpenPeriod);
        Assert.Empty(network.Requests);
    });

    [Fact(DisplayName = "andere Server gehen weiter ins Netz")]
    public void OtherServers() => MainThread.Run(async () =>
    {
        var odoo = new FakeOdoo();
        var model = MakeModel(odoo.Client());
        await model.SignInAsync("odoo.example.de", odoo.ApiKey, null, null);
        Assert.Equal("odoo.example.de", model.ServerDisplay);
        Assert.NotNull(model.OdooWebUrl);
    });

    [Fact(DisplayName = "die Daten liegen um den heutigen Tag")]
    public void Data()
    {
        var demo = DemoData.MakeOdoo(TestClock.Now, TestClock.Zone);
        demo.WithLock(() =>
        {
            Assert.Equal(2, demo.Lines.Count(line => line.Date == "2026-10-23"));
            Assert.Equal(3, demo.Lines.Count(line => line.Date == "2026-10-22"));
            // 08:10 Ortszeit, in Odoo als UTC
            Assert.Equal("2026-10-22 06:10:00", demo.Attendances.FirstOrDefault()?.CheckIn);
            Assert.All(demo.Attendances, attendance => Assert.NotNull(attendance.CheckOut));
        });
    }
}
