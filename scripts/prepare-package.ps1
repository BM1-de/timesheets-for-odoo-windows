# Zeiterfassung für Odoo
# Copyright (C) 2026 Baumgärtner Marketing GmbH
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Setzt Version und Identität im Paketmanifest, bevor MSBuild das Paket baut. Die Fassung von GitHub trägt als
# Herausgeber den Inhaber des Signaturzertifikats (Variable MSIX_PUBLISHER), die Fassung für den Microsoft Store die
# Werte aus dem Partner Center (STORE_IDENTITY_NAME, STORE_PUBLISHER, STORE_PUBLISHER_DISPLAY_NAME).
#
#   scripts/prepare-package.ps1 -Version 0.1.0 [-Variant github|store]

param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [ValidateSet('github', 'store')] [string] $Variant = 'github'
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path (Join-Path $PSScriptRoot '..\src\Zeiterfassung.App\Package.appxmanifest')).Path
$manifest = [xml]::new()
$manifest.PreserveWhitespace = $true
$manifest.Load($path)
$identity = $manifest.Package.Identity
$identity.Version = "$Version.0"

if ($Variant -eq 'store') {
    foreach ($name in 'STORE_IDENTITY_NAME', 'STORE_PUBLISHER', 'STORE_PUBLISHER_DISPLAY_NAME') {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name fehlt (Partner Center › Produktidentität)." }
    }
    $identity.Name = $env:STORE_IDENTITY_NAME
    $identity.Publisher = $env:STORE_PUBLISHER
    $manifest.Package.Properties.PublisherDisplayName = $env:STORE_PUBLISHER_DISPLAY_NAME
}
elseif ($env:MSIX_PUBLISHER) {
    $identity.Publisher = $env:MSIX_PUBLISHER
}

$manifest.Save($path)
Write-Host "$Variant`: $($identity.Name) $($identity.Version), Herausgeber $($identity.Publisher)"
