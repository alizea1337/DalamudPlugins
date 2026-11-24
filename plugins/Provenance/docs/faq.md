# Questions

Common questions about Provenance, many of them "why does it do X and not Y". What each page does is in
the [README](../README.md). Something not answered here? Ask in
[Issues](https://github.com/alizea1337/DalamudPlugins/issues).

## General

### Why doesn't Provenance use the market board?

It's left out on purpose. Provenance turns a list into finished items from what the game hands out
itself: gathering, fishing, vendors, scrip exchanges, retainers, monster drops and crafting. Something only
other players sell is listed under "Get these yourself", with that as the reason.

### Why doesn't it use Artisan, GatherBuddy Reborn or AutoHook?

It gathers, fishes and crafts by itself, so one run plans all of it together. Early versions drove
GatherBuddy Reborn and AutoHook and had to take over their lists and settings during a run; since 0.7
(gathering) and 0.8 (fishing) neither is needed. The gathering rotations, the crafting (Vulcan, with the
Raphael solver) and much of the fishing are ported from those two, as [NOTICE.md](../NOTICE.md) lists;
Artisan isn't needed either.

### Why does it need vnavmesh?

Provenance walks and flies with vnavmesh's navigation. vnavmesh is published without a licence, so its
code can't be copied into Provenance the way parts of GatherBuddy Reborn and AutoHook were; navigation of
its own is on the to-do list. Planning works without vnavmesh, and teleports and the aethernet are
Provenance's own, so no teleport plugin is needed.

### Why aren't Grand Company supply missions, the Doman Enclave or leves supported?

They aren't planned for now. They may come later if players ask for them.

### What about Ocean Fishing and the Diadem?

Both are planned: Ocean Fishing as the whole voyage (queueing, spectral currents, presets per zone), and
gathering in the Diadem, clouded and umbral nodes included. Until then, items only found there are listed
under "Get these yourself", and their pages under Expeditions only say what they're for.

### Can I queue several orders?

Not yet: a run takes one order at a time. A queue of orders is on the to-do list.

## Orders & planning

### What does "Get these yourself" mean?

These are things a run can't get on its own: sold only for another currency or by other players, over your
vendor price cap (Settings, General), monster drops with no known spawn area, or only found in the Diadem,
Cosmic Exploration or Ocean Fishing. Each row says which. The order doesn't start until you have them, so a
run never stops halfway for something it was never going to get; each row's trash button takes the ordered
items that need it off the order instead.

### Why does the plan say my character can't do something?

The plan checks the order against your character: job levels, master books and folklore tomes, specialist
recipes, the Craftsmanship, Control and Perception asked for, and whether a rotation finishes each craft, all
with your best gearsets plus the food and medicine you picked. It goes by the last check of your character;
*Check again* on that card reads gearsets, stats, books and tomes again after you level or change gear.

### Which of my items count, and why does it make more of an item I already have?

Materials count from your inventory, crystals and saddlebag, and from your retainers with *Retainers* ticked
(Provenance remembers what each one holds whenever you open it at a summoning bell); the armoury chest never
counts. Ordered items themselves are made on top of what you own: 1,000 owned and 2,000 ordered means 2,000
more. *Count ordered items I already have* (Settings, General) changes that.

### Where do the time estimates come from?

Crafting times come from each rotation's length at your checked stats, and your own runs adjust the rest:
every craft and gathering session, how fast you fish, and what a collectable node visit gives and takes.
Waits for timed nodes and fish windows are counted in.

## Gathering & fishing

### Why does it throw away extra fish or overgathered items?

A node is always gathered to the end, so it often brings in a few more than the order needs; afterwards the
extras are thrown away, so 93 of 91 leaves 91. Fish caught on the side (fish with the same tug and bite time
as the wanted one get hooked now and then) go once gathering is done. Only what this run brought in is thrown
away, never what you had before or anything the order uses, and of gathered items only normal quality.
*Throw away extra gathered items* (with *Also from timed nodes and rare fish*) and *Throw away fish caught on
the side* turn this off.

### Why are timed fish left out of aetherial reduction?

Waiting for a fish's time or weather window held whole runs up for the sake of one reduction. Since 0.21.4
reduction uses gathered collectables and fish without a window; an item that only comes from timed fish is
one to get yourself, and the plan says so. *Fish for aetherial reduction* (Settings, Fishing) leaves fish out
of reduction altogether.

### Where are node positions stored, and are they uploaded?

On your PC, next to the plugin's settings. The gathering node positions and the places to stand for fishing
that a run learns are never uploaded.

### Why does it wait before gathering a collectable?

Before a collectable on a regular node the run waits until there's GP for the whole collectable rotation
(600), so the collectability reaches the top reward. With *Drink cordials instead of waiting* (on to start)
it drinks the smallest cordial from your bags that fills the gap instead. Timed nodes are never waited for.

### Why did aetherial reduction stop with something in the way?

The game's reduction takes all your reducible collectables, not only the ones the run gathered. So the run
never reduces while a collectable the order needs is in your bags, nor, with *Reduce spare collectables*
off, while other reducible collectables are there; it stops and says which instead.

### What bait does it buy?

Bait sold for gil (under the price cap in Settings) and bait sold for scrips at a scrip exchange, earning the
scrips first when short; how much bait a catch uses is learned from your own sessions. Crafted bait has to be
in your bags, unless *Versatile Lure when the bait can't be bought* is on, which buys Versatile Lure in Limsa
Lominsa Lower Decks instead.

## Crafting

### Why does it craft at a crafting spot and not out in the field?

Monsters attack out in the field. While timed nodes are closed, the run crafts at your crafting spot (with 10
minutes or more to spare), where it already is when that's safe (a city, a housing district, an inn room, a
house, or next to an aetheryte), or by an aetheryte of the next node's zone; with less time it waits on the
mount. Save a crafting spot under Settings, Crafting.

