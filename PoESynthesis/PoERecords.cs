using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis
{
    /// <summary>The PoE (and vanilla) records the xEdit patcher looks up by EditorID in PoEVars.pas.</summary>
    public class PoERecords
    {
        readonly PatchContext ctx;
        readonly Dictionary<Type, Dictionary<string, FormKey>> poeByType = new();
        readonly Dictionary<Type, Dictionary<string, FormKey>> loByType = new();
        readonly HashSet<string> missing = new();

        // crafting
        public readonly FormKey? Cloth, Pelt, PerkArcane, PerkMeltdown, PerkTailor, PerkJeweler;
        public readonly FormKey? QualityClothing, Masterwork, MasterworkActive;
        // armor
        public readonly FormKey? Conductive, Intimidating, IntimidatingHead, Persuasive, Royal;
        public readonly Dictionary<string, (FormKey? Heavy, FormKey? Light)> MaterialMastery = new();
        public readonly FormKey? MaterialMasteryStealth;
        // weapons
        public readonly FormKey? BoundWeapon, Recurve, SpringLoaded, LightWeight, Silenced, PerkArbalester, PerkMasterRanger;
        public readonly FormKey? Fittings, IngotSteel, IngotQuicksilver, VampireDust, DwarvenOil, Longbow;
        // jewelry
        public readonly FormKey? Amethyst, Diamond, Emerald, Garnet, Ruby, Sapphire;
        // ammo
        public readonly FormKey? Lucky, LuckyAmmoList, LuckyAmmoRegList, LuckyTokenList, Barbed, Broadhead, PerkAdvancedBolts;
        // alchemy
        public readonly FormKey? GreenThumb, GrimHarvest, AdaptiveMutationAllFilters, AdaptiveMutationAllAbilities, ResistDiseaseList, PlantableFlora;
        public readonly List<(string EffectKeyword, FormKey? List)> AdaptiveMutationLists = new();
        // monsters
        public readonly FormKey? CanPickpocket, NoKnockdowns, Legendary, ArkayMonster;
        public readonly FormKey?[] ApexPoints;
        public readonly Dictionary<string, FormKey?> Apex = new(StringComparer.OrdinalIgnoreCase);
        // taming
        public readonly FormKey? TameAnimal, TameCreature, AlwaysFemale, Rideable, HasMilk, HasVenom, RacesFrom, RacesTo, TameExcluded;
        public readonly FormKey? MilkSm, MilkMd, VenomSpiderSm, VenomSpiderMd, VenomSpiderLg, VenomChaurusSm, VenomChaurusMd, VenomChaurusLg, VenomNetchSm, VenomNetchLg, VenomScrib;
        // vanilla
        public readonly FormKey MagicHatMarker = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x0435A5);
        public readonly FormKey EitherHand = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x013F44);

        public static readonly string[] MaterialMasteryTypes =
        {
            "Blades", "Bonemold", "Chitin", "Daedric", "Dawnguard", "Dragon", "Dwarven", "Ebony", "Elven", "Falmer", "Forsworn", "Fur",
            "Glass", "Hide", "Iron", "Leather", "Nordic", "Orcish", "Scaled", "Steel", "Stalhrim", "Stormcloak", "Vampire",
        };

        public static readonly string[] ApexTypes =
        {
            "Bear", "Chaurus", "Daedra", "Dragon", "Draugr", "Dwarven", "Falmer", "Gargoyle", "Giant", "Hagraven", "Mammoth", "Mudcrab",
            "Riekling", "Sabrecat", "Spider", "Spriggan", "Troll", "Werebeast", "Wisp", "Wolf",
        };

        public PoERecords(PatchContext ctx)
        {
            this.ctx = ctx;

            Cloth = P<IMiscItemGetter>("IWP_MISC_CleanLinen");
            Pelt = P<IMiscItemGetter>("IWP_MISC_CleanedPelt");
            PerkArcane = V<IPerkGetter>("ArcaneBlacksmith");
            PerkMeltdown = P<IPerkGetter>("IWP_PERK_SMITH_Meltdown");
            PerkTailor = P<IPerkGetter>("IWP_PERK_SMITH_Tailor");
            PerkJeweler = P<IPerkGetter>("IWP_PERK_SMITH_Jeweler");
            QualityClothing = P<IKeywordGetter>("SF_K_QualityClothing");
            Masterwork = P<IKeywordGetter>("SF_K_Masterwork");
            MasterworkActive = P<IGlobalGetter>("IWP_GLO_SMITH_DragonbloodMasterworkActive");

            foreach (var t in MaterialMasteryTypes)
            {
                MaterialMastery[t] = (P<IKeywordGetter>($"SF_K_MaterialMastery{t}Heavy"), P<IKeywordGetter>($"SF_K_MaterialMastery{t}Light"));
            }
            MaterialMasteryStealth = P<IKeywordGetter>("SF_K_MaterialMasteryStealthLight");
            Conductive = P<IKeywordGetter>("SF_K_ArmorMaterialConductive");
            Intimidating = P<IKeywordGetter>("SF_K_ArmorClothingIntimidating");
            IntimidatingHead = P<IKeywordGetter>("SF_K_ArmorClothingIntimidating_Head");
            Persuasive = P<IKeywordGetter>("SF_K_ArmorClothingPersuasive");
            Royal = P<IKeywordGetter>("SF_K_ArmorClothingHeadRoyal");

            BoundWeapon = P<IKeywordGetter>("MAG_WeapTypeBound");
            Recurve = P<IKeywordGetter>("PoE_K_CrossbowRecurve");
            SpringLoaded = P<IKeywordGetter>("PoE_K_CrossbowSpringLoaded");
            LightWeight = P<IKeywordGetter>("PoE_K_CrossbowLightWeight");
            Silenced = P<IKeywordGetter>("PoE_K_CrossbowSilenced");
            PerkArbalester = P<IPerkGetter>("PoE_PERK_ARCH_Arbalester");
            PerkMasterRanger = P<IPerkGetter>("PoE_PERK_ARCH_MasterRanger");
            Fittings = V<IMiscItemGetter>("BYOHMaterialFittingsIron");
            IngotSteel = V<IMiscItemGetter>("IngotSteel");
            IngotQuicksilver = V<IMiscItemGetter>("IngotQuicksilver");
            VampireDust = V<IIngredientGetter>("vampireDust");
            DwarvenOil = V<IIngredientGetter>("DwarvenOil");
            Longbow = P<IKeywordGetter>("PoE_K_WeapTypeLongbow");

            Amethyst = P<IKeywordGetter>("PoE_K_JewelryAmethystMoonstone");
            Diamond = P<IKeywordGetter>("PoE_K_JewelryDiamond");
            Emerald = P<IKeywordGetter>("PoE_K_JewelryEmerald");
            Garnet = P<IKeywordGetter>("PoE_K_JewelryGarnetOnyx");
            Ruby = P<IKeywordGetter>("PoE_K_JewelryRuby");
            Sapphire = P<IKeywordGetter>("PoE_K_JewelrySapphire");

            Lucky = P<IKeywordGetter>("PoE_K_AmmoLucky");
            LuckyAmmoList = P<IFormListGetter>("PoE_LIST_ARCH_LuckyArrow_ammoList");
            LuckyAmmoRegList = P<IFormListGetter>("PoE_LIST_ARCH_LuckyArrow_ammoList_reg");
            LuckyTokenList = P<IFormListGetter>("PoE_LIST_ARCH_LuckyArrow_tokenList");
            Barbed = P<IKeywordGetter>("PoE_K_BoltBarbed");
            Broadhead = P<IKeywordGetter>("PoE_K_BoltBroadhead");
            PerkAdvancedBolts = P<IPerkGetter>("PoE_PERK_ARCH_AdvancedBolts");

            GreenThumb = P<IGlobalGetter>("ISP_GLO_ALCH_GreenThumb");
            GrimHarvest = P<IGlobalGetter>("ISP_GLO_ALCH_GrimHarvest");
            AdaptiveMutationAllFilters = P<IFormListGetter>("ISP_LIST_ALCH_AdaptiveMutation__AllFilters");
            AdaptiveMutationAllAbilities = P<IFormListGetter>("ISP_LIST_ALCH_AdaptiveMutation__AllAbilities");
            ResistDiseaseList = P<IFormListGetter>("ISP_LIST_ALCH_AdaptiveMutationFilterResistDisease");
            PlantableFlora = V<IFormListGetter>("flPlanterPlantedFlora");
            foreach (var (kw, list) in new[]
            {
                ("MagicAlchFortifyOneHanded", "1H"), ("MagicAlchFortifyTwoHanded", "2H"), ("MagicAlchFortifyAlteration", "ALT"),
                ("MagicAlchFortifyMarksman", "ARCH"), ("MagicAlchFortifySpeechcraft", "Barter"), ("MagicAlchFortifyBlock", "Block"),
                ("MagicAlchFortifyCarryWeight", "Carry"), ("MagicAlchFortifyConjuration", "CON"), ("MagicAlchFortifyDestruction", "DES"),
                ("MagicAlchFortifyEnchanting", "ENCH"), ("MagicAlchFortifyHeavyArmor", "HArmor"), ("MagicAlchFortifyHealth", "HP"),
                ("MagicAlchFortifyIllusion", "ILL"), ("MagicAlchFortifyLightArmor", "LArmor"), ("MagicAlchFortifyLockpicking", "Lock"),
                ("MagicAlchFortifyMagicka", "Mag"), ("MagicAlchFortifyPickPocket", "Pick"), ("MagicAlchFortifyHealRate", "RegenHP"),
                ("MagicAlchFortifyMagickaRate", "RegenMag"), ("MagicAlchFortifyStaminaRate", "RegenStam"), ("MagicAlchFortifyRestoration", "RES"),
                ("MagicAlchResistFire", "ResistFire"), ("MagicAlchResistFrost", "ResistFrost"), ("MagicAlchResistMagic", "ResistMagic"),
                ("MagicAlchResistPoison", "ResistPoison"), ("MagicAlchResistShock", "ResistShock"), ("MagicAlchFortifySmithing", "Smith"),
                ("MagicAlchFortifySneak", "Sneak"), ("MagicAlchFortifyStamina", "Stam"),
            })
            {
                AdaptiveMutationLists.Add((kw, P<IFormListGetter>("ISP_LIST_ALCH_AdaptiveMutationFilter" + list)));
            }

            CanPickpocket = P<IKeywordGetter>("ISP_K_PICK_DaringFingersmithAllowPickpocket");
            NoKnockdowns = P<IKeywordGetter>("PoE_K_NoKnockdowns");
            Legendary = P<IKeywordGetter>("PoE_K_HeroOfAgesLegendaryFoe");
            ArkayMonster = P<IKeywordGetter>("PoE_K_ArkaysJudgementMonster");
            ApexPoints = new[] { null, P<IKeywordGetter>("PoE_K_ApexHunter_pts3"), P<IKeywordGetter>("PoE_K_ApexHunter_pts5"), P<IKeywordGetter>("PoE_K_ApexHunter_pts7") };
            foreach (var t in ApexTypes) Apex[t] = P<IKeywordGetter>("PoE_K_ApexHunter" + t);

            TameAnimal = P<IKeywordGetter>("ISP_K_AnimalTameable");
            TameCreature = P<IKeywordGetter>("ISP_K_CreatureTameable");
            AlwaysFemale = P<IKeywordGetter>("ISP_K_AnimalCreatureAlwaysFemale");
            Rideable = P<IKeywordGetter>("ISP_K_AnimalCreatureRideable");
            HasMilk = P<IKeywordGetter>("ISP_K_TamedCollectMilk");
            HasVenom = P<IKeywordGetter>("ISP_K_TamedCollectVenom");
            RacesFrom = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_FromRaces");
            RacesTo = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_ToRaces");
            TameExcluded = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_excluded");
            MilkSm = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectMilkRaces_Sm");
            MilkMd = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectMilkRaces_Md");
            VenomSpiderSm = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomFrostbiteRaces_Sm");
            VenomSpiderMd = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomFrostbiteRaces_Md");
            VenomSpiderLg = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomFrostbiteRaces_Lg");
            VenomChaurusSm = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomChaurusRaces_Sm");
            VenomChaurusMd = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomChaurusRaces_Md");
            VenomChaurusLg = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomChaurusRaces_Lg");
            VenomNetchSm = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomNetchRaces_Sm");
            VenomNetchLg = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomNetchRaces_Lg");
            VenomScrib = P<IFormListGetter>("ISP_LIST_SPEECH_AnimalTamer_CollectVenomScribRaces");

            if (missing.Count > 0)
            {
                Console.WriteLine($"WARNING: {missing.Count} records were not found, the parts that need them are skipped: {string.Join(", ", missing.OrderBy(m => m))}");
            }
        }

        /// <summary>Record in PathsOfEmpyrean.esp by EditorID (like GetRecordByEDID(empFile, ...)), falling back to the load order.</summary>
        FormKey? P<T>(string edid) where T : class, IMajorRecordGetter
        {
            if (!poeByType.TryGetValue(typeof(T), out var map))
            {
                map = new(StringComparer.OrdinalIgnoreCase);
                var poe = ctx.State.LoadOrder.ContainsKey(PatchContext.PoEKey) ? ctx.State.LoadOrder[PatchContext.PoEKey].Mod : null;
                if (poe != null)
                {
                    foreach (var r in poe.EnumerateMajorRecords<T>())
                    {
                        if (r.EditorID != null) map.TryAdd(r.EditorID, r.FormKey);
                    }
                }
                poeByType[typeof(T)] = map;
            }
            return map.TryGetValue(edid, out var fk) ? fk : V<T>(edid);
        }

        /// <summary>Winning record in the load order by EditorID (vanilla records like ArcaneBlacksmith).</summary>
        FormKey? V<T>(string edid) where T : class, IMajorRecordGetter
        {
            if (!loByType.TryGetValue(typeof(T), out var map))
            {
                map = new(StringComparer.OrdinalIgnoreCase);
                foreach (var r in ctx.State.LoadOrder.PriorityOrder.WinningOverrides<T>())
                {
                    if (r.EditorID != null) map.TryAdd(r.EditorID, r.FormKey);
                }
                loByType[typeof(T)] = map;
            }
            if (map.TryGetValue(edid, out var fk)) return fk;
            missing.Add(edid);
            return null;
        }

        /// <summary>True when every record is present; otherwise logs which part is skipped.</summary>
        public static bool Require(string what, params FormKey?[] records)
        {
            if (records.All(r => r != null)) return true;
            Console.WriteLine($"Skipping {what}: required PoE records are missing.");
            return false;
        }
    }
}
