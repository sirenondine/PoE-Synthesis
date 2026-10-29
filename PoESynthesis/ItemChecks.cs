using System;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis
{
    /// <summary>Item classification ported from SFFunctionsWeaponsArmorClothing.pas and PoEFunctionsWeaponsArmorClothing.pas.</summary>
    public class ItemChecks
    {
        static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        static readonly FormKey[] HumanoidRaces =
            new uint[] { 0x19, 0x13740, 0x13741, 0x13742, 0x13743, 0x13744, 0x13745, 0x13746, 0x13747, 0x13748, 0x13749 }
            .Select(id => new FormKey(Skyrim, id)).ToArray();

        readonly PatchContext ctx;
        public ItemChecks(PatchContext ctx) => this.ctx = ctx;

        public static bool Has(string? s, string sub) => s != null && s.Contains(sub, StringComparison.OrdinalIgnoreCase);
        public static bool Has(string? edid, string? name, string sub) => Has(edid, sub) || Has(name, sub);
        public static string Name(INamedGetter r) => r.Name ?? "";

        // ---------------- armor ----------------

        public bool IsJewelry(IArmorGetter a) =>
            ctx.HasKeyword(a, "ArmorJewelry") || ctx.HasKeyword(a, "ClothingCirclet") || ctx.HasKeyword(a, "ClothingNecklace") || ctx.HasKeyword(a, "ClothingRing");

        public bool IsClothing(IArmorGetter a) => a.BodyTemplate?.ArmorType == ArmorType.Clothing && !IsJewelry(a);

        public static bool IsHeavy(IArmorGetter a) => a.BodyTemplate?.ArmorType == ArmorType.HeavyArmor;
        public static bool IsLight(IArmorGetter a) => a.BodyTemplate?.ArmorType == ArmorType.LightArmor;

        static bool Slot(IArmorGetter a, BipedObjectFlag f) => a.BodyTemplate != null && (a.BodyTemplate.FirstPersonFlags & f) != 0;
        public static bool SlotHead(IArmorGetter a) => Slot(a, BipedObjectFlag.Head);
        public static bool SlotHeadOrHair(IArmorGetter a) => Slot(a, BipedObjectFlag.Head | BipedObjectFlag.Hair | BipedObjectFlag.LongHair);
        public static bool SlotBody(IArmorGetter a) => Slot(a, BipedObjectFlag.Body);
        public static bool SlotHands(IArmorGetter a) => Slot(a, BipedObjectFlag.Hands | BipedObjectFlag.Forearms);
        public static bool SlotFeet(IArmorGetter a) => Slot(a, BipedObjectFlag.Feet | BipedObjectFlag.Calves);
        public static bool SlotShield(IArmorGetter a) => Slot(a, BipedObjectFlag.Shield);
        public static bool SlotCirclet(IArmorGetter a) => Slot(a, BipedObjectFlag.Circlet);

        public static bool IsDummy(IMajorRecordGetter r, string? name) => string.IsNullOrEmpty(name) || Has(r.EditorID, "dummy");

        public static bool IsArmorPlayable(IArmorGetter a) =>
            (a.MajorFlags & Armor.MajorFlag.NonPlayable) == 0
            && (!string.IsNullOrEmpty(a.WorldModel?.Male?.Model?.File.DataRelativePath.Path)
                || !string.IsNullOrEmpty(a.WorldModel?.Female?.Model?.File.DataRelativePath.Path));

        public static bool IsHumanoidArmor(IArmorGetter a) => HumanoidRaces.Contains(a.Race.FormKey);

        public static bool HasTemplate(IArmorGetter a) => !a.TemplateArmor.IsNull;
        public static bool HasTemplate(IWeaponGetter w) => !w.Template.IsNull;
        public static bool IsEnchanted(IArmorGetter a) => !a.ObjectEffect.IsNull;
        public static bool IsEnchanted(IWeaponGetter w) => !w.ObjectEffect.IsNull;

        /// <summary>GetIsValidArmor: named, playable, has a world model, made for the default humanoid races.</summary>
        public static bool IsValidArmor(IArmorGetter a) => !IsDummy(a, a.Name?.String) && IsArmorPlayable(a) && IsHumanoidArmor(a);

        // ---------------- weapons / ammo ----------------

        public static bool IsWeaponPlayable(IWeaponGetter w) =>
            (w.MajorFlags & Weapon.MajorFlag.NonPlayable) == 0
            && (w.Data == null || (w.Data.Flags & (WeaponData.Flag.NonPlayable | WeaponData.Flag.CantDrop)) == 0);

        public static bool IsBound(IWeaponGetter w) => w.Data != null && (w.Data.Flags & WeaponData.Flag.BoundWeapon) != 0;

        /// <summary>GetIsValidWeapon: named, playable, one-handed/two-handed/archery (no staves).</summary>
        public static bool IsValidWeapon(IWeaponGetter w) =>
            !IsDummy(w, w.Name?.String) && IsWeaponPlayable(w)
            && w.Data?.Skill is Skill.OneHanded or Skill.TwoHanded or Skill.Archery;

        public static bool IsValidAmmo(IAmmunitionGetter a) =>
            (a.MajorFlags & Ammunition.MajorFlag.NonPlayable) == 0
            && (a.Flags & Ammunition.Flag.NonPlayable) == 0
            && !a.Projectile.IsNull
            && !string.IsNullOrEmpty(a.Name?.String);

        public static bool IsBolt(IAmmunitionGetter a) => (a.Flags & Ammunition.Flag.NonBolt) == 0;

        public bool IsGem(IMiscItemGetter m) => Has(m.EditorID, "gem") && !Has(m.EditorID, "soul") && ctx.HasKeyword(m, "VendorItemGem");

        public static float Value(IMajorRecordGetter? r) => r switch
        {
            IArmorGetter a => a.Value,
            IWeaponGetter w => w.BasicStats?.Value ?? 0,
            IAmmunitionGetter a => a.Value,
            IBookGetter b => b.Value,
            IIngredientGetter i => i.Value,
            IKeyGetter k => k.Value,
            IScrollGetter s => s.Value,
            ISoulGemGetter s => s.Value,
            IMiscItemGetter m => m.Value,
            IIngestibleGetter i => i.Value,
            _ => 0,
        };

        // ---------------- PoE tagging heuristics (option defaults in the xEdit UI) ----------------

        public bool IsConductive(IArmorGetter a, string? edid, string? name) =>
            ctx.HasKeyword(a, "ArmorMaterialIron") || ctx.HasKeyword(a, "ArmorMaterialIronBanded")
            || ctx.HasKeyword(a, "ArmorMaterialSteel") || ctx.HasKeyword(a, "ArmorMaterialSteelPlate")
            || ctx.HasKeyword(a, "ArmorMaterialDwarven") || ctx.HasKeyword(a, "ArmorMaterialOrcish")
            || ctx.HasKeyword(a, "DLC2ArmorMaterialNordicHeavy") || ctx.HasKeyword(a, "DLC2ArmorMaterialNordicLight")
            || Has(edid, name, "nordic");

        static readonly string[] IntimidatingWords =
            { "necro", "warlock", "daedr", "orcish", "ebony", "dragon", "alduin", "assassin", "darkbroth", "thief", "thieve", "vampire", "dagothur", "thalmor" };

        public static bool IsIntimidating(string? edid, string? name) => IntimidatingWords.Any(w => Has(edid, name, w));

        public bool IsRareClothing(IArmorGetter a, string? edid, string? name)
        {
            if (!IsClothing(a) || ctx.HasKeyword(a, "ClothingPoor")) return false;
            return ctx.HasKeyword(a, "ClothingRich") || Has(edid, "fancy") || Has(name, "fine ")
                || Has(edid, name, "royal") || Has(edid, name, "noble") || Has(edid, name, "jarl");
        }

        public bool IsPersuasive(IArmorGetter a, string? edid, string? name) => !IsIntimidating(edid, name) && IsRareClothing(a, edid, name);

        public static bool IsRoyalHead(string? edid, string? name) =>
            Has(name, " mask") || Has(name, "mask ") || Has(name, "masked ") || Has(name, "-mask")
            || Has(edid, name, "circlet") || Has(edid, name, "crown") || Has(edid, name, "diadem") || Has(edid, name, "tiara");

        public static bool IsFur(string? name) =>
            Has(name, "fur ") || Has(name, "-fur") || Has(name, "fur-") || Has(name, "furry") || Has(name, "furred");

        /// <summary>Material Mastery type in the xEdit dropdown's priority order, or null for N/A.</summary>
        public string? MaterialMasteryType(IArmorGetter a, string? edid, string? name)
        {
            bool validPart = ctx.HasKeyword(a, "ArmorHelmet") || ctx.HasKeyword(a, "ArmorCuirass") || ctx.HasKeyword(a, "ArmorGauntlets") || ctx.HasKeyword(a, "ArmorBoots");
            bool validSlot = SlotHeadOrHair(a) || SlotBody(a) || SlotHands(a) || SlotFeet(a);
            if (!validPart || !validSlot) return null;

            bool K(params string[] kws) => kws.Any(k => ctx.HasKeyword(a, k));
            bool S(params string[] subs) => subs.Any(s => Has(edid, name, s));

            if (K("ArmorMaterialBlades") || S("blades", "akavir", "samurai")) return "Blades";
            if (IsLight(a) && (K("ArmorMaterialThievesGuild", "ArmorMaterialThievesGuildLeader", "ArmorDarkBrotherhood", "ArmorNightingale") || S("thief", "thieve"))) return "Stealth";
            if (K("ArmorMaterialStormcloak", "ArmorMaterialBearStormcloak") || S("stormcloak")) return "Stormcloak";
            if (K("DLC1ArmorMaterialVampire") || S("vampire", "volkihar")) return "Vampire";
            if (K("DLC2ArmorMaterialStalhrimHeavy", "DLC2ArmorMaterialStalhrimLight") || S("stalhrim")) return "Stalhrim";
            if (K("ArmorMaterialForsworn", "ArmorMaterialMS02Forsworn") || S("forsworn", "reachm")) return "Forsworn";
            if (K("ArmorMaterialFalmer", "DLC1ArmorMaterialFalmerHardened", "DLC1ArmorMaterielFalmerHeavy") || S("falmer", "chaurus")) return "Falmer";
            if (K("DLC1ArmorMaterialDawnguard") || S("dawnguard", "paladin", "divine", "crusader", "silver")) return "Dawnguard";
            if (K("DLC2ArmorMaterialBonemoldHeavy", "DLC2ArmorMaterialBonemoldLight") || S("bonemold")) return "Bonemold";
            if (K("DLC2ArmorMaterialChitinHeavy", "DLC2ArmorMaterialChitinLight") || S("chitin")) return "Chitin";
            if (K("ArmorMaterialDaedric") || S("daedr")) return "Daedric";
            if (K("ArmorMaterialDragonplate", "ArmorMaterialDragonscale") || S("dragon", "alduin")) return "Dragon";
            if (K("ArmorMaterialDwarven") || S("dwarven", "dwemer")) return "Dwarven";
            if (K("ArmorMaterialEbony") || S("ebony")) return "Ebony";
            if (K("ArmorMaterialGlass") || S("glass", "crystal")) return "Glass";
            if (K("DLC2ArmorMaterialNordicHeavy") || S("nordic") || Has(edid, "ancientnord") || Has(name, "ancient nord")) return "Nordic";
            if (K("ArmorMaterialOrcish", "ccBGS_ArmorMaterialOrcishLight") || S("orcish")) return "Orcish";
            if (K("ArmorMaterialScaled")) return "Scaled";
            if (K("ArmorMaterialElven", "ArmorMaterialElvenGilded") || S("elven", "thalmor")) return "Elven";
            if (K("ArmorMaterialSteel", "ArmorMaterialSteelPlate", "ArmorMaterialImperialHeavy") || S("steel", "imperial")) return "Steel";
            if (K("ArmorMaterialLeather") || S("leather")) return "Leather";
            if (K("ArmorMaterialIron", "ArmorMaterialIronBanded") || S("iron")) return "Iron";
            if (IsFur(name)) return "Fur";
            if (K("ArmorMaterialHide") || S("hide", "bandit", "brigand")) return "Hide";
            return null;
        }
    }
}
