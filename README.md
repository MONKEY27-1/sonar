# Sonar

A fast Windows soundboard and voice changer for gamers and streamers, built with **.NET 8**, **WPF**, and **NAudio**.

[![License: MIT](https://img.shields.io/github/license/MONKEY27-1/sonar)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/MONKEY27-1/sonar)](https://github.com/MONKEY27-1/sonar/releases/latest)
[![Build](https://github.com/MONKEY27-1/sonar/actions/workflows/build.yml/badge.svg)](https://github.com/MONKEY27-1/sonar/actions/workflows/build.yml)

**[Download the latest release](https://github.com/MONKEY27-1/sonar/releases/latest)** · **[Website](https://sonars.netlify.app)**

<!--
  Screenshots go here — a Grid-view library shot and a Voice Changer shot are the most useful.
  ![Sonar main library](docs/screenshots/library.png)
  ![Sonar Voice Changer](docs/screenshots/voice-changer.png)
-->

## Features

- **Instant playback** — import unlimited MP3, WAV, OGG, and FLAC files via button or drag-and-drop; low-latency WASAPI playback with simultaneous sounds
- **Global hotkeys** that work while games are focused (low-level keyboard/mouse hooks), including push-to-play
- **Voice Changer** — live pitch, formant, and true tempo shifting (phase-vocoder based, keeps duration independent of pitch), plus robot, echo, distortion, overdrive, delay, reverb, and proximity effects, with a global effect-strength dial and saved voice presets
- **Multi-device audio routing** — play through several headphone outputs and capture from several microphones at once, with automatic mic passthrough and real RMS-based loudness normalization
- **Community Plugins & Marketplace** — install small sandboxed scripts (JS via Jint, no CLR access) that add their own tiles and panel buttons, or author and share your own
- **Performance Mode** — reduces visual effects, virtualizes the sound library, and lowers background polling for large libraries or low-end hardware
- **Organize your way** — favorites, recently used, most played, folders, tags, instant search, and multi-select bulk actions (delete, move, favorite, tag)
- **Dark, Light, AMOLED, and custom themes**
- **Accounts & Pro tier** — optional sign-in for cloud-ready profiles and license status; a one-time Pro upgrade unlocks unlimited folders, custom themes, and cloud sync (the app is fully usable offline on the free tier)
- **Auto-updates** — checks GitHub Releases on launch and offers a one-click install for new versions
- **Export/import full collections** (`.sbpack`) for backup or moving to a new PC

## Requirements

- Windows 10/11
- Nothing else — the installer bundles the .NET runtime, so no separate .NET install is needed

## Installing

Grab the installer from the [latest release](https://github.com/MONKEY27-1/sonar/releases/latest) and run it. It installs per-user (no admin rights needed) and sets up Start Menu/desktop shortcuts.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/MONKEY27-1/sonar.git
cd sonar
dotnet restore
dotnet build
dotnet run --project src\Soundboard\Soundboard.csproj
```

To build the installer itself, see [installer/Soundboard.iss](installer/Soundboard.iss) — it expects a `dotnet publish` output and packages it with Inno Setup.

## Data Directory

All application data is stored in:

```
%LocalAppData%\Soundboard\
├── Sounds\
├── Icons\
├── Profiles\
├── Backups\
├── logs\
├── settings.json
└── library.json
```

Back up or move this entire folder to preserve your soundboard (or use Settings → Export Collection for a single-file backup).

## Microphone Routing (Virtual Audio)

To play sounds through your microphone in games or voice chat:

1. Install a virtual audio cable such as [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)
2. In **Settings → Audio**, add **CABLE Input** as a microphone output device
3. In your game/Discord/OBS, select **CABLE Output** as the microphone
4. Set per-sound or global routing to **Microphone** or **Both**

Mic passthrough (so your real voice — with any active Voice Changer effect — also reaches the virtual mic) is automatic once a virtual mic output device is configured.

## Architecture

```
UI (WPF)           → ViewModels (MVVM, CommunityToolkit.Mvvm)
PlaybackManager     → coordinates play/stop/queue
AudioEngine         → NAudio WASAPI multi-device output + phase-vocoder voice effects
LibraryService      → import, metadata, search, bulk operations
HotkeyManager        → global keyboard/mouse hooks
SettingsService      → JSON persistence
ThemeService         → dynamic resource theming
SessionService / LicenseService → accounts and Pro-tier gating (Supabase-backed)
```

## Release notes

See [GitHub Releases](https://github.com/MONKEY27-1/sonar/releases) for the full version history.

## License

[MIT](LICENSE)
