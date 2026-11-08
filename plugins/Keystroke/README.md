# Keystroke

Automates the Gold Saucer's **Keybound Brawler** typing minigame: reads each prompt and types
the answer for you, with human-paced timing by default, then queues the next game.

Open it with `/keystroke`. `/keystroke compact` toggles the compact strip, `/keystroke inspector`
opens the Inspector.

## Features

- **Typing** — three human-paced profiles built on measured typing and reaction data
  (Relaxed ≈38 wpm, Normal ≈52 wpm, Skilled ≈105 wpm) plus a fully custom one: per-key delay
  distribution, hesitations, reaction window and reading time per character. Types while the game
  is in the background. Opt-in *Instant mode* sends every key of a word in one frame.
- **Targeting** — the barrier phrase first in the boss phase, casting enemies next (typing their
  word interrupts the attack), weakest enemy after. Pauses while input-blocking attacks land.
- **Automation** — start on duty entry, auto-queue from Keybound Square (Register → Commence),
  counted loops, repeat until stopped, auto-leave after the result screen, party auto-commence,
  and stop conditions (after N games, at an MGP target, at a time of day).
- **Keybound Coffers** — open every coffer you carry with one click.
- **Rewards** — session and lifetime MGP/MGC tracking with a 14-day chart, games per hour, WPM
  and accuracy; MGC cap warning at a configurable percentage.
- **Alerts** — in-game toast, sound effect and chat line for loop end, MGC cap, errors and
  (optionally) every game result.
- **Hotkeys** — start/stop, pause, overlay and window shortcuts that work while the game is
  unfocused.
- **Overlay** — small status strip with the word as keycaps and session counters.
- **Inspector** — live view of the Keybound Brawler addons, arrays and enemy casts, with a
  one-click text summary or JSON snapshot for bug reports (Advanced → Inspector).

## Interface

A bevelled charcoal panel with an RGB (or cyan/accent/gold) stripe along the top, an icon rail
on the left and pages laid out as group boxes. The word being typed is shown as keycaps that
press down and light up as keys go out — on the dashboard, in the compact strip and on the
overlay. Six accent colours, effects and reduce-motion switches live on the **look** page.

## Installing

Add this custom repository in Dalamud Settings → Experimental → Custom Plugin Repositories:

```
https://raw.githubusercontent.com/alizea1337/DalamudPlugins/main/repo.json
```

Keystroke then appears in the plugin installer like any other plugin.

## Building

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher) at
`%AppData%\XIVLauncher\addon\Hooks\dev`, or set `DalamudLibPath`.

```
dotnet build plugins/Keystroke -c Release
```

The packaged plugin lands in `plugins/Keystroke/bin/Release/Keystroke/` (`latest.zip` plus the
loose files). Load the DLL through Dalamud's dev plugin locations for testing.

## Third-party

- [DejaVu Sans](https://dejavu-fonts.github.io) — Bitstream Vera / DejaVu license
- [Lucide](https://lucide.dev) — ISC License
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) — MIT License

## License

MIT — see [LICENSE](../../LICENSE).

Keystroke is not affiliated with Square Enix. Automating gameplay is against the FFXIV terms of
service; use at your own discretion.
