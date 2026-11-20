# Searching in Browse

The search box on the Browse page finds items by name, and narrows them down with filters. Everything you
type has to match, together with the job chips, the content chips and the level range under it.
*Reset filters* (next to the number of items) clears all of it at once. The search is kept when you close
the window.

## Words

Plain words search item names, in your game's language.

| You type | It finds |
|---|---|
| `ingot` | names with "ingot" in them |
| `turali corn` | names with both "turali" and "corn" in them, in any order |
| `"turali corn"` | names with exactly "turali corn" in them |
| `-oil` | names **without** "oil" |

## Filters

A filter is `field:value`. Capitals don't matter.

| Filter | What it means | Examples |
|---|---|---|
| `job:` | the job that makes or gets it: abbreviation or name, in English or your game's language, or `crafter` / `gatherer` | `job:fsh`, `job:fisher`, `job:Fischer`, `job:crafter` |
| `level:` | the recipe or node level | `level:70`, `level:50-70`, `level:>=50`, `level:<70`, `level:90+` |
| `level:mine` | up to your own level in that job | `job:btn level:mine` |
| `ilvl:` | the item's item level | `ilvl:600+`, `ilvl:640-690` |
| `stars:` | recipe stars | `stars:1+`, `stars:0` |
| `cat:` | the item's category, as its tooltip names it; any part of the name works | `cat:metal`, `cat:lumber`, `cat:"primary tool"` |
| `in:` | where it comes from | `in:world`, `in:diadem`, `in:cosmic`, `in:ocean` |
| `is:` | what kind of item it is (see below) | `is:timed`, `is:reduction` |
| `name:` | the name, like a plain word | `name:ingot` |

Numbers can be written as `70`, `50-70` (or `50..70`), `>=50`, `>50`, `<=70`, `<70` and `90+`.

### `is:` values

| Value | Means |
|---|---|
| `recipe`, `gather`, `fish` | crafted, gathered on a node, or fished |
| `timed` | only on timed nodes (unspoiled, legendary, ephemeral) |
| `reduction` | comes from aetherial reduction |
| `expert` | an expert recipe |
| `hq` | can be crafted in high quality |
| `book` | needs a master book (recipes) or a folklore tome (nodes and fish) |
| `unlearned` | needs a book or tome you haven't learned |
| `specialist` | only a specialist can craft it |
| `ready` | you can make or get it now: your level is high enough, the book or tome is learned, and you're a specialist where the recipe needs one (from the Character page's last check) |

## Combining

| You type | It means |
|---|---|
| `job:min is:timed` | a space (or `and`): both have to match |
| `job:min or job:btn` | either one (`\|\|` works too) |
| `job:min or job:btn is:timed` | **or goes before and**: (MIN or BTN) and timed |
| `job:fsh,min` | a comma means one of these: FSH or MIN |
| `-is:expert`, `not is:expert` | leave these out |
| `(job:fsh is:timed) or is:reduction` | brackets group, when "or goes first" isn't what you mean |

## More names for the same thing

You don't have to remember the short forms. These work too:

- `aetherial.reduction`, `reduce`, `reduced` for `is:reduction` (`aetherial.reduction` even on its own)
- `lvl`, `level.range` for `level`; `itemlevel`, `item.level` for `ilvl`; `category`, `type` for `cat`
- `craft`, `gathered`, `fishing`, `unspoiled`, `legendary`, `ephemeral`, `canhq`, `masterbook`, `tome`, `spec`,
  `canmake` and similar for the `is:` values
- `openworld`, `cosmicexploration`, `oceanfishing` for the `in:` values

## When something isn't understood

Nothing you type breaks the search. A filter it doesn't understand is left out, the rest still applies, and
an amber line under the search says what was left out, with the likely meaning to click:

> Ignored "job:fishr". Did you mean **job:fisher**?

Half-typed filters such as `job:` are simply waited for.

## Favourite orders

With the *Favourites* chip on, only plain words search, through the favourites' names and items. Filters
like `job:fsh` only narrow the items.

## Examples

| Search | Finds |
|---|---|
| `job:fsh or job:fisher aetherial.reduction level.range:50-99` | fish caught for aetherial reduction, level 50 to 99 |
| `is:timed job:min,btn level:90+` | timed nodes for miners and botanists, level 90 and up |
| `is:ready is:recipe -is:expert` | recipes you can craft now, without expert ones |
| `cat:metal ilvl:600+` | metals of item level 600 and up |
| `is:unlearned` | everything waiting on a master book or folklore tome you haven't learned |
| `in:diadem job:btn` | what botanists gather in the Diadem |
