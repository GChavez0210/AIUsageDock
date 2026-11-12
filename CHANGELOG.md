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
- The pages and bands now follow the tray panel of [Win-CodexBar](https://github.com/nesszer/Win-CodexBar), reimplemented in C# with no dependency on its binaries:
  - **Pace line** under each window that has a length and a reset time: "33% in reserve · Lasts until reset", "5% in deficit · Runs out in 2d 20h", or for a session "Projected empty in 33m". **Show pace** turns it off.
  - **AI usage overview** page with every enabled provider as one compact card each, and an **AI usage overview** Dock band that names the tightest provider in its title and the others in its subtitle. The band reuses what the provider bands read.
  - **Show usage as**: percent left (the default, as before) or percent used. Bands, bars, percentages and warnings all follow it.
  - **Show reset times as**: a countdown (the default) or the clock time of the reset.
  - **Hide personal information**: leaves the account, organization and any email address off the pages.
  - **Check provider status**: reads the public status pages of Claude (`status.claude.com`) and OpenAI (`status.openai.com`), at most every five minutes, and shows a line on the page and `incident` on the band while one is open. No credentials or account data are sent.
  - **Quota warnings**: a Windows notification when a window reaches the **Warn at** or **Critical at** threshold, or runs out. Each window notifies once per level and again only after it has reset.
  - **Warn at** and **Critical at** set the usage where bars turn yellow and red.

### Changed

- Detail pages use the tray panel layout and fit without scrolling for a typical provider: the provider name with the account email opposite, "Updated 2 minutes ago · source" with the plan opposite, then per window a thin rounded bar with a tick where an even spend would stand, the percentage with the reset opposite, and the pace line. Account details are small label/value rows; the plan and email are not repeated there. Refreshing, refresh-failure and incident notices are single lines.
- Bars are drawn as small images instead of tinted containers, so they are a few pixels tall and solid in both themes. **Usage bar colors → Accent color** now means a fixed blue (`#0A84FF`), not the system accent.
- Band subtitles show the reset as a countdown (`wk 55% · 1h 23m`) unless **Show reset times as** is set to the clock time.
- A bar turns yellow at 75% used and red at 90% used. It used to change one point later (under 25% and under 10% left).
- **Refresh AI usage** now waits for a background refresh already under way and then reads again, instead of returning at once without reading.

- Background refresh runs every 10 minutes by default instead of every minute.
- Detail pages open at once with the last reading and refresh in the background. A fresh cache answers without I/O and draws its final state; a slower read shows **Refreshing…** and redraws when it lands; a failed read keeps the last good reading under **Refresh failed: …**. The page's **Refresh** command now refreshes only that provider, also in the background. Pages previously blocked until the provider's CLI or API answered.

### Fixed

- Claude's model-scoped weekly caps (`weekly_scoped` limits, shown as "Fable only" on claude.ai) appear as their own **Week (&lt;model&gt; only)** window. They were merged into **Week (all models)**, which then showed the scoped cap's percentage.
- A Claude band set to show one window no longer slips to another. When the reading came from `claude /usage` instead of the usage cache, its windows were named after the printed text ("Current week (all models)") and only the tightest limit per period was kept, so the chosen window was missing and the band fell back to the tightest limit until the page reread the cache. Both sources now keep every limit under the same names (**Session**, **Week (all models)**, **Week (&lt;model&gt; only)**), and the cache is preferred whenever `/usage` refreshed it. The legacy per-model buckets are now labelled **Week (Opus only)** and **Week (Sonnet only)**.

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
