# Changelog

## Unreleased

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
