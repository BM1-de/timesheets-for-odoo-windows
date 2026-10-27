// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Zeiterfassung.App.Platform;

namespace Zeiterfassung.App;

/// <summary>
/// Start der App. Es läuft immer nur eine: Startet man sie ein zweites Mal, etwa aus dem Startmenü, öffnet die
/// laufende ihr Fenster und die neue beendet sich gleich wieder.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Bildschirmfotos für Prüfungen laufen neben einer installierten App her.
        if (!ScreenshotMode.IsRequested(args))
        {
            var main = AppInstance.FindOrRegisterForKey("main");
            if (!main.IsCurrent)
            {
                RedirectActivation(main, AppInstance.GetCurrent().GetActivatedEventArgs());
                return 0;
            }
        }
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(args);
        });
        return 0;
    }

    /// <summary>Gibt den Start an die laufende App weiter und lässt sie ihr Fenster nach vorn holen.</summary>
    private static void RedirectActivation(AppInstance target, AppActivationArguments arguments)
    {
        NativeMethods.AllowSetForegroundWindow(target.ProcessId);
        var done = NativeMethods.CreateEventW(0, true, false, null);
        Task.Run(() =>
        {
            try
            {
                target.RedirectActivationToAsync(arguments).AsTask().Wait();
            }
            catch (AggregateException error)
            {
                Debug.WriteLine($"Weitergabe an die laufende App gescheitert: {error.InnerException?.Message}");
            }
            NativeMethods.SetEvent(done);
        });
        NativeMethods.CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
    }
}
