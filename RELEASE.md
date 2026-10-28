# Releases

How a version of the Windows app is built and published. The app is distributed through the
Microsoft Store, which signs it and delivers updates; GitHub carries the source code and the release
notes. The workflow [`.github/workflows/release.yml`](.github/workflows/release.yml) does the work;
this page lists what it needs and what to check.

## What a release contains

- `Zeiterfassung_<version>_Store.msixbundle` (workflow artifact `store`): the app for x64 and ARM64
  in one bundle with the identity from Partner Center, uploaded by hand to a new submission. The
  Store signs it itself.
- A GitHub release with the notes from `CHANGELOG.md` and the link to the Store entry.

Windows can move an installation from a single package to a bundle, but not back. The releases are
bundles from the first version on; keep it that way.

## One-time setup

### Microsoft Store

In Partner Center (company account *BM1.de*), the app *Zeiterfassung für Odoo* is reserved with the
Store ID `9PLPXXJ8QH0Q`; the other names to reserve are listed in [docs/store.md](docs/store.md).
The repository variables under *Settings › Secrets and variables › Actions*, tab *Variables*, hold
the values from *Product management › Product identity*:

| Name | Value in Partner Center |
|---|---|
| `STORE_IDENTITY_NAME` | `Package/Identity/Name` |
| `STORE_PUBLISHER` | `Package/Identity/Publisher` |
| `STORE_PUBLISHER_DISPLAY_NAME` | `Package/Properties/PublisherDisplayName` |
| `STORE_PRODUCT_ID` | Store ID, for the link in the release notes |

Partner Center accepts submissions only while the verification of the company account is complete
(*Account settings › Legal info*, tab *Developer*).

### Optional: download from GitHub

A package downloaded outside the Store installs only with a signature Windows trusts. Azure Artifact
Signing provides one for a monthly fee. With it, the workflow also builds
`Zeiterfassung_<version>.msixbundle`, signs it with the certificate of Baumgärtner Marketing GmbH
and writes `Zeiterfassung.appinstaller`; both are attached to the release. Users open the App
Installer file to install, and the app installed this way reads the same file under
`releases/latest/download/` once a day to find newer versions, so a release must never be marked as
pre-release.

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

Then add these repository variables:

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
5. **Workflow:** runs the tests, builds the Store bundle for x64 and ARM64 (artifact `store`) and
   creates the GitHub release. With Artifact Signing it also signs the bundle for GitHub, writes
   `Zeiterfassung.appinstaller` and attaches both to the release.
6. **Store:** in Partner Center, create a new submission, upload the bundle from the artifact
   `store`, update *What's new in this version* and submit.
7. **Afterwards:** once the Store has published the version, install it from the Store on a Windows
   machine and check that it starts. With the download from GitHub, also open
   `Zeiterfassung.appinstaller` from the release on a machine that has not seen the app, and check
   that an installed older version offers the update (*Nach Updates suchen …* in the menu of the
   clock icon).

A run started by hand (*Actions › Release › Run workflow*) builds the bundles as workflow artifacts
without publishing anything; without the signing variables the bundle for GitHub is not signed. To
install an unsigned test bundle, sign it with a self-made certificate whose subject matches the
publisher and trust that certificate on the test machine.

The development status is _Alpha_ until the app has been used daily for half a year, _Beta_
after that.
