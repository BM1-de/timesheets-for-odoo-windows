// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Zeiterfassung.App.Platform;

/// <summary>Hilfen rund um Fenster, die das Windows App SDK nicht selbst bietet.</summary>
internal static partial class Native
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeMethods.RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeMethods.RECT value, int size);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public static nint Handle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>Skalierung des Bildschirms, auf dem das Fenster steht: 1 bei 100 %, 1,5 bei 150 %.</summary>
    public static double ScaleFor(Window window)
    {
        var dpi = GetDpiForWindow(Handle(window));
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    /// <summary>Runde Ecken wie die Flyouts von Windows 11; unter Windows 10 ohne Wirkung.</summary>
    public static void SetRoundedCorners(Window window)
    {
        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(Handle(window), NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>
    /// Hat ein Fenster dieser App den Fokus? Rückfragen, Menüs und Auswahllisten sind eigene Fenster der App; das
    /// Fenster im Infobereich bleibt dann offen.
    /// </summary>
    public static bool IsForeground(Window window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == Handle(window)) return true;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    public static bool IsControlPressed() => (GetKeyState(0x11) & 0x8000) != 0;

    /// <summary>Unsichtbarer Teil des Fensterrahmens je Seite, in Pixeln; Windows legt ihn zum Greifen um das Fenster.</summary>
    public static (int Left, int Top, int Right, int Bottom) InvisibleFrame(Window window)
    {
        var hwnd = Handle(window);
        if (!GetWindowRect(hwnd, out var outer)
            || DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return (0, 0, 0, 0);
        }
        return (visible.Left - outer.Left, visible.Top - outer.Top, outer.Right - visible.Right, outer.Bottom - visible.Bottom);
    }
}
