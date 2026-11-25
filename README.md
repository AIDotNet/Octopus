<div align="center">

<img src="src/Octopus.App/Assets/Icon/logo.png" alt="Octopus Logo" width="116" />

# Octopus

**Cross-Platform Terminal & AI Coding Agent Workbench**

Organize terminals, 60+ AI coding agents, files, Git worktrees, SSH tunnels, and remote devices by project — compiled natively with NativeAOT and protected with end-to-end encryption.

**English** · [简体中文](README.zh-CN.md)

[![GitHub Release](https://img.shields.io/github/v/release/AIDotNet/Octopus?include_prereleases&style=flat-square)](https://github.com/AIDotNet/Octopus/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/AIDotNet/Octopus/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/AIDotNet/Octopus/actions/workflows/ci.yml)
[![GitHub Stars](https://img.shields.io/github/stars/AIDotNet/Octopus?style=flat-square)](https://github.com/AIDotNet/Octopus/stargazers)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![Avalonia 12.2](https://img.shields.io/badge/Avalonia-12.2.0-8B44AC?style=flat-square)](https://avaloniaui.net/)
[![TKCode.Arc 1.3](https://img.shields.io/badge/TKCode.Arc-1.3.1-0EA5E9?style=flat-square)](https://docs.tkcode.si)

[Website](https://octoterm.si/en) · [Downloads](https://github.com/AIDotNet/Octopus/releases) · [Changelog](CHANGELOG.md) · [Issues](https://github.com/AIDotNet/Octopus/issues) · [WeChat Group](#community)

</div>

---

## At a Glance

| Pillar | What Octopus Gives You |
| --- | --- |
| **Native Performance** | Built on .NET 10, Avalonia 12, and NativeAOT across macOS, Windows, and Linux — fast startup, low memory usage, no browser runtime. |
| **61 Agents · Dual Mode** | Auto-detects 61 coding agent CLIs; switch seamlessly between real PTY terminals and an **ACP visual GUI** (49 agents supported) with multi-account isolation. |
| **Zero-Knowledge Remote** | Pick up work from another computer or the Android companion app over LAN, official encrypted relay, or self-hosted server — keys never leave your device. |
| **Extensible & Transparent** | Sandboxed JavaScript plugin engine (`Jaunt`) with explicit permission grants, built-in 92-day usage & cost billing, and ECDSA-signed updates. |

---

## Features

### Project-Centric Workbench
- **Projects & Groups**: Group terminals, coding agents, and open files under each project; switching projects keeps background sessions running. Customize project tiles with Lucide icons, pixel icons, or your own images, and reorder via drag-and-drop.
- **Shared Group Directories**: Enable *Merge Working Directories* on a project group to automatically pass sibling local project folders to agents that support additional directories.
- **Split Layouts & Branch Workspaces**: Drag tabs into horizontal or vertical split panes saved per project. Spin up **Git Worktree** branch workspaces so multiple agents can edit isolated branches in parallel without conflicts.
- **Refined Desktop Experience**: Switch between **Compact** and **Comfortable** UI density, light/dark themes, and English / Simplified Chinese UI. Supports system tray / menu bar residency, automatic session & open-tab restore on startup, and crash resilience with logs stored under `~/.octopus/logs/`.

### 61 Built-in AI Coding Agents & ACP Visual GUI
- **Dual-Mode Workflow (Terminal ⇄ ACP GUI)**:
  - Run any agent in a native terminal, or switch to the **ACP (Agent Client Protocol)** visual interface (supported by **49 of 61** built-in agents via native ACP or official adapters, plus custom agent definitions and optional ACP Registry integration).
  - Features a unified work timeline and docked activity tray, interactive permission approval cards and form prompts, collapsible tool calls and inline code diffs, queued prompts with **Immediate Send**, bundled model/provider brand icons, third-party API model discovery, and one-click **Continue in Terminal** handoff.
- **Multi-Account & Session Lifecycle**:
  - Manage multiple subscription accounts per agent with isolated config directories (concurrent across projects/tabs) or keychain credential snapshots. Bind default accounts per project or pick one at launch.
  - Browse, resume by ID, copy across accounts, or hand off session histories between agents in the Explorer's **Sessions** tab.
- **Live Telemetry & Background Tracking**:
  - Tracks active background workflows and async subagents across turn pauses, showing real-time running indicators and wait-for-reply notifications via hooks and the bundled `octopus-signal` local IPC forwarder.
  - Automatic system proxy injection (`HTTP_PROXY` / `HTTPS_PROXY`) for CLI and ACP child processes without overriding explicit user proxy variables.

<details>
<summary><strong>View all 61 built-in coding agents (28 Native ACP · 21 ACP Adapter · 12 Terminal CLI)</strong></summary>

#### Native ACP + Terminal (28)

| | | | |
| --- | --- | --- | --- |
| Antigravity CLI (`agy`) | Augment Code (Auggie) | Autohand Code | Cline |
| CodeBuddy Code | Cursor Agent | Devin CLI | Factory Droid |
| Gemini CLI | GitHub Copilot CLI | Goose | Grok CLI |
| Hermes Agent | IBM Bob | Junie | Kilo Code |
| Kimi Code CLI | Kiro | MiMo Code | Muse Code |
| omp | OpenCode | OpenHands CLI | Qoder CLI |
| Qoder CLI CN | Qwen Code | Stakpak | VT Code |

#### ACP Adapter + Terminal (21)

| | | | |
| --- | --- | --- | --- |
| Agoragentic | Amp | Claude Code | Codex CLI |
| Cortex Code | Corust Agent | crow-cli | DeepAgents |
| DimCode | Dirac | fast-agent | GLM Agent |
| Harn | Kimchi | MiniMax Code | Minion Code |
| Mistral Vibe | Nova | Pi | Poolside |
| siGit Code | | | |

#### Terminal CLI (12)

| | | | |
| --- | --- | --- | --- |
| Aider | Codebuff | Comate CLI | Continue CLI |
| Crush | ForgeCode | Jules Tools | Letta Code |
| Nanocoder | OpenClaw | Trae Agent | Warp Oz CLI |

> You can also add custom agents or override launch commands, arguments, and environment variables in **Settings → Agents**.

</details>

### High-Performance Native Terminal
- Powered by `MiniPty` and `TKCode.Arc.Terminal` with synchronized output (`RenderHold`) to reduce tearing and flicker.
- Supports scrollback scrollbar (configurable up to 100,000 lines), clickable **OSC 8 hyperlinks** with URL preview, `Ctrl` / `Cmd` + click on existing file paths (including `:line:col`) to open directly in the workspace viewer, clipboard image paste (auto-saved to a temporary PNG path), Kitty extended keyboard protocol, full IME / CJK input, and platform-native shortcuts.

### Files, Viewers & Git Integration
- **Lazy-Loaded Explorer Panel**: Stays completely idle when closed; loads on demand with colored VS Code-style file icons, full file-tree CRUD (move to Trash where supported), and fast global filename search.
- **Built-In Viewers**: Syntax-highlighted code viewer, side-by-side **Markdown editor & live preview with native Mermaid diagram rendering** (no browser needed), image viewer (including SVG and animated formats), and syntax-highlighted Diff viewer.
- **Git Workflow**: Inspect changes, stage/unstage hunks or files, commit with raw Git error reporting, browse branches and commit diffs, and manage Git Worktrees directly from the right panel.

### SSH Hosts & Port Forwarding Tunnels
- **SSH Config Manager**: Add, edit, delete, and test connections directly against `~/.ssh/config` (supports **SSH**, **Mosh**, and **Telnet**, including Unicode / non-ASCII host aliases via isolated generated wrappers).
- **Remote Workspace & Tunnels**: SSH projects include an interactive remote terminal, remote directory browser & file tree, cross-machine file copy, and a visual **Tunnels** tab for **Local (`-L`)** and **Remote (`-R`)** port forwarding with start/stop controls and live logs.

### Remote Access, Mobile Companion & End-to-End Security
Control your desktop projects, terminals, and ACP agents from another computer or your phone — no account required for local desktop use or LAN direct pairing.

| Mode | Account Needed | How It Works |
| --- | --- | --- |
| **LAN Direct** | No | Pair directly on the same network via 6-digit verification code or pairing link; all traffic is encrypted end to end. |
| **Official Relay (`octoterm.si`)** | Yes | Sign in on your devices to view online/offline hosts under your account and connect across networks through a zero-knowledge ciphertext relay. |
| **Self-Hosted Server** | Custom | Deploy your own Octopus Server and point clients to its URL at sign-in — identical end-to-end encryption guarantees. |

- **Zero-Knowledge End-to-End Encryption**: Every device generates a local **ECDSA P-256** identity key (private keys never leave the device). Sessions negotiate ephemeral **ECDH P-256** keys with **HKDF-SHA256** and encrypt all frames with **AES-256-GCM**. The relay only forwards opaque ciphertext.
- **Device Access Password**: Signed-in desktop hosts generate an 8-character access password stored in the OS keychain (or customizable). Another device under the same account authorizes once via end-to-end **PBKDF2-SHA256 + HMAC-SHA256** proof — the password is never sent to or stored by the relay, and any paired device can be revoked immediately.
- **Mobile Companion (`Octopus.Mobile`)**: Touch-first app built with `TKCode.Arc.Mobile` featuring remote **ACP GUI conversations** with a unified work area and sliding live ticker, remote interactive terminals, remote file browser, Git changes & Diff viewer, project search, activity filters, workspace grouping, image upload, and Android in-app updates.

### Plugins, Usage Billing & Signed Updates
- **Sandboxed JS Plugins**: Directory-packaged plugins run inside the standalone **`Jaunt`** JavaScript engine with declarative manifests and explicit per-capability permission grants (agent signals/probes, scoped file reads, allowlisted hosts, keychain secrets).
- **Built-In Usage & Billing (`octopus.agent-usage`)**: Aggregates multi-account subscription windows (Claude Code, Codex, Antigravity CLI, Copilot, Grok, Cursor, OpenCode) and API provider balances, retains **92 days** of local token/cost history, sends threshold alerts, and exports printable **HTML receipts** or **CSV** breakdowns.
- **Signed Auto-Updates**: Update manifests (`update.json`) are verified against pinned **ECDSA P-256** public keys alongside package size and **SHA-256** checksums. Supports Stable / Preview channel switching and Markdown release notes preview.

---

## Download & Installation

Download installers for your platform from [GitHub Releases](https://github.com/AIDotNet/Octopus/releases) or [octoterm.si](https://octoterm.si/en). All desktop builds are NativeAOT compiled binaries.

| Platform | Artifacts | Requirements & Notes |
| --- | --- | --- |
| **macOS** | `Octopus-<ver>-osx-arm64.dmg`<br>`Octopus-<ver>-osx-x64.dmg` | **macOS 13+** (Apple Silicon & Intel). Drag into Applications. Matching `.zip` packages are published for auto-updates. Before uninstalling, optionally run `/Applications/Octopus.app/Contents/MacOS/Octopus --cleanup-hooks` to remove external agent hooks. |
| **Windows** | `Octopus-<ver>-win-x64-setup.exe`<br>`Octopus-<ver>-win-x64.zip` | **Windows 10/11 (64-bit)**. Inno Setup installer supports custom install paths and per-user / all-users install, and automatically cleans up external agent hooks on uninstall. Portable `.zip` also provided. |
| **Linux** | `Octopus-<ver>-linux-x64.run`<br>`Octopus-<ver>-linux-x64.tar.gz` | **Linux x64**. `.run` supports interactive install or `--prefix DIR` (defaults to `/opt/octopus` for root, `~/.local/share/octopus` for user) and includes `uninstall.sh` with automatic hook cleanup. Portable `.tar.gz` also provided. |
| **Android** | `Octopus-<ver>-android.apk` | **Android 7.0+ (API 24+)**. Official release APKs are release-signed, included in `update.json`, and support in-place upgrades and in-app updates. *(Custom/fork builds without signing secrets produce `*-android-debug.apk`.)* |

> **iOS**: Source code for iPhone (`iOS 15+`, NativeAOT device builds) is included in [`Octopus.Mobile.slnx`](Octopus.Mobile.slnx); App Store release is not yet published.

### Data, Cache & Crash Logs

Desktop app data is stored uniformly under `.octopus` in your home directory (existing installations automatically migrate once from legacy OS config paths on first launch):

| Platform | App Data & Crash Logs | Disposable Cache |
| --- | --- | --- |
| **macOS** | `~/.octopus/` *(logs in `~/.octopus/logs/`)* | `~/Library/Caches/Octopus/` |
| **Linux** | `~/.octopus/` *(logs in `~/.octopus/logs/`)* | `~/.cache/octopus/` *(or `$XDG_CACHE_HOME/octopus`)* |
| **Windows** | `%UserProfile%\.octopus\` *(logs in `logs\`)* | `%LocalAppData%\Octopus\Cache\` |

---

## Terminal Tips & Platform Notes

<details>
<summary><strong>Shortcuts, Scrollback, File Links & Windows ConPTY Details</strong></summary>

### Terminal Shortcuts & Interactions
- **Scrollback Navigation**: A scrollbar appears automatically when scrollback history exists. Use mouse wheel, `Shift+PageUp` / `Shift+PageDown`, or `Shift+Home` / `Shift+End`. When an app/agent enables mouse reporting, normal wheel events go to the app — hold `Shift` while scrolling to inspect terminal history. Alternate-screen apps (`vim`, `less`) manage their own scrolling.
- **Hyperlinks & File Jumps**: Click OSC 8 links directly (hover reveals the real URL). Hold `Ctrl` (`Cmd` on macOS) and hover over existing file paths (including `:line:col`) to open files in a workspace tab or reveal folders in the file manager *(local terminals only)*.
- **Editing, Copy/Paste & Images**:
  - `Shift+Enter` and `Ctrl+J` insert newlines without submitting in CLI agents.
  - Pasting when the clipboard contains only an image saves a temporary PNG and pastes its path (auto-cleaned after 24h).
  - **macOS**: Native line editing (`Cmd+←/→`, `Cmd+Backspace`, `Option+←/→`) in normal mode without interfering with fullscreen or Kitty-protocol apps.
  - **Windows / Linux**: `Ctrl+C` copies when text is selected and sends interrupt (`SIGINT`) when no selection is active; right-click copies selection or pastes when unselected; `Shift+Insert` pastes on all platforms, and plain `Ctrl+V` pastes on Windows.

### Windows Platform Notes
- Uses native **Windows ConPTY** — no Bash or WSL required. Automatically selects the default shell in priority order: **PowerShell 7 → Windows PowerShell → cmd** (or pick any installed shell such as Git Bash in **Settings → Terminal**).
- Merges the latest user and system registry `PATH` on terminal launch so newly installed CLIs work immediately without restarting Octopus.
- Probes npm/package-manager `.cmd` / `.bat` agent shims via `cmd.exe` with output capture and timeout protection, and resolves Windows batch shims for native ACP adapters.
- Follows ConPTY console session lifetime (without forcing children into a Job Object) for Microsoft Store PowerShell compatibility — closing a terminal ends the console session without killing detached GUI apps.

</details>

---

## Development & Architecture

### Prerequisites
- **.NET 10 SDK** (exact version pinned in [`global.json`](global.json)); macOS 13+ supported.
- Optional: `android` / `ios` .NET workloads for building mobile heads in [`Octopus.Mobile.slnx`](Octopus.Mobile.slnx).

### Quick Start

```bash
# Run the desktop application
dotnet run --project src/Octopus.App

# Run the mobile companion desktop preview shell
dotnet run --project src/Octopus.Mobile.Desktop

# Build and run all tests
dotnet build Octopus.slnx
dotnet test Octopus.slnx

# Run the live terminal smoke test (briefly opens a window)
dotnet run --project tests/Octopus.Terminal.Smoke -- --script
```

> The terminal smoke test launches the native default PowerShell on Windows (verifying I/O, resize, `Ctrl+C`, exit codes, and restart) and the login shell on macOS / Linux (additionally verifying device responses, `vim`, and `terminfo`).

### Project Structure

```text
src/
├── Octopus.Core                              Domain models, settings & service endpoints
├── Octopus.Storage                           Local persistence (projects.json, settings.json, etc.)
├── Octopus.Platform(.MacOS/.Linux/.Windows)  Shell discovery, env, paths, OS keychain & migration
├── Octopus.Terminal(.MiniPty/.Avalonia)      PTY abstractions, MiniPty backend & Avalonia control
├── Octopus.Agents                            61 built-in agent definitions, scanner, accounts, sessions & signals
├── Octopus.Acp                               ACP JSON-RPC runtime, 19 agent dialects & adapter installer
├── Octopus.SignalForwarder                   Bundled NativeAOT IPC signal forwarder (octopus-signal)
├── Octopus.FileSystem                        File tree, watcher, search & directory browser
├── Octopus.Git                               Git status, diff, staging, commits & worktrees
├── Octopus.Ssh                               SSH / Mosh / Telnet config, remote tree & port tunnels
├── Octopus.Plugins(.Jaunt)                   Plugin manifest, permission model & Jaunt JS runtime
├── Octopus.Updates                           ECDSA-signed update manifest verification & updater state machine
├── Octopus.Remote(.Host/.Client)             P-256 identity, E2E secure channel, LAN & relay transport
├── Octopus.App                               Cross-platform Avalonia desktop app & built-in plugins
└── Octopus.Mobile(.Desktop/.Android/.iOS)    Mobile companion UI & platform heads (Android/iOS in Octopus.Mobile.slnx)
tests/                                        Module unit tests, headless UI tests & terminal smoke test
packaging/                                    macOS plist/entitlements, Windows Inno Setup & Linux installer assets
tools/                                        Packaging, icon generation, update signing & release helper scripts
```

### CI, Packaging & Releases

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | Push to `main` & PRs | Restores, builds, and runs all tests on Linux, macOS, and Windows. |
| [`.github/workflows/mobile.yml`](.github/workflows/mobile.yml) | Mobile/shared path changes | Builds Android Release APK and iOS simulator + unsigned NativeAOT device binaries. |
| [`.github/workflows/release.yml`](.github/workflows/release.yml) | Tags `vX.Y.Z` / `vX.Y.Z-preview.N` | Builds macOS (arm64/x64), Windows (x64), Linux (x64), and Android APK, signs `update.json`, and publishes GitHub Release & Octopus Server *(manual trigger runs a dry run)*. |

```bash
# Local packaging scripts
tools/package-macos.sh osx-arm64 1.0.0 dist
tools/package-linux.sh 1.0.0 <linux-x64-publish-dir> dist
```

<details>
<summary><strong>Release Signing Secrets & Maintainer Checklist</strong></summary>

- **Update Manifest Signing**: `tools/make-update-manifest.py` signs `update.json` with `UPDATE_SIGNING_KEY` (ECDSA P-256 PEM private key generated via `tools/make-update-key.sh`; trusted public keys live in [`src/Octopus.Updates/UpdateEndpoints.cs`](src/Octopus.Updates/UpdateEndpoints.cs)). Setting `OCTOPUS_RELEASE_TOKEN` also syncs the release to Octopus Server (`https://octoterm.si` by default).
- **macOS Signing & Notarization**: Configured via `APPLE_CERTIFICATE` (base64 `.p12`), `APPLE_CERTIFICATE_PASSWORD`, `APPLE_ID`, `APPLE_TEAM_ID`, and `APPLE_APP_SPECIFIC_PASSWORD`. Without them, `.dmg` builds are ad-hoc signed and unnotarized. Windows installers are currently unsigned.
- **Android Release Signing**: Configured via `ANDROID_KEYSTORE` (base64 keystore from `tools/make-android-keystore.sh`), `ANDROID_KEYSTORE_PASSWORD`, and `ANDROID_KEY_ALIAS`. Without them, CI emits `*-android-debug.apk` and omits Android from `update.json`.
- **Full Release Procedure**: See [`.agents/skills/publish-release/SKILL.md`](.agents/skills/publish-release/SKILL.md).

</details>

---

## Community

Scan the QR code below to join the Octopus WeChat group:

<img src="images/wechat.png" alt="Octopus WeChat Group" width="260" />

---

## Star History

<a href="https://www.star-history.com/#AIDotNet/Octopus&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date&theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date" />
    <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date" />
  </picture>
</a>
