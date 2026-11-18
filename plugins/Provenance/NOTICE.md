# Notices

Provenance is MIT licensed. It includes work adapted from the projects below under their own
licenses.

## GatherBuddy Reborn

<https://github.com/FFXIV-CombatReborn/GatherBuddyReborn>, Apache License 2.0.

Adapted files:

- `src/Data/GearStats.cs`: gear and materia stat sums with item-level caps, from
  `GatherBuddy/Crafting/GearsetStatsReader.cs` (Vulcan). Changed to cover gathering stats and to
  return plain arrays.

A copy of the Apache License 2.0 is at <https://www.apache.org/licenses/LICENSE-2.0>.

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

## ECommons

<https://github.com/NightmareXIV/ECommons>, MIT License, Copyright (c) 2023 NightmareXIV. Used for
clicking game windows (recipe log, repair, confirmations).

## Nunito

<https://github.com/googlefonts/nunito>, SIL Open Font License 1.1 (`Resources/Fonts/LICENSE-Nunito.txt`).
