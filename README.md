# Soundboard

A high-performance Windows desktop soundboard built with **.NET 8**, **WPF**, and **NAudio**.

## Features

- Import unlimited MP3, WAV, OGG, and FLAC files via button or drag-and-drop
- Self-contained library: imported files are copied into `%LocalAppData%\Soundboard\Sounds`
- Low-latency WASAPI playback with simultaneous sounds
- Route audio to headphones, microphone (virtual cable), or both
- Global hotkeys that work while games are focused (low-level keyboard/mouse hooks)
- Favorites, recently used, folders, tags, and instant search
- Dark, Light, AMOLED, and custom themes
- Export/import full collections (`.sbpack` zip)
- Automatic library rescan when files change in the Sounds folder

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build & Run

```powershell
cd C:\Users\drago\Projects\Soundboard
dotnet restore
dotnet build
dotnet run --project src\Soundboard\Soundboard.csproj
```

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

Back up or move this entire folder to preserve your soundboard.

## Microphone Routing (Virtual Audio)

To play sounds through your microphone in games:

1. Install a virtual audio cable such as [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)
2. In **Settings → Microphone route**, select **CABLE Input**
3. In your game, select **CABLE Output** as the microphone
4. Set per-sound or global routing to **Microphone** or **Both**

## Gaming Tips

- Use **Low** latency mode for fastest response
- Assign global hotkeys for **Stop All**
- Import sounds while playing; imports run asynchronously and won't interrupt playback
- Keep the app running in the background; global hooks remain active

## Architecture

```
UI (WPF)           → ViewModels (MVVM)
PlaybackManager    → coordinates play/stop/queue
AudioEngine        → NAudio WASAPI multi-output
LibraryService     → import, metadata, search
HotkeyManager      → global keyboard/mouse hooks
SettingsService    → JSON persistence
ThemeService       → dynamic resource theming
```

## License

MIT
