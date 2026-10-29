// Zeiterfassung für Odoo
// Copyright (C) 2026 Baumgärtner Marketing GmbH
// SPDX-License-Identifier: AGPL-3.0-or-later

using Zeiterfassung.Model;

namespace Zeiterfassung.Tests;

/// <summary>
/// Einstellungen. Nicht übernommen sind die Tests zum Übernehmen der Einstellungen älterer Mac-Versionen und zur Zeit in
/// der Menüleiste (takesOverSwitch, keepsStoredOff, menuBarDefaults, menuBarFromRunningTimeSwitch): Die
/// Windows-App hat keine älteren Versionen und keine Menüleiste.
/// </summary>
public sealed class PreferencesTests
{
    // Swift prüft zusätzlich, dass „duration.rounding“ gleich mit 15 gespeichert wird. Das dient nur dem Übernehmen
    // älterer Versionen (eine spätere Anmeldung soll nichts mehr ändern) und fehlt deshalb hier.
    [Fact(DisplayName = "neu installiert wird auf 15 Minuten gerundet")]
    public void RoundingDefault()
    {
        var settings = new MemorySettingsStore();
        Assert.Equal(DurationRounding.QuarterHour, new Preferences(settings).DurationRounding);
    }

    [Fact(DisplayName = "die Auswahl bleibt gespeichert")]
    public void StoresRounding()
    {
        var settings = new MemorySettingsStore();
        new Preferences(settings).DurationRounding = DurationRounding.Hour;
        Assert.Equal(DurationRounding.Hour, new Preferences(settings).DurationRounding);
    }

    [Fact(DisplayName = "die laufende Projektzeit übersteht einen Neustart")]
    public void StoresRunningTimer()
    {
        var settings = new MemorySettingsStore();
        var timer = new RunningTimer("https://odoo.example.de||7", 3, "Kundenmagazin", 32, "Layout", "Titelseite", TestClock.Now);
        new Preferences(settings).RunningTimer = timer;
        Assert.Equal(timer, new Preferences(settings).RunningTimer);
        Assert.Equal("Kundenmagazin › Layout", timer.Path);
    }
}
