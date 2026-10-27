// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using Zeiterfassung.Demo;
using Zeiterfassung.Odoo;

namespace Zeiterfassung.Tests;

/// <summary>JSON-2</summary>
public sealed class Json2TransportTests
{
    private const string Server = "https://odoo.example.de";
    private static readonly Dictionary<string, JsonNode?> NoContext = [];

    [Fact(DisplayName = "baut die Anfrage wie in der Odoo-Dokumentation")]
    public async Task BuildsRequest()
    {
        var http = new StubHttpSender(_ => StubHttpSender.Json(new JsonArray(new JsonObject { ["id"] = 1 })));
        var transport = new Json2Transport(Server, "geheim", "demo", http);
        await transport.ExecuteAsync(
            new OdooCall("res.users", "read", [7], new Dictionary<string, JsonNode?> { ["fields"] = new JsonArray("name") }),
            new Dictionary<string, JsonNode?> { ["lang"] = "de_DE" });
        var request = Expect.Require(http.Requests.FirstOrDefault());
        Assert.Equal("https://odoo.example.de/json/2/res.users/read", request.Url.AbsoluteUri);
        Assert.Equal("POST", request.Method);
        Assert.Equal("bearer geheim", request.Header("Authorization"));
        Assert.Equal("demo", request.Header("X-Odoo-Database"));
        Assert.True(request.Header("Content-Type")?.StartsWith("application/json", StringComparison.Ordinal));
        var sent = Expect.Require(StubHttpSender.Body(request));
        Expect.Json(Json.Ints([7]), sent.Field("ids"));
        Expect.Json(new JsonArray("name"), sent.Field("fields"));
        Expect.Json(new JsonObject { ["lang"] = "de_DE" }, sent.Field("context"));
    }

    [Fact(DisplayName = "ohne Datenbank kein Kopf X-Odoo-Database, Modellmethoden ohne ids")]
    public async Task OmitsOptionalParts()
    {
        var http = new StubHttpSender(_ => StubHttpSender.Json(new JsonArray()));
        var transport = new Json2Transport(Server, "geheim", null, http);
        await transport.ExecuteAsync(new OdooCall("project.project", "search_read"), NoContext);
        var request = Expect.Require(http.Requests.FirstOrDefault());
        Assert.Null(request.Header("X-Odoo-Database"));
        Assert.Null(StubHttpSender.Body(request).Field("ids"));
        Assert.Null(StubHttpSender.Body(request).Field("context"));
    }

    [Fact(DisplayName = "ordnet Fehler von Odoo zu")]
    public async Task MapsErrors()
    {
        static Json2Transport Failing(HttpReply reply) => new(Server, "k", null, new StubHttpSender(_ => reply));
        var call = new OdooCall("hr.attendance", "create");
        // Wie mehrere #expect: jeder Fall wird geprüft, auch wenn ein früherer scheitert.
        await Assert.MultipleAsync(
            () => Expect.OdooError(OdooErrorKind.Unauthorized, "", () =>
                Failing(StubHttpSender.Json(new JsonObject { ["name"] = "werkzeug.exceptions.Unauthorized", ["message"] = "Invalid apikey" }, 401))
                    .ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.AccessDenied, "Keine Berechtigung", () =>
                Failing(StubHttpSender.Json(new JsonObject { ["name"] = "odoo.exceptions.AccessError", ["message"] = "Keine Berechtigung" }, 403))
                    .ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.Rejected, "Bitte eine Aufgabe wählen", () =>
                Failing(StubHttpSender.Json(new JsonObject { ["name"] = "odoo.exceptions.ValidationError", ["message"] = "Bitte eine Aufgabe wählen" }, 422))
                    .ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.NotFound, "Die Schnittstelle wurde nicht gefunden (HTTP 404).", () =>
                Failing(StubHttpSender.Html(404)).ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.UnexpectedResponse, "Die Antwort war kein JSON (HTTP 200).", () =>
                Failing(StubHttpSender.Html(200)).ExecuteAsync(call, NoContext)));
    }

    [Fact(DisplayName = "folgt keiner Umleitung: Der API-Schlüssel geht nur an die eingetragene Adresse")]
    public async Task RefusesRedirects()
    {
        const string target = "https://anderer-server.example.com/json/2/res.users/context_get";
        var http = new StubHttpSender(_ => StubHttpSender.Redirect(target));
        var transport = new Json2Transport(Server, "geheim", null, http);
        await Expect.OdooError(OdooErrorKind.Redirected, target, () =>
            transport.ExecuteAsync(new OdooCall("res.users", "context_get"), NoContext));
        var legacy = new StubHttpSender(_ => StubHttpSender.Redirect("https://anderer-server.example.com/jsonrpc", 307));
        await Expect.OdooError(OdooErrorKind.Redirected, "https://anderer-server.example.com/jsonrpc", () =>
            JsonRpcTransport.CallAsync(Server, legacy, "common", "version", []));
    }
}

