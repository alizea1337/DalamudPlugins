# Provenance

Turns a Teamcraft list into finished items. Provenance works out every material down to the
crystals, shows where each one comes from, and then gets it all and crafts it for you: retainers,
books, vendors, monster drops, gathering and crafting in one run.

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
- **Browse** every recipe and gatherable (mining, botany, fishing) with a name search, job filter and job-level range, and add
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
- **Run (crafting)**: after gathering, every recipe is crafted, intermediates first, on the job's
  best gearset, with repair, your food and medicine, and a Raphael rotation for your live stats
  (best quality for ordered items, fastest for intermediates). Pause and Stop wait for the item
  being crafted. Recipes whose materials aren't in your bags are skipped and listed.
- **Books and scrips**: master books and folklore tomes the order needs are bought at a scrip or
  token exchange and read before anything else. With *Earn scrips for books* (Settings), a run that
  is short of scrips first makes or gathers the quickest collectables your jobs can handle, turns
  them in at a collectable appraiser, then buys the books.
- **Monster drops**: items only monsters drop are farmed with a basic combo on the combat job picked
  in Settings, in its highest item level gearset, one monster at a time. With Gysahl Greens in your
  bags the chocobo is called and put in the stance you pick (attacker by default). Spawn areas
  come from Teamcraft's crowd-sourced data, so not every drop is covered.
- **After the order**: stay, go back to where the run started, take an inn room in a city you pick,
  or teleport to your house or apartment (Settings).
- **Travel tools**: `/prov tp <aetheryte>` teleports with Provenance's own teleport (no other
  plugin), `/prov flag` walks or flies to the map flag with vnavmesh.

Commands: `/prov`, `/prov start`, `/prov pause` (again to resume), `/prov stop`, `/prov tp <name>`,
`/prov flag`, and `/prov plan | browse | import | character | settings` to open a tab.

## Planned

| Milestone | Scope |
|---|---|
| M2 | Gathering through GatherBuddy Reborn, movement with vnavmesh, own teleport (done) |
| M3 | Crafting: a port of GatherBuddy Reborn's Vulcan with the Raphael solver (done) |
| M4 | The run: craft while waiting for timed nodes, pause and stop rules (done) |
| M5 | Retainer withdrawal at a summoning bell, vendor buying (done) |
| M6 | Monster drops with a basic combo on a job you choose, chocobo companion (done) |
| Scrips | Buying books at exchanges, earning scrips with collectables (done) |

### To do

- Custom deliveries: a weekly mode that makes or gathers each client's items for crafters and
  gatherers and hands them in.
- Allied society (tribal) quests: a weekly mode for the crafter and gatherer societies.
- Several orders in a queue.
- An IPC for gathering lists in GatherBuddy Reborn (pull request upstream), instead of reflection.

## Requirements for a run

GatherBuddy Reborn, vnavmesh and AutoHook (for fishing). Planning works without them; the
settings page shows which ones are loaded.

## License

MIT. Parts are adapted from GatherBuddy Reborn (Apache 2.0); Nunito is under the SIL Open Font
License. See [NOTICE.md](NOTICE.md).