### Where do the crafting rotations come from?

From Raphael, a solver that works out a rotation for your live stats, food and medicine: the best quality for
HQ items, the fastest for everything else. Rotations are kept between sessions, so a recipe at the same
stats is ready at once next time.

### Why are the materials under an HQ item made HQ too?

NQ ingredients pull the quality down: a hard recipe made from NQ materials can't reach full quality. So the
crafted materials under an HQ item are made HQ, only the HQ ones you own count for them, and HQ ingredients go
in first. When even the best rotation falls short, the run log says how far it gets and what would help.

## Scrips

### How does Provenance get scrips?

With *Earn scrips when short* (Settings, General; on to start), a run that is short makes or gathers the
quickest collectables your jobs can handle, turns them in at a collectable appraiser, then buys what the order
needs: master books, folklore tomes, materials and bait sold for scrips. Anything a run can gather, fish, buy
for gil or reduce still comes from there first; the scrip exchange is the last resort.

### Why does it earn and buy in rounds?

Scrips stop at 4,000. When the order needs more, the run buys what the scrips at hand pay for (cheapest
first), earns up to the cap again, and buys again until everything is in.

### Which scrip exchange does it use?

The one in the area or city you're in, otherwise the one the teleport costs least to. The collectable
appraiser is picked the same way, so turning in and buying happen in one city.

## Cosmic Exploration

### Why does it only take missions it reliably golds?

"Reliably" means the rotation reaches gold without lucky procs. Until a mission has been done, gold is
judged from the quality the rotation reaches; after that, from what your runs of it actually scored. When no
mission reaches gold for a crafter, it takes silver ones and says so.

### Why does working out the plan take up to a minute?

Every mission a crafter could take is judged with a full rotation, and the plan takes the best found within a
minute. Rotations are kept between sessions, so it comes up much faster after the first time; it then stays
as it is until the planet, crafters, gear or food change, or until *Work it out again*.

### Does it do gathering and fishing missions?

Not their own missions yet: Miner, Botanist and Fisher only take part in two-class missions on Sinus Ardorum
(an extra), gathering what the crafter makes the item from. Gathering missions with critical missions and red
alerts come next, then fishing missions.

## Custom Deliveries

### Which goal does the delivery run pick for me?

