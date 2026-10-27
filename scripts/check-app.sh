#!/bin/bash
# Zeiterfassung für Odoo
# Copyright (C) 2026 Baumgärtner Marketing GmbH
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Kompiliert den C#-Teil der Windows-App auf dem Mac oder unter Linux. XAML prüft erst der Bau unter Windows.
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf tools/MacCheck/obj/stubs
python3 scripts/make-xaml-stubs.py src/Zeiterfassung.App tools/MacCheck/obj/stubs > /dev/null
dotnet build tools/MacCheck -p:Platform=x64 -nologo -clp:NoSummary -v:quiet
