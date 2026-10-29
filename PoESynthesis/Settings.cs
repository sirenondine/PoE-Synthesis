using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis.Settings;

namespace PoESynthesis
{
    public class Settings
    {
        [SynthesisSettingName("General")]
        public GeneralSettings General = new();
        [SynthesisSettingName("Weapons & Armor")]
        public WeaponArmorSettings WeaponsArmor = new();
        [SynthesisSettingName("Clothing")]
        public ClothingSettings Clothing = new();
        [SynthesisSettingName("Gems & Jewelry")]
        public GemsJewelrySettings GemsJewelry = new();
        [SynthesisSettingName("Ammo")]
        public AmmoSettings Ammo = new();
        [SynthesisSettingName("Alchemy (flora, ingredients, death items)")]
        public AlchemySettings Alchemy = new();
        [SynthesisSettingName("Monsters")]
        public MonsterSettings Monsters = new();
        [SynthesisSettingName("Animal Taming")]
        public AnimalTamingSettings AnimalTaming = new();
    }

    public class GeneralSettings
    {
        [SynthesisTooltip("Skip any record that PathsOfEmpyrean.esp, or a plugin with PathsOfEmpyrean.esp as a master (official/community PoE patches), already overrides.\n" +
            "Generated records (Masterwork, Quality, Lucky, ... versions) that already exist in the load order are never created twice, whatever this is set to.")]
        public bool SkipRecordsHandledByPoEPatches = true;

        [SynthesisTooltip("Also treat records listed in SkyPatcher formList INIs (Data/SKSE/Plugins/SkyPatcher/formList) as already added to PoE form lists.")]
        public bool ReadSkyPatcherFormLists = true;

        [SynthesisTooltip("Only patch records that come from these plugins. Empty = every plugin that is not excluded below.")]
        public List<ModKey> OnlyPlugins = new();

        [SynthesisTooltip("Never patch records that come from these plugins. PathsOfEmpyrean.esp and plugins that have it as a master are always excluded.")]
        public List<ModKey> ExcludedPlugins = new()
        {
            ModKey.FromNameAndExtension("Skyrim.esm"),
            ModKey.FromNameAndExtension("Update.esm"),
            ModKey.FromNameAndExtension("Dawnguard.esm"),
            ModKey.FromNameAndExtension("HearthFires.esm"),
            ModKey.FromNameAndExtension("Dragonborn.esm"),
        };

        [SynthesisTooltip("Don't make Masterwork/Quality/variant copies, new recipes or breakdowns for items that carry Papyrus scripts " +
            "(Devious Devices restraints, quest items, ...): copies of those lose the script's identity checks. Keywords and conditions on their existing recipes still apply.")]
        public bool SkipScriptedItems = true;

        [SynthesisTooltip("Log every patched or skipped record, not just the totals.")]
        public bool Verbose = false;
    }

    public class WeaponArmorSettings
    {
        [SynthesisTooltip("Master switch for every weapon and armor patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Material Mastery keywords")]
        [SynthesisTooltip("Tag helmets, cuirasses, gauntlets and boots with a Light/Heavy Material Mastery keyword guessed from vanilla material keywords and the item's name.")]
        public bool MaterialMastery = true;

        [SynthesisSettingName("Presence keywords")]
        [SynthesisTooltip("Tag Intimidating armor (necromancer, daedric, ebony, dragon, thalmor, ...) with the Presence keyword.")]
        public bool Presence = true;

        [SynthesisSettingName("Royal headgear keyword")]
        [SynthesisTooltip("Tag crowns, circlets, diadems, tiaras and masks as royal headgear.")]
        public bool RoyalHeadgear = true;

        [SynthesisSettingName("Conductive keyword")]
        [SynthesisTooltip("Tag iron, steel, dwarven, orcish and nordic armor as conductive.")]
        public bool Conductive = true;

        [SynthesisSettingName("Meltdown condition on breakdown recipes")]
        [SynthesisTooltip("Existing weapon/armor breakdown (smelter/tanning rack) recipes require the Meltdown perk.")]
        public bool MeltdownConditions = true;

        [SynthesisSettingName("Create breakdown recipes")]
        [SynthesisTooltip("Craftable weapons and armor without a breakdown recipe get one (requires Meltdown).")]
        public bool BreakdownRecipes = true;

        [SynthesisSettingName("Create Dragonblood Masterwork versions")]
        [SynthesisTooltip("Craftable weapons and armor get a Masterwork version with crafting, tempering and breakdown recipes.")]
        public bool Masterwork = true;

