// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Zeiterfassung.App.Platform;
using Zeiterfassung.App.Views;
using Zeiterfassung.Demo;
using Zeiterfassung.Model;
using Zeiterfassung.Odoo;
using Zeiterfassung.Support;

namespace Zeiterfassung.App;

/// <summary>
/// Bildschirmfotos der wichtigsten Zustände mit erfundenen Daten, für die Prüfung auf GitHub und für den Store:
/// <c>Zeiterfassung.exe --screenshots &lt;Ordner&gt; [--language de|en|fr]</c>. Uhr und Daten stehen fest, damit die
/// Bilder bei jedem Lauf gleich aussehen.
/// </summary>
public static class ScreenshotMode
{
    public static bool IsRequested(string[] arguments) => arguments.Contains("--screenshots");

    private static string Argument(string[] arguments, string name) =>
        Array.IndexOf(arguments, name) is var index and >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : "";

    /// <summary>Freitag, 23. Oktober 2026, 14:52 in Berlin, wie bei den Bildschirmfotos der Mac-App.</summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 23, 14, 52, 0, TimeSpan.FromHours(2));

    public static async Task RunAsync(string[] arguments, Action<AppModel> setModel, Action<Updates> setUpdates)
    {
        var folder = Argument(arguments, "--screenshots");
        if (folder.Length == 0) folder = Path.Combine(Path.GetTempPath(), "zeiterfassung-bilder");
        Directory.CreateDirectory(folder);
        ErrorLog.Folder = folder;
        // Das Protokoll wächst Zeile für Zeile, damit auch ein Absturz zeigt, wie weit es kam.
        var path = Path.Combine(folder, "protokoll.txt");
        File.WriteAllText(path, "");
        void Log(string line) => File.AppendAllText(path, line + Environment.NewLine);
        try
        {
            await CaptureAllAsync(arguments, folder, Log, setModel, setUpdates);
        }
        catch (Exception error)
        {
            Log($"Abbruch: {error}");
            ErrorLog.Write(error, "Bildschirmfotos");
        }
    }

    private static async Task CaptureAllAsync(string[] arguments, string folder, Action<string> log, Action<AppModel> setModel,
                                              Action<Updates> setUpdates)
    {
        if (Argument(arguments, "--language") is { Length: > 0 } language)
        {
            Loc.Language = language;
            Formatting.Culture = Formatting.MakeCulture(Loc.Language, System.Globalization.CultureInfo.GetCultureInfo(language));
        }
        log($"Sprache {Loc.Language}, Kultur {Formatting.Culture.Name}");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = FixedNow;
        var demo = DemoData.MakeOdoo(now, zone);
        var http = new DemoRoutingHttpSender(new StubHttpSender(_ => throw OdooException.Offline("Keine Netzwerkanfragen bei Bildschirmfotos.")), demo);
        var preferences = new Preferences(new MemorySettingsStore());
        var model = new AppModel(preferences, http, new MemorySecretStore(), zone, () => now);
        setModel(model);
        setUpdates(new Updates(preferences));

        // Scheitert das XAML einer Ansicht, meldet WinUI beim Fenster nur „XAML parsing failed“; einzeln angelegt zeigt
        // sich, welche Ansicht es ist.
        XamlSelfTest.Run(log);
        Probe("LoginView", () => new LoginView(), log);
        Probe("PanelView", () => new PanelView(), log);
        Probe("SettingsWindow", () => new SettingsWindow().Close(), log);
        log("Fenster wird angelegt");
        var window = new FlyoutWindow { KeepsOpen = true };
        await model.StartAsync();
        window.ShowNear(null);
        await Capture(window, folder, "01-anmeldung", log);

        await model.SignInAsync("demo", "", null, null);
        model.FinishSignIn();
        await model.RefreshAsync(force: true);
        log($"Tooltip: {model.TrayContent.ToolTip.Replace('\n', '|')}");
        await Capture(window, folder, "02-tag", log);

        // Neuer Eintrag mit Projekt und Aufgabe, dann starten
        model.SelectProject(model.Projects.Single(project => project.Id == 5));
        await model.LoadTasksAsync(5);
        model.SelectTask(52);
        model.DraftDescription = Loc.T("Startseite: Varianten für den Kopfbereich");
        await Capture(window, folder, "03-formular", log);
        await model.StartTimerAsync();
        now = now.AddMinutes(42).AddSeconds(17);
        await model.TickAsync();
        log($"Tooltip mit Timer: {model.TrayContent.ToolTip.Replace('\n', '|')}");
        await Capture(window, folder, "04-timer", log);

        // Eintrag bearbeiten
        model.BeginEditing(model.Entries[0]);
        await Capture(window, folder, "05-bearbeiten", log);
        model.CancelEditing();

        // Gestern: Anwesenheit aufgeklappt
        model.ShowPreviousDay();
        await model.RefreshAsync(force: true);
        model.SetAttendanceListExpanded(true);
        await Capture(window, folder, "06-gestern", log);
        model.ShowToday();
        await model.RefreshAsync(force: true);

        // Dunkel
        if (window.Content is FrameworkElement root) root.RequestedTheme = ElementTheme.Dark;
        await Capture(window, folder, "07-dunkel", log);
        if (window.Content is FrameworkElement light) light.RequestedTheme = ElementTheme.Default;

        // Einstellungen
        var settings = new SettingsWindow();
        settings.Activate();
        await Capture(settings, folder, "08-einstellungen", log);
        settings.Close();

        log("Fertig");
    }

    private static void Probe(string name, Action create, Action<string> log)
    {
        try
        {
            create();
            log($"{name}: angelegt");
        }
        catch (Exception error)
        {
            log($"{name}: {error.GetType().Name} 0x{error.HResult:X8} {error.Message} {error.InnerException}");
        }
    }

    /// <summary>Wartet, bis das Fenster gezeichnet ist, und speichert seinen Inhalt als PNG.</summary>
    private static async Task Capture(Window window, string folder, string name, Action<string> log)
    {
        if (window.Content is not FrameworkElement root) return;
        var previous = (root as Microsoft.UI.Xaml.Controls.Panel)?.Background;
        if (root is Microsoft.UI.Xaml.Controls.Panel panel)
        {
            // Acryl und Mica kommen auf Bildschirmfotos nicht mit; dafür der feste Grund des Erscheinungsbilds.
            panel.Background = Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] as Brush;
        }
        for (var round = 0; round < 8; round++) await Task.Delay(120);
        root.UpdateLayout();
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var pixels = await bitmap.GetPixelsAsync();
            var path = Path.Combine(folder, $"{name}.png");
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var dpi = 96 * root.XamlRoot.RasterizationScale;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight,
                                 dpi, dpi, pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
            log($"{name}: {bitmap.PixelWidth} × {bitmap.PixelHeight}");
        }
        catch (Exception error)
        {
            log($"{name}: Fehler {error}");
        }
        finally
        {
            if (root is Microsoft.UI.Xaml.Controls.Panel restore) restore.Background = previous;
        }
    }
}