Until you pick one, *Rank up* while any client you've unlocked is below the top level, otherwise *Scrips*.
Picking a goal keeps it; *Pick the goal for me again* goes back to the automatic one.

### Why does Rank up serve the clients below the top level first?

The 12 deliveries a week are shared between all clients, and a delivery to a top-level client levels nobody.
Under *Rank up* the clients below the top level get theirs first, the lowest level first, up to their next
level; what's left goes to the top-level clients, bonus requests first. Under *Scrips* and *Gil*, bonus
requests come first.

### Why does Rank up use the lowest collectability?

Every tier gives the same satisfaction towards the next level, so *Rank up* takes the lowest. Under *Scrips*
and *Gil*, crafted requests aim for the best tier your gear reliably reaches, with food and medicine when
*Food and medicine for a higher tier* is on, and gathered requests aim for the top tier.

### Where do the components and crystals come from?

What's in your bags counts first. The other components of a crafted request are bought from the vendor next
to the client. Crystals are never bought: they have to be in your bags, and a crafted request whose crystals
aren't there isn't planned (the plan says which crystals it needs).

### Why does the run go round again after the tour?

A rank-up changes the client's requests, so after the tour the run plans again while deliveries are left. It
goes up to 3 rounds, and only when the round before delivered something.

### Why does it skip a client's delivery at the scrip cap?

Scrips stop at 4,000, and what a delivery pays past the cap is lost. So the plan only makes as many deliveries
as the scrips still fit, and a client whose scrips wouldn't fit at all gets nothing this time ("its scrips
would go over the cap"). Under *Rank up*, *Deliver anyway while ranking up* delivers to the clients below the
top level regardless. A shopping list to spend scrips at the cap is planned.

### Why are fishing deliveries not done yet?

The run comes in steps: crafted and gathered requests came first (0.21), and fish and spearfishing requests
are the next step. Until then the plan leaves fish requests out before it judges the others, so a client whose
crafted and gathered requests can't be made shows why one of those can't, or "none of this week's requests can
be made yet". With *Prefer crafting and gathering* on, fish will only be delivered when neither the crafted nor
the gathered request can be done.

### Why is a client left out of this week's plan?

