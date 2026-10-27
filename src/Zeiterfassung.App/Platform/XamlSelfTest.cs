// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.UI.Xaml.Markup;

namespace Zeiterfassung.App.Platform;

/// <summary>
/// Lädt jede Ressource und jede Eigenheit, die die Ansichten verwenden, einzeln mit <see cref="XamlReader"/>. Scheitert
/// das XAML einer ganzen Ansicht, meldet WinUI nur „XAML parsing failed“; hier steht dann, welches Stück es ist. Läuft
/// mit den Bildschirmfotos.
/// </summary>
internal static class XamlSelfTest
{
    private const string Namespaces =
        "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
        "xmlns:z=\"using:Zeiterfassung.App\" " +
        "xmlns:views=\"using:Zeiterfassung.App.Views\" " +
        "xmlns:controls=\"using:Microsoft.UI.Xaml.Controls\"";

    private static readonly string[] ThemeBrushes =
    [
        "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush", "CardBackgroundFillColorDefaultBrush",
        "CardStrokeColorDefaultBrush", "ControlFillColorDefaultBrush", "ControlStrokeColorDefaultBrush",
        "DividerStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush", "SubtleFillColorTertiaryBrush",
        "SystemFillColorAttentionBackgroundBrush", "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush",
        "SystemFillColorCriticalBrush", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush",
        "TextFillColorDisabledBrush", "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush",
        "SolidBackgroundFillColorBaseBrush",
    ];

    private static readonly (string Element, string Key)[] Styles =
    [
        ("TextBlock", "SecondaryCaptionStyle"), ("TextBlock", "SectionTitleStyle"), ("TextBlock", "CaptionTextBlockStyle"),
        ("TextBlock", "BodyStrongTextBlockStyle"), ("TextBlock", "SubtitleTextBlockStyle"), ("TextBlock", "TitleTextBlockStyle"),
        ("Border", "GroupStyle"), ("Border", "GroupFooterStyle"), ("Rectangle", "SeparatorStyle"),
        ("Button", "SubtleButtonStyle"), ("Button", "RowButtonStyle"), ("Button", "IconButtonStyle"), ("Button", "AccentButtonStyle"),
    ];

    private static readonly (string Name, string Xaml)[] Pieces =
    [
        ("Text-Erweiterung", "<TextBlock NS Text=\"{z:Str Key='Heute'}\" />"),
        ("Text-Erweiterung als Kopf", "<TextBox NS Header=\"{z:Str Key='Server'}\" />"),
        ("Text-Erweiterung im Tooltip", "<Button NS ToolTipService.ToolTip=\"{z:Str Key='Erneut starten'}\" />"),
        ("Bild aus dem Paket", "<Image NS Source=\"ms-appx:///Assets/Square44x44Logo.targetsize-48.png\" />"),
        ("Ziffern gleich breit", "<TextBlock NS Typography.NumeralAlignment=\"Tabular\" />"),
        ("Bedienhilfe", "<Image NS AutomationProperties.AccessibilityView=\"Raw\" AutomationProperties.Name=\"x\" />"),
        ("Eingabe Adresse", "<TextBox NS InputScope=\"Url\" IsSpellCheckEnabled=\"False\" TextAlignment=\"Center\" />"),
        ("Eingabe Zahl", "<TextBox NS InputScope=\"Number\" />"),
        ("Eingabe E-Mail", "<TextBox NS InputScope=\"EmailNameOrAddress\" />"),
        ("Passwortfeld", "<PasswordBox NS PlaceholderText=\"x\" />"),
        ("Infoleiste", "<controls:InfoBar NS IsOpen=\"True\" Severity=\"Warning\" Message=\"x\"><controls:InfoBar.ActionButton><Button Content=\"x\" /></controls:InfoBar.ActionButton></controls:InfoBar>"),
        ("Fortschritt", "<controls:ProgressRing NS Width=\"16\" Height=\"16\" IsActive=\"True\" />"),
        ("Liste", "<ItemsRepeater NS />"),
        ("Schalter", "<ToggleSwitch NS MinWidth=\"0\" />"),
        ("Auswahl", "<ComboBox NS MinWidth=\"140\" />"),
        ("Verweis", "<HyperlinkButton NS Content=\"x\" NavigateUri=\"https://example.org\" />"),
        ("Symbol", "<FontIcon NS Glyph=\"&#xE72E;\" FontSize=\"12\" />"),
        ("Tastenkürzel", "<Grid NS KeyboardAcceleratorPlacementMode=\"Hidden\"><Grid.KeyboardAccelerators><KeyboardAccelerator Key=\"Left\" Modifiers=\"Menu\" /></Grid.KeyboardAccelerators></Grid>"),
        ("Flyout am Knopf", "<Button NS><FlyoutBase.AttachedFlyout><Flyout Placement=\"Top\"><Flyout.FlyoutPresenterStyle><Style TargetType=\"FlyoutPresenter\"><Setter Property=\"Padding\" Value=\"0\" /></Style></Flyout.FlyoutPresenterStyle><Grid /></Flyout></FlyoutBase.AttachedFlyout></Button>"),
        ("Kontextmenü", "<Button NS><Button.ContextFlyout><MenuFlyout /></Button.ContextFlyout></Button>"),
        ("Stapel mit Rand", "<StackPanel NS CornerRadius=\"4\" BorderThickness=\"1\" Padding=\"2\" Spacing=\"2\" />"),
        ("Raster mit Abstand", "<Grid NS ColumnSpacing=\"4\" RowSpacing=\"2\" CornerRadius=\"4\" />"),
        ("Drehung", "<FontIcon NS Glyph=\"&#xE76C;\" RenderTransformOrigin=\"0.5,0.5\"><FontIcon.RenderTransform><RotateTransform Angle=\"90\" /></FontIcon.RenderTransform></FontIcon>"),
        ("Rechteck", "<Rectangle NS Width=\"3\" Height=\"16\" RadiusX=\"1.5\" RadiusY=\"1.5\" Fill=\"White\" />"),
        ("Bildlauf", "<ScrollViewer NS VerticalScrollBarVisibility=\"Auto\" HorizontalScrollMode=\"Disabled\" />"),
        ("Vorlagenwahl Einträge", "<views:EntryTemplateSelector NS />"),
        ("Vorlagenwahl Anwesenheit", "<views:AttendanceTemplateSelector NS />"),
        ("Vorlagenwahl Projekte", "<views:PickerTemplateSelector NS />"),
        ("Eckenradius", "<Border NS CornerRadius=\"{ThemeResource ControlCornerRadius}\" />"),
        ("Schriftgröße", "<TextBlock NS FontSize=\"{ThemeResource ControlContentThemeFontSize}\" />"),
    ];

    public static void Run(Action<string> log)
    {
        var failures = 0;
        void Test(string name, string xaml)
        {
            try
            {
                XamlReader.Load(xaml.Replace(" NS", " " + Namespaces));
            }
            catch (Exception error)
            {
                failures++;
                log($"XAML {name}: 0x{error.HResult:X8} {error.Message.ReplaceLineEndings(" ")}");
            }
        }

        foreach (var (name, xaml) in Pieces) Test(name, xaml);
        foreach (var key in ThemeBrushes) Test(key, $"<Border NS Background=\"{{ThemeResource {key}}}\" />");
        foreach (var (element, key) in Styles) Test(key, $"<{element} NS Style=\"{{StaticResource {key}}}\" />");
        log(failures == 0 ? "XAML-Selbsttest: alles geladen" : $"XAML-Selbsttest: {failures} Fehler");
    }
}
