# Provenance

Turns a Teamcraft list into finished items. Provenance works out every material down to the
crystals, shows where each one comes from, and (in later releases) gathers, buys and crafts
them for you.

Open it with `/provenance` (or `/prov`).

## What works today (0.1)

- **Import** a Teamcraft list in any of three forms:
  - a list link (`ffxivteamcraft.com/list/…`), downloaded from Teamcraft's public database,
    with item and recipe ids, so it works in any client language;
  - an import string (`ffxivteamcraft.com/import/…` or the bare string);
  - the text from Teamcraft's *Copy as text*. Pasting the whole list is fine: materials are
    recognised and dropped, and only the final items stay.

  By default only the amounts still left to make are imported; *Ignore Teamcraft progress*
  takes the full amounts.
- **Browse** every recipe with a name search, crafter filter and crafting-level range, and add
  recipes to the order.
- **Plan** shows each item as a tree of its materials. Every row says how the item is obtained
  (gather, timed node, fish, vendor, monster drop, or *get it yourself*) and how many you have
  against how many the order needs. Owned items count from your inventory, crystals and
  saddlebag; the armoury chest never counts. The plan lists anything the run can't get on its
  own, and the order can't start until you have those.

## Planned

| Milestone | Scope |
|---|---|
| M2 | Gathering through GatherBuddy Reborn, movement with vnavmesh, own teleport |
| M3 | Crafting: a port of GatherBuddy Reborn's Vulcan with the Raphael solver |
| M4 | The run: craft while waiting for timed nodes, pause and stop rules |
| M5 | Retainer withdrawal at a summoning bell, vendor buying |
| M6 | Monster drops with a basic combo on a job you choose |
| Later | Scrip items, several orders in a queue |

## Requirements for a run

GatherBuddy Reborn, vnavmesh and AutoHook (for fishing). Planning works without them; the
settings page shows which ones are loaded.

## License

MIT. Nunito is under the SIL Open Font License (`Resources/Fonts/LICENSE-Nunito.txt`).
