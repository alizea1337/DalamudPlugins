# Provenance

Turns a Teamcraft list into finished items. Provenance works out every material down to the
crystals, shows where each one comes from, and then gets it all and crafts it for you: retainers,
books, vendors, monster drops, gathering, fishing and crafting in one run.

Open it with `/provenance` (or `/prov`). The pages are in a sidebar: Browse and Plan on top, then
Run under Status, Cosmic Exploration under Expeditions, Character under Tools, and the settings, one page each for General, Interface, Crafting,
Gathering, Fishing, Monster drops, After the order and Security. A *crafting spot* (Settings, Crafting:
stand there and save it) is where the run goes before it crafts: a corner of your house or apartment
(through the door), an inn room, or anywhere a teleport and a walk reach. The Interface page sets how
the window looks: *Interface scale* (75% to 150%) sizes the spacing, buttons, icons and the window,
*Text size* (75% to 150%) sizes all its text, *Tooltip size* sets how big the hover texts are,
*Colours* picks the accent, background, cards and text (colour wheel or hex code), and *Reduce motion*
keeps it from sliding things around.

## What it does


- **Import** a Teamcraft list (*Import list* on the Browse page) in any of three forms:
  - a list link (`ffxivteamcraft.com/list/…`), downloaded from Teamcraft's public database,
    with item and recipe ids, so it works in any client language;
  - an import string (`ffxivteamcraft.com/import/…` or the bare string);
  - the text from Teamcraft's *Copy as text*. Pasting the whole list is fine: materials are
    recognised and dropped, and only the final items stay.

  By default only the amounts still left to make are imported; *Ignore Teamcraft progress*
  takes the full amounts.
- **Browse** every recipe and gatherable (mining, botany, fishing) with a name search, job filter and job-level range, and add
  recipes to the order. The search takes filters too, such as `job:fsh is:reduction level:50-70` or `is:ready cat:metal`:
  see [Searching in Browse](docs/search.md), or the *?* next to the search.
