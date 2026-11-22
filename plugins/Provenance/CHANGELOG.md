# Changelog

## 0.19.9

- Fixed (Cosmic Fortunes): the game could crash at the fortune wheels. Picking the second wheel sent the
  game a click it wasn't made for. The wheel is now picked the way the game itself marks it, with no
  click; the prizes are read only from what the window really holds (a draw can come with one wheel);
  and Spin is pressed only once it's there and enabled. When the game offers another draw on the spot,
  it says yes while there's enough to pay with, and no otherwise.
- Cosmic Exploration: no more long wait before reporting a mission. After a mission's last craft the
  game doesn't open its crafting log again, and the crafter waited eight seconds for it each time; now a
  moment.
- The debug log goes per run: every order run and every Cosmic loop writes a file of its own (the plugin's
  logs folder, the last 40 runs), and the bug report carries the whole run going and the one before it
  (up to 5,000 lines each), so an early problem in a long run, or a run that ended in a crash, comes
  along. The order run's log goes in too, and Cosmic Fortunes notes every step (menu, wheels and prizes,
  clicks, draws).

## 0.19.8

- Fixed (Cosmic Exploration): the second craft of a mission with Stellar Steady Hand started without a
  rotation and was finished with Basic Synthesis alone, so it broke. Its rotation was dropped to be worked
  out again for the uses left, but the craft didn't wait for the new one; now it does.
- Fixed (Cosmic Exploration): crafting another item for gold didn't see the Cosmic Containers left when
  they sat in the bags rather than the Cosmopouch, so a mission was reported after one craft. Supplies
  and gathered materials now count wherever they are.
- A detailed debug log: every step of the Cosmic loop and the crafter, what the board offered and how it
  was judged, each rotation and action, and each decision with its numbers, kept in memory and in a file
  per day in the plugin's logs folder (a week's worth). The bug report carries its last 150 lines.

## 0.19.7

- New option (After the order): *Also after Cosmic Exploration*. With it on, the Cosmic Exploration loop
  goes to the place picked there (where it started, an inn room, home, the crafting spot) when it
  finishes on its own: its goal, its time limit, or the shopping list bought, after any fortunes it still
  draws. Stopping it yourself or a problem leaves you where you are; Pause and Stop work on the way.
- Cosmic Exploration: *Pause* and *Resume* next to *Stop* (on its page, on the Plan page and in the
  compact strip). A pause waits for the item being crafted; anything else (the trip, buying, drawing
  fortunes, gathering) stops where it is, fishing reels in first, and Resume carries on from there. A
  mission's own time limit keeps running meanwhile.
- Pausing an order run on its way back after the order (home, inn room, crafting spot) now stops the
  trip; Resume sets off again. Before, the trip went on regardless.

## 0.19.6

- Character page: in the food and medicine lists, the three that add the most at your stats wear a pill
  on the right, *Best* in gold, *2nd best* in silver and *3rd best* in bronze, instead of a few words at
  the start of the line where they got lost.

## 0.19.5

- Cosmic Exploration, from the first runs in game:
  - Fixed: a mission lends Stellar Steady Hand for all its crafts together, but every craft's rotation
    counted on the full uses. The second craft then had none left, its Rapid Synthesis steps failed and
    the synthesis broke. The uses left now carry from craft to craft, and each rotation is worked out
    with them; the plan judges a mission of several crafts without them for the last.
  - Fixed: the loop reported after the crafts the objective names, though the score adds up per item:
    one Botanic Garden Sheet was bronze with supplies for another left. While short of gold, with
    supplies left and the score still rising, it now crafts another (a few at most) before reporting.
    What earlier runs learned is cleared once, as it came from stopping too early.
  - Fixed: Cosmic Fortunes didn't answer "Pay 1,000 … credits for a cosmic fortune?" (it's a menu with
    Yes and No), so it waited, then gave up before picking a wheel.
  - The compact strip shows the Cosmic loop (mission, Cosmocredits against the goal) with Start and
    Stop, instead of the empty order.
- Character page: the food and medicine pickers' tooltips name the three that add the most at your
  stats (the strongest crafter's or gatherer's gearset, HQ where they come HQ), with what each adds; the
  list marks them too. Ranked by what they add, CP (or GP) counting double.

## 0.19.4

- Fixed: with the Cosmic Exploration plan on the Plan page (and no order), the action bar still belonged
  to the empty order: "The order is empty" and a Start that couldn't be pressed. It now shows the Cosmic
  plan's pace, starts the Cosmic loop, and has *Clear* to untick the crafters and go back to the order
  (the goal, extras and shopping list stay); *Stop* while the loop runs.