Under "Not this time" the plan lists each ticked client that gets nothing this time, with the reason: a story
quest first, no crafter or gatherer at the client's level, why its requests can't be made (a class level, a
saved gearset, crystals, a collectability your gear doesn't reach), done for the week, the scrip cap, or no
deliveries left. Locked clients and the ones you've unticked aren't listed there; opening a locked client's
row on the page names the quest that unlocks it. Clients in the Firmament only take deliveries on your home
world, where the Firmament is open. The run does no quests, so unlocks and story quests are yours to do.

### Why is the Custom Deliveries page empty?

The game only sends the delivery data once you open its client list (Timers, then Custom Deliveries) or talk
to a client. *Open the client list* on the page opens it for you.

### What does "Differs from the game" mean?

At the top level a client's requests change every week. Provenance works them out from the week's data the
way the game does, and checks them when you open that client's delivery window; "Differs from the game" means
the window showed other items. Please send a bug report then.

## Tribes

The run does the dailies of all 20 societies. The crafter and gatherer societies' dailies have been done since
0.22. The 11 battle societies' dailies, and the Ixal's with a fight or a password, are new and still being
tested: a bug report helps when one goes wrong. Some dailies stay "do it yourself"; the questions below say
which and why. Rank quests and the intersocietal quests by button, and a shopping list for the society shops,
are planned.

### I used Tribes before the battle societies were added. What changes?

The battle societies are ticked from the start. With a job picked under *Monster drops* (Settings), the day's
12 allowances now also go to them (under *Rank up* the lowest ranks first), and battle dailies already in
your journal are picked up and fought. To leave a society out, untick *Include in the run* in its opened row
on the Tribes page.

### How many dailies does it take a day?

Within the 12 allowances a day, up to 3 from each giver. Most societies have one giver; the Ixal and the four
ARR battle societies (Amalj'aa, Sylphs, Kobolds, Sahagin) have one for each rank, so more than 3 a day is
possible there. On the day you rank up, the giver offers 3 more when the new rank has dailies of its own:
reaching Sworn gives none for most societies, and reaching Bloodsworn none for any.

### What does Rank up do with my allowances?

Reputation past a full bar is lost until the rank quest is done, so *Rank up* first takes the dailies that
fill each society's bar, the lowest rank first. Allowances still left after that go to more dailies, since
they still pay currency, gil, crystals and EXP. A society whose bar is full waits for its rank quest: it gets a
notice, and its allowances go elsewhere. *Currency* and *Levelling* keep doing its dailies.

### Which goal does the Tribes run pick for me?

Until you pick one, *Rank up* while a ticked society is below its top rank, then *Currency*. A society counts
for that only when the run can take one of its dailies today: a battle society doesn't while no combat job
qualifies for its dailies.

### Why do some givers' dailies come last under Currency?

Their dailies pay none of the society's currency, so under *Currency* those givers get allowances last: the
Ixal's givers below Respected (no Oaknots), and the Neutral and Recognized givers of the Amalj'aa, Sylphs,
Kobolds and Sahagin, whose dailies pay a Venture and no tokens.

### How are the days to a rank counted?

From the reputation each daily gives at your rank, up to 3 dailies a day, plus the 3 more on a rank-up day
when the new rank has dailies of its own, with each rank quest done as soon as the bar fills. The page counts
to the next rank and to the society's top rank: Bloodsworn for most, Sworn for the Ixal.

### What do "the run does it", "do it yourself" and "needs …" mean?

"The run does it": the run can take it today. "Do it yourself" comes with the reason the run doesn't take it
(a minigame, a FATE, a rank quest and the like). "Needs …" says what's missing first: crystals, a class level, a
saved gearset, a combat job for a fight, or an item to wear (the Ixal's Ehcatl Wristgloves, sold by their
vendor).

### Which job does the run fight on?

A battle society's daily is accepted and fought on the job picked under *Monster drops* (Settings) when it
qualifies: Provenance has a combo for it, it's at the quest's level, and it has a saved gearset (the job
you're on needs none). When it doesn't qualify, the daily goes to the combat job you're on, if that one
qualifies. A job that was on earlier in the same run also counts. Otherwise the quest shows "needs …" and
isn't accepted. Fights use the basic combo and the chocobo as set under *Monster drops*.

The Tribes page shows the picked job under *Combat job*, and each quest says what's missing: "Pick a combat job
under Settings, Monster drops", a level ("PLD is below level 60") or a gearset ("No gearset saved for PLD").

### What does the run attack?

On a combat job: the quest's own enemies, and whatever attacks you or your chocobo. Other monsters, FATE
monsters and enemies someone else is fighting are left alone. As a crafter or gatherer the run doesn't fight;
it carries on with its step.

Riding its own mount on the way, the run rides on past things that attack unless they're the quest's, and
fights when it's still attacked on arrival. It gets off its own mount for a fight. On a quest mount with an
action, that action is used on the quest's enemies, and the run rides on past anything else.

Where a quest has a key item for a weakened enemy, the run stops attacking and uses the item. An item that can
be thrown from 10 yalms or more is thrown first, and where the quest wants nothing else of the enemy only the
item is used. Before starting the next fight the run waits for health to come back to 70%.

### What if a fight goes badly?

An enemy that isn't attacking and can't be reached or hurt is left after a time: 45 seconds without reaching
it, 30 seconds without hurting it, 3 minutes in all. Something that attacks is always fought. After two
enemies left like that, or 15 defeats that didn't move the quest on, the run pauses and asks you, once the
fighting is over.

A knock-out counts against the *Knock-outs* limit (Settings, Security). The run returns, leaves the quest it
happened in to you for today, waits for Weakness to wear off before the next fight, and plans the day again.

### How are the Ixal's dailies with a fight done?

They're accepted as a crafter like the Ixal's others. The run changes to the *Monster drops* job for the fight
and back to the crafter for talks and hand-overs, always out of combat, because the quest only moves on as the
class it was accepted as. That job needs level 50 and a saved gearset; without it the quest shows "needs …"
and isn't accepted. Like all battle dailies these are new and still being tested: a bug report helps when one
goes wrong.

### Why does it craft some dailies for high quality?

Some crafting dailies only count their item made high quality (the journal shows a small mark after the item's
name, and the Tribes page says "high quality only"). The quest's own script says so, and the run reads it:
those are crafted for full quality, counted and handed in high quality, and the others with the fastest
rotation. When a craft comes out normal quality all the same, the run fetches new materials and tries again;
after two tries it asks you, since gear that can't fill the quality bar won't do better next time (food,
medicine or better gear help). The game itself says to discard the normal-quality ones: they serve no purpose.

### Where do the crystals for crafting dailies come from?

From your bags, then your retainers, then gathering. A quest the crystals still don't cover is left out, with
the reason. The game only says how many crafts a daily asks for once it's accepted, so the check assumes up
to 4.

### Why won't the run do the shooting and bow minigames?

The only known way to automate them fakes the minigame's result to the game, and Provenance doesn't fake
results. The Namazu's bow quest and the Kojin, Ananta and Pixies snipes are marked "do it yourself", with that
reason, and not accepted. Once rank quests come (planned), the two with a minigame, the Kojin's "Under Wraps"
and the Namazu's "Out of the Frypan", stay "do it yourself" too.

### Why are the Dwarves' cannon dailies "do it yourself"?

"Demolition Demo" and "Substitute Tester" fire a cannon that has to be aimed at each target, which the run
can't do yet. They're "do it yourself" for now.

### Why are some emote dailies "do it yourself"?

In nine dailies the emote goes to people or things the quest's data doesn't name; it only points at an area or
an object. Emotes are public, so the run only emotes at someone the quest names, and leaves these to you for
now: the Moogles' "The Moogle Motivator", the Yok Huy's "Romanced by the Stone", the Kobolds' "Hells Have No
Fury", the Vanu Vanu's "Dance Commander", "Electric Gunduloo" and "Wildwort and Sundrop", the Kojin's "You Have
the Rite", the Ananta's "When Feathers Fly" and the Arkasodara's "Blood from a Stonemason". Phrases for `/say`
are different: when a step points at a place, the run goes there and says the phrase once.

### Why are the Sylphs' stealth dailies "do it yourself"?

"Now You See Me", "Waterbringer" and "Mist Opportunities" are done while a sylph's spell keeps you unseen,
before it runs out; blooms on the way make it last longer. The run can't do that yet, so they're "do it
yourself" for now.

### Why are the FATE dailies left out?

The nine ARR FATE dailies wait on a named FATE spawning, sometimes only after other FATEs, which can take a
long time. They're marked "do it yourself" and not accepted.

### Why is the Ixal's "Deliverance" left out?

It has a quota system of its own, so it's marked "do it yourself" and not accepted.

### Which Ixal dailies does the run do?

The crafting ones and, new and still being tested, the ones with a fight and the ones with a password to
`/say`. Nine have a step for a Miner, Botanist or Fisher partway through; the run doesn't change to a gatherer
inside a quest yet, so those are "do it yourself" for now, whatever else they ask. "Deliverance" is left out
too.

### Why doesn't it take gatherer dailies as Fisher?

The game data has no fishing spot or bait for those quest fish. Gatherer dailies are taken as Miner or
Botanist instead.

### Why does the Levelling goal skip my lowest combat job?

*Levelling* only takes a battle quest on a job at least 5 levels above the quest's level, and picks the
lowest such job that Provenance has a combo for and that has a saved gearset (the job you're on needs none).
Between jobs of the same level the *Monster drops* job comes first, then the better-geared one. The basic combo
has little healing, and ARR society quests aren't level-synced, so the extra levels make the fights safer.
When no job is that far above, the quest goes to the job it's fought on under the other goals.

### Does the run unlock societies or do rank quests?

It doesn't do unlock quests: the page shows what's missing for each society, and those quests are yours. Rank
quests are yours too for now: the page gives a notice when one is ready. They're planned for a later release,
started with *Do the rank quest*; the three with a solo duty (Kojin, Ananta, Pixies) will warn you beforehand
and pause at the duty for you to do it. The intersocietal quests for the "Allied" rank are planned with them.

### Can I keep some allowances for myself?

Tick only the societies the run should do (*Include in the run*, in each society's opened row). It takes up to
3 quests from each giver of a ticked society, within the 12 allowances a day, so ticking fewer leaves the rest
for you; there's no separate setting for it.

### Will it say things in public chat?

Some quests ask you to `/say` a phrase. The run says the quest's own phrase once; if the game doesn't take it,
the run hands that step to you. *Leave out quests that /say in public* (off to start) skips those quests
altogether. Emotes get one more try after 15 seconds when nothing changed.

### What does the bug report option on the Tribes page do?

With *Note what the run would do while I do society quests myself (for bug reports)*, while you do a society
quest by hand and no Provenance run is going, the debug log notes what the run would do, with no player names.
Bug reports carry it, which helps check the run before it acts on a quest. It's on to start, and only active
once you've opened the Tribes page.

## Safety & bug reports

### What does the Tripwire do?

While a run, the Cosmic loop, the delivery run or the Tribes run works, it stops everything at once, even a
craft under way, and the chat says why. It goes off when the character is moved with nothing to explain it (a
game master can summon a character, for example) or a game master is around, and, if you turn them on, when a
player appears close by or a hidden player is close by. Your own teleports, Provenance's own trips and your
party never count; it's under Settings, Security.

### Why is a player in the debug log shown as a "ref"?

When a player sets the Tripwire off, the debug log writes their name and world base64-encoded, as a "ref", so
it doesn't read at a glance in a bug report. Base64 is an encoding, not a hash: anyone can decode it, which is
what makes it useful for debugging. Remove that line before you post a report if you'd rather not share it.

### Which plugins get in Provenance's way?

Plugins that click through the game's windows or play the character by themselves. Provenance takes care of
three of them itself:

- YesAlready and TextAdvance are paused while a run is going, and carry on as soon as it pauses, finishes or
  stops. That's *Pause YesAlready and TextAdvance while a run is going* (Settings, Security; on to start).
  Their own settings stay as they are. With it off, YesAlready is still paused while custom deliveries are
  handed in.
- Questionable can't be paused from outside. A run won't start while it's running its quests, and a run under
  way stops when it starts: two plugins can't drive the character at once.

The Security page shows what each of the three is doing, and the run pages warn while one of them is in the way.

Turn these off yourself while a run is going:

- Pandora's Box: the features that act by themselves, such as filling in hand-overs, mounting up and
  interacting with things.
- Artisan: its automatic action mode. Provenance crafts by itself.
- GatherBuddy Reborn's auto-gather, and AutoHook. Provenance needs neither.
- AutoRetainer: switching itself on when the summoning bell is opened, and Multi Mode.
- Rotation plugins' autorotation, and BossMod's AI.
- Any other automation for the same content, such as another plugin for Cosmic Exploration missions, custom
  deliveries or allied society quests.

Plugins that only show things, and teleport plugins, are fine.

### What's in a bug report, and how do I send one?

*Copy bug report* (Settings, General) or `/prov bugreport` copies what Provenance knows right now: settings,
the order and its plan, what the run is doing and its log, the debug log of the current and the previous run,
nearby gathering nodes, your zone, position and levels, and the plugins loaded. There's no character name,
world or retainer names in it. *Open on GitHub* copies it too and opens a new bug report with the plugin and
version filled in; paste the report into it.

### Does Provenance send anything over the internet?

Only when you import a Teamcraft list by its link: it downloads the list from Teamcraft's public database.
Bug reports go to the clipboard for you to post yourself, and everything a run learns (node positions, places
to stand, rotations, timings) stays on your PC.

### What happens when I'm knocked out, a duty pops or the bags fill up?

After a knock-out the run accepts a raise or returns to the home point and carries on, up to the *Knock-outs*
limit (3 to start); a duty pauses the run until it ends; and the run stops at 2 free inventory slots (*Free
slots*). Every stop shows a notification and a chat line. These limits are under Settings, Security, with gil
and time limits for a run.
