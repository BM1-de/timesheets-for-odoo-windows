// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Zeiterfassung.App.Platform;

/// <summary>
/// Unerwartete Fehler in einer Textdatei bei der App, damit sich ein Absturz nachvollziehen lässt. Die Datei bleibt auf
/// dem Rechner; die App sendet sie nirgendwohin.
/// </summary>
internal static class ErrorLog
{
    private const long MaximumSize = 1_000_000;

    /// <summary>Ordner der Datei <c>fehler.log</c>; bei Bildschirmfotos der Ordner der Bilder.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zeiterfassung");

    public static void Write(Exception error, string? context = null) => Write(error.ToString(), context);

    public static void Write(string text, string? context = null)
    {
        System.Diagnostics.Debug.WriteLine($"Zeiterfassung: {context} {text}");
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, "fehler.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaximumSize) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {context}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            // Ohne beschreibbaren Ordner bleibt nur die Ausgabe im Debugger.
        }
    }
}