## 0.19.3

- Cosmic Exploration: new option *Leave out EX and EX+ missions* (under the crafters): the loop and the
  plan take nothing above class A, even when EX or EX+ missions would reliably reach gold. The plan lists
  them as left out.

## 0.19.2

- Crafting rotations are kept between sessions (next to the settings): once worked out, a recipe at the
  same stats, level and food is there at once next time, so the Cosmic plan and order estimates come
  up much faster after the first time. A new raphael-cli starts them afresh.
- Up to three rotations are worked out at a time (was two).
- The Cosmic plan says when it was worked out, and *Work it out again* shows the loading screen for a
  moment, so the click is seen even when every rotation is already known (it then comes to the same).
  A rotation going past the most quality an item has reads "reaches full quality".
- The Cosmic plan works out a mission's steps in between (pulp before the paper) for progress only:
  the judgement only goes by the last craft's quality, and the craft itself still aims every step at full
  quality.

## 0.19.1

- Cosmic Exploration: the plan (and the loop, for the board's missions) gives the rotations up to a minute
  to be worked out before taking the best found, instead of 30 seconds.

## 0.19.0

- Fixed: the first action of a craft could be sent while the character was still sitting down to craft,
  so the game refused it (seen with Quick Innovation), and the crafter then finished the item with Basic
  Synthesis alone, burning the durability with CP left and quality low. The crafter now waits out the
  game's animation lock before every action. Should the game still refuse one, the crafter skips it and
  goes on with the rotation (Basic Synthesis only finishes what progress is left); only an action that
  saves or restores durability still means Basic Synthesis. A refused specialist action (Heart and Soul,
  Quick Innovation) leaves them out of the rotations for the rest of the session, and the log says the
  game's reason, for bug reports.
- Cosmic Exploration:
  - **Two-class missions** (Sinus Ardorum): a miner, botanist or fisher gathers the material at the
    mission's map marker (the fisher with the mission's bait, at the hole's recorded stands) and the
    crafter it pairs with makes the item, round after round until gold, keeping time to report. Tick
    both classes and turn on *Two-class missions* under Extras. Miner, Botanist and Fisher can be ticked
    for that now; their own missions come later.
  - **Cosmic Fortunes** (extra): draws with the planet's credits, 1,000 a spin, when they near the 10,000
    cap and before the loop ends; Big Bang tickets are drawn too. It picks the wheel with the better
    prizes (unlockables, then rarer items).
  - **Drone Modules** can go on the shopping list: bought from Kaede on Oizys or Auxesia with that
    planet's dronebits, and bought when dronebits near their 5,000 cap.
  - The page shows Cosmocredits, the planet's credits and dronebits against their caps; the loop's log
    says when one nears its cap and what would spend it.
  - The Plan page shows a loading screen while Provenance works out the missions, and takes the
    best found within 30 seconds; the plan then stays as it is until the planet, crafters, gear or food
    change (or *Work it out again*), instead of switching around while late rotations come in. The loop
    gives the board's missions the same 30 seconds.

## 0.18.0

- **Cosmic Exploration** (new sidebar section *Expeditions*): a loop that does stellar missions on the
  planets with your crafters. Pick the planet, tick the crafters, and choose a goal: a number of
  Cosmocredits, a shopping list from the Cosmocredit trader, or until you stop it, with an optional time
  limit. The loop goes to the planet (from anywhere through Bestways Burrow, between planets with
  Cruisingway), takes the best-paying mission each crafter reliably golds with its gearset, crafts it with
  a Raphael rotation (using the mission's Stellar Steady Hand), reports it and goes on. When no mission
  reaches gold for a crafter it takes silver ones and says so.
- "Reliably" means the rotation reaches it without lucky procs. Until a mission has been done, gold is
  judged from the quality the rotation reaches; after that, from what runs of it actually scored, and the
  pace and credits per mission go by what runs of it paid and took.
- The Plan page shows the Cosmic Exploration plan: the mission each crafter takes, Cosmocredits an hour,
  how long the goal takes, and why the other missions aren't taken. When the crafting food and medicine
  from Settings, Crafting would let a crafter reliably gold better missions, a card says how many it
  takes and how much sooner the goal is reached; with them in use, it drops back to the lower missions
  when they run out.
- Extras the loop may take on, each explained in its tooltip: chained missions, weather and time missions
  (only while they're on the board), Master missions on Auxesia, and buying the top crafter and gatherer
  materia when Cosmocredits reach the cap (down to 5,000). Gathering and fishing missions, critical
  missions and red alerts, two-class missions, Cosmic Fortunes and mech ops come in later versions; the
  page says so where they'd be.
- The bug report has a Cosmic Exploration section.

## 0.17.1

- Things to get yourself now say where they really come from, and whether it's the game or Provenance
  that can't get them:
  - sold for another currency, with the price and who sells it ("Sold for 3 Bicolor Gemstones by the
    gemstone trader in Old Sharlayan, and Provenance doesn't spend Bicolor Gemstones"), for Grand
    Company seals, brought back by retainers on ventures, or sold by other players on the market board;
  - only in the Diadem, Cosmic Exploration or Ocean Fishing: "which Provenance doesn't support yet";
  - over the vendor price cap: where to change it (Settings, General).
  Before, many of them only said "No gathering, fishing, vendor or monster source".

## 0.17.0

- Colours (Settings, Interface): pick the accent, the background, the cards and the text. Each opens a
  colour wheel with a field for the hex code (#86D0C2, or the short #8DC) and a few swatches to pick at
  once; the window changes while you pick, Escape puts the colour back. Fields, buttons, lines and the
  muted text follow as shades of what you pick. When the text gets hard to read on the cards or the
  background, or the accent hard to see, the card says so with the contrast. *Reset to defaults* brings
  back the first colours.

## 0.16.2

- Bait sold only for scrips is bought at a scrip exchange before fishing, like other scrip items: with
  the scrips you have, or earned first with collectables. Before, the run only bought bait for gil and
  fished with Versatile Lure instead, on which every fish in the hole bites. Red Maggots for Purple
  Palate, for example, cost 5 Orange Gatherers' Scrips each.
- Running out of such bait during a run no longer means Versatile Lure: the run goes back to the
  exchange and buys what the catches still need (with the spare), earning the scrips first with
  collectables when short, then goes on fishing.
- Bait per catch is learned: every session with a fish's own bait counts the bait used up and the
  fish landed, and the next plan buys by that. Some holes take far more than the data's guess, because
  other fish with the same tug and bite time get hooked in its place (Purple Palate, for one).
- Fish caught for aetherial reduction bring their bait onto the plan, like fish on the order. Before,
  their bait didn't show at all, so nothing said it was missing.
- New setting (Fishing): *Fish for aetherial reduction*. Off, the run only reduces gathered
  collectables and waits for their nodes, crafting meanwhile; an item that only comes from fish is
  then one to get yourself.

## 0.16.1

- Fixed: when a timed node's time ran out before the order had enough, the run kept flying round its
  spawn spots until it came back (up to a whole Eorzean day), while other things on the order could
  have been gathered meanwhile. The run now leaves a timed node as soon as its time is over, on the way
  there or between its spots, and gathers what's up instead (or waits for the next window when nothing
  is). The run log says when it moves on.

## 0.16.0

Filters in the Browse search, and a reset for the Interface page.

- The Browse search takes filters as well as words, such as
  `job:fsh or job:fisher aetherial.reduction level:50-70`: `job:`, `level:` (also `level:mine`), `ilvl:`,
  `stars:`, `cat:` (the item's category), `in:` (world, diadem, cosmic, ocean) and `is:` (recipe,
  gather, fish, timed, reduction, expert, hq, book, unlearned, specialist, ready). A space means and,
  `or` goes before and, a comma means one of, `-` leaves out, and brackets group. Job names work in
  English and in your game's language. The chips and the level range still apply on top.
- Anything the search doesn't understand is left out, not fatal: an amber line under the search says
  what, with the likely meaning to click ("Did you mean job:fisher?").
- The *?* next to the search shows what it understands, with examples to click, and links to the full
  guide on GitHub (docs/search.md).
- *Reset filters* next to the number of items clears the search, the chips and the level range at once.
  The search is now kept when you close the window, like the chips.
- Hovering an item's name in Browse shows its category and item level.
- A star on every Browse row saves that item as a favourite order of its own, with the amount and
  HQ choice set on the row. The star shows amber while it's a favourite; clicking it again removes it.
- Settings, Interface: *Reset to defaults* puts the interface scale, text size, tooltip size and the
  window's size back to how they started. It shows while any of them is changed. Reduce motion stays.

## 0.15.3

- "Open on GitHub" next to "Copy bug report" (Settings, General): copies the report and opens a new
  bug report on GitHub in your browser, with the plugin and version filled in. Paste the report into
  its "Provenance bug report" box. Bugs and suggestions can be filed there from now on.

## 0.15.2

- Fixed: "The aethernet didn't open" when a trip started at an aethernet shard (anywhere in Ul'dah -
  Steps of Thal, Limsa's Upper Decks, Old Gridania, and at any shard nearer than the city's
  aetheryte). Shards weren't recognised as something to talk to, only the big aetherytes were. The
  run also walks right up to a shard now.
- Scrip exchanges are picked by the trip: the one in the area or city you're in first, then the one
  the teleport costs the least to from where you stand. Before, it was always the same one (Ul'dah,
  for most things), whatever it cost. The collectable appraiser is picked the same way, so turning
  in and buying happen in one city. In Radz-at-Han, for one, the run now stays in Radz-at-Han.
- At a city's aetheryte the run picks "Aethernet" by the game's own text for it, in every client
  language, and no longer risks "Residential District Aethernet".
- An aethernet window that doesn't list the stop (or lists it as not attuned) is closed and the run
  says so, instead of waiting in it.
- When the aethernet still doesn't open, the run log says what happened at the crystal: none found,
  how far away it was, or which menu came up.

## 0.15.1

- Fixed: HQ items came out HQ only sometimes. A hard recipe like Caramel Popcorn reached about two
  thirds of its quality (22% HQ) because everything went in NQ. Now:
  - The crafted materials that go into an HQ item are made HQ too (a quality rotation instead of the
    fastest one), all the way down. The plan marks them "HQ", and only the HQ ones you own count for
    them; NQ ones in your bags would pull the quality down.
  - An HQ craft puts the HQ ingredients in first, swapping out NQ ones the game filled in.
  - Raphael is told which ingredients go in HQ. They start the craft with quality, so the rotation
    reaches 100% HQ where it couldn't before, and is often shorter.
  - When even the best rotation can't reach full quality, the run log says how far it gets and
    what would help (HQ ingredients, food and medicine, better gear).
- The bug report shows which plan lines are made HQ and the last rotation: stats, HQ ingredients,
  steps and the quality it reaches.

## 0.15.0

An Interface page, and a text size of its own.

- Settings has a new Interface page, after General, for how the window looks: Interface scale, Text
  size, Tooltip size and Reduce motion. They used to sit in General's Window card.
- Interface scale and Text size are separate settings now, each 75% to 150%. Interface scale sizes
  the spacing, buttons, icons and the window itself, on top of Dalamud's own scale. Text size sizes
  all the text in the window: item names, page names in the sidebar, headings, buttons, chips and
  the drop-down lists. If you'd picked an interface size before, the text starts at that size.
- Both change at once. Until the sharp fonts for a new text size are built, the text in use is
  stretched to it ("sharpening the text…" next to Size), so it never stays at the old size.
- Tooltips keep their own size (Small to Largest), whatever the other two are.
- The bug report gives the interface scale and the text size.

## 0.14.1

- Fixed: the text didn't change with the interface size (nor with the tooltip size). Fonts made after
  the plugin loaded waited for a rebuild of the font atlas that nothing asked for, so the new size
  never came. The atlas is now asked to build them, and asked again if they're still missing a few
  seconds later; anything that goes wrong is written to /xllog.
- The bug report says which interface size is in use and how the fonts are doing.

## 0.14.0

Crafting somewhere safe, and an interface size.

- Interface size (Settings, General, Window): 75%, 90%, 100%, 110%, 125% or 150%. Text, spacing,
  icons and the window grow or shrink together, on top of Dalamud's own scale; the text is built at
  the new size, so it stays sharp. The old size stays on screen until the new fonts are ready.

- Fixed: crafting while waiting for a timed node could start on the mount, in the air (after
  travelling ahead to the node), and the run stopped with "The synthesis didn't start". The crafter
  now gets off the mount first, landing when flying (and flying down to the ground when a landing
  doesn't take).
- Crafting while timed nodes are closed no longer happens out in the field, where monsters attack:
  - with a crafting spot saved and 10 minutes or more until the window: at the crafting spot,
    leaving in time to be back when the node opens;
  - otherwise where the run is, when that's safe (a city, a housing district, an inn room, a house,
    the crafting spot, or next to an aetheryte);
  - otherwise by the aetheryte of the next node's zone nearest to where it spawns, with 4 minutes
    or more to go;
  - with less time than that it waits, on the mount, and says so.
  The run doesn't fly out to the node first when it's going to craft somewhere else meanwhile.
- The crafting after gathering does the same without a crafting spot: to the nearest aetheryte of
  the zone first, unless the run is somewhere safe already.

## 0.13.0

Favourite orders, a bug report, and nodes next to each other.

- Fixed: with two nodes of what the order needs side by side, the run gathered one and then stood
  waiting ("trying the node again soon"). The node just gathered stays targetable for a moment while
  it despawns; the run tried to open it again, failed, and gave up on the whole node group, the node
  next to it included. Now the node just gathered is left alone, a node of the group already in view
  is gone to straight away, marking a node done no longer marks its neighbour, and a node that won't
  open only sets that node aside while another of its group is in view. Choosing the next node also
  prefers one in view over a trip to another spawn spot.
- Bug report (Settings, General, or /prov bugreport): copies what Provenance knows right now to the
  clipboard: settings, the order and its plan, what the run, the gatherer and the fisher are doing,
  the run log, the gathering nodes within 120 yalms with what they hold, and the zone and position.
  No character name, world or retainer names.

- Browse has a "Favourites" chip after the content chips. With it on, the saved orders show instead
  of the items, each with a cover of its first items' icons, its name, what's on it and when it was
  saved; hovering lists every item.
- "Save current order" keeps the order as it is now and puts the cursor in the new favourite's name.
  Names rename in place (click and type), like the order's name on the Plan page.
- Load makes a favourite the order (asking once before it replaces a different one); Add puts its
  items on the order as it is. Overwrite takes the current order over a favourite, Delete removes it
  (both ask once).
- Copy puts the favourite on the clipboard as Teamcraft text, one "10x Item" per line: paste it into
  Teamcraft, or back into Provenance with Import list.
- The search finds favourites by name or by the items on them.

## 0.12.0

Buying materials with scrips.

- Materials only sold at a scrip exchange (white, purple or orange scrips, such as Flint Corn for
  15 Orange Crafters' Scrips) are no longer "Get these yourself": the plan lists them under "To
  buy with scrips", with the price, the exchange, and whether your scrips pay for them now.
- The run buys them at the exchange before gathering, as many at a time as the exchange hands over.
  Short of scrips, it earns them first with collectables (the setting is now "Earn scrips when
  short"), together with any books the order needs: one budget for both, cheapest per item first,
  and past the 4,000 cap it buys what it can, earns more and buys again.
- Bait sold only for scrips is bought the same way.
- Anything a run can gather, fish, buy for gil or reduce still comes from there first; the scrip
  exchange is the last resort before "Get these yourself".

## 0.11.1

Ephemeral nodes, and aetherial reduction you can follow.

- Fixed: ephemeral nodes never counted as up, so a run waited for them forever without saying when
  they open. Every ephemeral node points at an empty placeholder row of the game's timed-node table,
  which was read as "never up" instead of using the node's own hours. This hit most aethersands
  (Levin Quartz, Calamus Root, Goldbranch, Electrocoal, Fire Moraine and the rest come from ephemeral
  nodes) and anything else ordered from an ephemeral node; 90 nodes in all.
- The Run page lists, under each item from aetherial reduction, every collectable it comes from: the
  job, the zone, and what's going on with it now ("node up now", "node opens in 12 min (04:00–08:00
  ET)", "bites now", or why the run can't get it), plus how many wait in the bags to be reduced. Almost
  every aethersand comes from ephemeral or unspoiled nodes, so a run often waits for one to open; now
  it says which and when.
- The run log says where each reduction item comes from when gathering starts.
- The aetherial reduction windows close when the reduction is done (the result, the automatic run and
  the item list); the run waits for that before calling the order done.
- No trip to the crafting spot when the order has nothing to craft (an order of aethersand only).
- Crafting spots out in a housing district (in a ward, not inside a house) are saved with the ward
  and reached through the teleport to your house or apartment there; before, the run couldn't get
  back to them ("No unlocked aetheryte leads there"). A spot is only saved where a run can get back
  from anywhere, not just from where you stand.
- A run no longer stands still when collectables are waiting to be reduced while the character is on
  a mount (after travelling ahead to a node, or when the run starts mounted): it gets off (landing
  first when flying) and reduces them.

## 0.11.0

Aetherial reduction from fish.

- Items that come from reducing fish collectables are now fetched by a run, rod fishing and
  spearfishing alike (Dawnborne, Seaborne and Everdeep Aethersand, the fish behind Levinchrome
  Aethersand, and the rest). The fisher turns Collector's Glove on for those fish only, keeps them
  as collectables, and reduces them between catches; every other fish still comes in as a normal item
  with the glove off. Items with both gathered and fished collectables are worked from whichever is up.
- Browse shows these with the teal "aetherial reduction" tag under FSH; amber is left for
  collectables from content a run can't go to.
- The run's scheduling goes by the collectables an item is reduced from, gathered or fished: it waits
  for their windows, travels ahead and crafts in the meantime, and reduces what's in the bags before
  going on.

## 0.10.0

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
