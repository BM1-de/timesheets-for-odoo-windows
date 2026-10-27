// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Zeiterfassung.App.Platform;

namespace Zeiterfassung.App.Views;

/// <summary>
/// Das Fenster über dem Symbol. Es ist so hoch wie sein Inhalt; passt der nicht über die Taskleiste, wird es so hoch
/// wie möglich und der Inhalt scrollt. Verliert es den Fokus, schließt es sich, wie die Flyouts von Windows.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    /// <summary>Breite im Entwurf.</summary>
    public const double PanelWidth = 372;

    /// <summary>Abstand zum Rand der Arbeitsfläche, also auch zur Taskleiste.</summary>
    private const double Margin = 12;

    private readonly OverlappedPresenter presenter;
    private DateTimeOffset lastHidden = DateTimeOffset.MinValue;
    private bool isShown;
    private RectInt32 workArea;
    private TrayIcon? anchor;

    public FlyoutWindow()
    {
        InitializeComponent();
        Title = Support.AppInfo.Name;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        Native.SetRoundedCorners(this);
        Activated += OnActivated;
        Panel.ContentHeightChanged += () => DispatcherQueue.TryEnqueue(FitHeight);
        Panel.SettingsRequested += () => SettingsRequested?.Invoke();
        // Schließen heißt verstecken; die App läuft im Infobereich weiter.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            HideFlyout();
        };
    }

    public event Action? SettingsRequested;

    public bool IsShown => isShown;

    /// <summary>Für Bildschirmfotos: bleibt ohne Fokus offen und zeigt den ganzen Inhalt, auch über den Bildschirm hinaus.</summary>
    public bool IsForScreenshots { get; set; }

    public PanelView PanelView => Panel;

    /// <summary>
    /// Klick auf das Symbol, während das Fenster offen ist: Es hat beim Klick schon den Fokus verloren und sich
    /// geschlossen. Dann bleibt es zu, statt gleich wieder aufzugehen.
    /// </summary>
    public bool ClosedJustNow => DateTimeOffset.Now - lastHidden < TimeSpan.FromMilliseconds(400);

    public void ShowNear(TrayIcon? tray)
    {
        anchor = tray;
        var area = DisplayArea.GetFromPoint(AnchorPoint(), DisplayAreaFallback.Primary);
        workArea = area.WorkArea;
        FitHeight();
        AppWindow.Show(true);
        Activate();
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        isShown = true;
        _ = App.Current.Model.PanelDidOpenAsync();
        DispatcherQueue.TryEnqueue(Panel.FocusFirstField);
    }

    public void HideFlyout()
    {
        if (!isShown) return;
        isShown = false;
        lastHidden = DateTimeOffset.Now;
        AppWindow.Hide();
        App.Current.Model.PanelDidClose();
    }

    /// <summary>Mitte des Symbols; ohne Angabe die Ecke unten rechts des Hauptbildschirms.</summary>
    private PointInt32 AnchorPoint()
    {
        if (anchor?.Bounds() is { } bounds) return new PointInt32(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var primary = DisplayArea.Primary.WorkArea;
        return new PointInt32(primary.X + primary.Width - 1, primary.Y + primary.Height - 1);
    }

    /// <summary>Größe und Lage: so hoch wie der Inhalt, an der Ecke der Arbeitsfläche, die dem Symbol am nächsten ist.</summary>
    private void FitHeight()
    {
        if (workArea.Width == 0) workArea = DisplayArea.Primary.WorkArea;
        var scale = Native.ScaleFor(this);
        var margin = (int)Math.Round(Margin * scale);
        var width = (int)Math.Round(PanelWidth * scale);
        var height = (int)Math.Round(Math.Max(120, Panel.NaturalHeight) * scale);
        if (!IsForScreenshots) height = Math.Min(height, workArea.Height - 2 * margin);
        AppWindow.ResizeClient(new SizeInt32(width, height));

        // Den Abstand zum Rand hält die Fläche selbst; der Rahmen darum ist zum Teil unsichtbar.
        var outer = AppWindow.Size;
        var frameX = (outer.Width - width) / 2;
        var frameY = (outer.Height - height) / 2;
        var point = AnchorPoint();
        var right = point.X > workArea.X + workArea.Width / 2;
        var bottom = point.Y > workArea.Y + workArea.Height / 2;
        var x = right ? workArea.X + workArea.Width - width - margin : workArea.X + margin;
        var y = bottom ? workArea.Y + workArea.Height - height - margin : workArea.Y + margin;
        AppWindow.Move(new PointInt32(x - frameX, y - frameY));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        // Offene Rückfragen, Menüs und Auswahllisten gehören zum Fenster; erst wenn wirklich etwas anderes den Fokus hat.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (isShown && !IsForScreenshots && !Native.IsForeground(this)) HideFlyout();
        });
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !e.Handled)
        {
            e.Handled = true;
            HideFlyout();
        }
        else if (e.Key == (VirtualKey)188 && Native.IsControlPressed())
        {
            // Strg+, öffnet die Einstellungen.
            e.Handled = true;
            SettingsRequested?.Invoke();
        }
    }
}
