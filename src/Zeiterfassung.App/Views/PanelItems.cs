// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.App.Views;

/// <summary>Farben aus dem aktuellen Erscheinungsbild für Zeilen, die im Code entstehen.</summary>
internal static class Brushes
{
    public static Brush Theme(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush();

    public static Brush Primary => Theme("TextFillColorPrimaryBrush");
    public static Brush Secondary => Theme("TextFillColorSecondaryBrush");
    public static Brush Tertiary => Theme("TextFillColorTertiaryBrush");
    public static Brush Accent => Theme("AccentTextFillColorPrimaryBrush");
    public static Brush Critical => Theme("SystemFillColorCriticalBrush");
    public static Brush Caution => Theme("SystemFillColorCautionBrush");
    public static Brush Success => Theme("SystemFillColorSuccessBrush");
    public static Brush Selected => Theme("SubtleFillColorSecondaryBrush");
    public static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}

/// <summary>Zeile der Einträge: Zwischenüberschrift einer Firma oder ein Eintrag.</summary>
public sealed record EntryRowItem(
    string Key,
    bool IsHeader,
    string Title,
    string Detail,
    string Duration,
    bool IsLocked,
    string LockLabel,
    bool IsSelected,
    bool CanStart,
    bool IsUntitled,
    string AccessibilityText,
    string ToolTipText,
    TimesheetEntry? Entry)
{
    public Brush TitleBrush => IsUntitled ? Brushes.Secondary : Brushes.Primary;

    public Brush SelectionBrush => IsSelected ? Brushes.Selected : Brushes.Transparent;
}

public sealed partial class EntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Header { get; set; }
    public DataTemplate? Entry { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => item is EntryRowItem { IsHeader: true } ? Header! : Entry!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public enum AttendanceTemplate
{
    Period,
    InsertedBreak,
    Editor,
}

/// <summary>Zeile der „Zeiten des Tages“: Anwesenheit, Pause oder die Felder zum Korrigieren an ihrer Stelle.</summary>
public sealed record AttendanceRowItem(
    string Key,
    AttendanceTemplate Template,
    string Range,
    string Duration,
    string Remark,
    bool IsGap,
    bool CanEdit,
    string AccessibilityText,
    AttendanceRow? Row,
    AttendanceEditorState? Editor)
{
    public Brush RangeBrush => IsGap ? Brushes.Secondary : Brushes.Primary;
}

public sealed partial class AttendanceTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Period { get; set; }
    public DataTemplate? InsertedBreak { get; set; }
    public DataTemplate? Editor { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => (item as AttendanceRowItem)?.Template switch
    {
        AttendanceTemplate.InsertedBreak => InsertedBreak!,
        AttendanceTemplate.Editor => Editor!,
        _ => Period!,
    };

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Die Felder „von“ und „bis“ beim Korrigieren. Bleibt dasselbe Objekt, solange korrigiert wird, damit die Liste beim
/// Tippen nicht neu entsteht und das Feld den Fokus behält.
/// </summary>
public sealed partial class AttendanceEditorState(AppModel model) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private AttendanceEditor? Editor => model.AttendanceEditor;

    public string From
    {
        get => Editor?.From ?? "";
        set => model.SetAttendanceEditorTimes(value, Editor?.To ?? "");
    }

    public string To
    {
        get => Editor?.To ?? "";
        set => model.SetAttendanceEditorTimes(Editor?.From ?? "", value);
    }

    public string Heading => Editor?.Kind switch
    {
        AttendanceEditorKind.Gap => Loc.T("Wann war deine Pause?"),
        AttendanceEditorKind.Add => Loc.T("Zeitraum nachtragen"),
        _ => "",
    };

    public string Warning => Editor is { Kind: AttendanceEditorKind.Period, Period.IsAutoCheckOut: true }
        ? Loc.T("Odoo hat dich automatisch ausgestempelt. Trag ein, wann du wirklich gegangen bist.")
        : "";

    /// <summary>Nach dem automatischen Ausstempeln zählt das Ende, sonst der Beginn.</summary>
    public bool FocusesEnd => Editor is { Kind: AttendanceEditorKind.Period, Period.IsAutoCheckOut: true };

    public bool IsOpenEnd => Editor is { Kind: AttendanceEditorKind.Period, Period.IsOpen: true };

    public string Duration => model.AttendanceEditorDuration ?? "";

    public string Hint => model.AttendanceEditorHint?.Text ?? "";

    public Brush HintBrush => model.AttendanceEditorHint?.IsProblem == true ? Brushes.Caution : Brushes.Secondary;

    public string Error => Editor?.Error ?? "";

    public bool CanDelete => Editor?.Kind == AttendanceEditorKind.Period;

    public bool IsSaving => model.IsSavingAttendance;

    public string SaveTitle => Editor?.Kind == AttendanceEditorKind.Add ? Loc.T("Nachtragen") : Loc.T("Sichern");
}

public enum PickerItemKind
{
    Header,
    Separator,
    Option,
}

/// <summary>Zeile der Projektauswahl.</summary>
public sealed record PickerItem(
    string Key,
    PickerItemKind Kind,
    string Title,
    string Company,
    bool IsSelected,
    bool IsHighlighted,
    string AccessibilityText,
    Project? Project)
{
    public Brush HighlightBrush => IsHighlighted ? Brushes.Selected : Brushes.Transparent;
}

public sealed partial class PickerTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Header { get; set; }
    public DataTemplate? Separator { get; set; }
    public DataTemplate? Option { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => (item as PickerItem)?.Kind switch
    {
        PickerItemKind.Header => Header!,
        PickerItemKind.Separator => Separator!,
        _ => Option!,
    };

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
