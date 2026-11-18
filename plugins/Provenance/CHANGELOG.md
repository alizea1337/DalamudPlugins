# Changelog

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
