// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Zeiterfassung.Support;

namespace Zeiterfassung.App;

/// <summary>
/// Text aus dem Katalog in XAML: <c>Text="{z:Str Key='Heute'}"</c>. Die Sprache steht beim Start fest, deshalb
/// genügt es, den Text einmal zu lesen.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Str : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}

/// <summary>Hilfen für x:Bind.</summary>
public static class Show
{
    public static Visibility If(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Nur für Texte, die nie null sind: Ist ein Argument null, ruft x:Bind die Funktion nicht auf, und die Zeile bliebe
    /// im alten Zustand. Kann der Text fehlen, bindet die Ansicht an einen Wahrheitswert.
    /// </summary>
    public static Visibility IfText(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;
}
