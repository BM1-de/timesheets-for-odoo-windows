// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Serialization;
using Zeiterfassung.Support;

namespace Zeiterfassung.Odoo;

[JsonConverter(typeof(JsonStringEnumConverter<OdooInterface>))]
public enum OdooInterface
{
    /// <summary>JSON-2, ab Odoo 19.</summary>
    Json2,
    /// <summary>JSON-RPC, Odoo 17 und 18 sowie Odoo Online ohne JSON-2.</summary>
    JsonRpc,
}

/// <summary>Gespeicherte Verbindung zu Odoo, ohne den API-Schlüssel (der liegt in der Anmeldeinformationsverwaltung).</summary>
public sealed record OdooSession(
    string ServerUrl,
    string? Database,
    string? Login,
    OdooInterface Interface,
    int Uid,
    string? Language,
    int? ServerMajorVersion)
{
    /// <summary>Konto, unter dem der API-Schlüssel abgelegt ist.</summary>
    [JsonIgnore]
    public string SecretAccount => $"{ServerUrl}|{Database ?? ""}|{Uid}";

    public IOdooTransport MakeTransport(string apiKey, IHttpSender http) => Interface switch
    {
        OdooInterface.Json2 => new Json2Transport(ServerUrl, apiKey, Database, http),
        _ => new JsonRpcTransport(ServerUrl, Database ?? "", Uid, apiKey, http),
    };
}

/// <summary>Stellt die Verbindung her: erst JSON-2, sonst JSON-RPC.</summary>
public sealed class Connector(IHttpSender http)
{
    public async Task<OdooSession> ConnectAsync(string serverUrl, string apiKey, string? login, string? database)
    {
        database = string.IsNullOrWhiteSpace(database) ? null : database.Trim();

        // 1. JSON-2 (Odoo 19 und neuer): Der API-Schlüssel genügt.
        var json2 = new Json2Transport(serverUrl, apiKey, database, http);
        try
        {
            var context = await json2.ExecuteAsync(new OdooCall("res.users", "context_get"), Empty).ConfigureAwait(false);
            if (context.Field("uid").IntValue() is not { } uid)
            {
                throw OdooException.Unexpected(Loc.T("Odoo hat keine Benutzer-ID geliefert."));
            }
            return new OdooSession(serverUrl, database, null, OdooInterface.Json2, uid, context.Field("lang").StringValue(),
                                   await Json2MajorVersionAsync(serverUrl).ConfigureAwait(false) ?? 19);
        }
        catch (OdooException error) when (MeansNoJson2(error))
        {
            // Odoo 19 mit mehreren Datenbanken findet ohne Datenbankangabe keine JSON-2-Schnittstelle.
            // Odoo Online ausgenommen: Dort fehlt JSON-2 ohne Custom-Tarif, weiter mit JSON-RPC.
            var isOdooOnline = new Uri(serverUrl).Host.EndsWith(".odoo.com", StringComparison.OrdinalIgnoreCase);
            if (database is null && !isOdooOnline && await Json2MajorVersionAsync(serverUrl).ConfigureAwait(false) is >= 19)
            {
                throw new OdooException(OdooErrorKind.DatabaseRequired);
            }
        }

        // 2. JSON-RPC (Odoo 17 und 18, Odoo Online ohne JSON-2): braucht Benutzername und Datenbank.
        login = login?.Trim();
        if (string.IsNullOrEmpty(login)) throw new OdooException(OdooErrorKind.LoginRequired);
        var resolvedDatabase = await ResolveDatabaseAsync(serverUrl, database).ConfigureAwait(false);
        var uidValue = await JsonRpcTransport.CallAsync(serverUrl, http, "common", "authenticate",
                                                        [resolvedDatabase, login, apiKey, new System.Text.Json.Nodes.JsonObject()])
                                             .ConfigureAwait(false);
        if (uidValue.IntValue() is not { } rpcUid || rpcUid <= 0) throw OdooException.Unauthorized();

        var transport = new JsonRpcTransport(serverUrl, resolvedDatabase, rpcUid, apiKey, http);
        var rpcContext = await transport.ExecuteAsync(new OdooCall("res.users", "context_get"), Empty).ConfigureAwait(false);
        int? major = null;
        try
        {
            var version = await JsonRpcTransport.CallAsync(serverUrl, http, "common", "version", []).ConfigureAwait(false);
            major = version.Field("server_version_info").ArrayValue()?.FirstOrDefault().IntValue();
        }
        catch (OdooException)
        {
        }
        return new OdooSession(serverUrl, resolvedDatabase, login, OdooInterface.JsonRpc, rpcUid,
                               rpcContext.Field("lang").StringValue(), major);
    }

    private static readonly IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> Empty =
        new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();

    /// <summary>
    /// Fehler, bei denen der Server JSON-2 offenbar nicht anbietet. Odoo 18 kennt den Pfad nicht und
    /// leitet auf seine Anmeldeseite um; die App folgt dem nicht, sondern fragt per JSON-RPC.
    /// </summary>
    private static bool MeansNoJson2(OdooException error) => error.Kind switch
    {
        OdooErrorKind.NotFound or OdooErrorKind.UnexpectedResponse or OdooErrorKind.Server or OdooErrorKind.AccessDenied => true,
        OdooErrorKind.Redirected => error.Detail.Contains("/web/login", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Datenbank für JSON-RPC: angegeben, einzige auf dem Server, oder bei Odoo Online die Subdomain.</summary>
    private async Task<string> ResolveDatabaseAsync(string serverUrl, string? given)
    {
        if (given is not null) return given;
        try
        {
            var list = await JsonRpcTransport.CallAsync(serverUrl, http, "db", "list", []).ConfigureAwait(false);
            var names = list.ArrayValue()?.Select(item => item.StringValue()).OfType<string>().ToList();
            if (names is { Count: 1 }) return names[0];
        }
        catch (OdooException)
        {
        }
        var host = new Uri(serverUrl).Host;
        if (host.EndsWith(".odoo.com", StringComparison.OrdinalIgnoreCase)) return host.Split('.')[0];
        throw new OdooException(OdooErrorKind.DatabaseRequired);
    }

    /// <summary>Hauptversion über <c>/json/version</c> (gibt es ab Odoo 19).</summary>
    private async Task<int?> Json2MajorVersionAsync(string serverUrl)
    {
        try
        {
            var reply = await http.SendAsync(HttpRequests.Get(HttpRequests.Append(serverUrl, "json/version"))).ConfigureAwait(false);
            if (reply.Status != 200) return null;
            return Json.Parse(reply.Body).Field("version_info").ArrayValue()?.FirstOrDefault().IntValue();
        }
        catch (OdooException)
        {
            return null;
        }
    }
}

/// <summary>Liest die Serveradresse so, wie Benutzer sie eingeben oder aus dem Browser kopieren.</summary>
public static class ServerAddress
{
    /// <summary><c>odoo.example.de</c>, <c>https://odoo.example.de/odoo/timesheets</c> → <c>https://odoo.example.de</c></summary>
    public static string? Normalize(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.Length == 0 || host.Contains(' ', StringComparison.Ordinal)) return null;
        if (uri.HostNameType == UriHostNameType.IPv6) host = $"[{host.Trim('[', ']')}]";
        return uri.IsDefaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{uri.Port}";
    }

    /// <summary>https, oder http nur auf dem eigenen Rechner (lokale Testumgebung).</summary>
    public static bool IsSecure(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "https") return true;
        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal);
    }

    public static string Host(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Host : serverUrl;
}
