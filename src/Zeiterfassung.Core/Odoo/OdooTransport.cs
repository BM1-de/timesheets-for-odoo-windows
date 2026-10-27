// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using System.Text.Json.Nodes;
using Zeiterfassung.Support;

namespace Zeiterfassung.Odoo;

/// <summary>Aufruf einer Odoo-Methode, unabhängig von der Schnittstelle.</summary>
/// <param name="Ids">Datensätze, auf denen die Methode läuft; <c>null</c> für Methoden des Modells wie
/// <c>search_read</c> oder <c>create</c>.</param>
/// <param name="Arguments">Benannte Argumente, z. B. <c>domain</c> und <c>fields</c>.</param>
/// <param name="PositionalForJsonRpc">Argumente, die die ältere Schnittstelle (JSON-RPC) als Position statt als
/// Namen braucht, in dieser Reihenfolge. Odoo verlangt das bei <c>create</c>.</param>
public sealed record OdooCall(
    string Model,
    string Method,
    IReadOnlyList<int>? Ids = null,
    IReadOnlyDictionary<string, JsonNode?>? Arguments = null,
    IReadOnlyList<string>? PositionalForJsonRpc = null);

/// <summary>Eine Schnittstelle zu Odoo: JSON-2 (ab Odoo 19) oder JSON-RPC (Odoo 17 und 18).</summary>
public interface IOdooTransport
{
    Task<JsonNode?> ExecuteAsync(OdooCall call, IReadOnlyDictionary<string, JsonNode?> context);
}

/// <summary>JSON-2, die Schnittstelle ab Odoo 19: <c>POST /json/2/&lt;Modell&gt;/&lt;Methode&gt;</c> mit API-Schlüssel.</summary>
public sealed class Json2Transport(string baseUrl, string apiKey, string? database, IHttpSender http) : IOdooTransport
{
    public async Task<JsonNode?> ExecuteAsync(OdooCall call, IReadOnlyDictionary<string, JsonNode?> context)
    {
        var body = new JsonObject();
        foreach (var (name, value) in call.Arguments ?? new Dictionary<string, JsonNode?>()) body[name] = value?.DeepClone();
        if (call.Ids is { } ids) body["ids"] = Json.Ints(ids);
        if (context.Count > 0) body["context"] = ContextObject(context);

        var headers = new Dictionary<string, string> { ["Authorization"] = $"bearer {apiKey}" };
        if (!string.IsNullOrEmpty(database)) headers["X-Odoo-Database"] = database;
        var request = HttpRequests.PostJson(HttpRequests.Append(baseUrl, $"json/2/{call.Model}/{call.Method}"),
                                            Encoding.UTF8.GetBytes(body.ToJsonString()), headers);
        var reply = await http.SendAsync(request).ConfigureAwait(false);
        return Decode(reply);
    }

    internal static JsonObject ContextObject(IReadOnlyDictionary<string, JsonNode?> context)
    {
        var result = new JsonObject();
        foreach (var (name, value) in context) result[name] = value?.DeepClone();
        return result;
    }

    public static JsonNode? Decode(HttpReply reply)
    {
        var status = reply.Status;
        if (status is >= 300 and < 400) throw OdooException.Redirected(reply.Header("Location") ?? "");
        var isJson = reply.Header("Content-Type")?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false;
        var value = isJson ? Json.Parse(reply.Body) : null;
        var parsed = isJson && (value is not null || IsJsonNull(reply.Body));

        if (status is >= 200 and < 300)
        {
            if (!parsed) throw OdooException.Unexpected(Loc.T("Die Antwort war kein JSON (HTTP %lld).", status));
            return value;
        }
        if (value.Field("name").StringValue() is { } name)
        {
            throw OdooException.FromOdoo(name, value.Field("message").StringValue() ?? "", status);
        }
        throw status switch
        {
            401 => OdooException.Unauthorized(),
            404 => OdooException.NotFound(Loc.T("Die Schnittstelle wurde nicht gefunden (HTTP 404).")),
            _ => OdooException.Server($"HTTP {status}"),
        };
    }

    private static bool IsJsonNull(byte[] body) => Encoding.UTF8.GetString(body).Trim() == "null";
}

/// <summary>
/// JSON-RPC, die ältere Schnittstelle (<c>/jsonrpc</c>). Odoo 19 kündigt sie ab, entfernt werden soll
/// sie mit Odoo 22. Sie braucht Datenbank, Benutzer-ID und API-Schlüssel.
/// </summary>
public sealed class JsonRpcTransport(string baseUrl, string database, int uid, string apiKey, IHttpSender http) : IOdooTransport
{
    public Task<JsonNode?> ExecuteAsync(OdooCall call, IReadOnlyDictionary<string, JsonNode?> context)
    {
        var positional = new JsonArray();
        if (call.Ids is { } ids) positional.Add(Json.Ints(ids));
        var keywords = new JsonObject();
        foreach (var (name, value) in call.Arguments ?? new Dictionary<string, JsonNode?>()) keywords[name] = value?.DeepClone();
        foreach (var name in call.PositionalForJsonRpc ?? [])
        {
            var value = keywords[name];
            keywords.Remove(name);
            positional.Add(value);
        }
        if (context.Count > 0) keywords["context"] = Json2Transport.ContextObject(context);

        return CallAsync(baseUrl, http, "object", "execute_kw",
                         [database, uid, apiKey, call.Model, call.Method, positional, keywords]);
    }

    /// <summary>Ruft einen Dienst von <c>/jsonrpc</c> auf (<c>common</c>, <c>db</c> oder <c>object</c>).</summary>
    public static async Task<JsonNode?> CallAsync(string baseUrl, IHttpSender http, string service, string method,
                                                  JsonNode?[] arguments)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Random.Shared.Next(1, 1_000_000),
            ["params"] = new JsonObject
            {
                ["service"] = service,
                ["method"] = method,
                ["args"] = new JsonArray(arguments),
            },
        };
        var request = HttpRequests.PostJson(HttpRequests.Append(baseUrl, "jsonrpc"), Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var reply = await http.SendAsync(request).ConfigureAwait(false);
        if (reply.Status is >= 300 and < 400) throw OdooException.Redirected(reply.Header("Location") ?? "");
        if (reply.Status is < 200 or >= 300)
        {
            if (reply.Status == 404) throw OdooException.NotFound(Loc.T("Die Schnittstelle /jsonrpc wurde nicht gefunden."));
            throw OdooException.Server($"HTTP {reply.Status}");
        }
        var value = Json.Parse(reply.Body);
        if (value is not JsonObject) throw OdooException.Unexpected(Loc.T("Die Antwort war kein JSON."));
        if (value.Field("error") is JsonObject error)
        {
            var details = error.Field("data");
            var name = details.Field("name").StringValue() ?? "";
            var message = details.Field("message").StringValue() ?? error.Field("message").StringValue() ?? "";
            throw error.Field("code").IntValue() switch
            {
                100 => OdooException.Unauthorized(),
                404 => OdooException.NotFound(message),
                _ => OdooException.FromOdoo(name, message, null),
            };
        }
        return value.Field("result");
    }
}