        [SynthesisSettingName("Create crossbow variants")]
        [SynthesisTooltip("Craftable crossbows get Recurve, Spring-Loaded, Light-Weight and Silenced variants (plus the Master Ranger dual-upgrade combinations).")]
        public bool CrossbowVariants = true;

        [SynthesisSettingName("Write crossbow variant descriptions")]
        [SynthesisTooltip("Write a Description Framework INI (<patch name>_DESC.ini) to the Data folder describing each crossbow variant.")]
        public bool CrossbowDescriptions = true;

        [SynthesisSettingName("Create longbow versions")]
        [SynthesisTooltip("Bows get a Longbow version and are renamed '... Shortbow'. Off by default: the xEdit patcher only does this for bows you pick, usually with a custom longbow mesh.")]
        public bool Longbows = false;

        [SynthesisSettingName("Longbow bows")]
        [SynthesisTooltip("Bows to make longbows for. Empty = every valid bow. Leave Mesh empty to reuse the shortbow mesh (path relative to Data/meshes).")]
        public List<LongbowEntry> LongbowBows = new();

        [SynthesisSettingName("Bound weapon keyword")]
        [SynthesisTooltip("Tag bound weapons with MAG_WeapTypeBound.")]
        public bool BoundWeapons = true;
    }

    public class LongbowEntry
    {
        public IFormLinkGetter<IWeaponGetter> Bow = FormLink<IWeaponGetter>.Null;
        public string Mesh = "";
    }

    public class ClothingSettings
    {
        [SynthesisTooltip("Master switch for every clothing patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Presence keywords")]
        [SynthesisTooltip("Tag rich clothing as Persuasive and sinister clothing as Intimidating.")]
        public bool Presence = true;

        [SynthesisSettingName("Tailor condition on crafting recipes")]
        [SynthesisTooltip("Existing clothing crafting recipes require the Tailor perk to be absent (Quality versions require it).")]
        public bool TailorConditions = true;

        [SynthesisSettingName("Meltdown condition on breakdown recipes")]
        public bool MeltdownConditions = true;

        [SynthesisSettingName("Create breakdown recipes")]
        public bool BreakdownRecipes = true;

        [SynthesisSettingName("Make clothing craftable")]
        [SynthesisTooltip("Clothing without a crafting recipe gets one at the tanning rack (linen, plus a pelt for fur items). Rare clothing is only craftable as its Quality version.")]
        public bool CreateCraftingRecipes = true;

        [SynthesisSettingName("Create Quality versions")]
        [SynthesisTooltip("Craftable clothing gets a Quality version that requires the Tailor perk.")]
        public bool QualityVersions = true;

        [SynthesisSettingName("Never make craftable")]
        [SynthesisTooltip("Clothing that should never get a new crafting recipe.")]
        public List<IFormLinkGetter<IArmorGetter>> NotCraftable = new();
    }

    public class GemsJewelrySettings
    {
        [SynthesisTooltip("Master switch for every gem and jewelry patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Jeweler condition on jewelry recipes")]
        [SynthesisTooltip("Jewelry crafting recipes that use gems require the Jeweler perk.")]
        public bool JewelerConditions = true;

        [SynthesisSettingName("Lithomancy gem keywords")]
        [SynthesisTooltip("Tag jewelry with its gem type (ruby, sapphire, emerald, amethyst/moonstone, garnet/onyx, diamond) for Lithomancy.")]
        public bool GemKeywords = true;

        [SynthesisSettingName("Royal keyword on circlets")]
        public bool CircletRoyal = true;

        [SynthesisSettingName("Gem <-> flawless gem recipes")]
        [SynthesisTooltip("New gems with a matching flawless gem get 3 -> 1 flawless and 1 flawless -> 2 smelter recipes (requires Jeweler).")]
        public bool GemRecipes = true;
    }

    public class AmmoSettings
    {
        [SynthesisTooltip("Master switch for every ammo patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Lucky arrow/bolt variants")]
        [SynthesisTooltip("Retrievable ammo gets a Lucky version and token, registered in PoE's Lucky Arrow form lists.")]
        public bool LuckyVariants = true;

        [SynthesisSettingName("Barbed/Broadhead bolt variants")]
        [SynthesisTooltip("Craftable bolts get Barbed and Broadhead versions with recipes (requires Advanced Bolts).")]
        public bool AdvancedBolts = true;
    }

    public class AlchemySettings
    {
        [SynthesisTooltip("Master switch for every alchemy patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Harvest overhaul for flora/trees")]
        [SynthesisTooltip("Harvestables give leveled amounts sized from their EditorID (small/large, animal sources), compatible with Green Thumb / Grim Harvest.")]
        public bool HarvestOverhaul = true;

        [SynthesisSettingName("Flora to leave alone")]
        public List<FormKey> ExcludedHarvestables = new();

