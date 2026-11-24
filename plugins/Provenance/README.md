# Provenance

Turns a Teamcraft list into finished items. Provenance works out every material down to the
crystals, shows where each one comes from, and then gets it all and crafts it for you: retainers,
books, vendors, monster drops, gathering, fishing and crafting in one run.

Open it with `/provenance` (or `/prov`). The pages are in a sidebar: Browse and Plan on top, then
Run under Status, Cosmic Exploration, Tribes and Custom Deliveries under Expeditions, Character under Tools, and the settings, one page each for General, Interface, Crafting,
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
  Two-class missions on Sinus Ardorum (a miner, botanist or fisher gathers, the crafter makes) and Cosmic
  Fortunes (spending the planet's credits) are extras; Drone Modules can go on the shopping list. The
  gatherers' and fisher's own missions come later.
- **Custom Deliveries** (*Expeditions*): the week's custom deliveries for crafted and gathered requests,
  planned, made and handed in. The page shows the deliveries left of the 12 a week and when they reset, the
  bonus requests, and each client's level, this week's requests and what the next level brings; a locked
  client names the quest that unlocks it (the run doesn't do quests). Start plans the week for the goal:
  *Rank up* (the clients below the top level first, the lowest first, up to their next level each round, at
  the lowest collectability, since every tier gives the same satisfaction; deliveries left over go to the
  clients at the top level, bonus requests first), *Scrips* or *Gil* (bonus requests first; crafted requests
  at the best tier your gear reliably reaches, gathered ones at the top tier). Until you pick one, the goal is
  Rank up while any client you've unlocked is below the top level, otherwise Scrips. The run takes what's in
  your bags first, buys the other components from the vendor next to the client (crystals are never bought:
  they come from your bags), crafts with a Raphael rotation or gathers the collectables, and hands everything
  in on one tour, the Firmament included (through Foundation's aetheryte and the Firmament's own aethernet; on
  your home world only). At a rank-up the rewards are accepted and the cutscene skipped (*Pause for rank-up
  cutscenes* pauses for it instead). A rank-up changes the client's requests, so after the tour the run plans
  again while deliveries are left: up to 3 rounds, and only when the round before delivered something. A
  delivery whose scrips would go over the 4,000 cap is left out; under Rank up, *Deliver anyway while ranking
  up* still delivers to the clients below the top level (the scrips over the cap are lost). *Include in the
  run* is in each client's opened row (not for a locked client). The week's plan (what goes to whom, made by
  which class, at which collectability, and who gets nothing this time and why) shows on the page, and on the
  Plan page when no order is on it and there's something to deliver; Pause, Resume and Stop are in the action
  bar and the compact strip (`/prov compact`). Fish and spearfishing requests come later.
- **Tribes** (*Expeditions*): the daily allied society quests.
  - The page: all 20 allied societies; whether each is unlocked and, if not, what's missing (the run doesn't
    do unlock quests); rank and reputation, with the dailies and days to the next rank and to the society's
    top rank (Bloodsworn for most, Sworn for the Ixal); a notice when a rank quest is ready; today's quests
    from each giver, worked out in advance and checked against the game when you talk to the giver, each
    with what it asks and as which class, and marked "the run does it", "do it yourself" (with the reason) or
    "needs …" (crystals, a class level, a saved gearset, a combat job, or an item to wear, such as the Ixal's
    Ehcatl Wristgloves); the combat job the fights are done on; the allowances left and the countdown to the
    daily reset (15:00 UTC, shown in your local time); what each rank opens in the society's shop and what
    you already own; and the achievements and titles.
  - The run, for all 20 societies. The crafter and gatherer societies (the Moogles, Dwarves, Loporrits, Yok
    Huy, Namazu, Qitari, Omicrons and Mamool Ja, and the Ixal's crafting dailies) have been done since 0.22.
    The dailies of the 11 battle societies (the Amalj'aa, Sylphs, Kobolds, Sahagin, Vanu Vanu, Vath, Kojin,
    Ananta, Pixies, Arkasodara and Pelupelu) and the Ixal's dailies with a fight or a password are new. The
    run takes them too, apart from the ones listed below as "do it yourself", and they're still being tested:
    please send a bug report when one goes wrong. If you used Tribes before they were added: the battle
    societies are ticked from the start, so with a *Monster drops* job set the day's 12 allowances now also go
    to them (under *Rank up* the lowest ranks first), and battle dailies already in your journal are picked up
    and fought. Untick a society on the Tribes page to leave it out.
    The run takes today's quests within the 12 daily
    allowances: up to 3 from each giver (the Ixal and the four ARR battle societies have one giver for each
    rank, so more than 3 a day there), and on the day you rank up the 3 more the giver offers when the new
    rank has dailies of its own (reaching Sworn gives none for most societies, reaching Bloodsworn none for
    any). Dailies already in your journal come first, and one accepted before the reset first of all, since
    the game gives no new dailies until it's handed in. The run does the errands too: talking, handing items
    over, emotes, `/say`, key items, quest mounts and so on. A `/say` is the quest's own phrase, said once;
    if the game doesn't take it, the run hands that step to you. An emote gets one more try after 15 seconds
    when nothing changed.
    Crafting quests are crafted with a Raphael rotation: the fastest one, or one for full quality when the
    quest only counts its item made high quality (the page says "high quality only"; a normal-quality one
    doesn't count, so after two tries without one the run asks you). Crystals come from
    your bags, then retainers, then gathering; otherwise the quest is left out, with the reason. The crystal
    check assumes up to 4 crafts a quest, since the game only says how many once it's accepted. Gathering
    quests are done as Miner or Botanist at the society's own nodes. Today's plan shows on the page, and on
    the Plan page when no order is on it and there are dailies to do; Pause, Resume and Stop are in the
    action bar and the compact strip.
  - Fights (new, and still being tested like all battle dailies):
    - The job: a battle society's daily is accepted and fought on the job picked under *Monster drops*
      (Settings) when it qualifies: Provenance has a combo for it, it's at the quest's level, and it has a
      saved gearset (the job you're on needs none). When it doesn't qualify, the daily goes to the combat job
      you're on, if that one qualifies. A job that was on earlier in the same run also counts. Otherwise the
      quest shows "needs …" and isn't accepted. Fights use the basic combo and the chocobo as set under
      *Monster drops*.
    - What it fights: on a combat job, the quest's own enemies and whatever attacks you or your chocobo.
      Other monsters, FATE monsters and enemies someone else is fighting are left alone. As a crafter or
      gatherer the run doesn't fight; it carries on with its step. Riding its own mount on the way, it rides
      on past things that attack unless they're the quest's, and fights when it's still attacked on arrival.
      On a quest mount with an action, that action is used on the quest's enemies, and the run rides on past
      anything else.
    - Key items: where a quest has an item for a weakened enemy, the run stops attacking and uses it. An item
      that can be thrown from 10 yalms or more is thrown first, and where the quest wants nothing else of the
      enemy only the item is used.
    - Health: before starting the next fight the run waits for health to come back to 70%.
    - When a fight goes badly: an enemy that isn't attacking and can't be reached or hurt is left after a
      time (45 seconds without reaching it, 30 seconds without hurting it, 3 minutes in all). Something that
      attacks is always fought. After two enemies left like that, or 15 defeats that didn't move the quest on,
      the run pauses and asks you, once the fighting is over.
    - Knock-outs count against the *Knock-outs* limit (Settings, Security). The run returns, leaves the quest
      it happened in to you for today, waits for Weakness to wear off before the next fight, and plans the day
      again.
    - Ixal dailies with a fight are accepted as a crafter like the Ixal's others. The run changes to the
      *Monster drops* job for the fight and back to the crafter for talks and hand-overs, always out of
      combat. That job needs level 50 and a saved gearset; without it the quest shows "needs …" and isn't
      accepted.
  - Goals: *Rank up* first takes the dailies that fill each society's bar, the lowest rank first;
    allowances still left after that go to more dailies, since they still pay currency, gil, crystals and
    EXP. A society whose bar is full waits for its rank quest: it gets
    a notice, and its allowances go elsewhere. *Currency* (for the society shops) takes as many dailies as
    the day allows; the givers whose dailies pay none of the society's currency come last: the Ixal's below
    Respected (no Oaknots) and the ARR battle societies' Neutral and Recognized ones (a Venture, no tokens).
    *Levelling* takes each daily on your lowest class that qualifies; a battle quest only on a combat job at
    least 5 levels above the quest's level, the lowest such job, since the basic combo has little healing and
    ARR society quests aren't level-synced (when no job is that far above, the job it's fought on under the
    other goals). Between classes of the same level the *Monster drops* job comes first, then the better-geared
    one. Currency and Levelling keep doing the dailies of a society whose bar is full. Until you pick one, the
    goal is Rank up while a ticked society is below its top rank, then Currency; a society counts for that
    only when the run can take one of its dailies today (a battle society doesn't while no combat job
    qualifies for its dailies). Under Rank up and Currency a crafter's or gatherer's quest is accepted on your
    current class when it qualifies, otherwise on your best-geared one, and a battle quest on the *Monster
    drops* job or, when that one doesn't qualify, on the combat job you're on (see Fights).
  - Options: *Include in the run* in each society's opened row; *Pause for cutscenes* (otherwise they're
    skipped); *Leave out quests that /say in public* (off to start), which skips those quests altogether; and
    *Note what the run would do while I do society quests myself (for bug reports)* (on to start, and only
    active once you've opened the Tribes page). While you do a society quest by hand and no Provenance run is
    going, it notes in the debug log what the run would do, with no player names. Bug reports carry it, which
    helps check the run before it acts.
  - Never done by the run: these are marked "do it yourself", with the reason, and not accepted.
    - Shooting and bow minigames (the Namazu's bow; the Kojin, Ananta and Pixies snipes): the only known way
      to automate them fakes the minigame's result to the game, and Provenance doesn't fake results.
    - The nine ARR FATE dailies: they wait on a named FATE spawning, sometimes only after other FATEs, which
      can take a long time.
    - The Ixal's "Deliverance": it has a quota system of its own.
    - Fisher for the gatherer dailies: the game data has no fishing spot or bait for those quest fish, so
      gatherer dailies are taken as Miner or Botanist.
  - Not yet done by the run, and "do it yourself" for now, with the reason:
    - The Dwarves' cannon dailies, "Demolition Demo" and "Substitute Tester": the cannon has to be aimed.
    - Dailies whose emote goes to people or things the quest doesn't name (it only points at an area or an
      object), since the run only emotes at someone it's sure of: the Moogles' "The Moogle Motivator", the
      Yok Huy's "Romanced by the Stone", the Kobolds' "Hells Have No Fury", the Vanu Vanu's "Dance
      Commander", "Electric Gunduloo" and "Wildwort and Sundrop", the Kojin's "You Have the Rite", the
      Ananta's "When Feathers Fly" and the Arkasodara's "Blood from a Stonemason".
    - The Sylphs' three stealth dailies, "Now You See Me", "Waterbringer" and "Mist Opportunities": they're
      done while a spell keeps you unseen, before it runs out.
    - The nine Ixal dailies with a step for a Miner, Botanist or Fisher partway through: the run doesn't
      change to a gatherer inside a quest yet.
  - Planned for later releases:
    - Rank quests, started with *Do the rank quest*, escort and follow steps included, and with them the
      intersocietal quests for the "Allied" rank. Three rank quests hold a
      solo duty (the Kojin's "A Test of Courage", the Ananta's "Schism between Sisters", the Pixies' "As the
      Heart Bids"): the run warns you beforehand and pauses at the duty for you to do it, then Resume
      carries on. Two contain a minigame and stay "do it yourself": the Kojin's "Under Wraps" and the
      Namazu's "Out of the Frypan". Until then rank quests are yours to do.
    - A shopping list for the society shops.
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
| Cosmic Exploration | Crafting missions (done; since 0.18), two-class missions and Cosmic Fortunes (done; since 0.19), then gathering missions with critical missions and red alerts, then fishing missions, then Master missions and token shopping, and orders buying cosmic items; later the cosmic tools (relic), Artifact Search, class levels and mech ops |
| Custom Deliveries | The page with the week and every client (done; since 0.20), the run for crafted and gathered requests (done; since 0.21), then fish and spearfishing requests, a shopping list for scrips at the cap and a trip after the run; later making items ahead and a Levelling goal |
| Tribes | The page and the run for the crafter and gatherer societies' dailies (done; since 0.22), the battle societies' dailies and the Ixal's dailies with a fight or a password (new; still being tested), then rank quests and the intersocietal quests by button, then a shopping list for the society shops |

### To do

Tracked in [Issues](https://github.com/alizea1337/DalamudPlugins/issues), where bugs and suggestions go too.

- Ocean Fishing: the whole voyage (queueing, spectral currents, presets per zone) ([#8](https://github.com/alizea1337/DalamudPlugins/issues/8)).
- The Diadem: gathering there, including clouded and umbral nodes ([#9](https://github.com/alizea1337/DalamudPlugins/issues/9)).
- Several orders in a queue ([#10](https://github.com/alizea1337/DalamudPlugins/issues/10)).
- Own navigation instead of vnavmesh ([#11](https://github.com/alizea1337/DalamudPlugins/issues/11)).

## Requirements for a run

vnavmesh. Planning works without it; the General settings page shows whether it is loaded. GatherBuddy
Reborn and AutoHook are no longer needed.

Other automation plugins can get in a run's way. Provenance pauses YesAlready and TextAdvance by itself
while a run is going (*Pause YesAlready and TextAdvance while a run is going*, Settings, Security) and
lets them carry on when the run pauses, finishes or stops. Questionable can't be paused from outside, so
a run won't start while it's running its quests, and a run under way stops when it starts. Anything else
that acts by itself is yours to turn off for the run: the
[FAQ](docs/faq.md#which-plugins-get-in-provenances-way) lists what to look for.

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

## Questions

Why no market board? Why does it craft at a crafting spot and not out in the field? Why are some
allied society quests "do it yourself"? These and other common questions are answered in the
[FAQ](docs/faq.md).

## License

MIT. Parts are adapted from GatherBuddy Reborn (Apache 2.0) and AutoHook (BSD 3-Clause); Nunito is
under the SIL Open Font License. See [NOTICE.md](NOTICE.md).
