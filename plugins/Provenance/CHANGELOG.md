# Changelog

## 0.10.0 (testing)

A sidebar instead of tabs.

- The pages are listed down the left side of the window, in groups: Browse and Plan on top, then
  Run (Status), Character (Tools) and the settings. The selection slides to the page you pick and
  stretches on the way; the page comes in with a short fade, rising from below or settling from
  above depending on where it sits in the list.
- Settings are split into pages: General (items you already have, vendors, books and scrips, the
  summoning bell, the plugins a run needs), Crafting, Gathering, Fishing, Monster drops, After the
  order, and Security.
- While a run is going, a dot on Run shows it from any page: pulsing while it works, amber while
  it pauses or stops. The plan's count of things to sort out first sits on Plan.
- Each page keeps its own scroll position.
- "Reduce motion" (General, Window) turns the sliding off; fades stay, shorter. It also stops the
  drop-down lists and the import window from sliding in.
- Crafting spot (Settings, Crafting): stand where the run should craft and press "Save where I'm
  standing". Before it crafts, the run goes there: into your house (teleport, walk to the door, enter),
  your apartment (the building's entrance, "Go to your apartment"), an inn room (the innkeeper), or
  anywhere a teleport and a walk reach, then walks to the exact spot. Crafting while waiting for timed
  nodes stays by the nodes. When it can't get there, it says why and crafts where it is. "After the
  order" can go back there too. Private chambers and other people's houses can't be saved.
- Crafting spot, "Go there now": makes the trip right away, outside a run, to see it work. The house
  you stand in is now recognised by the game's own house ids, which fixes free company houses being
  turned down as "not in your teleport list".
- Aetherial reduction from timed nodes: Levinchrome Aethersand and the like come from collectables
  that only grow on ephemeral nodes (Levin Quartz, Calamus Root). The run treated them as always up and
  stood still without saying why; now it waits for their hours like any timed node, says when the next
  one opens, travels ahead and crafts in the meantime.
- The Teamcraft import no longer turns the whole screen hazy: only Provenance's window darkens behind it.
- "Tooltip size" (General, Window) picks how big the text in tooltips is: Small, Normal, Large,
  Larger or Largest. Their lines get wider with the text. Next to it, "Hover for a sample" shows one.
- The Character page puts the crafter cards two to a row when the window is too narrow for four.
- The window starts a little larger (1100 by 720) and can't go narrower than 960.

## 0.9.0

Books on the order, and items that only come from aetherial reduction.

- Character page: every master book and folklore tome row has a round add button, and each section
  has "Add all missing". They put the missing books on the order (joining whatever is already on it).
  Books a run can't get by itself are left out, and a note under the heading says which and why: not
  sold at an exchange, sold where you can't teleport yet, or sold for something a run can't earn.
- Books on the order are read, not made: the run buys each at a scrip exchange, earning the scrips
  with collectables when you're short, and reads it. One copy each, and books you've already learned
  are skipped.
- Scrips stop at 4,000, so a big batch of books is earned and bought in turns: the run buys every book
  the scrips at hand pay for (cheapest first), earns up to the cap with collectables, buys again, and
  so on until every book is in. Before, each book that the scrips could pay for on its own counted as
  bought, even when all of them together cost more.
- Aetherial reduction: items that only come from reducing collectables (the aethersands and others,
  from Teamcraft's reduction data) are now planned and fetched. The run gathers the miner's or
  botanist's collectables they come from (with the collectable rotation, for the best yield), reduces
  them between nodes, and keeps going until the amount is in. The extras are kept.
- Reduction only runs when it's safe: never while a collectable the order needs is in the bags, and,
  with "Reduce spare collectables" off, never while other reducible collectables are there (the
  game's reduction takes them all). Otherwise the run stops and says what's in the way.
- Items only reduced from fish collectables, or from collectables of the Diadem, are listed under
  "Get these yourself" with the reason; a run doesn't fish collectables for reduction yet.
- Browse lists the items that come from reduction under the job that gathers their collectables,
  tagged "aetherial reduction"; the tag is amber when a run can't do it.
- The Import tab is gone: "Import list" on the Browse tab (next to the search) opens the Teamcraft
  import in a smaller window over the page, with the same field and options. Ctrl+Enter imports,
  Escape closes, Cancel stops a download that's under way, and after an import it shows what came
  in, with "Open plan". `/prov import` opens it too.
- Plan page: a "To reduce" section with the collectables each item comes from. The character check
  blocks reduction until its quest ("No Longer a Collectable") is done and warns when the
  collectables are above your gatherer's level.

## 0.8.0

Fishing and spearfishing without AutoHook.

- This is also the first release with 0.7.0's changes, which didn't go out on their own: Provenance
  gathers by itself (GatherBuddy Reborn is no longer needed), with collectables, cordials for GP,
  materia extraction and aetherial reduction between nodes, and content filters in Browse. The full
  list is under 0.7.0 in the changelog.
- Orders with fish run now. Provenance fishes by itself, rod fishing and spearfishing; AutoHook and
  GatherBuddy Reborn aren't needed. The catching is ported from AutoHook, getting to the water from
  GatherBuddy Reborn.
- Getting there: the run picks the fishing hole by the fish's time and weather window, your Fisher
  level and the zones you can teleport to, changes into your Fisher gearset, travels to a recorded
  place to stand, lands, faces the water and checks that a cast works (stepping forward a little
  when it doesn't). Places to stand that work are remembered on this PC (never uploaded).
- Catching: only the fish the order needs are hooked, with the right hookset; everything else is let
  go. Mooch chains and the fish for Fisher's Intuition are caught along the way, timed so Intuition
  is up when the window opens. Patience, Chum, Surface Slap, Identical Cast and the lures are used
  when the catch calls for them, with cordials for GP. Fish come in as normal items: the Collect
  toggle stays off.
- When the fish turn wary, the run moves to another place to stand at least 10 yalms away; when they
  sense something amiss, it tries another spot.
- Bait: the plan lists the bait the fish need, and the run buys it from a gil vendor before fishing,
  and again if it runs out. Bait only sold for scrips or crafted has to be in your bags, unless
  "Versatile Lure when the bait can't be bought" is on (the default): those fish are then caught
  with Versatile Lure, bought in Limsa Lominsa Lower Decks.
- Spearfishing: the run goes from node to node at the spot (never straight back to the last two)
  and gigs only the wanted fish. It leaves a node when none of the wanted fish can show up there any
  more (their window has closed).
- Gathering and fishing share the run: a fish whose window is open comes first, then nodes, then fish
  that always bite, and a switch waits for the node or catch under way. While only closed windows are
  left, the run crafts in the meantime, as it does for timed nodes, and is back at the water in time
  ("Be at the fishing hole" in Settings, 3 minutes before by default, plus the time to catch the fish
  for Fisher's Intuition).
- Pause finishes the catch under way and reels in; Stop reels in. The rod is put away before a
  teleport, before crafting and when the run ends.
- Fish caught on the side are kept while fishing and thrown away once gathering is done ("Throw away
  fish caught on the side" in Settings, on by default): main bags only, never anything the order
  uses, never what you had before. Release is never used. "Also from timed nodes" is now "Also from
  timed nodes and rare fish": extra fish that only bite at some hours or in some weather follow it too.
- Run page: a Fishing card shows the fish and its fishing hole, catches and casts (nodes when
  spearfishing), when it bites, Fisher's Intuition, and the bait with how much is left. Fish in the
  list say when they bite ("fish, bites in 12 min"), progress says "caught" for fish, and the
  headline names the fish the run is waiting for.
- Plan page: the bait the fish need is listed with what to buy, fish rows say when they bite and with
  which bait, and the check of what your character can do covers fishing (Fisher gearset, a fishing
  hole you can use, the actions a fish needs), with a heads-up for big fish, long or weather-bound
  windows and Fisher's Intuition.
- Settings: a new Fishing section for buying bait and how much spare, the Versatile Lure stand-in,
  fishing buffs, cordials, how early to be at the fishing hole, and throwing away fish caught on the
  side.
- Time left counts the waits for fish windows, and how fast you fish is learned from your own runs.
- A run with fish only starts when every fish can be caught, and otherwise says which one and why:
  Fisher level, a fishing hole you can't reach, a missing action such as Mooch or Snagging, no
  Fisher gearset, or a fish (or the fish it needs for Fisher's Intuition) that doesn't bite in the
  next 7 days of the weather forecast. Within those 7 days the run waits for the window. Fish that only live in a housing district need a house or apartment there in your
  teleport list (yours, your free company's or a shared one); Provenance doesn't travel between
  wards. Ocean Fishing and the Diadem stay "get it yourself".
- Earning scrips no longer waits forever when a collectable's materials include fish.

## 0.7.0

Gathering without GatherBuddy Reborn.

- Provenance gathers by itself now; GatherBuddy Reborn is no longer needed. It picks the next node
  (timed nodes while up, then the current zone, then the cheapest teleport), changes into the
  gatherer's best gearset, uses Prospect/Triangulate and Stealth, travels and flies to the node's
  known spawn spots, lands and walks the last yalms (nodes set in cliffs open from their foot),
  opens the node and works it with a rotation solver ported from GatherBuddy
  Reborn (King's Yield, Bountiful, Gift of the Land, Tidings, Solid Reason, Wise to the World,
  Twelve's Bounty, The Giving Land; best yield on timed nodes, best yield per GP on regular ones)
  or the collectable rotation (Scour, Brazen, Meticulous, Scrutiny), always to the end of the node.
- Settings: "Throw away extra gathered items" has a new option under it, "Also from timed nodes"
  (on by default). Switch it off to keep the extras that unspoiled, legendary and ephemeral nodes
  bring in.
- Collectables: items nodes hand out as collectables (the Rarefied ones, aethersand sources such as
  Fire Moraine, custom delivery and quest items) are counted from the collectables in your bags,
  and aetherial reduction never touches anything the order names, even when the bags already hold
  it. Before a collectable on a regular node the run waits until there's
  GP for the full rotation (600), so collectability reaches the top reward; with "Drink cordials
  instead of waiting" (on by default) it drinks a Hi-Cordial, Cordial or Watered Cordial from your
  bags instead, the smallest that fills the gap. Timed nodes are never waited for.
- Estimates count collectables by node visits and include the wait for GP and the cordials drunk;
  the plan says how much of the gathering time is waiting. What a collectable visit gives, spends
  and takes is learned from your own runs.
- Between nodes: materia extraction from fully spiritbonded gear, and aetherial reduction of spare
  collectables when the bags run low (never while a reducible collectable the order needs is in the bags).
  Both in Settings.
- Node positions seen during runs are remembered on this PC (never uploaded).
- Fishing isn't in this testing build yet; orders with fish can't start until it arrives.
- Items that only come from content a run can't go to are no longer planned as gathering: the plan
  lists them under "Get these yourself" and names the content, for example "Only gathered in Cosmic
  Exploration" or "Only fished in Ocean Fishing", instead of the run stopping halfway. This covers
  the Diadem, Cosmic Exploration (its gathering, fishing and mission recipes), Ocean Fishing, and
  quest-only or removed content.
- Browse: a new row of content filters (Open world, The Diadem, Cosmic Exploration, Ocean Fishing).
  Toggle one or more to show only those; "All content" shows everything. Items a run can't get
  carry an amber tag naming where they come from.
- Browse no longer lists what can't be got any more: items only gathered or fished during quests
  or in removed content (the older versions of the Diadem), and quest-only recipes.
- Browse: when the jobs you picked have nothing (Ocean Fishing with only Miner and Botanist on, for
  example), it says which jobs do and offers "Show all jobs".
- Gathering finds more nodes: items the game links to single nodes (Mythrite Ore in Coerthas
  Western Highlands, for one) are now known.
- Groundwork for gathering and fishing without GatherBuddy Reborn and AutoHook: exact node
  positions, the fish database (bait, mooch chains, intuition, time and weather windows, hooks,
  bite times) and fishing spots with places to stand ship with the plugin (tools/GbrExport), plus
  Eorzea weather forecasting and fish uptime windows.

## 0.6.2

- Plan page: quieter remove actions. Each row in "Get these yourself" and "Your character can't do
  this yet" has a small round trash button, centred on the row, muted until hovered; hovering a row
  highlights it, and the tooltip names what gets removed. "Remove all" sits in the card's title line
  as a quiet red action, lined up with the trash column. "Check again" for the stats sits on the
  stats line.

## 0.6.1

- Specialist crafters: intermediates no longer fail with "Raphael found no rotation". raphael-cli
  crashes when offered Heart and Soul / Quick Innovation for a rotation that needs no quality, so
  those are only offered when quality is wanted.
- Raphael errors show the real reason instead of Rust's backtrace hint; an internal error is retried
  without the specialist actions, then without Manipulation, and only "no solution" keeps a run
  from starting. A craft that needs no quality and still can't be planned is finished with Basic
  Synthesis instead of stopping the run.
- Plan page: "Your character can't do this yet" and "Get these yourself" rows have a Remove button
  that takes the ordered items needing that material out of the order (the tooltip names them),
  plus "Remove all" for everything the character can't do.

## 0.6.0

Scrips, monster drops, travel and security.

- Timed nodes: while waiting for one, the run goes to where it spawns (teleport, then mount up and
  ride or fly there) and crafts or waits on the spot, so gathering starts as soon as it opens.
  "Go to the next timed node while waiting" in Settings turns this off. During a run, GatherBuddy
  Reborn's "teleport to next node" setting is on (your own value comes back afterwards), so it
  doesn't go home while waiting.
- Trips to vendors and nodes mount up for longer distances and fly where the zone allows it.
- Time left while waiting for a timed node includes the wait.
- A node is gathered to the end even after the order's amount is in (GatherBuddy Reborn's "abandon
  nodes" is off during a run). Afterwards the extras are thrown away, so 93 of 91 leaves 91;
  normal quality only, never what you had before. "Throw away extra gathered items" in Settings.
- Readiness check on the Plan page: job levels, the Craftsmanship/Control a recipe asks for,
  specialist-only recipes, whether Raphael finds a rotation that finishes each craft, and the
  Perception gathered items ask for, all against your best gearsets plus the picked food and
  medicine. Anything the character can't do keeps the run from starting.
- Run page counts gathered items ("6 of 10 gathered") instead of finished kinds.
- Master books and folklore tomes are bought at scrip and token exchanges when you have the
  currency: the run travels to a scrip exchange it can reach, picks the page and subpage, buys the
  book and reads it before anything else. The plan says what it costs and how much you have; a
  book you can't afford still has to be got first. Exchange NPC positions are now in vendors.json.
- Earning scrips ("Earn scrips for books" in Settings, on by default): when the books cost more
  scrips than you have, the run first makes or gathers the quickest collectables your jobs can
  handle (fewest items for the scrips missing, counted at the middle reward), crafts them for the
  top collectability, turns them in at a collectable appraiser (job tab, row, submit; stops at the
  scrip cap), buys the books and then runs the order. Short after a round: up to three rounds.
- Monster drops (M6): items only monsters drop are farmed after the vendors, before gathering, with
  a basic combo on the combat job picked in Settings (highest item level gearset): one monster at
  a time, never one someone else is fighting, attackers first, the job's heal or Rampart/Second
  Wind below a health threshold. Spawn areas come from Teamcraft's drop data (Resources/mobs.json,
  tools/MobTable); drops without a known spawn are "get it yourself".
- Chocobo companion: "Call the chocobo" (on by default) uses Gysahl Greens from the bags and sets
  the stance picked in Settings (attacker by default).
- Travel mounts up for longer trips in open zones (and flies where the zone allows it).
- Books: the exchange is picked by what you can pay now, then a scrip the run can earn, then the
  cheapest; a tome no longer lands on a token exchange you have no tokens for. Scrip names are
  written properly ("Purple Crafters' Scrips").
- Combat job picker is a dropdown with a search box; job names and role colours.
- Earning scrips weighs the materials: every candidate collectable is planned, those needing
  monster drops, books or items to get yourself are left out, and crafts, gathering and timed
  nodes count as effort; the least work wins. The card says how many materials are on timed nodes.
- Scrips in one round: collectables are counted at the appraiser's lowest reward, extra gathered
  materials are kept while earning scrips, and leftovers become more collectables before the
  turn-in. Collectables in the bags are found by their collectable flag (they don't count as normal
  items); ones below the appraiser's minimum are named with their collectability, and a turn-in
  that takes nothing stops the run with the reason instead of starting another round.
- Crafting: the rotation is planned only once the live stats have settled after a gearset change
  or food (a rotation planned on the previous job's numbers fell short). If one still falls short,
  the next craft gets a fresh rotation.
- Compact view: the progress text no longer runs into the bar.
- Aethernet inside cities: when an attuned shard is much closer to where the run is going than
  walking, it walks to the nearest crystal, takes the aethernet and walks the rest. Aetheryte and
  shard positions now come from the zone map's markers (the game data has no other position for
  them), which also picks the right aetheryte to teleport to in a zone with several.
- After the order (Settings): once an order is finished the run can stay, go back to where it was
  started (into the same inn if it started in an inn room), take a room at an inn you pick (the
  innkeepers of the eight cities with inn rooms), or teleport to a house or apartment from your
  teleport list. A stopped run stays where it is.
- Security tab: advanced unstuck for Provenance's own walking (path again, jump and back off,
  side-step, teleport, give up) with an adjustable stuck time; refuse trade requests (during runs or
  always); limits for a run: gil spent at vendors, run time, knock-outs to get back up from, free
  inventory slots to stop at.

## 0.5.0

Retainers and vendors.

- Retainers: Provenance remembers what each retainer holds whenever you open it at a summoning
  bell. With "Retainers" ticked, those items count as owned and a run takes them out first: it
  walks to a nearby bell (or teleports to the aetheryte set in Settings), opens each retainer that
  holds something the order needs, and moves the stacks into your bags.
- Vendors: materials sold for gil under your price cap are bought. The run picks a vendor in a
  zone you can teleport to (never a housing district), closest to an aetheryte, reuses a vendor
  already on the route, walks there, picks the shop from the menu and buys up to 99 at a time.
- Order of a run: retainers, books, vendors, gathering (crafting while timed nodes are closed),
  crafting.
- Getting stuck while walking: retried, then restarted from the zone's aetheryte, then reported.
- Plan shows where vendor items are sold; the action bar says what an order needs instead of
  "0 crafts".

## 0.4.0

The whole run.

- One run takes the order start to finish: books first, then gathering, and once only timed
  nodes are left and none is up, it crafts what already has its materials, handing back to
  gathering a set number of minutes before the next window (2 by default). Afterwards it crafts
  everything left.
- Materials used up by crafting in between still count as gathered, and GatherBuddy Reborn's
  targets are lowered to match, so nothing is gathered twice.
- Knock-outs: the run accepts a raise or returns to the home point and carries on (up to three
  times). Duties pause gathering or crafting until they end.
- Plan shows when each timed node is up or opens next; settings for crafting while waiting and
  the hand-back time.

## 0.3.0

Crafting.

- After gathering, the run crafts the whole order, intermediates first. For each recipe it
  switches to the job's best gearset, repairs below a set percentage (self-repair with Dark
  Matter), uses the food and medicine picked on the Character page, and asks Raphael for a
  rotation worked out for your live stats. Ordered items aim for high quality; intermediates get
  the fastest rotation.
- Ingredients are filled in NQ first, then HQ. The rotation is played action by action; if it
  ends early the item is finished with Basic Synthesis.
- Pause and Stop wait for the item being crafted, so no materials are lost ("Stop now" skips
  the wait). Recipes whose materials aren't in your bags are skipped and listed at the end.
- Run page shows the recipe, craft number, the current action of the rotation and the queue.
- Crafting settings: aim for high quality, specialist actions, repair threshold.
- HQ per item: a toggle in Browse and on the plan; only HQ items get a quality rotation.
- Compact strip (`/prov compact`): run state, progress and buttons in a small window.
- Time estimates: each ordered item shows when it's ready ("ready in about 12 min", counted in
  the order the run works), the plan shows the whole order split into gathering and crafting,
  and the Run page and compact strip show the time left. Crafting times come from Raphael's
  rotation length at your checked stats; every craft and gathering session adjusts the timings.
- Master books and folklore tomes: Browse names the book a recipe needs ("needs Master
  Carpenter VIII") and where it's sold. Ordering such a recipe, or a material only found on
  legendary nodes, adds a "Learn first" step to the plan: once the book is in your bags the run
  reads it before anything else; until then it's listed as something to get first.

## 0.2.0

Gathering runs.

- *Start* on the plan runs the gathering step: Provenance gives GatherBuddy Reborn a list named
  "Provenance" with exactly what the order still needs (your own lists are paused and restored
  afterwards), switches auto-gather on, and stops when everything is in your bags.
- Pause finishes the current node first; Resume carries on; Stop ends the run.
- Stops on its own when knocked out, at 2 free inventory slots, or when GatherBuddy Reborn or
  vnavmesh is unloaded. A duty pauses gathering until it ends. Each stop notifies you.
- Run page shows what the run is doing, per-item progress and a log of what happened.
- Provenance's own teleport (`/prov tp <aetheryte>`) and vnavmesh movement to the map flag
  (`/prov flag`).

## 0.1.0

First testing build: plan an order, but don't run it yet.

- Import Teamcraft lists from a list link, an import string, or *Copy as text*. Pasted
  materials are dropped automatically; Teamcraft progress is respected unless you turn it off.
- Recipe browser with name search, crafter filter and a crafting-level range.
- Plan page: every material once, totalled over the whole order and grouped by source (gather,
  fish, buy, monster drops, craft first, crystals), with how many are left and have/need.
  Anything the run can't get on its own is listed at the top and blocks the start.
- Owned items from inventory, crystals and saddlebag are used first; the armoury chest never is.
- Vendor price cap and a choice to buy vendor materials instead of gathering them.
- Character page with *Check character*: levels, the best gearset per crafter and gatherer with
  its stats, specialist and Manipulation, master books and folklore tomes. Recipes above your
  level or behind an unlearned master book, and legendary nodes behind an unread tome, show up
  in the plan as items to get yourself, with the reason.
- Food and medicine for crafting and for gathering, picked on the Character page (with HQ and
  how many you own). Each job's stats show what they add in brackets.