        [SynthesisSettingName("Adaptive Mutation lists for ingredients")]
        [SynthesisTooltip("Add new ingredients with beneficial effects to the matching Adaptive Mutation filter lists.")]
        public bool AdaptiveMutationLists = true;

        [SynthesisSettingName("New Adaptive Mutation abilities")]
        [SynthesisTooltip("For new beneficial peak-value effects PoE has no list for, create a new Adaptive Mutation ability and filter list.")]
        public bool NewAdaptiveMutationAbilities = true;

        [SynthesisSettingName("Grim Harvest on creature death items")]
        [SynthesisTooltip("New creature death items get Grim Harvest bonus ingredients.")]
        public bool DeathItems = true;

        [SynthesisSettingName("Death items to leave alone")]
        public List<IFormLinkGetter<ILeveledItemGetter>> ExcludedDeathItems = new();
    }

    public enum ApexPoints
    {
        One, Three, Five, Seven
    }

    public class MonsterSettings
    {
        [SynthesisTooltip("Master switch for every creature race patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Daring Fingersmith pickpocketing")]
        [SynthesisTooltip("Clothed creatures (falmer, goblins, rieklings, minotaurs, giants, ...) can be pickpocketed with Daring Fingersmith rank 1.")]
        public bool Pickpocket = true;

        [SynthesisSettingName("No knockdowns for huge creatures")]
        [SynthesisTooltip("Extra large races, mammoths, dragons and giants can't be knocked down by Haymaker, Tsunami, ...")]
        public bool NoKnockdowns = true;

        [SynthesisSettingName("Apex Hunter categories")]
        public bool ApexHunter = true;

        [SynthesisSettingName("Apex Hunter points")]
        [SynthesisTooltip("Points a kill of a newly tagged race gives (the xEdit patcher's default is 1).")]
        public ApexPoints ApexHunterPoints = ApexPoints.One;

        [SynthesisSettingName("Arkay's Judgement monsters")]
        [SynthesisTooltip("Dragons, hagravens, daedra and were-creatures are monsters for Arkay's Judgement.")]
        public bool ArkaysJudgement = true;

        [SynthesisSettingName("Legendary foes (Hero of Ages)")]
        [SynthesisTooltip("Races to tag as legendary foes. The xEdit patcher leaves this unchecked for every race.")]
        public List<IFormLinkGetter<IRaceGetter>> LegendaryRaces = new();

        [SynthesisSettingName("Races to leave alone")]
        public List<IFormLinkGetter<IRaceGetter>> ExcludedRaces = new();
    }

    public class AnimalTamingSettings
    {
        [SynthesisTooltip("Master switch for every animal taming patcher below.")]
        public bool Enabled = true;

        [SynthesisSettingName("Create tameable races")]
        [SynthesisTooltip("New animal/creature races get a tameable '_Tamed' race for Animal Tamer (rank 2 for creatures).")]
        public bool TameableRaces = true;

        [SynthesisSettingName("Collectable items (milk/venom)")]
        [SynthesisTooltip("Cows/goats give milk; spiders, chaurus, netch and scribs give venom with Well Trained.")]
        public bool CollectItems = true;

        [SynthesisSettingName("Trust PoE patch coverage")]
        [SynthesisTooltip("Don't add tameable races from plugins an official PoE patch already covers (e.g. the Creation Club content PoE ships patches for): " +
            "if PoE's author didn't make a creature from that mod tameable, it was a choice.")]
        public bool TrustPoEPatchCoverage = true;

        [SynthesisSettingName("New creature types")]
        [SynthesisTooltip("Make creatures (not animals) tameable even when they don't share a skeleton with a race PoE already made tameable. " +
            "Off by default: in a whole load order this catches things like mimics and quest-only monsters.")]
        public bool NewCreatureTypes = false;

        [SynthesisSettingName("Races to never make tameable")]
        public List<IFormLinkGetter<IRaceGetter>> ExcludedRaces = new();

        [SynthesisSettingName("Actors that can't be tamed")]
        [SynthesisTooltip("Specific actors of tameable races to add to the Animal Tamer exclusion list (e.g. a unique quest animal).")]
        public List<IFormLinkGetter<INpcGetter>> ExcludedActors = new();

        [SynthesisSettingName("Rideable races")]
        [SynthesisTooltip("Races rideable with Well Trained, with the path to their custom riding skeleton.")]
        public List<RideableEntry> RideableRaces = new();
    }

    public class RideableEntry
    {
        public IFormLinkGetter<IRaceGetter> Race = FormLink<IRaceGetter>.Null;
        public string Skeleton = "";
    }
}
