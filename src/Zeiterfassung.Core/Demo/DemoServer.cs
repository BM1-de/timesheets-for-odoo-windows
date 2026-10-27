// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Demo;

/// <summary>
/// Ausprobieren ohne eigenes Odoo: Wer als Server „demo“ einträgt, arbeitet mit erfundenen Daten, die nur im
/// Arbeitsspeicher liegen. So prüft auch Microsoft die App, bevor sie in den Store kommt.
/// </summary>
public static class DemoServer
{
    /// <summary>Die Endung <c>.invalid</c> ist reserviert und kommt im Netz nie vor; keine Anfrage verlässt den PC.</summary>
    public const string Url = "https://demo.invalid";

    /// <summary>Der Demo-Server nimmt diesen Schlüssel, gleich was im Feld steht.</summary>
    public const string ApiKey = "demo";

    public static bool IsRequested(string server) => string.Equals(server.Trim(), "demo", StringComparison.OrdinalIgnoreCase);

    public static bool IsDemo(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Host == "demo.invalid";
}

/// <summary>Anfragen an den Demo-Server beantwortet ein Odoo im Arbeitsspeicher, alle anderen gehen ins Netz.</summary>
public sealed class DemoRoutingHttpSender(IHttpSender network, FakeOdoo? demo = null) : IHttpSender
{
    public FakeOdoo Demo { get; } = demo ?? DemoData.MakeOdoo(DateTimeOffset.Now, TimeZoneInfo.Local);

    public Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!DemoServer.IsDemo(request.Url.AbsoluteUri)) return network.SendAsync(request, cancellationToken);
        try
        {
            return Task.FromResult(Demo.Reply(request));
        }
        catch (Exception error)
        {
            return Task.FromException<HttpReply>(error);
        }
    }
}

/// <summary>
/// Erfundene Projekte, Aufgaben und Einträge für den Demo-Modus, in der Sprache der App: gestern ein voller
/// Arbeitstag mit einem schon abgerechneten Eintrag, heute zwei Einträge und noch nicht eingestempelt. Was man
/// einträgt, ändert oder stempelt, bleibt bis zum Beenden der App erhalten.
/// </summary>
public static class DemoData
{
    public static FakeOdoo MakeOdoo(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = CalendarDay.From(now, zone);
        var yesterday = today.AddingDays(-1);
        string Time(CalendarDay day, int hour, int minute) => OdooDateTime.Format(day.At(hour, minute, zone));

        var odoo = new FakeOdoo(FakeOdooMode.Odoo19);
        odoo.WithLock(() =>
        {
            odoo.ApiKey = DemoServer.ApiKey;
            odoo.LogsCalls = false;
            odoo.UserName = Loc.T("Erika Mustermann");
            odoo.Projects =
            [
                (1, Loc.T("Interne Arbeiten")),
                (2, Loc.T("Website-Betreuung (Muster GmbH)")),
                (3, Loc.T("Kundenmagazin (Beispiel AG)")),
                (4, Loc.T("Online-Shop (Demo KG)")),
                (5, Loc.T("Website-Relaunch (Musterhaus GmbH)")),
                (6, Loc.T("Social Media (Café Beispiel)")),
            ];
            // Das Online-Shop-Projekt hat keine Aufgaben: Dort bucht die App aufs Projekt.
            odoo.Tasks = new()
            {
                [1] = [(11, Loc.T("Verwaltung")), (12, Loc.T("Weiterbildung"))],
                [2] = [(21, Loc.T("Wartung")), (22, Loc.T("Umsetzung"))],
                [3] = [(31, Loc.T("Text")), (32, Loc.T("Layout"))],
                [4] = [],
                [5] = [(51, Loc.T("Konzept")), (52, Loc.T("Design"))],
                [6] = [(61, Loc.T("Redaktion"))],
            };
            odoo.Lines =
            [
                new() { Id = 1, Date = yesterday.OdooString, Name = Loc.T("Beiträge für den nächsten Monat"), ProjectId = 6, TaskId = 61, Hours = 2.5, Invoiced = true },
                new() { Id = 2, Date = yesterday.OdooString, Name = Loc.T("Kontaktformular angepasst"), ProjectId = 2, TaskId = 22, Hours = 1.5 },
                new() { Id = 3, Date = yesterday.OdooString, Name = Loc.T("Seitenstruktur abgestimmt"), ProjectId = 5, TaskId = 51, Hours = 4 },
                new() { Id = 4, Date = today.OdooString, Name = Loc.T("Plugin-Updates"), ProjectId = 2, TaskId = 21, Hours = 0.25 },
                new() { Id = 5, Date = today.OdooString, Name = Loc.T("Angebot vorbereitet"), ProjectId = 1, TaskId = 11, Hours = 2 },
            ];
            odoo.Attendances =
            [
                new() { Id = 1, CheckIn = Time(yesterday, 8, 10), CheckOut = Time(yesterday, 12, 5) },
                new() { Id = 2, CheckIn = Time(yesterday, 12, 45), CheckOut = Time(yesterday, 17, 10) },
            ];
        });
        return odoo;
    }
}
