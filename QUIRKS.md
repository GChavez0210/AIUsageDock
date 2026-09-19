# Quirks and workarounds

Things about AI Usage Dock, Command Palette, or the CLIs it talks to that are not obvious and that you may run into. Each entry says what happens, why, and what the extension or you can do about it.

## Command Palette

### Enabling the extension does not put anything on the Dock

Turning on **AI Usage Dock** in Command Palette's Extensions list only registers the provider. The bands are added separately, one per service, under **Settings → Dock → Bands** or through **Edit Dock → +** on the Dock itself. Look for **Codex usage**, **Claude usage**, and **Antigravity usage**.

### Place the AI bands after the Performance Monitor band

Keep the AI usage bands **after** the built-in **Performance Monitor** band (CPU, memory, network, GPU). Extension bands load a moment after the built-ins, and Command Palette 0.100 rebuilds every band that follows a late insertion. The Performance Monitor widget loses its sampler when that happens and freezes at 0%. With the AI bands positioned after it, both keep updating.

### Bands update in place, not through a provider reload

A refresh updates each band's title and subtitle directly. The extension deliberately does not raise `ItemsChanged` on refresh: that makes Command Palette reload every provider's commands and rebuild the Dock, which resets the other bands.

### Detail pages follow the palette theme

Detail pages are Adaptive Cards rendered by Command Palette. Colours, fonts, and the exact shade of green, yellow, and red on the bars come from the host's Adaptive Card theme, not from the extension.

## CLI processes

### CLI self-updaters open a terminal window

`agy` checks for updates every 16 minutes. When it decides to update, it launches a detached copy of itself that asks Windows for a console of its own, and the terminal host draws a window for it, even though the extension started the parent with `CreateNoWindow`. Claude Code has an equivalent background updater.

The extension runs both CLIs with their updater switched off (`AGY_CLI_DISABLE_AUTO_UPDATE=true` for Antigravity, `DISABLE_AUTOUPDATER=1` for Claude). Only the processes the extension starts are affected; the CLIs you run yourself keep updating on their own schedule.

If a window still flashes, check where it comes from before assuming it is this extension. On the machine this was diagnosed on, the Codex desktop app also spawns `ssh.exe` every 20 seconds, hidden but easy to confuse when tracing processes.

### npm `.cmd` launchers are unwrapped

When a CLI resolves to an npm launcher such as `codex.cmd`, running it means running `cmd.exe`, a console-subsystem process that Windows gives a console regardless of `CreateNoWindow`. `NodeShimResolver` reads the launcher, finds the Node script it would run, and starts `node.exe` on that script directly. Batch files that are not npm launchers still go through `cmd.exe`.

### Codex prefers the native binary and falls back to the shim

The extension looks for `codex.exe` on `PATH`, then for the platform package npm installs under `@openai/codex` (`codex-win32-x64` or `codex-win32-arm64`, matching the OS architecture), and only then for `codex.cmd`. If the native launch or the app-server protocol fails for a reason other than authentication, it retries once through `codex.cmd`.

### `ANTHROPIC_API_KEY` disables Claude refreshes

If `ANTHROPIC_API_KEY` is set in the environment, the extension never launches `claude -p /usage`. Claude Code would bill that call to the API key instead of the subscription, silently switching billing modes. The Claude band then shows only what is already in `~/.claude.json`, and reports it as stale once the cache ages out.

### Claude usage comes from a cache first

Claude Code keeps a service-reported `cachedUsageUtilization` snapshot in `~/.claude.json`. The extension reads that first and only asks the CLI for a refresh when the snapshot is older than five minutes or one of its windows has already reset. CLI refreshes are throttled to one every 30 seconds. A snapshot whose windows have expired is never shown as current.

### Antigravity `/usage` does not consume tokens

`agy -p "/usage" --mode plan` returns the quota payload without starting a model turn, so the periodic refresh does not spend Antigravity credits.

## Data and display

- Percentages show **remaining** capacity: `100%` is a full window and drains toward `0%`.
- Bars are green at 25% or more remaining, yellow ("Running low") from 10% to 24%, and red ("Almost out", or "Exhausted" at 0%) below 10% or when the window is locked. Thresholds are `LowThresholdPercent` and `CriticalThresholdPercent` in `Pages/UsageCard.cs`.
- Windows the CLI does not report are shown as unavailable, never estimated.
- If a live refresh fails but a still-valid cached reading exists, the page shows that reading with a note saying when it was taken.
- Automatic refreshes run once a minute, are throttled per provider, and overlapping refreshes are coalesced into one.

## Privacy boundaries

The extension never reads or stores OAuth access tokens and never calls provider endpoints directly. The files it opens (`~/.claude.json`, `~/.gemini/google_accounts.json`) contain account metadata only; credentials live in separate files the extension does not touch. Usage numbers always come from the CLI the vendor ships.

## Packaging

The installer is an `.msix` rather than an `.exe` or `.msi` because the extension registers a COM server and a `windows.appExtension`, and Windows only honours either from a package with MSIX identity. A bare executable has no package identity, so Command Palette would never discover it.

Releases are signed with a `CN=AIUsageDock-Dev` certificate. Windows will not install the package until that certificate's public half (`AIUsageDock.cer`) is in **Local Machine → Trusted People**; the [README](README.md#install-a-release) has the two commands.
