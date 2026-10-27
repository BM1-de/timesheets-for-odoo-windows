# Releases

How a version of the Windows app is built, signed and published. The workflow
[`.github/workflows/release.yml`](.github/workflows/release.yml) does the work; this page lists what
it needs and what to check.

## What a release contains

- `Zeiterfassung_<version>.msixbundle`: the app for x64 and ARM64 in one bundle, signed with the
  certificate of Baumgärtner Marketing GmbH from Azure Artifact Signing. Windows only installs and
  updates packages whose signature it trusts and whose publisher matches the installed app.
- `Zeiterfassung.appinstaller`: the file users open to install. The app reads the same file under
  `releases/latest/download/` to find out whether a newer version exists, so a release must never
  be marked as pre-release.
- For the Microsoft Store, a second bundle with the identity from Partner Center (workflow artifact
  `store`), uploaded by hand to a new submission. The Store signs it itself.

Windows can move an installation from a single package to a bundle, but not back. The releases are
bundles from the first version on; keep it that way.

## One-time setup

### Signing with Azure Artifact Signing

1. In an Azure subscription of the company, create an *Artifact Signing* account (formerly Trusted
   Signing), choose the region, and note its endpoint, for example
   `https://weu.codesigning.azure.net/`.
2. Complete the identity validation for the organization *Baumgärtner Marketing GmbH*.
3. Create a certificate profile of type *Public Trust*. Its subject (shown on the profile, for
   example `CN=Baumgärtner Marketing GmbH, O=Baumgärtner Marketing GmbH, L=Bayreuth, S=Bayern,
   C=DE`) becomes the publisher of the package; it must match exactly.
4. Create an app registration in Microsoft Entra ID for GitHub, add a federated credential for
   *GitHub Actions* with organization `BM1-de`, repository `timesheets-for-odoo-windows`, entity
   *Environment* and name `release`, and give the app registration the role *Artifact Signing
   Certificate Profile Signer* on the certificate profile.

In the GitHub repository under *Settings › Secrets and variables › Actions*, tab *Variables*:

| Name | Content |
|---|---|
| `MSIX_PUBLISHER` | subject of the certificate profile, exactly as shown |
| `AZURE_CLIENT_ID` | application (client) ID of the app registration |
| `AZURE_TENANT_ID` | directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | ID of the subscription with the signing account |
| `SIGNING_ENDPOINT` | endpoint of the signing account |
| `SIGNING_ACCOUNT` | name of the signing account |
| `SIGNING_PROFILE` | name of the certificate profile |

No secrets are needed: the workflow signs in to Azure with OpenID Connect from the environment
`release`, which GitHub creates on the first run. Under *Settings › Environments › release* the
environment can be limited to tags `v*`.

The publisher is part of the package identity. Once a version is installed anywhere, changing
`MSIX_PUBLISHER` means existing installations no longer receive updates.

### Microsoft Store

In Partner Center (company account), reserve the names listed in [docs/store.md](docs/store.md).
Under *Product management › Product identity*, copy these values into repository variables:

| Name | Value in Partner Center |
|---|---|
| `STORE_IDENTITY_NAME` | `Package/Identity/Name` |
| `STORE_PUBLISHER` | `Package/Identity/Publisher` |
| `STORE_PUBLISHER_DISPLAY_NAME` | `Package/Properties/PublisherDisplayName` |

Without these variables the workflow builds only the GitHub version.

## Publishing a version

1. **Code on `main`:** all changes merged; `CHANGELOG.md` has a section `## <version> – <date>`.
   Its text becomes the release notes.
2. **Checks before tagging:**
   - unit tests: `dotnet test --project tests/Zeiterfassung.Core.Tests`;
   - the build workflow on `main` is green, and its screenshots (artifact `bilder`) look right;
   - a short manual run on a Windows machine: sign in, book an entry, start and stop a timer,
     check in and out, switch the functions in the settings.
3. **No real data:** the repository contains invented names only; screenshots come from
   `--screenshots` with the built-in demo data.
4. **Tag:** `git tag -a v<version> -m "Zeiterfassung für Odoo <version>"` and
   `git push origin v<version>`. The version comes from the tag; the project does not need to be
   changed.
5. **Workflow:** runs the tests, builds the app for x64 and ARM64, bundles and signs it, writes
   `Zeiterfassung.appinstaller` and creates the GitHub release. With the Store variables set, the
   Store bundle is in the artifact `store`.
6. **Store:** in Partner Center, create a new submission, upload the bundle from the artifact
   `store`, update *What's new in this version* and submit.
7. **Afterwards:** on a Windows machine that has not seen the app, open
   `Zeiterfassung.appinstaller` from the release, and check that an installed older version
   offers the update (*Nach Updates suchen …* in the menu of the clock icon).

A run started by hand (*Actions › Release › Run workflow*) builds the bundles as workflow artifacts
without publishing anything; without the signing variables the bundle is not signed. To install an
unsigned test bundle, sign it with a self-made certificate whose subject matches the publisher and
trust that certificate on the test machine.

The development status is _Alpha_ until the app has been used daily for half a year, _Beta_
after that.
