# Distributing WinQEMU

How WinQEMU is packaged, signed, released and updated, and what moving to a trusted signature, the Microsoft Store or
WinGet would take. For users, the README covers installing, updating and uninstalling.

## Packaging

- **One MSIX for x64**, built by `tools/Build-Package.ps1` (`dotnet publish -c Release` with
  `GenerateAppxPackageOnBuild`). The Windows App SDK is inside the package (`WindowsAppSDKSelfContained`): a PC needs
  nothing else but QEMU. Never trimmed (`PublishTrimmed` false): bindings and the JSON settings use reflection.
- **QEMU is never in the package**, nor downloaded: WinQEMU finds an installed QEMU and says how to install one (the
  readiness assistant). The TAP-Windows installer (OpenVPN, GPLv2) is bundled for internal virtual networks; its source and
  hash are in `WinQEMU/Assets/Drivers/README.txt`.
- **Identity**: `Name` `cfde84db-a7f2-4241-ae31-4fd9bc34967b`, `Publisher` `CN=troposteff`. Windows keys everything on
  the identity: settings (`%LOCALAPPDATA%\Packages\<family name>\LocalCache\Local\WinQEMU\settings.json`), the startup task,
  the `winqemu` alias. Changing either part makes **another application** (see Changing the identity).
- **Capabilities**: `runFullTrust` (a WinUI 3 desktop app runs at medium integrity; needed for QEMU, the named pipes, the
  console's window reparenting) and `microphone` (QEMU runs in the package's name; asked for only for machines given the
  microphone). `systemAIModels`, left over from the project template and never used, was removed in 2026.0.3.

## Versions

`2026.0.1_beta`, `2026.0.2_beta`… while features are developed, then `2026.1.0` for the release. MSIX takes four numbers
and nothing else, so the manifest says `2026.0.1.0`; the suffix exists only in what people read: the settings
(`WinQEMU.Core.AppVersion`), the file name, the Git tag and the release title. A minor version of 0 is a beta.
`tools/Get-WinQemuVersion.ps1` applies the same rule for the scripts and the release workflow.

Raise `Version` in `WinQEMU/Package.appxmanifest` for each release: Windows only updates to a higher version, and refuses to
register a changed manifest under the same one.

## Releasing