/// <summary>JSON-RPC</summary>
public sealed class JsonRpcTransportTests
{
    private const string Server = "https://odoo.example.de";
    private static readonly Dictionary<string, JsonNode?> NoContext = [];

    [Fact(DisplayName = "execute_kw mit ids, benannten Argumenten und Kontext")]
    public async Task BuildsExecuteKW()
    {
        var http = new StubHttpSender(_ => StubHttpSender.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = true }));
        var transport = new JsonRpcTransport(Server, "demo", 7, "geheim", http);
        await transport.ExecuteAsync(
            new OdooCall("hr.attendance", "write", [5],
                         new Dictionary<string, JsonNode?> { ["vals"] = new JsonObject { ["check_out"] = "2026-10-23 12:00:00" } }),
            new Dictionary<string, JsonNode?> { ["lang"] = "de_DE" });
        var request = Expect.Require(http.Requests.FirstOrDefault());
        Assert.Equal("https://odoo.example.de/jsonrpc", request.Url.AbsoluteUri);
        var parameters = Expect.Require(StubHttpSender.Body(request).Field("params"));
        Assert.Equal("object", parameters.Field("service").StringValue());
        Assert.Equal("execute_kw", parameters.Field("method").StringValue());
        var args = Expect.Require(parameters.Field("args").ArrayValue());
        Expect.Json(new JsonArray("demo", 7, "geheim", "hr.attendance", "write"),
                    new JsonArray(args.Take(5).Select(item => item?.DeepClone()).ToArray()));
        Expect.Json(new JsonArray(Json.Ints([5])), args[5]);
        Expect.Json(new JsonObject { ["check_out"] = "2026-10-23 12:00:00" }, args[6].Field("vals"));
        Expect.Json(new JsonObject { ["lang"] = "de_DE" }, args[6].Field("context"));
    }

    [Fact(DisplayName = "create übergibt die Werte als Position, wie Odoo es verlangt")]
    public async Task CreateIsPositional()
    {
        var http = new StubHttpSender(_ => StubHttpSender.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonArray(12) }));
        var transport = new JsonRpcTransport(Server, "demo", 7, "geheim", http);
        await transport.ExecuteAsync(
            new OdooCall("account.analytic.line", "create", null,
                         new Dictionary<string, JsonNode?> { ["vals_list"] = new JsonArray(new JsonObject { ["name"] = "x" }) },
                         ["vals_list"]),
            NoContext);
        var request = Expect.Require(http.Requests.FirstOrDefault());
        var args = Expect.Require(StubHttpSender.Body(request).Field("params").Field("args").ArrayValue());
        Expect.Json(new JsonArray(new JsonArray(new JsonObject { ["name"] = "x" })), args[5]);
        Expect.Json(new JsonObject(), args[6]);
    }

    [Fact(DisplayName = "ordnet Fehler zu")]
    public async Task MapsErrors()
    {
        static StubHttpSender Reply(string name, int code = 200) => new(_ => StubHttpSender.Json(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = "Odoo Server Error",
                ["data"] = new JsonObject { ["name"] = name, ["message"] = "Meldung" },
            },
        }));
        var call = new OdooCall("res.users", "context_get");
        // Wie mehrere #expect: jeder Fall wird geprüft, auch wenn ein früherer scheitert.
        await Assert.MultipleAsync(
            () => Expect.OdooError(OdooErrorKind.Unauthorized, "", () =>
                new JsonRpcTransport(Server, "d", 1, "k", Reply("odoo.exceptions.AccessDenied")).ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.NotFound, "Meldung", () =>
                new JsonRpcTransport(Server, "d", 1, "k", Reply("builtins.KeyError")).ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.Rejected, "Meldung", () =>
                new JsonRpcTransport(Server, "d", 1, "k", Reply("odoo.exceptions.UserError")).ExecuteAsync(call, NoContext)),
            () => Expect.OdooError(OdooErrorKind.Unauthorized, "", () =>
                new JsonRpcTransport(Server, "d", 1, "k", Reply("odoo.http.SessionExpiredException", 100)).ExecuteAsync(call, NoContext)));
    }

    [Theory(DisplayName = "ein fehlendes Modell ist „nicht installiert“, auch in übersetzten Meldungen")]
    [InlineData("Object hr.attendance doesn't exist")]
    [InlineData("Objekt hr.attendance existiert nicht")]
    [InlineData("L'objet hr.attendance n'existe pas !")]
    public void MissingModel(string message)
    {
        var error = OdooException.FromOdoo("odoo.exceptions.UserError", message, 200);
        Assert.Equal((OdooErrorKind.NotFound, message), (error.Kind, error.Detail));
    }
}

