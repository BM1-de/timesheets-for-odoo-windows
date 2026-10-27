// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Zeiterfassung.Support;

/// <summary>Angaben zur App, die die Windows-Seite beim Start setzt.</summary>
public static class AppInfo
{
    /// <summary>„Zeiterfassung für Odoo“, „Timesheets for Odoo“ … in der Sprache der App.</summary>
    public static string Name => Loc.T("Zeiterfassung für Odoo");

    public static string Version { get; set; } = "0.1.0";

    /// <summary>Aus dem Microsoft Store installiert: Updates kommen über den Store.</summary>
    public static bool IsStore { get; set; }

    public const string SourceUrl = "https://github.com/BM1-de/timesheets-for-odoo-windows";
    public const string LicenseUrl = "https://www.gnu.org/licenses/agpl-3.0.html";

    /// <summary>Eigene Kennung: Manche Server hinter Cloudflare sperren unbekannte Standardkennungen.</summary>
    public static string UserAgent => $"ZeiterfassungFuerOdoo/{Version} (Windows)";

    /// <summary>Hinweise nach AGPL-3.0 § 5 d für die Einstellungen.</summary>
    public static string LegalNotice =>
        Loc.T("Copyright © 2026 Baumgärtner Marketing GmbH. Freie Software unter der GNU Affero General Public License, Version 3 oder neuer, ohne jede Gewährleistung. Odoo ist eine Marke der Odoo S.A.; dieses Projekt steht in keiner Verbindung zu Odoo S.A.");
}
