# Changelog

All notable changes to AI Usage Dock. Versions match the `v*` tags and the package identity in `Package.appxmanifest`.

## Unreleased

### Added

- Ollama Cloud band (shown as **Ollama**). It reads `GET https://ollama.com/api/balance` with the API key from a masked **API key** field under the Ollama toggle, which is stored in Windows Credential Manager rather than `settings.json` (type `clear` to remove it). Without a saved key it falls back to the `OLLAMA_API_KEY` environment variable. Legacy Pro/Max plans show session and weekly windows; current plans show included credits for the billing period. A 429 response's `Retry-After` is honoured even on forced refreshes.
- Native arm64 builds. `build-and-install.ps1` and `build-release.ps1` take `-Architecture x64|arm64` and default to the OS architecture.
- **Dock shows** dropdown under each provider toggle: **Auto** (session, else the tightest limit, as before), **Tightest limit**, or any window the provider has reported, such as **Week (Fable only) · 7d**. A chosen window that is not reported falls back to Auto. The band's `wk` figure is dropped when the band already shows a weekly window.
- Detail pages have **Refresh** (reads usage again and redraws the page in place) and **Settings** (gear) in their command bar, so a page opened from the Dock reaches both directly.
- The Ollama icon sits on a white tile so the black mark stays visible on a dark Dock.
- **Refresh every** setting for the background refresh: 1, 5, 10, 15, 30 or 60 minutes.
- **Usage bar colors** setting: keep the green/yellow/red status colours, or use the accent colour for every bar. The status container styles render as pale tints in the light theme.

### Changed

- Background refresh runs every 10 minutes by default instead of every minute.

### Fixed

- Claude's model-scoped weekly caps (`weekly_scoped` limits, shown as "Fable only" on claude.ai) appear as their own **Week (&lt;model&gt; only)** window. They were merged into **Week (all models)**, which then showed the scoped cap's percentage.

## 1.0.1 — 2026-09-19

### Fixed

- A terminal window no longer flashes during Dock refreshes. The window came from `agy`'s auto-updater, which every 16 minutes launches a detached copy of the CLI that asks Windows for its own console. The Antigravity and Claude refresh commands now run with the CLI's self-updater switched off (`AGY_CLI_DISABLE_AUTO_UPDATE=true`, `DISABLE_AUTOUPDATER=1`). `ProcessHelpers` gained an environment parameter so providers can set variables on the processes they start. The CLIs you run yourself keep updating as usual.

### Changed

- Usage bars are coloured by remaining capacity: green at 25% or more, yellow from 10% to 24% (flagged "Running low"), and red below 10% or when a window is locked (flagged "Almost out", or "Exhausted" at 0%). The "N% left" label takes the same colour. Healthy bars previously used the accent colour and only turned red at 0%. ([#2](https://github.com/GChavez0210/AIUsageDock/pull/2))

## 1.0.0 — 2026-09-19

First tagged release.

### Added

- Continuous integration. `.github/workflows/build.yml` builds and tests on `windows-latest` for every push to `main` and every pull request, packs a signed `AIUsageDock.msix` with `makeappx`, and uploads it with the certificate's public half. Pushing a `v*` tag stamps the version into the package identity and publishes a GitHub release with the `.msix` and `.cer`. ([#1](https://github.com/GChavez0210/AIUsageDock/pull/1))
- `build-release.ps1`, which produces the same signed package locally into `dist\`, creating a self-signed `CN=AIUsageDock-Dev` certificate the first time.
- Install instructions for a released package in the README.

### Fixed

- Refreshes that resolved a CLI to an npm `.cmd` launcher ran through `cmd.exe`, which Windows always gives a console. `NodeShimResolver` now reads the launcher and starts `node.exe` on the CLI's script directly; launchers it does not recognise still go through `cmd.exe`. Node options that carry a separate value (`-r esm`) are kept together. ([#1](https://github.com/GChavez0210/AIUsageDock/pull/1))
- The CI job trusts the build certificate before verifying the packed `.msix`.

## 0.2.0 and earlier

Development before the first tagged release, in order.

- Dock bands for Codex, Claude Code, and Antigravity with detail pages showing per-window usage and account information.
- Detail pages rendered as Adaptive Cards with filled remaining-capacity bars, later slimmed.
- Screenshots of the Dock bands and detail pages.
- Codex resolves to the native `codex.exe` shipped in the npm platform package, with `codex.cmd` as a fallback.
- **Select subscriptions** settings page to hide services you do not use; disabled services are not refreshed.
- Official AI Usage Dock branding and icon.
