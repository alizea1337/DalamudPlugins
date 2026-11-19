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
- `src/Run/Appraiser.cs`: the collectable appraiser window callbacks (job tab, row, submit, the
  scrip-cap prompt) and reading its item list, from `GatherBuddy/AutoGather/Collectables/`.
- `tools/VendorTable`: corrected positions of two collectable appraisers, from
  `GatherBuddy/Vulcan/Vendors/VendorNpcLocationCache.cs`.

Bundled data, built by `tools/GbrExport` from GatherBuddy Reborn's data (Apache License 2.0):

- `Resources/nodes.json`: exact world positions of gathering points (from `world_locations.json`)
  and stand spots for awkward nodes (from `node_offsets.json`).
- `Resources/fish.json`: the fish database (bait, mooch chains, predators and intuition, uptime
  windows, weather, hooksets and tugs, snagging, lures, spearfishing size and speed, fishing spots).
- `Resources/fishspots.json`: fishing spots with recorded places to stand (from `fish_records.json`).

A copy of the Apache License 2.0 is at <https://www.apache.org/licenses/LICENSE-2.0>.

## AutoHook

<https://github.com/PunishXIV/AutoHook>, BSD 3-Clause License, Copyright (c) 2024, Puni.sh.
Per-fish bite times in `Resources/fish.json` come from AutoHook's `fish_list.json`, which is built
from Teamcraft's fishing data (MIT).

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

## FFXIV Teamcraft (vendor positions, monster drops)

`Resources/vendors.json` holds world positions of gil vendors, scrip exchanges and collectable
appraisers, converted by `tools/VendorTable` from Teamcraft's NPC data. `Resources/mobs.json` holds
which monsters drop which items and where they spawn, built by `tools/MobTable` from Teamcraft's
`drop-sources.json` and `monsters.json`. Both from <https://github.com/ffxiv-teamcraft/ffxiv-teamcraft>,
MIT License, Copyright (c) 2017 Flavien Normand.

## ECommons

<https://github.com/NightmareXIV/ECommons>, MIT License, Copyright (c) 2023 NightmareXIV. Used for
clicking game windows (recipe log, repair, confirmations).

## Nunito

<https://github.com/googlefonts/nunito>, SIL Open Font License 1.1 (`Resources/Fonts/LICENSE-Nunito.txt`).
