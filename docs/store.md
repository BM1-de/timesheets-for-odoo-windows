# Eintrag im Microsoft Store

Texte und Angaben für das Partner Center. Das Paket für den Store baut der Release-Workflow
(siehe [RELEASE.md](../RELEASE.md)); der Store signiert es selbst.

## Für alle Sprachen

| Feld | Wert |
|---|---|
| Reservierte Namen | Zeiterfassung für Odoo · Timesheets for Odoo · Feuilles de temps pour Odoo |
| Paketidentität | aus *Produktverwaltung › Produktidentität*: `Package/Identity/Name`, `Package/Identity/Publisher`, `Package/Properties/PublisherDisplayName`; als Variablen `STORE_IDENTITY_NAME`, `STORE_PUBLISHER`, `STORE_PUBLISHER_DISPLAY_NAME` im Repository hinterlegen |
| Kategorie | Geschäftlich › Zeit und Ausgaben |
| Preis | kostenlos, alle Märkte |
| Altersfreigabe | IARC-Fragebogen; keine Frage trifft zu (3+) |
| Datenschutz-URL | https://github.com/BM1-de/timesheets-for-odoo/blob/main/PRIVACY.md (gilt für alle Fassungen, Windows muss vor der ersten Einreichung dort ergänzt sein) |
| Website | https://github.com/BM1-de/timesheets-for-odoo-windows |
| Support | https://github.com/BM1-de/timesheets-for-odoo-windows/issues |
| Copyright | © 2026 Baumgärtner Marketing GmbH |
| Weitere Lizenzbedingungen | GNU Affero General Public License 3.0 oder neuer, https://www.gnu.org/licenses/agpl-3.0.html |
| Systemanforderungen | Windows 10 Version 2004 oder neuer, x64 oder ARM64 |

**Eingeschränkte Funktion `runFullTrust`**, Begründung bei der Einreichung:

The app is a packaged desktop app built with WinUI 3 and the Windows App SDK. runFullTrust is the
standard capability for MSIX-packaged desktop apps; the app runs as a full-trust process for its
icon in the notification area and its own windows. It does not access other apps' data.

## Hinweise für die Zertifizierung

The app is a client for the user's own Odoo server (self-hosted, Odoo.sh or Odoo Online). For
certification, please use the built-in demo, which needs no server:

1. Start the app. Its window opens above the clock icon in the notification area of the taskbar.
   Later, click the clock icon to open it again; on Windows 11 it may be in the overflow (^)
   next to the system clock.
2. Enter Server: demo and API key: demo, click Connect, then Done.
3. Try booking time (project, task, description, duration), or leave the duration empty and
   click Start; the running time appears at the top of the window and a green dot on the icon,
   and the red button stops and books it. Check in and out, browse days with the arrows, and open
   Settings with the gear icon. Right-click the icon for the menu.

The demo uses invented data held in memory; it sends nothing over the network.

## Deutsch

- **Kurzbeschreibung:** Stunden auf Projekte und Aufgaben buchen oder mit Start und Stopp messen,
  ein- und ausstempeln, direkt aus dem Infobereich der Taskleiste.
- **Suchbegriffe:** Odoo · Zeiterfassung · Stundenzettel · Timesheet · Anwesenheit · Stempeluhr · Arbeitszeit

**Beschreibung**

Zeiterfassung für Odoo bringt deine Zeiterfassung in die Taskleiste: Uhrsymbol anklicken, Projekt und Aufgabe wählen, Dauer eintragen oder die Zeit starten, fertig. Der Eintrag landet sofort in deinem Odoo.

ZEITEINTRÄGE
• Projekte mit Suche, die zuletzt verwendeten stehen oben
• Aufgabe, Beschreibung und Dauer wie 1:30, 1,5 oder 90 min
• Auf Wunsch auf 15, 30 oder 60 Minuten gerundet
• Frühere Tage durchblättern und nachtragen
• Einträge per Klick ändern oder löschen
• Mehrere Firmen: Projekte nach Firma, Zwischensumme je Firma

PROJEKT-TIMER
• Ohne Dauer starten, stoppen bucht sofort
• Ohne Projekt starten, beim Stoppen zuordnen
• Mit ▶ eine Arbeit des Tages erneut starten
• Immer nur eine laufende Zeit; ein neuer Start bucht die alte
• Läuft weiter, auch wenn die App beendet ist

ANWESENHEIT
• Ein- und ausstempeln mit einem Klick
• Zeiten und Pausen des Tages
• Beginn, Ende und Pausen korrigieren, Vergessenes nachtragen
• Wie viel davon noch nicht auf Projekte gebucht ist

INFOBEREICH
• Grüner Punkt am Symbol, solange eine Projektzeit läuft
• Der Tooltip am Symbol nennt alle Zeiten von heute
• Rechtsklick: laufende Zeit stoppen und buchen, ohne das Fenster zu öffnen

