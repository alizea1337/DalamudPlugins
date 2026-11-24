# Notices

Provenance is MIT licensed. It includes work adapted from the projects below under their own
licenses.

## GatherBuddy Reborn

<https://github.com/FFXIV-CombatReborn/GatherBuddyReborn>, Apache License 2.0.

Adapted files:

- `src/Data/GearStats.cs`: gear and materia stat sums with item-level caps, from
  `GatherBuddy/Crafting/GearsetStatsReader.cs` (Vulcan). Changed to cover gathering stats and to
  return plain arrays.
- `src/Data/ExchangeIndex.cs`: the special-shop currency mapping and the inclusion shop
  page/subpage routes, from `GatherBuddy/Vulcan/Vendors/VendorShopResolver.cs` (which mirrors
  AllaganLib's). Reduced to master books and folklore tomes.
- `src/Run/BookBuyer.cs`: the exchange window callbacks (inclusion shop page, subpage and item;
  plain exchange rows; confirmation dialogs) and where the windows keep their item lists, from
  `GatherBuddy/Vulcan/Vendors/VendorInteractionHelper.cs` and `VendorPurchaseManager.cs`.
- `src/Core/Gathering/GatherSolver.cs`: the gathering rotation solver, from
  `GatherBuddy/AutoGather/Helpers/RotationSolver.cs`. Changed to be pure and to leave out the Diadem.
- `src/Core/Gathering/CollectableRotation.cs`: the collectable rotation, from
  `GatherBuddy/AutoGather/AutoGather.Collectables.cs`.
- `src/Run/Gathering/GatherActions.cs`, `GatheringWindows.cs`, `Upkeep.cs` and `Gatherer.cs`:
  gathering action ids and statuses, the gathering and collectable window layouts, materia
  extraction and aetherial reduction, and the approach of the auto-gather loop, from
  `GatherBuddy/AutoGather/`.
- `Resources/ilvconvert.csv`: base Gathering and Perception per gathering item level, from
  `GatherBuddy/CustomInfo/IlvConvertTable.csv`.
- `src/Core/Run/Landing.cs` (`DoorOut`) and `src/Integrations/Travel.Indoors.cs`: landing on the
  walking path to the target and walking the rest, and pressing Dismount when hovering low over
  the landing point, after `GatherBuddy/AutoGather/AutoGather.Movement.cs` (`FindCombinedPath`,
  `ForceLandAndDismount`). Written anew for targets under a roof: the landing is where the walking
  path comes out of the building.
- `src/Run/Appraiser.cs`: the collectable appraiser window callbacks (job tab, row, submit, the
  scrip-cap prompt) and reading its item list, from `GatherBuddy/AutoGather/Collectables/`.
- `tools/VendorTable`: corrected positions of two collectable appraisers, from
  `GatherBuddy/Vulcan/Vendors/VendorNpcLocationCache.cs`.
- `src/Run/Fishing/Fisher.cs`, `Fisher.Choose.cs`, `Fisher.Rod.cs` and `Fisher.Spear.cs` (the fishing
  driver), with `src/Core/Fishing/SpotChoice.cs`, `SpotStands.cs` and `SpotNodes.cs`: choosing the
  fish and its spot, the Fisher gearset, bait in the bags, travel to a recorded place to stand,
  landing, facing the water and checking that a cast works, moving on when the fish turn wary or
  sense something amiss, and going from node to node when spearfishing, from
  `GatherBuddy/AutoGather/` (`AutoGather.cs`, `AutoGather.Movement.cs`, `AutoGather.Actions.cs`,
  `AutoGather.FishingBuffs.cs`, `AutoGather.FishingCollectable.cs`, `AutoGather.Spearfishing.cs`).
- `src/Core/Fishing/FishWindow.cs`: when a fish bites, from the time and weather rules in
  `GatherBuddy/Plugin/UptimeManager.cs`.
- `src/Core/Fishing/CatchPlan.cs` (`CatchWindow`): narrowing a fish's window to the times the fish of its
  mooch chain bite too, an idea from the mooch uptime intersection in
  `GatherBuddy/AutoGather/Lists/ActiveItemList.cs`. The code is new and searches the whole forecast.
- `src/Integrations/Fishing/SpearGame.cs`: finding and recognising spearfishing nodes, from
  `GatherBuddy/AutoGather/AutoGather.Spearfishing.cs`; and `src/Integrations/Fishing/RodMessages.cs`:
  matching the wary and amiss warnings, from `GatherBuddy/AutoGather/AutoGather.cs`.

Bundled data, built by `tools/GbrExport` from GatherBuddy Reborn's data (Apache License 2.0):

- `Resources/nodes.json`: exact world positions of gathering points (from `world_locations.json`)
  and stand spots for awkward nodes (from `node_offsets.json`).
- `Resources/fish.json`: the fish database (bait, mooch chains, predators and intuition, uptime
  windows, weather, hooksets and tugs, snagging, lures, spearfishing size and speed, fishing spots).
- `Resources/fishspots.json`: fishing spots with recorded places to stand (from `fish_records.json`).

A copy of the Apache License 2.0 is at <https://www.apache.org/licenses/LICENSE-2.0>.

## AutoHook

<https://github.com/PunishXIV/AutoHook>, BSD 3-Clause License, Copyright (c) 2024, Puni.sh.

Ported logic:

- `src/Integrations/Fishing/Angler.cs`, `RodHooks.cs`, `RodGame.cs` and `RodMessages.cs` (the rod
  engine): the fishing state the game keeps, the hooks on casting, the bite and the catch, the
  one-action-per-tick executor and its delays, changing bait, hooking by tug and bite time with the
  right hookset and resting on the rest, mooching, the buffs (Patience, Chum, Surface Slap, Identical
  Cast, the lures and others), cordials between casts, the warnings when the fish turn wary or sense
  something amiss or the bait runs out, and the collectable prompt, from `AutoHook/Modules/Rod/`,
  `AutoHook/Modules/FishingSessionManager.cs`, `AutoHook/Modules/AutoCollectables.cs`,
  `AutoHook/World/UpdateModules/` (`HooksUpdateModule.cs`, `FishingUpdateModule.cs`) and
  `AutoHook/Actions/`.
- `src/Core/Fishing/FishingIds.cs`, `CatchPlan.cs`, `CatchPlanner.cs` and `CatchDecider.cs` (the catch
  planner and per-cast decisions): bait and mooch chains, the fish caught first for Fisher's
  Intuition, which tug and bite window to hook in each cast, and when the buffs pay off, from
  `AutoHook.FishSolver/`, `AutoHook/FishSolverIntegration/` and `AutoHook/Data/IDs.cs`.
- `src/Integrations/Fishing/SpearGame.cs` with `src/Core/Fishing/SpearFish.cs`, `SpearAim.cs` and
  `SpearCatches.cs` (AutoGig): recognising spearfish by size and speed, timing the gig and counting
  the catches, from `AutoHook/Spearfishing/AutoGig.cs`, `AutoHook/Modules/Gig/` and
  `AutoHook/World/UpdateModules/SpearfishingUpdateModule.cs`.

Per-fish bite times in `Resources/fish.json` come from AutoHook's `fish_list.json`, which is built
from Teamcraft's fishing data (MIT).

```
BSD 3-Clause License

Copyright (c) 2024, Puni.sh

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## BossMod (allied society daily quests)

<https://github.com/awgil/ffxiv_bossmod>, BSD 3-Clause License, Copyright (c) 2022-2024, Andrew Gilewsky.

Ported logic:

- `src/Core/Tribes/DailyPick.cs`: the generator and the pick of the daily quests a society's quest giver
  offers, from `BossMod/Debug/DebugQuests.cs`. Changed to work on plain data and to include the
  "exclusive" pool (`Quest.DailyQuestPool` 3), which BossMod's copy leaves out.

BSD 3-Clause License. Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the copyright notice, this list of conditions and the
disclaimer are retained (full text: <https://github.com/awgil/ffxiv_bossmod/blob/master/LICENSE>).
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES ARE DISCLAIMED.

## Vulcan / Artisan (crafting)

The crafting step follows the approach of GatherBuddy Reborn's Vulcan, whose crafting core derives
from Artisan by Puni.sh: open the recipe, assign ingredients through the recipe log, start the
synthesis, and play a rotation action by action while reading the Synthesis window. The layout
facts used (recipe ingredient counts, Synthesis window values) and the per-job mapping of crafting
actions come from that work.

BSD 3-Clause License, Copyright (c) 2023, Puni.sh. Redistribution and use in source and binary
forms, with or without modification, are permitted provided that the copyright notice, this list of
conditions and the disclaimer are retained (full text: <https://github.com/PunishXIV/Artisan/blob/main/LICENCE.md>).
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES ARE DISCLAIMED.

## Raphael

`raphael-cli.exe` is built from raphael-rs by KonaeAkira (<https://github.com/KonaeAkira/raphael-rs>),
Apache License 2.0 (`Resources/LICENSE-raphael.txt` in release builds). Provenance runs it as a
separate program to work out crafting rotations.

## FFXIV Teamcraft (vendor positions, monster drops, aetherial reduction)

`Resources/vendors.json` holds world positions of gil vendors, scrip exchanges and collectable
appraisers, converted by `tools/VendorTable` from Teamcraft's NPC data. `Resources/mobs.json` holds
which monsters drop which items and where they spawn, built by `tools/MobTable` from Teamcraft's
`drop-sources.json` and `monsters.json`. `Resources/reduction.json` holds which collectables reduce into
which items, Teamcraft's `libs/data/src/lib/json/reduction.json` as it is. All from
<https://github.com/ffxiv-teamcraft/ffxiv-teamcraft>, MIT License, Copyright (c) 2017 Flavien Normand.

## ECommons

<https://github.com/NightmareXIV/ECommons>, MIT License, Copyright (c) 2023 NightmareXIV. Used for
clicking game windows (recipe log, repair, confirmations, a quest's hand-over and reward), and for typing an
allied society quest's /say phrase or emote into the chat box (`Chat`).

## Nunito

<https://github.com/googlefonts/nunito>, SIL Open Font License 1.1 (`Resources/Fonts/LICENSE-Nunito.txt`).
