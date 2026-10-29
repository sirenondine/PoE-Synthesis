# Paths of Empyrean — Synthesis Patcher

A [Synthesis](https://github.com/Mutagen-Modding/Synthesis) port of the official
[Paths of Empyrean xEdit Patcher](https://www.nexusmods.com/skyrimspecialedition/mods/195614?tab=files)
by steelfeathers. It does what the xEdit scripts do, but across the whole load order in one run.
Each part has its own on/off switch, and records that PoE or a PoE patch already covers are skipped.

Ported from xEdit patcher **v1.1** (2026-10-24). Its scripts are identical to v1; v1.1 only adds the icon for the file-picker button.

## What it patches

| Patcher | Toggles |
|---|---|
| **Weapons & Armor** | Material Mastery / Presence / Royal / Conductive keywords · Meltdown condition on breakdown recipes · new breakdown recipes · Dragonblood Masterwork versions (with craft/temper/breakdown recipes) · crossbow variants (Recurve, Spring-Loaded, Light-Weight, Silenced and the six Master Ranger combinations) plus a Description Framework INI · longbow versions (off by default) · bound weapon keyword |
| **Clothing** | Presence keywords · Tailor condition on recipes · Meltdown condition · breakdown recipes · tanning-rack recipes for clothing that can't be crafted yet · Quality versions |
| **Gems & Jewelry** | Jeweler condition on gem jewelry recipes · Lithomancy gem keywords · royal keyword on circlets · gem ↔ flawless gem recipes |
| **Ammo** | Lucky arrow/bolt versions (with tokens and PoE's Lucky Arrow lists) · Barbed/Broadhead bolts |
| **Alchemy** | harvest overhaul for flora/trees (Green Thumb / Grim Harvest) · Adaptive Mutation lists · new Adaptive Mutation abilities · Grim Harvest on creature death items |
| **Monsters** | Daring Fingersmith pickpocketing · no knockdowns · Apex Hunter type and points · Arkay's Judgement · legendary foes (pick the races) |
| **Animal Taming** | tameable `_Tamed` races · milk/venom collection · excluded races and actors · rideable races (pick a race and its skeleton) |

Each patcher has a master switch. Every feature inside it has its own switch too.

The xEdit patcher shows a form for each mod, with checkboxes and dropdowns. This port uses the
values that form starts with. Example: a crown is pre-checked as royal, so it gets the royal keyword.
Choices that the xEdit form leaves unchecked by default become explicit lists in the settings:
legendary races, longbow bows, rideable races and excluded actors.

## Already-patched records

Records are skipped when any of these is true:

- **PoE already covers it.** `PathsOfEmpyrean.esp`, or any plugin with it as a master, overrides
  the record. That includes official and community PoE patches. Records those plugins define
  themselves are never patched either.
- **The keyword or condition is already there.** Every check looks for its own keyword or
  condition first. Examples: an armor that has a Material Mastery keyword, or a recipe that already
  has the Meltdown/Tailor/Jeweler condition.
- **The generated record already exists.** Each new record is first checked by EditorID
  (`PoE_<item>_M`, `PoE_<item>_Q`, `PoE_<ammo>_lucky`, `<race>_Tamed`, …). A Masterwork version is
  also skipped when the base recipe already has the Masterwork-active condition.
- **The form already holds it.** This covers form lists, including entries that SkyPatcher
  formList INIs under `SKSE/Plugins/SkyPatcher/formList` add at runtime.

## Settings worth knowing

- **General → Only/Excluded plugins**: limit or exclude by plugin. The base game masters are
  excluded by default, because PoE itself handles vanilla.
- **General → Skip scripted items** (on): items with Papyrus scripts get no copies, new recipes
  or breakdowns. Devious Devices restraints and quest items are examples. Keyword tagging still
  applies.
- **Weapons & Armor → Crossbow descriptions**: writes `<patch name>_DESC.ini` to the Data folder
  in Description Framework's `0xID~Plugin|text` format.

## Where it differs from the xEdit scripts

- It covers the whole load order instead of one mod at a time. Recipes for an item are found
  wherever they live in the load order.
- Form-list additions go straight into FLST overrides in the patch instead of an exported SkyPatcher
  INI. The paired lists (Animal Tamer From/To, Lucky regular/lucky/token) are always appended
  together, so they stay lined up.
- Things that look like bugs in the xEdit scripts are fixed:
  - Common clothing that gets a new recipe also gets its Quality version. In the xEdit script the rank value was
    -1, so the Quality step never ran.
  - "Head and body" clothing really needs both slots.
  - Tamed races keep their race flags above bit 21. The xEdit script rebuilt only the first 22 bits.
  - Grim Harvest and Green Thumb bonus lists can no longer share an EditorID.
  - The breakdown leather check also reads the item name.
- A longbow without a tempering recipe gets a stub tempering recipe that uses the bow's main
  material. The xEdit stub has no input at all.

## Building / running locally

```sh
dotnet build -c Release
dotnet PoESynthesis/bin/Release/net10.0/PoESynthesis.dll run-patcher --GameRelease SkyrimSE \
  --DataFolderPath <Data> --LoadOrderFilePath <plugins.txt> --OutputPath <out>/PoE_Synthesis_Patch.esp
```

In Synthesis, add it as a local solution patcher (`PoE-Synthesis.sln`) or as a Git patcher once it is pushed somewhere.

## Credits

All PoE design, values and heuristics: steelfeathers ([Paths of Empyrean](https://www.nexusmods.com/skyrimspecialedition/mods/58656)).
This repo contains no PoE assets or xEdit scripts.
