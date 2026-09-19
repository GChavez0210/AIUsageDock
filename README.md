# AI Usage Dock

<p align="center">
<img src="AIUsageDock/Assets/AIUsageDock.svg" alt="AI Usage Dock logo" width="96">
</p>

A Windows PowerToys Command Palette extension that shows remaining Codex, Claude Code, and Antigravity subscription capacity in the Command Palette Dock.

The extension delegates authentication and usage retrieval to the installed CLIs:

- Codex: starts the native `codex app-server --stdio` executable and calls `account/read` and `account/rateLimits/read`.
- Claude Code: reads the service-reported `cachedUsageUtilization` snapshot and the non-secret `oauthAccount` profile that Claude Code maintains in `~/.claude.json`. When the snapshot is stale, it asks `claude -p "/usage" --output-format json` for a refresh and rereads the cache.
- Antigravity: calls `agy -p "/usage" --mode plan --output-format json`, which returns quota data without consuming tokens, and reads the active account name from `~/.gemini/google_accounts.json`.

It never reads or stores OAuth access tokens and never calls provider endpoints directly. The profile files it reads contain only account metadata; credentials live in separate files the extension does not open.

See [QUIRKS.md](QUIRKS.md) for the non-obvious behaviour of the extension, Command Palette, and the CLIs, and [CHANGELOG.md](CHANGELOG.md) for what changed in each release.

## Screenshots

The three bands sit on the Dock next to the built-in Performance Monitor. Selecting a band opens its detail page.

![Claude detail page showing session and weekly bars, plan, billing, and extra usage](screenshots/claude.png)

| Codex | Antigravity |
|---|---|
| ![Codex detail page with a session window running low](screenshots/codex.png) | ![Antigravity detail page with per-model-group windows](screenshots/antigravity.png) |

## Install a release

Requirements: Windows 11, PowerToys Command Palette 0.9 or newer with the Dock enabled, and a signed-in Codex, Claude Code, and/or Antigravity CLI.

Download the `.msix` and `.cer` from the [Releases](https://github.com/GChavez0210/AIUsageDock/releases) page, trust the certificate once, then install (elevated PowerShell):

```powershell
Import-Certificate -FilePath .\AIUsageDock.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\AIUsageDock.msix
```

Restart Command Palette, then add the bands you want under **Settings → Dock → Bands**: **Codex usage**, **Claude usage**, and **Antigravity usage**. Enabling the extension in the Extensions list does not add bands by itself. Keep the AI bands after the built-in Performance Monitor band; [QUIRKS.md](QUIRKS.md#place-the-ai-bands-after-the-performance-monitor-band) explains why.

Use **Select subscriptions** in the extension, or its settings in Command Palette's installed extensions list, to turn off services you do not use. Disabled services are hidden from the extension's commands and Dock bands and are not refreshed.

## What you see

Each band shows remaining capacity for its service: `100%` is a full window and drains toward `0%`. Selecting a band opens a detail page with:

- One section per limit window with a remaining-capacity bar, the window length (`5h`, `7d`), a countdown to the reset and the local reset time. Bars are green, turn yellow under 25% remaining, and red under 10% or when the window is locked.
- An **Account** block with what the CLI reports about the subscription:
  - Claude: plan, account, organization and role for team plans, billing type, subscription date, rate-limit tier when it is not the default, and extra-usage spend against the monthly cap. Per-model weekly caps appear as their own windows when the service reports them.
  - Codex: ChatGPT plan, account email, sign-in method, and credit balance.
  - Antigravity: active Google account, plus a plan or tier if a future CLI adds one to the quota payload.
- A footer with the data source and how long ago it was read.

When a provider cannot be read, the page names the cause (CLI missing, not signed in, timed out, no usage data) and says what to do about it.

## Build from source

Requirements: .NET 10 SDK (`10.0.401` or a later 10.0 feature band) and the Windows 11 SDK / WinUI development tools.

```powershell
./build.ps1
```

To build, test, register the staged package for the current user in development mode, and restart Command Palette:

```powershell
./build-and-install.ps1
```

The install script does not add a development certificate to a trust store.

## Continuous integration and releases

`.github/workflows/build.yml` builds and tests the extension on `windows-latest` for every push to `main` and every pull request, then packs a signed `AIUsageDock.msix` with `makeappx` and uploads it, together with the certificate's public half, as build artifacts.

Signing uses the `SIGNING_CERTIFICATE` repository secret, a base64 encoded PFX whose subject is `CN=AIUsageDock-Dev` so it matches the `Publisher` in `Package.appxmanifest`, with its password in `SIGNING_CERTIFICATE_PASSWORD`. Without those secrets the workflow generates a throwaway self-signed certificate, which still produces an installable package but means importing `AIUsageDock.cer` into **Local Machine → Trusted People** before Windows accepts it.

To release: bump `Version` in `Package.appxmanifest`, add the entry to [CHANGELOG.md](CHANGELOG.md), and push a matching `v*` tag, for example `v1.0.1`. The workflow stamps the version into the package identity and publishes a GitHub release carrying the `.msix` and the `.cer`.

To build the same signed package locally, run `./build-release.ps1`; it writes `AIUsageDock-<version>-x64.msix` and `AIUsageDock-Dev.cer` to `dist\`, creating a self-signed `CN=AIUsageDock-Dev` certificate in your user store the first time.

## Project layout

```text
AIUsageDock/
  Bands/       Dock presentation
  Models/      Shared usage model
  Pages/       Command Palette detail pages
  Providers/   Claude, Codex, and Antigravity CLI adapters
AIUsageDock.Tests/
  Provider parser and process launch regression tests
```

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for projects consulted while designing the extension.

## About

AIUsageDock by Gabriel Chavez - Developed in Mexico with love
