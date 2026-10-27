# Zeiterfassung für Odoo
# Copyright (C) 2026 Baumgärtner Marketing GmbH
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Schreibt die App-Installer-Datei eines Releases. Sie liegt immer unter „releases/latest/download“; Windows installiert
# darüber die App, und die App liest dort, ob es eine neuere Version gibt. Name und Herausgeber kommen aus dem
# Paketmanifest, wie scripts/prepare-package.ps1 es gesetzt hat. Updates sucht die App selbst (abschaltbar in ihren
# Einstellungen), deshalb fehlen hier die UpdateSettings, mit denen Windows selbst suchen würde.
#
#   scripts/make-appinstaller.ps1 -Version 0.1.0 -Bundle Zeiterfassung_0.1.0.msixbundle -Output build\Zeiterfassung.appinstaller

param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $Bundle,
    [Parameter(Mandatory)] [string] $Output
)

$ErrorActionPreference = 'Stop'
$repository = 'https://github.com/BM1-de/timesheets-for-odoo-windows'
$manifest = [xml](Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot '..\src\Zeiterfassung.App\Package.appxmanifest'))
$identity = $manifest.Package.Identity
$escape = { param($text) [Security.SecurityElement]::Escape($text) }

$content = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021"
              Uri="$repository/releases/latest/download/Zeiterfassung.appinstaller"
              Version="$Version.0">
  <MainBundle Name="$(& $escape $identity.Name)"
              Publisher="$(& $escape $identity.Publisher)"
              Version="$Version.0"
              Uri="$repository/releases/download/v$Version/$Bundle" />
</AppInstaller>
"@

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[IO.File]::WriteAllText($Output, $content, [Text.UTF8Encoding]::new($false))
Write-Host $content