/// <summary>Verbindung herstellen</summary>
public sealed class ConnectorTests
{
    private const string Url = "https://odoo.example.de";

    [Fact(DisplayName = "Odoo 19: API-Schlüssel genügt")]
    public async Task Json2()
    {
        var odoo = new FakeOdoo(FakeOdooMode.Odoo19);
        var session = await new Connector(odoo.Client()).ConnectAsync(Url, odoo.ApiKey, null, null);
        Assert.Equal(OdooInterface.Json2, session.Interface);
        Assert.Equal(7, session.Uid);
        Assert.Equal("de_DE", session.Language);
        Assert.Equal(19, session.ServerMajorVersion);
    }

    [Fact(DisplayName = "Odoo 19: mehrere Datenbanken verlangen den Namen, statt nach dem Benutzernamen zu fragen")]
    public async Task Json2NeedsDatabase()
    {
        var odoo = new FakeOdoo(FakeOdooMode.Odoo19) { Databases = ["demo", "test"] };
        var connector = new Connector(odoo.Client());
        await Expect.OdooError(OdooErrorKind.DatabaseRequired, "", () => connector.ConnectAsync(Url, odoo.ApiKey, null, null));
        var session = await connector.ConnectAsync(Url, odoo.ApiKey, null, "test");
        Assert.Equal(OdooInterface.Json2, session.Interface);
        Assert.Equal("test", session.Database);
    }

    [Fact(DisplayName = "Odoo 19: falscher Schlüssel")]
    public async Task Json2Unauthorized()
    {
        var odoo = new FakeOdoo(FakeOdooMode.Odoo19);
        await Expect.OdooError(OdooErrorKind.Unauthorized, "", () =>
            new Connector(odoo.Client()).ConnectAsync(Url, "falsch", null, null));
    }

    [Fact(DisplayName = "Odoo 17: verlangt den Benutzernamen und meldet sich per JSON-RPC an")]
    public async Task Legacy()
    {
        var odoo = new FakeOdoo(FakeOdooMode.Odoo17);
        var connector = new Connector(odoo.Client());
        await Expect.OdooError(OdooErrorKind.LoginRequired, "", () => connector.ConnectAsync(Url, odoo.ApiKey, null, null));
        var session = await connector.ConnectAsync(Url, odoo.ApiKey, odoo.Login, null);
        Assert.Equal(OdooInterface.JsonRpc, session.Interface);
        Assert.Equal("demo", session.Database);
        Assert.Equal(odoo.Login, session.Login);
        Assert.Equal(17, session.ServerMajorVersion);
    }

    [Fact(DisplayName = "Odoo 17: mehrere Datenbanken verlangen den Namen")]
    public async Task LegacyNeedsDatabase()
    {
        var odoo = new FakeOdoo(FakeOdooMode.Odoo17) { Databases = ["demo", "test"] };
        var connector = new Connector(odoo.Client());
        await Expect.OdooError(OdooErrorKind.DatabaseRequired, "", () => connector.ConnectAsync(Url, odoo.ApiKey, odoo.Login, null));
        var session = await connector.ConnectAsync(Url, odoo.ApiKey, odoo.Login, "test");
        Assert.Equal("test", session.Database);
    }

    [Fact(DisplayName = "Odoo 17: falsches Passwort")]
    public async Task LegacyUnauthorized()
    {
        var odoo = new FakeOdoo(FakeOdooMode.Odoo17);
        await Expect.OdooError(OdooErrorKind.Unauthorized, "", () =>
            new Connector(odoo.Client()).ConnectAsync(Url, "falsch", odoo.Login, null));
    }

    [Fact(DisplayName = "ohne Netz")]
    public async Task Offline()
    {
        var odoo = new FakeOdoo { Offline = true };
        await Expect.OdooError(OdooErrorKind.Offline, "Die Netzwerkverbindung wurde unterbrochen.", () =>
            new Connector(odoo.Client()).ConnectAsync(Url, odoo.ApiKey, null, null));
    }
}
