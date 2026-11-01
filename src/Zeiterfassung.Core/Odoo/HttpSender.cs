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

/// <summary>
/// Schickt Anfragen nacheinander und wartet, wenn Odoo „zu viele Anfragen“ meldet (HTTP 429).
/// Odoo Online lehnt mehrere gleichzeitige Anfragen schnell ab. Eine mit 429 abgelehnte Anfrage hat
/// Odoo nicht ausgeführt, die Wiederholung legt also nichts doppelt an. Hilft das Warten nicht, fragt
/// die App eine Weile gar nicht mehr an, statt die Sperre mit jedem Versuch zu verlängern.
/// </summary>
public sealed class PacedHttpSender(
    IHttpSender network,
    Func<DateTimeOffset>? now = null,
    Func<TimeSpan, CancellationToken, Task>? wait = null) : IHttpSender
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    /// <summary>Länger wartet eine Anfrage nicht, solange andere hinter ihr anstehen.</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShortestPause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(300);

    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> wait = wait ?? Task.Delay;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset pausedUntil = DateTimeOffset.MinValue;

    public async Task<HttpReply> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (now() < pausedUntil) throw new OdooException(OdooErrorKind.RateLimited);
            for (var attempt = 0; ; attempt++)
            {
                var reply = await network.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (reply.Status != 429) return reply;
                TimeSpan? retryAfter = int.TryParse(reply.Header("Retry-After"), out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
                if (attempt >= RetryDelays.Length || (retryAfter ?? RetryDelays[attempt]) > LongestWait)
                {
                    var pause = TimeSpan.FromTicks(Math.Clamp((retryAfter ?? TimeSpan.Zero).Ticks, ShortestPause.Ticks, LongestPause.Ticks));
                    pausedUntil = now() + pause;
                    throw new OdooException(OdooErrorKind.RateLimited);
                }
                await wait(retryAfter ?? RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }
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
