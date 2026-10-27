// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Support;

namespace Zeiterfassung.Odoo;

public enum OdooErrorKind
{
    /// <summary>Die Adresse lässt sich nicht als Server lesen.</summary>
    InvalidServerAddress,
    /// <summary>Unverschlüsselte Adresse außerhalb des eigenen Rechners.</summary>
    InsecureServerAddress,
    /// <summary>Netzwerk weg, Zeitüberschreitung, Name nicht auflösbar.</summary>
    Offline,
    /// <summary>API-Schlüssel oder Benutzername abgelehnt, Schlüssel abgelaufen.</summary>
    Unauthorized,
    /// <summary>Auf dem Server liegen mehrere Datenbanken, keine ist angegeben.</summary>
    DatabaseRequired,
    /// <summary>Der Server kennt JSON-2 nicht (Odoo 17/18); für JSON-RPC fehlt der Benutzername.</summary>
    LoginRequired,
    /// <summary>Modell, Methode oder Schnittstelle fehlt.</summary>
    NotFound,
    /// <summary>Odoo verweigert den Zugriff (fehlende Rechte).</summary>
    AccessDenied,
    /// <summary>Odoo lehnt die Eingabe ab, mit einer Meldung für den Benutzer.</summary>
    Rejected,
    /// <summary>Sonstiger Fehler auf dem Server.</summary>
    Server,
    /// <summary>Antwort ohne erwartetes Format.</summary>
    UnexpectedResponse,
    /// <summary>
    /// Der Server leitet auf eine andere Adresse um; die App folgt nicht, damit der API-Schlüssel
    /// nicht an eine fremde Adresse geht.
    /// </summary>
    Redirected,
}

/// <summary>Fehler beim Gespräch mit Odoo, mit Texten für die Oberfläche.</summary>
public sealed class OdooException(OdooErrorKind kind, string detail = "") : Exception(detail)
{
    public OdooErrorKind Kind { get; } = kind;

    /// <summary>Meldung von Odoo, Adresse einer Umleitung oder Grund eines Netzwerkfehlers.</summary>
    public string Detail { get; } = detail;

    public static OdooException Offline(string reason) => new(OdooErrorKind.Offline, reason);
    public static OdooException Unauthorized() => new(OdooErrorKind.Unauthorized);
    public static OdooException NotFound(string message) => new(OdooErrorKind.NotFound, message);
    public static OdooException Rejected(string message) => new(OdooErrorKind.Rejected, message);
    public static OdooException Server(string message) => new(OdooErrorKind.Server, message);
    public static OdooException Unexpected(string message) => new(OdooErrorKind.UnexpectedResponse, message);
    public static OdooException Redirected(string location) => new(OdooErrorKind.Redirected, location);

    /// <summary>Ordnet eine Odoo-Ausnahme (<c>odoo.exceptions.AccessError</c> …) einem Fall zu.</summary>
    public static OdooException FromOdoo(string name, string message, int? status)
    {
        var shortName = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        switch (shortName)
        {
            case "AccessDenied" or "Unauthorized" or "SessionExpiredException":
                return Unauthorized();
            case "AccessError":
                return new OdooException(OdooErrorKind.AccessDenied, message);
            case "UserError" when IsMissingModel(message):
                // JSON-RPC meldet ein fehlendes Modell (App nicht installiert) als UserError.
                return NotFound(message);
            case "UserError" or "ValidationError" or "MissingError" or "RedirectWarning" or "LockError" or "UnprocessableEntity":
                return Rejected(message);
            case "NotFound" or "KeyError":
                return NotFound(message);
        }
        return status switch
        {
            401 => Unauthorized(),
            404 => NotFound(message),
            _ => Server(message.Length == 0 ? name : message),
        };
    }

    /// <summary>
    /// „Object hr.attendance doesn't exist“ (Odoo 19 ohne Übersetzung; ältere Versionen übersetzten
    /// den Satz, deshalb auch deutsch und französisch).
    /// </summary>
    private static bool IsMissingModel(string message) =>
        (message.StartsWith("Object ", StringComparison.Ordinal) && message.EndsWith(" doesn't exist", StringComparison.Ordinal))
        || (message.StartsWith("Objekt ", StringComparison.Ordinal) && message.EndsWith(" existiert nicht", StringComparison.Ordinal))
        || (message.StartsWith("L'objet ", StringComparison.Ordinal) && message.Contains(" n'existe pas", StringComparison.Ordinal));

    public override string Message => Kind switch
    {
        OdooErrorKind.InvalidServerAddress => Loc.T("Die Serveradresse ist ungültig."),
        OdooErrorKind.InsecureServerAddress =>
            Loc.T("Bitte eine Adresse mit https verwenden. Der API-Schlüssel darf nicht unverschlüsselt übertragen werden."),
        OdooErrorKind.Offline => Loc.T("Keine Verbindung zu Odoo."),
        OdooErrorKind.Unauthorized => Loc.T("Odoo hat die Anmeldung abgelehnt. Der API-Schlüssel ist ungültig oder abgelaufen."),
        OdooErrorKind.DatabaseRequired =>
            Loc.T("Auf diesem Server liegen mehrere Datenbanken. Bitte den Namen der Datenbank angeben."),
        OdooErrorKind.LoginRequired =>
            Loc.T("Dieses Odoo bietet die neue Schnittstelle nicht an (Odoo 17/18 oder ohne JSON-2). Bitte zusätzlich den Benutzernamen angeben."),
        OdooErrorKind.NotFound => Detail.Length == 0 ? Loc.T("Odoo kennt diese Funktion nicht.") : Detail,
        OdooErrorKind.AccessDenied => Detail.Length == 0 ? Loc.T("Dafür fehlen in Odoo die Rechte.") : Detail,
        OdooErrorKind.Rejected => Detail,
        OdooErrorKind.Server => Loc.T("Odoo meldet einen Fehler: %@", Detail),
        OdooErrorKind.UnexpectedResponse => Loc.T("Unerwartete Antwort von Odoo. %@", Detail),
        OdooErrorKind.Redirected => Detail.Length == 0
            ? Loc.T("Der Server leitet auf eine andere Adresse um. Bitte die Adresse eintragen, unter der Odoo direkt erreichbar ist.")
            : Loc.T("Der Server leitet nach %@ um. Bitte diese Adresse als Server eintragen.", Detail),
        _ => Detail,
    };
}
