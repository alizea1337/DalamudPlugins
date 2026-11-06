# Keybound Brawler game facts

Facts about the game client that Keystroke relies on. These are *facts* (names, IDs, layouts),
not code. Every row has a verification status; anything marked **unverified** must be confirmed
through the Inspector page in-game before code depends on it.

Sources: FFXIVClientStructs (MIT), Lumina sheets, in-game Inspector snapshots. Numbers that
originated as hints from third-party plugins are treated as unverified until seen in-game.

## Territories and content

| Fact | Value | Status |
|---|---|---|
| Keybound Brawler duty territory (`TerritoryType`) | 1359 (`w1en`, place "The Gold Saucer") | **verified 2026-11-06** |
| Keybound Brawler lobby territory (`TerritoryType`) | 1360 (`w1eo`, place "Keybound Square") | **verified 2026-11-06** |
| `ContentFinderCondition` row for the duty | 1095 | unverified |
| Registrar NPC | `ENpcResident` row to be found via Lumina (do not match by English name) | todo |
| Leave-duty confirmation text | `Addon` sheet row 17854 | unverified |

## Addons (`AtkUnitBase` names)

| Addon | Purpose | Status |
|---|---|---|
| `KTGInput` | Text input field. Node 7 is the `AtkComponentTextInput`. | unverified |
| `KTGEWord` | 20 `Component(1001)` slots (node ids 20000+). Each slot: text node **20** (64×34, the word), text node **22** (64×14, small label), text nodes 6/7 (128×14), images 11–18, nine-grids 24–26. Idle slots are parked at −10000,−10000. | **structure verified 2026-11-06**; which text is the answer still to confirm mid-game |
| `KTGResult` | End-of-game result screen; node 48 is the leave button | unverified |
| `KTGHud` | In-duty HUD (exists in ClientStructs) | unverified |
| `KTGEnterDialogue` | Entry dialogue (exists in ClientStructs) | unverified |
| `KTGEnemyCastBar`, `KTGHudGaugeHp` | Cast bar / HP gauge | unverified |
| `ContentsFinderConfirm` | Duty pop confirm; `AddonContentsFinderConfirm.CommenceButton` | ClientStructs |
| `SelectYesno` | Yes/No dialog | ClientStructs |

## Atk arrays (**verified 2026-11-06**, two mid-game captures)

| Array | Layout |
|---|---|
| `NumberArrays[161]` (size 261) | `[0]` = active slot count. Records of 13 ints from index 1: `+0` entityId (e.g. 1073892389), `+1` screenX, `+2` screenY (anchor near the enemy; the word bubble is drawn ~120 px above), `+3` unknown (looks like float bits), `+4` visible = 1, `+5` HP % (66/100/40/80), `+7` cast progress (45 while casting), `+11` = 2 while casting (meaning unknown), others zero |
| `StringArrays[128]` (size 60) | 20 triples: `+0` word ("cactuar", "Braver", "Regina"), `+1` reading (empty on EN), `+2` cast name ("??? Needles" while that enemy casts) |

Enemy casts also appear as `IBattleChara.CastActionId` (49524 "??? Needles", 7.0 s). Typing the caster's word
interrupts the cast (`KTGEnemyCastBar` shows "Interrupted!"). Cursed Gaze / Sensory Jammer ids still unseen.

Slot *i* is consistent across `numbers[1+13i]`, `strings[3i]` and `KTGEWord` component slot *i*.

## KTGInput (**verified 2026-11-06**)

- Component node **7** = `AtkComponentTextInput` (480×32 at ~824,934 on 2560×1440).
- Text node **16** inside it holds the typed text; it is **lowercase** regardless of the word's case
  ("regin" while the word is "Regina"), so matching is case-insensitive. Node 17 shows "1/1".
- `KTGPlayGuide`: "[Enter]: Submit Text  [Esc]: Delete Text/Quit".

## Other in-duty addons seen

`KTGHudTodo` (text 2 "Defeat 25 enemies", text 3 "5/25"), `KTGHudScore` (text 3/5 score), `KTGHudGaugeHp`,
`KTGIGaugeHp` (per-enemy HP bars), `KTGEnemyCastBar` (20 slots, text 3 "Interrupted!", text 5 "0"),
`KTGHudLog`, `KTGPlayerName`, `KTGScreenTextFever` ("TYPING FEVER!").

## Items, currencies, actions

| Fact | Value | Status |
|---|---|---|
| MGP item id | 29 | known (Lumina) |
| MGC (Keybound currency) item id | 52322 | **verified 2026-11-06** (461 read, matches in-game) |
| Keybound Coffer item id | 52450 | unverified (player had 0) |
| MGC cap | `CurrencyManager.GetItemMaxCount(52322)` = 10,000 | **verified 2026-11-06** |
| Cursed Gaze (input-blocking cast) action id | 49529 | unverified |
| Sensory Jammer (input-blocking cast) action ids | 49532, 49702 | unverified |

## Queueing

- `ContentsFinder.Instance()->QueueInfo` exposes `QueueState`, `QueuedEntries[]`, `PoppedQueueEntry`.
- Duty commence: `AddonContentsFinderConfirm.CommenceButton` click via its `AtkEventManager` ButtonClick listener.

## Agents (FFXIVClientStructs `AgentId`, confirmed 2026-11-06 from AgentModule.cs)

| Agent | Id | Status |
|---|---|---|
| `KTGHud` | 397 | ClientStructs |
| `Unk398` — "deals with input for KTG" | 398 | ClientStructs (unnamed) |
| `KTGEnterDialogue` | 399 | ClientStructs |
| `Unk400` | 400 | ClientStructs (unnamed) |
| `KTGResult` | 401 | ClientStructs |

No `Agent*`/`Addon*` structs exist for KTG in ClientStructs; node ids and array layouts must come
from the Inspector.

## AFK / activity

- `InputTimerModule` (`UIModule.GetInputTimerModule`, `Client.UI.Misc`) fields: `AfkTimer` 0x10,
  `ContentInputTimer` 0x14, `InputTimer` 0x18, `TerritoryTypeId` 0x42. Resetting these is the
  documented way to keep the inactivity kick away; writing into agent 398's memory is **not** and
  will not be done without an in-game check of what the field is.

## Duty finder

- `ContentsFinderQueueState`: None 0, Pending 1, Queued 2, Ready 3, Accepted 4, InContent 5.
- `ContentsFinderQueueInfo`: `QueuedEntries` (5 × `ContentsId`), `PoppedQueueEntry` (0x7C),
  `QueueState` (0x55), `PositionInQueue` (0x5C).