Für Odoo 17, 18 und 19, Community und Enterprise, selbst betrieben, auf Odoo.sh oder Odoo Online. Nötig sind die Odoo-Apps Zeiterfassung und/oder Anwesenheiten und ein API-Schlüssel deines Odoo-Benutzers. Die App spricht nur mit deinem Odoo; der Schlüssel liegt in der Anmeldeinformationsverwaltung von Windows.

Ohne Odoo ausprobieren: als Server „demo“ eintragen.

Freie Software (AGPL-3.0), der Quellcode liegt auf GitHub. Odoo ist eine Marke der Odoo S.A.; diese App steht in keiner Verbindung zu Odoo S.A.

## English

- **Short description:** Book hours to projects and tasks or time them with start and stop, check
  in and out, right from the notification area of your taskbar.
- **Search terms:** Odoo · timesheet · time tracking · attendance · time clock · hours · projects

**Description**

Timesheets for Odoo puts your time tracking in the taskbar: click the clock icon, pick project and task, enter the duration or start the timer, done. The entry goes straight to your Odoo.

TIME ENTRIES
• Projects with search, recently used ones on top
• Task, description and duration such as 1:30, 1.5 or 90 min
• Optionally rounded to 15, 30 or 60 minutes
• Browse earlier days and fill them in
• Edit or delete entries with a click
• Several companies: projects by company, subtotal per company

TIMER
• Start without a duration, stopping books right away
• Start without a project, assign it when stopping
• Start a task of the day again with ▶
• One timer at a time; starting another books the running one
• Keeps running even when the app is closed

ATTENDANCE
• Check in and out with one click
• Today's periods and breaks
• Correct start, end and breaks, add forgotten periods
• How much of it is not yet booked to projects

NOTIFICATION AREA
• A green dot on the icon while a timer runs
• The tooltip on the icon lists all of today's times
• Right-click to stop and book the running timer without opening the window

For Odoo 17, 18 and 19, Community and Enterprise, self-hosted, on Odoo.sh or Odoo Online. Requires the Odoo apps Timesheets and/or Attendances and an API key of your Odoo user. The app only talks to your Odoo; the key is stored in the Windows Credential Manager.

Try it without Odoo: enter "demo" as the server.

Free software (AGPL-3.0), source code on GitHub. Odoo is a trademark of Odoo S.A.; this app is not affiliated with Odoo S.A.

## Français

- **Description courte :** Saisissez vos heures sur projets et tâches ou mesurez-les avec le
  minuteur, pointez l’arrivée et le départ, depuis la zone de notification de la barre des tâches.
- **Termes de recherche :** Odoo · feuille de temps · suivi du temps · présence · pointeuse · heures · projets

**Description**

Feuilles de temps pour Odoo place votre suivi du temps dans la barre des tâches : cliquez sur l’horloge, choisissez le projet et la tâche, indiquez la durée ou démarrez le minuteur, c’est fait. La saisie arrive directement dans votre Odoo.

SAISIES DE TEMPS
• Projets avec recherche, les plus récents en tête
• Tâche, description et durée comme 1:30, 1,5 ou 90 min
• Arrondi à 15, 30 ou 60 minutes si vous le souhaitez
• Parcourir et compléter les jours précédents
• Modifier ou supprimer une saisie d’un clic
• Plusieurs sociétés : projets par société, sous-total par société

MINUTEUR
• Démarrer sans durée, l’arrêt enregistre aussitôt
• Démarrer sans projet, l’attribuer à l’arrêt
• Relancer une tâche du jour avec ▶
• Un seul minuteur à la fois ; en démarrer un autre enregistre le précédent
• Continue de tourner même quand l’app est fermée

PRÉSENCE
• Pointer l’arrivée et le départ d’un clic
• Périodes et pauses du jour
• Corriger l’arrivée, le départ et les pauses, ajouter une période oubliée
• Ce qui n’est pas encore saisi sur des projets

ZONE DE NOTIFICATION
• Un point vert sur l’icône tant qu’un minuteur tourne
• La bulle d’aide de l’icône indique tous les temps du jour
• Clic droit : arrêter et enregistrer le minuteur sans ouvrir la fenêtre

Pour Odoo 17, 18 et 19, Community et Enterprise, auto-hébergé, sur Odoo.sh ou Odoo Online. Nécessite les applications Odoo Feuilles de temps et/ou Présences et une clé API de votre utilisateur Odoo. L’app ne communique qu’avec votre Odoo ; la clé est enregistrée dans le Gestionnaire d’identification de Windows.

Essayer sans Odoo : indiquez « demo » comme serveur.

Logiciel libre (AGPL-3.0), code source sur GitHub. Odoo est une marque d’Odoo S.A. ; cette app n’a aucun lien avec Odoo S.A.
