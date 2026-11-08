# Changelog

## 1.1.0

New interface. A bevelled charcoal frame with a drifting cyan-lime-gold signal stripe, an icon
rail, and pages laid out as group boxes with cut-in titles. Checkbox rows, thin accent sliders,
inset fields, bevel buttons. The word being typed is shown as keycaps that press down as keys go
out, on the dashboard, the compact strip and the overlay. Six selectable accents; effects and
reduce-motion switches on the new "look" page. DejaVu Sans replaces Inter.

## 1.0.0

First stable release. Everything from the 0.1.0 testing build, verified in play: typing,
targeting, Cursed Gaze / Sensory Jammer pauses, auto-queue and commence, auto-leave, rewards
tracking, alerts, overlay and anti-AFK.

- Fix: wrong words are cleared with Esc instead of backspacing.
- Skilled profile raised to about 105 WPM.
- Uses the current Dalamud object API (BaseId).

## 0.1.0 (testing)

First testing release.

- Typing engine with Relaxed / Normal / Skilled / Custom profiles based on measured human typing
  and reaction data, plus an opt-in Instant mode. Types in the background without game focus.
- Smart targeting: barrier phrase first in the boss phase, casting enemies next, weakest after.
- Auto-queue from Keybound Square (Register → Commence), counted loops, Repeat, stop conditions
  (games, MGP target, time of day), auto-leave after the result screen, party auto-commence.
- Keybound Coffer opener.
- Session and lifetime rewards tracking with a 14-day MGP chart; MGC cap warning.
- Alerts via in-game toast, chat and sound effect.
- Global hotkeys that work while the game is unfocused; compact view; overlay pill.
- Dark macOS-style interface with optional glass transparency.
- Inspector for verifying game state and reporting problems.
