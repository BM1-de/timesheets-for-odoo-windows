// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Zeiterfassung.App.Platform.NativeMethods;

namespace Zeiterfassung.App.Platform;

public enum TrayState
{
    /// <summary>Angemeldet, keine Projektzeit läuft.</summary>
    Normal,
    /// <summary>Grüner Punkt: Eine Projektzeit läuft.</summary>
    Running,
    /// <summary>Gestrichelter Kreis: nicht mit Odoo verbunden.</summary>
    SignedOut,
}

/// <summary>Ein Eintrag im Kontextmenü am Symbol.</summary>
public sealed record TrayMenuItem(string Title, Action? Action, bool IsDefault = false, bool IsEnabled = true)
{
    public static readonly TrayMenuItem Separator = new("", null);
}

/// <summary>
/// Das Symbol im Infobereich der Taskleiste. Ein eigenes, unsichtbares Fenster empfängt Klicks und Systemnachrichten:
/// Klick und Eingabetaste öffnen das Fenster der App, Rechtsklick und Kontextmenütaste das Menü. Startet der Explorer
/// neu, kommt das Symbol wieder.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;
    private static readonly string ClassName = "ZeiterfassungFuerOdoo.Infobereich";

    private readonly WndProc wndProc;
    private readonly nint window;
    private readonly uint taskbarCreated;
    private readonly string iconFolder;
    private TrayState state = TrayState.SignedOut;
    private string toolTip = "";
    private nint icon;
    private bool added;

    /// <summary>Klick oder Eingabetaste auf dem Symbol.</summary>
    public event Action? Selected;

    /// <summary>Rechtsklick oder Kontextmenütaste; liefert die Einträge des Menüs.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuItems { get; set; }

    public TrayIcon(string iconFolder)
    {
        this.iconFolder = iconFolder;
        wndProc = WindowProcedure;
        var instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref windowClass);
        // Ein unsichtbares Fenster statt eines reinen Nachrichtenfensters: Nur so kommt „TaskbarCreated“ an.
        window = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public nint WindowHandle => window;

    /// <summary>Setzt Zustand und Tooltip; das Symbol erscheint beim ersten Aufruf.</summary>
    public void Update(TrayState newState, string newToolTip)
    {
        var iconChanged = newState != state || icon == 0;
        state = newState;
        toolTip = newToolTip;
        if (iconChanged) LoadIcon();
        Apply();
    }

    /// <summary>Lage des Symbols auf dem Bildschirm in Pixeln; <c>null</c>, wenn Windows sie nicht verrät.</summary>
    public RECT? Bounds()
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = window,
            uID = IconId,
        };
        return Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 ? rect : null;
    }

    private void Apply()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!added)
        {
            added = Shell_NotifyIconW(NIM_ADD, ref data);
            if (!added) return;
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref data);
        }
        else
        {
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = window,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        // Windows zeigt höchstens 127 Zeichen.
        szTip = toolTip.Length > 127 ? toolTip[..126] + "…" : toolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Die Uhr passend zur Taskleiste: dunkel auf heller, weiß auf dunkler.</summary>
    private void LoadIcon()
    {
        var shade = TaskbarIsLight() ? "hell" : "dunkel";
        var suffix = state switch
        {
            TrayState.Running => "-eingestempelt",
            TrayState.SignedOut => "-abgemeldet",
            _ => "",
        };
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        var path = Path.Combine(iconFolder, $"infobereich-{shade}{suffix}.ico");
        var loaded = LoadImageW(0, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (loaded == 0) return;
        if (icon != 0) DestroyIcon(icon);
        icon = loaded;
    }

    /// <summary>Einstellungen › Personalisierung › Farben › „Standard-Windows-Modus“.</summary>
    public static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    private nint WindowProcedure(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            switch (LowWord(lParam))
            {
                case NIN_SELECT or NIN_KEYSELECT:
                    Selected?.Invoke();
                    return 0;
                case WM_CONTEXTMENU:
                    ShowMenu(LowWord(wParam), HighWord(wParam));
                    return 0;
            }
            return 0;
        }
        if (msg == taskbarCreated && taskbarCreated != 0)
        {
            // Der Explorer ist neu gestartet: Das Symbol ist weg und muss neu angelegt werden.
            added = false;
            LoadIcon();
            Apply();
            return 0;
        }
        if (msg == WM_SETTINGCHANGE && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            LoadIcon();
            Apply();
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = MenuItems?.Invoke() ?? [];
        if (items.Count == 0) return;
        var menu = CreatePopupMenu();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item == TrayMenuItem.Separator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                AppendMenuW(menu, MF_STRING | (item.IsEnabled ? 0 : MF_GRAYED), (nuint)(index + 1), item.Title);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)(index + 1), 0);
            }
            // Ohne Vordergrund schließt sich das Menü nicht, wenn man daneben klickt.
            SetForegroundWindow(window);
            var chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN | TPM_RIGHTALIGN,
                                          x, y, window, 0);
            PostMessageW(window, WM_NULL, 0, 0);
            if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            added = false;
        }
        if (icon != 0) DestroyIcon(icon);
        icon = 0;
        DestroyWindow(window);
    }
}
