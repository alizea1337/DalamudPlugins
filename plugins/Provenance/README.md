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
- **Plan** lists what you're making, then every material once with its total for the whole
  order, grouped by how it is obtained: to gather (timed nodes marked), to fish, to buy, monster
  drops, and intermediates to craft first, plus the crystals. Each row shows how many are still
  to get and how many you have against how many the order needs. Owned items count from your inventory, crystals and
  saddlebag; the armoury chest never counts. The plan lists anything the run can't get on its
  own, and the order can't start until you have those.
- **Character**: *Check character* reads every crafter's and gatherer's level, the gearset a run
  would use (highest item level) with its Craftsmanship/Control/CP or Gathering/Perception/GP,
  specialist and Manipulation, and which master books and folklore tomes you have. The job you
  are on is measured live; other jobs are worked out from their gearset's items and materia plus
  the base stats measured on a job of the same kind. The plan uses levels, master books and
  folklore tomes live: a recipe you can't craft yet or a legendary node you can't see becomes
  *get it yourself*, with the reason. Food and medicine for crafting and for gathering are picked here
  too; every stat shows what they add in brackets, for example `5,844 (+92)`.

- **Run (gathering)**: *Start* hands the order's gathering list to GatherBuddy Reborn as a list
  named "Provenance" (your own lists are paused for the run and switched back on afterwards),
  turns auto-gather on and stops once the plan needs nothing more to gather. *Pause* finishes the
  current node first. The run stops on its own when you are knocked out, when only 2 inventory
  slots are left, or when GatherBuddy Reborn or vnavmesh is unloaded; a duty pauses it until the
  duty ends. Every stop shows a notification and a chat line.
- **Travel tools**: `/prov tp <aetheryte>` teleports with Provenance's own teleport (no other
  plugin), `/prov flag` walks or flies to the map flag with vnavmesh.

Commands: `/prov`, `/prov start`, `/prov pause` (again to resume), `/prov stop`, `/prov tp <name>`,
`/prov flag`, and `/prov plan | browse | import | character | settings` to open a tab.

## Planned

| Milestone | Scope |
|---|---|
| M2 | Gathering through GatherBuddy Reborn, movement with vnavmesh, own teleport (done) |
| M3 | Crafting: a port of GatherBuddy Reborn's Vulcan with the Raphael solver |
| M4 | The run: craft while waiting for timed nodes, pause and stop rules |
| M5 | Retainer withdrawal at a summoning bell, vendor buying |
| M6 | Monster drops with a basic combo on a job you choose |
| Later | Scrip items, several orders in a queue |

## Requirements for a run

GatherBuddy Reborn, vnavmesh and AutoHook (for fishing). Planning works without them; the
settings page shows which ones are loaded.

## License

MIT. Parts are adapted from GatherBuddy Reborn (Apache 2.0); Nunito is under the SIL Open Font
License. See [NOTICE.md](NOTICE.md).
