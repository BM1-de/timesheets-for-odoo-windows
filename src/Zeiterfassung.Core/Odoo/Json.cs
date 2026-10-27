// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeiterfassung.Odoo;

/// <summary>Verweis auf einen Odoo-Datensatz mit Anzeigename.</summary>
public sealed record Reference(int Id, string Name);

/// <summary>
/// Lesehilfen für JSON, wie Odoo es liefert: leere Felder als <c>false</c>, Verweise auf andere
/// Datensätze (Many2one) als <c>[id, "Name"]</c>.
/// </summary>
public static class Json
{
    public static int? IntValue(this JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue) return (int)wide;
        var real = value.DoubleValue();
        if (real is { } d && Math.Floor(d) == d && Math.Abs(d) < 9e15 && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return null;
    }

    public static double? DoubleValue(this JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.GetValueKind() != JsonValueKind.Number) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<decimal>(out var m)) return (double)m;
        if (value.TryGetValue<float>(out var f)) return f;
        if (value.TryGetValue<JsonElement>(out var element) && element.TryGetDouble(out var parsed)) return parsed;
        return null;
    }

    public static string? StringValue(this JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool? BoolValue(this JsonNode? node) =>
        node is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    public static JsonArray? ArrayValue(this JsonNode? node) => node as JsonArray;

    public static JsonObject? ObjectValue(this JsonNode? node) => node as JsonObject;

    /// <summary>Feld eines Objekts; <c>null</c>, wenn es fehlt oder kein Objekt ist.</summary>
    public static JsonNode? Field(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>Verweis auf einen anderen Datensatz (<c>[id, "Name"]</c>); <c>null</c> bei <c>false</c>.</summary>
    public static Reference? Reference(this JsonNode? node)
    {
        if (node is not JsonArray array || array.Count < 2 || array[0].IntValue() is not { } id) return null;
        return new Reference(id, array[1].StringValue() ?? "");
    }

    public static JsonArray Ints(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>Bedingung einer Odoo-Domäne, etwa <c>["date", "=", "2026-10-23"]</c>.</summary>
    public static JsonArray Condition(string field, string op, JsonNode? value) => new(field, op, value);

    public static string Serialize(JsonNode? node) => node?.ToJsonString() ?? "null";

    public static JsonNode? Parse(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
