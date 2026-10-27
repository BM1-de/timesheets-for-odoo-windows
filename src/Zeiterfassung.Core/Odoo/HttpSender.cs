// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;

namespace Zeiterfassung.Odoo;

/// <summary>Eine Anfrage an Odoo, unabhängig vom HTTP-Client.</summary>
public sealed record HttpRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, byte[]? Body)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Antwort auf eine Anfrage.</summary>
public sealed record HttpReply(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Schickt eine Anfrage und liefert die Antwort. In Tests und im Demo-Modus ersetzbar.</summary>
public interface IHttpSender
{
    Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP über <see cref="HttpClient"/>, ohne Cookies und ohne Umleitungen.
/// Cookies: Die Schnittstellen von Odoo arbeiten ohne Sitzung, und ein Sitzungs-Cookie würde mit dem
/// Kopf <c>X-Odoo-Database</c> kollidieren. Umleitungen: Eine Umleitung schickte den API-Schlüssel an
/// die neue Adresse. Die App folgt deshalb nie, sondern meldet die neue Adresse.
/// </summary>
public sealed class NetHttpSender : IHttpSender, IDisposable
{
    private readonly HttpClient client;

    public NetHttpSender(string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);
        }
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }
        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw OdooException.Redirected(response.Headers.Location?.ToString() ?? "");
            }
            return new HttpReply(status, headers, data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException error)
        {
            // Zeitüberschreitung
            throw OdooException.Offline(error.Message);
        }
        catch (HttpRequestException error)
        {
            throw OdooException.Offline(error.Message);
        }
    }

    public void Dispose() => client.Dispose();
}

internal static class HttpRequests
{
    public static HttpRequest PostJson(Uri url, byte[] body, IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Accept"] = "application/json",
        };
        foreach (var (name, value) in extra ?? []) headers[name] = value;
        return new HttpRequest("POST", url, headers, body);
    }

    public static HttpRequest Get(Uri url) =>
        new("GET", url, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>Hängt einen Pfad an die Serveradresse (<c>https://odoo.example.de</c> + <c>json/2/…</c>).</summary>
    public static Uri Append(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + "/" + path);
}
