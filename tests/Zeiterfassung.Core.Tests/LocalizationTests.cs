// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Resources;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.Tests;

/// <summary>
/// Tests, die die Sprache der App umstellen. <see cref="Loc.Language"/> gilt für alle Tests; diese laufen deshalb allein,
/// nachdem alle anderen fertig sind.
/// </summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection;

/// <summary>Sprachen</summary>
[Collection(nameof(LanguageCollection))]
public sealed partial class LocalizationTests
{
    private static readonly string[] Languages = ["en", "fr"];

    [Fact(DisplayName = "jeder Text ist englisch und französisch übersetzt, mit denselben Platzhaltern")]
    public void CatalogIsComplete()
    {
        var strings = Catalog();
        Assert.True(strings.Count > 150, $"nur {strings.Count} Texte");
        // Wie #expect in der Schleife: alle Lücken sammeln und gemeinsam melden.
        var problems = new List<string>();
        foreach (var (key, entry) in strings)
        {
            if (entry.Field("shouldTranslate").BoolValue() == false) continue;
            var localizations = entry.Field("localizations");
            foreach (var language in Languages)
            {
                var texts = Texts(localizations.Field(language));
                if (texts.Count == 0) problems.Add($"{language} fehlt: {key}");
                foreach (var text in texts)
                {
                    if (!Placeholders(text).SequenceEqual(Placeholders(key))) problems.Add($"{language}: {key} → {text}");
                }
            }
        }
        Assert.Empty(problems);
    }

    [Theory(DisplayName = "die gebaute App enthält die Übersetzungen")]
    [InlineData("en", "Settings…", "Timesheets for Odoo", "1 hour", "2 hours")]
    [InlineData("fr", "Réglages…", "Feuilles de temps pour Odoo", "1 heure", "2 heures")]
    public void BuiltTranslations(string language, string settings, string name, string oneHour, string twoHours) =>
        InLanguage(language, () =>
        {
            Assert.Equal(settings, Loc.T("Einstellungen …"));
            Assert.Equal(name, AppInfo.Name);
            Assert.Equal(oneHour, Loc.T("%lld Stunden", 1));
            Assert.Equal(twoHours, Loc.T("%lld Stunden", 2));

            // Swift fragt das Bundle nach jedem Schlüssel; hier muss Loc den Text aus dem Katalog liefern.
            var missing = new List<string>();
            foreach (var (key, entry) in Catalog())
            {
                if (entry.Field("shouldTranslate").BoolValue() == false || key.Contains("%lld", StringComparison.Ordinal)) continue;
                var expected = Texts(entry.Field("localizations").Field(language)).FirstOrDefault();
                if (expected is null || Loc.T(key) != Loc.Format(expected, [])) missing.Add(key);
            }
            Assert.True(missing.Count == 0, $"{language}: {string.Join(", ", missing)}");
        });

    [Fact(DisplayName = "französisch steht die Null in der Einzahl")]
    public void FrenchPlural() => InLanguage("fr", () =>
    {
        Assert.Equal("0 minute", Loc.T("%lld Minuten", 0));
        Assert.Equal("5 minutes", Loc.T("%lld Minuten", 5));
    });

    /// <summary>
    /// Deutsch ist die Ausgangssprache: Die deutschen Texte sind die Schlüssel selbst. Swift prüft dazu die
    /// Entwicklungssprache des Bundles; unter .NET entspricht ihr die neutrale Sprache der Assembly.
    /// </summary>
    [Fact(DisplayName = "die Tests laufen auf Deutsch, wie im Schema eingestellt")]
    public void TestsRunInGerman()
    {
        Assert.Equal("de", Loc.Language);
        Assert.Equal("de", typeof(Loc).Assembly.GetCustomAttribute<NeutralResourcesLanguageAttribute>()?.CultureName);
        Assert.Equal("Einstellungen …", Loc.T("Einstellungen …"));
        Assert.Equal("Zeiterfassung für Odoo", AppInfo.Name);
        Assert.Equal("1 Stunde 1 Minute", DurationInput.Spoken(61));
    }

    // Hilfen

    private static void InLanguage(string language, Action body)
    {
        var previous = Loc.Language;
        Loc.Language = language;
        try
        {
            body();
        }
        finally
        {
            Loc.Language = previous;
        }
    }

    /// <summary>
    /// Die Übersetzungskataloge, wie Loc sie liest: der der Mac-App und, falls vorhanden, der mit Texten nur für
    /// Windows, der gleiche Schlüssel ersetzt. In die Assembly eingebettet, aber Byte für Byte die Datei im Quellcode.
    /// </summary>
    private static Dictionary<string, JsonNode?> Catalog()
    {
        var strings = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var name in new[] { "Localizable.xcstrings", "Windows.xcstrings" })
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            if (JsonNode.Parse(stream).Field("strings") is not JsonObject catalog) continue;
            foreach (var (key, value) in catalog) strings[key] = value?.DeepClone();
        }
        return strings;
    }

    /// <summary>Alle Texte einer Sprache, bei Einzahl und Mehrzahl beide.</summary>
    private static List<string> Texts(JsonNode? localization)
    {
        if (localization is not JsonObject) return [];
        if (localization.Field("stringUnit").Field("value").StringValue() is { } value) return [value];
        return localization.Field("variations").Field("plural").ObjectValue()?
                   .Select(form => form.Value.Field("stringUnit").Field("value").StringValue())
                   .OfType<string>()
                   .ToList() ?? [];
    }

    [GeneratedRegex(@"%(?:\d+\$)?(?:lld|ld|d|@)")]
    private static partial Regex PlaceholderPattern();

    private static List<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal).ToList();
}