- **Favourite orders** (Browse, the *Favourites* chip, or the star on an item to keep just that item): save the order as it is to make it again
  later. Each favourite loads with one click (replacing the order, or added to it), renames in
  place, takes the current order over it, and copies out as Teamcraft text (one "10x Item" per
  line, which Teamcraft and Provenance's own import read back).
- **Plan** lists what you're making, then every material once with its total for the whole
  order, grouped by how it is obtained: to gather (timed nodes marked), to fish, to buy (with the bait the fish need), monster
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

- **Run (gathering)**: Provenance gathers by itself. It picks the next node (timed nodes while
  they are up, then the current zone, then the cheapest teleport), changes into the gatherer's
  best gearset, travels and flies there, finds the node among its known spawn spots, and works the
  gathering window with a rotation solver (ported from GatherBuddy Reborn) or the collectable
  rotation, always to the end of the node. Items that only come from aetherial reduction are gathered
  or fished as collectables (Collector's Glove on for those fish, rod or spearfishing, with their bait;
  *Fish for aetherial reduction* in Settings, Fishing turns the fishing off) and reduced
  between nodes and catches. Between nodes it extracts materia and reduces spare
  collectables. Node positions it sees are remembered on your PC (never uploaded). *Pause*
  finishes the current node (or catch) first. The run stops on its own when you are knocked out, when only a
  few inventory slots are left, or when vnavmesh is unloaded; a duty pauses it until the duty ends.
  Every stop shows a notification and a chat line.
- **Run (fishing)**: Provenance fishes by itself too, rod fishing and spearfishing, with logic ported
  from AutoHook and GatherBuddy Reborn (neither plugin is needed). It picks the fishing hole by the
  fish's time and weather window, your Fisher level and where you can teleport, changes into your
  Fisher gearset, goes to a recorded place to stand and faces the water. Only the fish the order
  needs are hooked, with the right hookset, plus whatever leads to them: mooch chains, and the fish
  for Fisher's Intuition timed to the window. Patience, Chum, Surface Slap, Identical Cast and the
  lures are used when the catch calls for them, with cordials for GP. When the fish turn wary it moves
  to another stand; when they sense something amiss it tries another spot. Spearfishing goes from
  node to node and gigs only the wanted fish. Fish come in as normal items (Collect stays off), and
  fish caught on the side are thrown away once gathering is done (a setting; Release is never used).
  Gathering and fishing share the run: a fish whose window is open comes first, then nodes, then fish
  that always bite, and a switch waits for the node or catch under way. Places to stand that work are
  remembered on your PC (never uploaded).
- **Run (crafting)**: after gathering, every recipe is crafted, intermediates first, on the job's
  best gearset, with repair, your food and medicine, and a Raphael rotation for your live stats
  (best quality for HQ items, with HQ ingredients first; the crafted materials under an HQ item are
  made HQ too, everything else gets the fastest rotation). Pause and Stop wait for the item
  being crafted. Recipes whose materials aren't in your bags are skipped and listed.
- **Scrips**: master books and folklore tomes the order needs are bought at a scrip or token
  exchange and read before anything else. Materials only sold for scrips (white, purple or orange,
  such as Flint Corn for orange crafters' scrips) are bought there too. With *Earn scrips when short*
  (Settings), a run that is short of scrips first makes or gathers the quickest collectables your
  jobs can handle, turns them in at a collectable appraiser, then buys what the order needs; past
  the 4,000 cap it earns and buys in rounds.
- **Monster drops**: items only monsters drop are farmed with a basic combo on the combat job picked
  in Settings, in its highest item level gearset, one monster at a time. With Gysahl Greens in your
  bags the chocobo is called and put in the stance you pick (attacker by default). Spawn areas
  come from Teamcraft's crowd-sourced data, so not every drop is covered.
- **After the order**: stay, go back to where the run started, take an inn room in a city you pick,
  or teleport to your house or apartment (Settings).
- **Security**: advanced unstuck, refusing trade requests, and limits for a run (gil, time,
  knock-outs, free inventory slots).
- **Cosmic Exploration** (*Expeditions*): a loop of stellar missions with your crafters, on the planet you
  pick. It takes the best-paying mission each ticked crafter reliably golds with its gearset (silver when
  none does), crafts it with a Raphael rotation, reports it, and goes on until the goal: a number of
  Cosmocredits, a shopping list from the Cosmocredit trader (bought when the credits are there), or until
  stopped, with an optional time limit. The Plan page shows which mission each crafter takes, Cosmocredits
  an hour and how long the goal takes, and suggests food and medicine when they'd reach better missions.
  Gathering and fishing missions come later.
- **Travel tools**: `/prov tp <aetheryte>` teleports with Provenance's own teleport (no other
  plugin), `/prov flag` walks or flies to the map flag with vnavmesh.

Something going wrong? *Copy bug report* (Settings, General) or `/prov bugreport` copies what Provenance knows
right now (settings, order, plan, what the run is doing and its log, nearby nodes, your zone and position, but
no character name) to paste into a report. *Open on GitHub* next to it copies it too and opens a new
[bug report](https://github.com/alizea1337/DalamudPlugins/issues/new/choose) with the plugin and version filled in.

Commands: `/prov`, `/prov start`, `/prov pause` (again to resume), `/prov stop`, `/prov bugreport`, `/prov tp <name>`,
`/prov flag`, `/prov plan | browse | character | settings` to open a page (settings opens General), and
`/prov import` to open the Teamcraft import on the Browse page.

## Planned

| Milestone | Scope |
|---|---|
| M2 | Gathering, movement with vnavmesh, own teleport (done; own gathering engine since 0.7) |
| M3 | Crafting: a port of GatherBuddy Reborn's Vulcan with the Raphael solver (done) |
| M4 | The run: craft while waiting for timed nodes, pause and stop rules (done) |
| M5 | Retainer withdrawal at a summoning bell, vendor buying (done) |
| M6 | Monster drops with a basic combo on a job you choose, chocobo companion (done) |
| Scrips | Buying books and materials at scrip exchanges, earning scrips with collectables (done) |
| Fishing | Rod fishing and spearfishing of its own, ported from AutoHook and GatherBuddy Reborn (done; since 0.8) |
| Cosmic Exploration | Crafting missions (done; since 0.18), then gathering missions with critical missions and red alerts, then fishing missions, then Cosmic Fortunes, Master and token shopping, and orders buying cosmic items; later the cosmic tools (relic), Artifact Search, class levels and mech ops |

### To do

Tracked in [Issues](https://github.com/alizea1337/DalamudPlugins/issues), where bugs and suggestions go too.

- Custom deliveries: a weekly mode that makes or gathers each client's items for crafters and
  gatherers and hands them in ([#6](https://github.com/alizea1337/DalamudPlugins/issues/6)).
- Allied society (tribal) quests: a weekly mode for the crafter and gatherer societies ([#7](https://github.com/alizea1337/DalamudPlugins/issues/7)).
- Ocean Fishing: the whole voyage (queueing, spectral currents, presets per zone) ([#8](https://github.com/alizea1337/DalamudPlugins/issues/8)).
- The Diadem: gathering there, including clouded and umbral nodes ([#9](https://github.com/alizea1337/DalamudPlugins/issues/9)).
- Several orders in a queue ([#10](https://github.com/alizea1337/DalamudPlugins/issues/10)).
- Own navigation instead of vnavmesh ([#11](https://github.com/alizea1337/DalamudPlugins/issues/11)).

## Requirements for a run

vnavmesh. Planning works without it; the General settings page shows whether it is loaded. GatherBuddy
Reborn and AutoHook are no longer needed.

Orders with fish also need:

- a gearset saved for Fisher, and the Fisher level the fishing holes ask for (the plan says what's
  missing);
- the actions a fish needs: Mooch for mooch chains, Snagging, Ambitious or Modest Lure, and Gig
  for spearfishing;
- bait: the run buys bait sold for gil, under the price cap in Settings, and bait sold for scrips at
  a scrip exchange. Crafted bait has to be in your bags, unless *Versatile Lure when the bait can't
  be bought* is on (Settings), which buys Versatile Lure in Limsa Lominsa Lower Decks instead.

Fish that only live in a housing district need a house or apartment there in your teleport list
(yours, your free company's or a shared one); Provenance doesn't travel between wards. Ocean
Fishing and the Diadem aren't covered: their fish are listed under "Get these yourself".

## License

MIT. Parts are adapted from GatherBuddy Reborn (Apache 2.0) and AutoHook (BSD 3-Clause); Nunito is
under the SIL Open Font License. See [NOTICE.md](NOTICE.md).
