# Keystroke

Automates the Gold Saucer's **Keybound Brawler** typing minigame: reads each prompt and types
the answer for you, with humanized timing by default. Dark, macOS-style interface.

Open it with `/keystroke`.

## Features

- **Typing** — humanized per-key timing (log-normal delays, occasional hesitations), a "Fast"
  preset, and fully custom timing. Types while the game is in the background. Optional
  *Instant mode* inserts whole answers (opt-in, with a warning).
- **Automation** — start on duty entry, auto-queue from the lobby, counted loops, auto-leave,
  party auto-commence, and stop conditions (after N games, at an MGP target, at a time of day).
- **Keybound Coffers** — open every coffer you carry with one click.
- **Rewards** — session and lifetime MGP/MGC tracking, games per hour, WPM and accuracy; MGC
  cap warning at a configurable percentage.
- **Alerts** — in-game toast, sound effect, chat line, or a Windows notification when the game is
  in the background.
- **Hotkeys** — global start/stop, pause, overlay and window shortcuts that work while the game
  is unfocused.
- **Overlay** — compact status pill in the lobby and duty.
- **Inspector** — live view of the Keybound Brawler addons, arrays and enemy casts, with a
  one-click JSON snapshot for bug reports (Advanced → Inspector).

## Installing

Add this custom repository in Dalamud Settings → Experimental → Custom Plugin Repositories:

```
https://raw.githubusercontent.com/alizea1337/DalamudPlugins/main/repo.json
```

Until 1.0, Keystroke is published on the **testing** track: enable "Get plugin testing builds"
for it in the installer.

## Building

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher) at
`%AppData%\XIVLauncher\addon\Hooks\dev`, or set `DalamudLibPath`.

```
dotnet build plugins/Keystroke -c Release
```

The packaged plugin lands in `plugins/Keystroke/bin/x64/Release/Keystroke/`. Load it through
Dalamud's dev plugin locations for testing.

## Third-party

- [DejaVu Sans](https://dejavu-fonts.github.io) — Bitstream Vera / DejaVu license
- [Lucide](https://lucide.dev) — ISC License
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) — MIT License

## License

MIT — see [LICENSE](../../LICENSE).

Keystroke is not affiliated with Square Enix. Automating gameplay is against the FFXIV terms of
service; use at your own discretion.
