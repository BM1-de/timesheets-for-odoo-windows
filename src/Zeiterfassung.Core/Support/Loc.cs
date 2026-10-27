// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zeiterfassung.Support;

/// <summary>
/// Texte der App in Deutsch, Englisch und Französisch. Schlüssel ist der deutsche Text mit den
/// Platzhaltern %@ und %lld, wie im String-Katalog der Mac-App (<c>Localizable.xcstrings</c>), damit
/// beide Apps dieselben Übersetzungen nutzen. <c>Windows.xcstrings</c> ergänzt Texte, die es nur
/// unter Windows gibt, und ersetzt Mac-Texte gleichen Schlüssels.
/// </summary>
public static class Loc
{
    public static readonly IReadOnlyList<string> Languages = ["de", "en", "fr"];

    private static string language = "de";
    private static readonly Lazy<Dictionary<string, Dictionary<string, Entry>>> Catalog = new(LoadCatalog);

    /// <summary>Sprache der App: <c>de</c>, <c>en</c> oder <c>fr</c>.</summary>
    public static string Language
    {
        get => language;
        set => language = Languages.Contains(value) ? value : "en";
    }

    /// <summary>Die erste unterstützte Sprache aus der Liste des Benutzers; sonst Englisch.</summary>
    public static string Choose(IEnumerable<string> preferred)
    {
        foreach (var tag in preferred)
        {
            var code = tag.Split('-', '_')[0].ToLowerInvariant();
            if (Languages.Contains(code)) return code;
        }
        return "en";
    }

    /// <summary>Text zum deutschen Schlüssel, Platzhalter der Reihe nach ersetzt.</summary>
    public static string T(string key, params object?[] args)
    {
        var template = key;
        if (Catalog.Value.TryGetValue(key, out var byLanguage)
            && (byLanguage.TryGetValue(language, out var entry) || byLanguage.TryGetValue("de", out entry)))
        {
            template = entry.Plural is { } plural ? plural.Select(FirstCount(args), language) : entry.Value ?? key;
        }
        return Format(template, args);
    }

    private static long FirstCount(object?[] args)
    {
        foreach (var arg in args)
        {
            switch (arg)
            {
                case int i: return i;
                case long l: return l;
            }
        }
        return 0;
    }

    /// <summary>Ersetzt %@, %lld, %ld, %d (auch mit Position wie %1$@) und %%.</summary>
    internal static string Format(string template, object?[] args)
    {
        if (template.IndexOf('%') < 0) return template;
        var result = new StringBuilder(template.Length + 16);
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '%' || i + 1 >= template.Length)
            {
                result.Append(c);
                continue;
            }
            var j = i + 1;
            if (template[j] == '%')
            {
                result.Append('%');
                i = j;
                continue;
            }
            int? position = null;
            var digits = j;
            while (digits < template.Length && char.IsAsciiDigit(template[digits])) digits++;
            if (digits > j && digits < template.Length && template[digits] == '$')
            {
                position = int.Parse(template.AsSpan(j, digits - j), CultureInfo.InvariantCulture) - 1;
                j = digits + 1;
            }
            var spec = template.AsSpan(j);
            int length;
            if (spec.StartsWith("@")) length = 1;
            else if (spec.StartsWith("lld")) length = 3;
            else if (spec.StartsWith("ld")) length = 2;
            else if (spec.StartsWith("d")) length = 1;
            else
            {
                result.Append(c);
                continue;
            }
            var index = position ?? next++;
            var value = index >= 0 && index < args.Length ? args[index] : null;
            result.Append(value switch
            {
                null => "",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            });
            i = j + length - 1;
        }
        return result.ToString();
    }

    private sealed record Entry(string? Value, PluralForms? Plural);

    private sealed record PluralForms(string? One, string? Other)
    {
        /// <summary>Französisch zählt auch die Null zur Einzahl.</summary>
        public string Select(long count, string language)
        {
            var isOne = language == "fr" ? count is 0 or 1 : count == 1;
            return (isOne ? One : Other) ?? Other ?? One ?? "";
        }
    }

    private static Dictionary<string, Dictionary<string, Entry>> LoadCatalog()
    {
        var catalog = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
        foreach (var name in new[] { "Localizable.xcstrings", "Windows.xcstrings" })
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("strings", out var strings)) continue;
            foreach (var item in strings.EnumerateObject())
            {
                // Windows.xcstrings ersetzt nur die Sprachen, die es nennt; die übrigen bleiben aus dem Mac-Katalog.
                if (!catalog.TryGetValue(item.Name, out var byLanguage))
                {
                    byLanguage = new Dictionary<string, Entry>(StringComparer.Ordinal) { ["de"] = new Entry(item.Name, null) };
                    catalog[item.Name] = byLanguage;
                }
                if (!item.Value.TryGetProperty("localizations", out var localizations)) continue;
                foreach (var localization in localizations.EnumerateObject())
                {
                    byLanguage[localization.Name] = ReadEntry(localization.Value);
                }
            }
        }
        return catalog;
    }

    private static Entry ReadEntry(JsonElement element)
    {
        if (element.TryGetProperty("variations", out var variations)
            && variations.TryGetProperty("plural", out var plural))
        {
            return new Entry(null, new PluralForms(Value(plural, "one"), Value(plural, "other")));
        }
        return new Entry(StringUnit(element), null);
    }

    private static string? Value(JsonElement plural, string form) =>
        plural.TryGetProperty(form, out var variant) ? StringUnit(variant) : null;

    private static string? StringUnit(JsonElement element) =>
        element.TryGetProperty("stringUnit", out var unit) && unit.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;
}