1. Raise the version in the manifest; commit it on `main`.
2. `pwsh ./tools/Build-Package.ps1`: builds and signs the package with the certificate in the author's store, and writes
   `artifacts\v<version>\`: the `.msix`, `WinQEMU.cer`, `Install-WinQEMU.ps1`, `SHA256SUMS.txt`, and for a release (not a
   beta) `WinQEMU.appinstaller`. It prints the `gh release create` command.
3. Tag and publish: betas with `--prerelease`, a release with `--latest`. Pushing the tag runs **Release check**
   (`.github/workflows/release-check.yml`): the tag must be the manifest's version, the tests must pass, and the tagged
   sources must build into a package, kept 30 days as an unsigned artifact.
4. Install the published package on a clean machine (Windows Sandbox works: it starts without QEMU and without the
   certificate) and on a PC with the previous version, to check the update keeps the settings and the machines.

The workflow signs and publishes nothing, with a read-only token: a tag pushed by anyone with write access cannot produce a
signed build. Signing stays where the key is.

### Betas and releases

| | Beta (`2026.0.x_beta`) | Release (from `2026.1.0`) |
|---|---|---|
| GitHub | Pre-release | Release, marked latest |
| `.appinstaller` | none | at `releases/latest/download/WinQEMU.appinstaller` |
| Updates | by hand, from each `.msix` | Windows checks at each start and updates in the background |

GitHub's `latest` never points at a pre-release, which is why betas have no App Installer file: it would name a package
that `latest` does not serve. A PC installed from the `.appinstaller` keeps that address and follows each release; one
installed from a `.msix` updates when a newer `.msix` is installed. Either way an update is in place: settings and machines
are kept.

**Withdrawing a release**: deleting it makes `latest` the previous release again, whose `.appinstaller` names an older
version; App Installer never downgrades, so PCs that took the withdrawn one keep it. Fix forward: publish a higher version.
Never reuse a version number for different contents.

## Signing today, and the trust it asks for

The package is signed by a self-signed certificate, `CN=troposteff` (5 years, in the author's personal store; public part in
`packaging/WinQEMU.cer`). Windows installs it only once the certificate is trusted, so `Install-WinQEMU.ps1`, run as
administrator:

1. checks the package against `SHA256SUMS.txt` when present, and that the package is signed by the very `WinQEMU.cer` it is
   about to trust;
2. imports the certificate into the PC's **Trusted People** store, not as a root authority: it lets Windows install
   packages it signed, it does not make it trusted for anything else (TLS, drivers, other code);
3. installs the package.

Administrator rights are needed for step 2 only. The private key never leaves the author's PC and is in no repository and
no CI secret.

### Smart App Control

On a PC where Smart App Control is on (enforcement mode), the package installs but **WinQEMU does not start**: seen on a
test PC. Smart App Control runs only code that Microsoft's cloud service knows, or that is signed by a certificate
authority of Microsoft's Trusted Root Program; a self-signed certificate in Trusted People does not count. Nothing on
WinQEMU's side can change that short of a trusted signature (below); the user's only other way is to turn Smart App
Control off, their decision.

QEMU may be refused there too, whatever WinQEMU's signature: the qemu.weilnetz.de build checked here (11.1) has its `.exe`
files signed by a certificate that expired in December 2023, without a valid timestamp, and its DLLs unsigned. Not
verified on a Smart App Control PC yet.

### GitHub secrets

None are needed today. If signing is ever moved into GitHub Actions (with a CI-friendly signing service, below, or a
certificate file), the safe shape is:

- a dedicated workflow on tags only, with `permissions: contents: write` for that job alone;
- the secrets in a GitHub **environment** (`release`) with **required reviewers**, so a pushed tag waits for the author's
  approval before any secret is readable;
- a repository rule protecting `v*` tags, so only maintainers can create them.

These are repository settings: the author's to make.

## Towards a trusted signature

A signature from a public authority removes the certificate import entirely, and lets WinQEMU start under Smart App
Control. Options, as of October 2026:

| Option | Cost | Notes |
|---|---|---|
| Microsoft Store | free for individuals | The Store re-signs the package with its own identity. Separate application (see below). |
| SignPath Foundation | free for open source | OSI licence (GPL-3.0 qualifies), public repository, automated builds through their CI integration, a published signing policy. **The certificate is in SignPath Foundation's name**: the manifest's `Publisher` must change to it. |
| Azure Artifact Signing (formerly Trusted Signing) | about 10 USD a month | Individuals must be in the United States or Canada: not available to this project's author. |
| A commercial OV certificate | about 200–500 USD a year | Publisher in the author's name; key on a hardware token or a cloud HSM. |

Any of them changes the `Publisher`, so the identity, so it is a migration, not an update.

### Changing the identity

Windows treats a package with another `Name` or `Publisher` as another application: it installs side by side, with empty
settings, its own startup task, and its own `winqemu` alias (the newest registered wins). It happened once, when the
publisher became `CN=troposteff` (version 1.1). What it takes:

1. Install the new package; do not remove the old one yet.
2. Copy `settings.json` from the old family's `LocalCache\Local\WinQEMU` to the new one's (the library folder, the virtual
   networks, the folders of the library, the preferences). The machines themselves stay where they are: their folders are
   not in the package and are not touched.
3. Turn « start at sign-in » on again in the new one if it was.
4. Remove the old package (`Remove-AppxPackage`), which deletes its own settings copy only.

Done by hand once. Step 2 is now automatic towards any other identity (the Store's): at its first start, a WinQEMU that has
no settings yet copies the GitHub installation's (`WinQEMU.Core.Services.SettingsImport`, by its family name
`cfde84db-…_6swaqgnpzyjaw`), then says so and asks for the GitHub installation to be uninstalled, so that a single WinQEMU
drives the machines. The source is copied, never moved. Checked with a package of a test identity: library, folders and
virtual networks taken over, the GitHub installation's file unchanged.

## Microsoft Store

Prepared, not submitted: the submission is the author's. The Store signs the package, so WinQEMU then starts under Smart
App Control, needs no certificate on users' PCs, and is updated by the Store. What is ready, in `packaging/store/`:

- **The package**: `pwsh ./tools/Build-StorePackage.ps1` writes an unsigned `.msix` with the Store identity, read from
  `packaging/store/identity.json` (`Name`, `Publisher`, `PublisherDisplayName`, from Partner Center, Product identity,
  once the name is reserved). `Package.appxmanifest` keeps the GitHub identity; the build is given a copy
  (`WinQemuStoreManifest`). Built and run with a test identity: it installs beside the GitHub package, takes its settings
  over, and carries no driver installer.
- **No bundled driver**: Store policy 10.2.4 generally refuses a dependency on a non-Microsoft driver. The Store build
  leaves the TAP-Windows installer out (`WinQemuStore`); creating an internal network then points the user at OpenVPN's
  installer, as an external network points at Npcap's (`TapDriver.IsBundled`). Both drivers serve optional features and
  are declared in the certification notes.
- **The listing**, English and French (`listing.en.md`, `listing.fr.md`): the QEMU dependency stated at the beginning of
  the description (policy 10.2.4).
- **The certification notes** (`certification-notes.md`): why `runFullTrust`, what runs elevated and when, the optional
  drivers, the microphone, how to test.
- **The privacy policy** ([`PRIVACY.md`](../PRIVACY.md)), required by the microphone capability: WinQEMU collects nothing.

Left to the author: the Partner Center account (free for individuals; identity check with a government ID), the name
reservation (the "QEMU" trademark in the name is the author's call, see the README notice), `identity.json`, age rating,
markets, price (free), screenshots (`docs/images`), and the submission. Betas can go to a package flight (a private
audience) instead of GitHub pre-releases.

Not needed for the Store: `WinQEMU.appinstaller`, `Install-WinQEMU.ps1`, the certificate, `SHA256SUMS.txt`.

## WinGet

Not submitted. The community repository installs each new package in a validation sandbox; a self-signed MSIX does not
install there, nor on any PC without the certificate. Two ways, once one exists:

- **From the Store**: `winget install` by the Store product id (the `msstore` source); nothing to maintain in winget-pkgs.
- **From GitHub Releases with a trusted signature**: a manifest in `microsoft/winget-pkgs`, `manifests/t/troposteff/WinQEMU/<version>/`,
  pointing at the release's `.msix`, with its SHA-256 (from `SHA256SUMS.txt`) and its `PackageFamilyName`, for each stable
  release (`wingetcreate update` produces it). Betas would stay out of it.

## Manual checks

What the tests cannot run, before a release:

- **Fresh install**: Windows Sandbox, no QEMU: run `Install-WinQEMU.ps1` as administrator; WinQEMU starts, opens the
  readiness assistant (QEMU missing), `winqemu help` answers.
- **Update**: on a PC with the previous version and some machines: install the new package; the library, the settings and
  the machines are all there, the version in the settings is the new one.
- **Without acceleration**: on a PC (or a VM with nested virtualisation) where Windows Hypervisor Platform is off: the
  assistant says so and offers to enable it (one UAC prompt, then a restart); a machine on "Automatic" starts in emulation
  instead of failing.
- **Altered download**: change a byte of the `.msix`: `Install-WinQEMU.ps1` refuses it before trusting anything.
