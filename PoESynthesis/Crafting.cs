using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace PoESynthesis
{
    public enum RecipeKind { Other, Crafting, Temper, Breakdown }

    public record RecipeInfo(FormKey Cobj, RecipeKind Kind, FormKey Output, FormKey FirstInput);

    /// <summary>Every recipe in the load order, classified like GetIsCraftingRecipe/GetIsTemperRecipe/GetIsBreakdownRecipe.</summary>
    public class RecipeIndex
    {
        readonly PatchContext ctx;
        public readonly List<RecipeInfo> All = new();
        readonly Dictionary<FormKey, List<RecipeInfo>> byOutput = new();
        readonly Dictionary<FormKey, List<RecipeInfo>> byInput = new();

        public RecipeIndex(PatchContext ctx)
        {
            this.ctx = ctx;
            foreach (var cobj in ctx.State.LoadOrder.PriorityOrder.ConstructibleObject().WinningOverrides()) Add(cobj);
            Console.WriteLine("Recipes indexed: " + string.Join(", ", All.GroupBy(r => r.Kind).Select(g => $"{g.Count()} {g.Key}")));
        }

        public void Add(IConstructibleObjectGetter cobj)
        {
            if (cobj.CreatedObject.IsNull) return;
            var first = cobj.Items?.FirstOrDefault()?.Item.Item.FormKey;
            if (first == null || first.Value.IsNull) return;
            if (!ctx.Cache.TryResolve<IMajorRecordGetter>(cobj.CreatedObject.FormKey, out var output)) return;
            ctx.Cache.TryResolve<IMajorRecordGetter>(first.Value, out var input);

            var bench = ctx.KeywordEdid(cobj.WorkbenchKeyword.FormKey) ?? "";
            bool outGear = output is IWeaponGetter or IArmorGetter;
            bool inGear = input is IWeaponGetter or IArmorGetter;
            var kind = RecipeKind.Other;
            if ((bench == "CraftingSmelter" || bench == "CraftingTanningRack") && !outGear && inGear && cobj.Items!.Count == 1)
                kind = RecipeKind.Breakdown;
            else if (bench is "CraftingSmithingForge" or "CraftingSmithingSkyforge" or "CraftingTanningRack")
                kind = RecipeKind.Crafting;
            else if (bench is "CraftingSmithingSharpeningWheel" or "CraftingSmithingArmorTable")
                kind = RecipeKind.Temper;

            var info = new RecipeInfo(cobj.FormKey, kind, output.FormKey, first.Value);
            All.Add(info);
            byOutput.GetOrAdd(info.Output).Add(info);
            byInput.GetOrAdd(info.FirstInput).Add(info);
        }

        public RecipeInfo? CraftingFor(FormKey item) => byOutput.GetValueOrDefault(item)?.FirstOrDefault(r => r.Kind == RecipeKind.Crafting);
        public RecipeInfo? TemperFor(FormKey item) => byOutput.GetValueOrDefault(item)?.FirstOrDefault(r => r.Kind == RecipeKind.Temper);
        public bool HasBreakdown(FormKey item) => byInput.GetValueOrDefault(item)?.Any(r => r.Kind == RecipeKind.Breakdown) == true;
        public IEnumerable<RecipeInfo> RecipesFor(FormKey output) => byOutput.GetValueOrDefault(output) ?? Enumerable.Empty<RecipeInfo>();
        public IEnumerable<RecipeInfo> RecipesUsing(FormKey input) => byInput.GetValueOrDefault(input) ?? Enumerable.Empty<RecipeInfo>();
    }

    /// <summary>Recipe and condition building, ported from SFFunctionsCrafting.pas and PoEFunctionsCrafting.pas.</summary>
    public class Crafting
    {
        public const string PrefixCraft = "PoE_Recipe_";
        public const string PrefixTemper = "PoE_Temper_";
        public const string PrefixBreak = "PoE_Break_";

        static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        public static readonly FormKey Smelter = new(Skyrim, 0x0A5CCE);
        public static readonly FormKey Forge = new(Skyrim, 0x088105);
        public static readonly FormKey TanningRack = new(Skyrim, 0x07866A);
        public static readonly FormKey SharpeningWheel = new(Skyrim, 0x088108);

        readonly PatchContext ctx;
        readonly ItemChecks items;
        public Crafting(PatchContext ctx, ItemChecks items)
        {
            this.ctx = ctx;
            this.items = items;
        }

        // ---------------- conditions ----------------

        public static ConditionFloat PerkCondition(FormKey perk, bool has)
        {
            var data = new HasPerkConditionData();
            data.Perk.Link.SetTo(perk);
            return new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = has ? 1 : 0, Data = data };
        }

        public static ConditionFloat GlobalCondition(FormKey global, CompareOperator op, float value)
        {
            var data = new GetGlobalValueConditionData();
            data.Global.Link.SetTo(global);
            return new ConditionFloat { CompareOperator = op, ComparisonValue = value, Data = data };
        }

        public static ConditionFloat ItemCountCondition(FormKey item, CompareOperator op, float value, bool or = false)
        {
            var data = new GetItemCountConditionData();
            data.ItemOrList.Link.SetTo(item);
            return new ConditionFloat { CompareOperator = op, ComparisonValue = value, Data = data, Flags = or ? Condition.Flag.OR : 0 };
        }

        public static ConditionFloat EquippedCondition(FormKey item, CompareOperator op, float value, bool or = false)
        {
            var data = new GetEquippedConditionData();
            data.ItemOrList.Link.SetTo(item);
            return new ConditionFloat { CompareOperator = op, ComparisonValue = value, Data = data, Flags = or ? Condition.Flag.OR : 0 };
        }

        public static bool HasPerkCondition(IConstructibleObjectGetter cobj, FormKey? perk) =>
            perk != null && cobj.Conditions.Any(c => c.Data is IHasPerkConditionDataGetter d && d.Perk.Link.FormKey == perk.Value);

        public static bool HasGlobalCondition(IConstructibleObjectGetter cobj, FormKey? global) =>
            global != null && cobj.Conditions.Any(c => c.Data is IGetGlobalValueConditionDataGetter d && d.Global.Link.FormKey == global.Value);

        /// <summary>Insert at the top, so OR groups further down keep working (AddPerkConditionAtTop).</summary>
        public static void AddAtTop(ConstructibleObject cobj, Condition c) => cobj.Conditions.Insert(0, c);

        /// <summary>The recipe's GetItemCount >= 1 AND (GetEquipped == 0 OR GetItemCount >= 2) block (AddBreakdownItemCountCondition).</summary>
        public static void AddBreakdownItemCountConditions(ConstructibleObject cobj, FormKey item)
        {
            cobj.Conditions.Add(ItemCountCondition(item, CompareOperator.GreaterThanOrEqualTo, 1));
            cobj.Conditions.Add(EquippedCondition(item, CompareOperator.EqualTo, 0, or: true));
            cobj.Conditions.Add(ItemCountCondition(item, CompareOperator.GreaterThanOrEqualTo, 2));
        }

        // ---------------- recipes ----------------

        public ConstructibleObject NewRecipe(string edid, FormKey output, int outputCount, IEnumerable<(FormKey Item, int Count)> inputs, FormKey workbench)
        {
            var cobj = ctx.Patch.ConstructibleObjects.AddNew(edid);
            cobj.CreatedObject.SetTo(output);
            cobj.CreatedObjectCount = (ushort)outputCount;
            cobj.WorkbenchKeyword.SetTo(workbench);
            cobj.Items = new();
            foreach (var (item, count) in inputs)
            {
                cobj.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = item.ToLink<IItemGetter>(), Count = count } });
            }
            Register(cobj);
            return cobj;
        }

        /// <summary>Copy of an existing recipe as a new record (wbCopyElementToFile(cobj, patch, True, True)).</summary>
        public ConstructibleObject CopyRecipe(IConstructibleObjectGetter source, string edid, FormKey output)
        {
            var cobj = ctx.Patch.ConstructibleObjects.DuplicateInAsNewRecord(source);
            cobj.EditorID = edid;
            cobj.CreatedObject.SetTo(output);
            Register(cobj);
            return cobj;
        }

        public ConstructibleObject Override(IConstructibleObjectGetter cobj) => ctx.Patch.ConstructibleObjects.GetOrAddAsOverride(cobj);

        void Register(ConstructibleObject cobj)
        {
            if (cobj.EditorID != null) ctx.RegisterEdid(cobj.EditorID);
            ctx.Recipes.Add(cobj);
        }

        /// <summary>FindRecipeMainIngredient: the input with the highest count, ties going to the most valuable.</summary>
        public (FormKey Item, int Count, int Index)? MainIngredient(IConstructibleObjectGetter cobj)
        {
            if (cobj.Items == null) return null;
            (FormKey Item, int Count, int Index)? best = null;
            float bestValue = 0;
            for (int i = 0; i < cobj.Items.Count; i++)
            {
                var entry = cobj.Items[i].Item;
                if (!ctx.Cache.TryResolve<IMajorRecordGetter>(entry.Item.FormKey, out var item)) continue;
                if (item is IArmorGetter or IWeaponGetter or IAmmunitionGetter) continue;
                var name = (item as INamedGetter)?.Name;
                if (item.EditorID == "LeatherStrips" || ItemChecks.Has(item.EditorID, name, "strips") || item.EditorID == "Gold001") continue;
                var value = ItemChecks.Value(item);
                if (best == null || entry.Count > best.Value.Count || (entry.Count == best.Value.Count && value >= bestValue))
                {
                    best = (item.FormKey, entry.Count, i);
                    bestValue = value;
                }
            }
            return best;
        }

        public bool CanCreateBreakdown(IMajorRecordGetter item, bool bypassEnchantCheck)
        {
            if (ctx.Recipes.HasBreakdown(item.FormKey)) return false;
            if (bypassEnchantCheck) return true;
            bool enchanted = item switch { IArmorGetter a => ItemChecks.IsEnchanted(a), IWeaponGetter w => ItemChecks.IsEnchanted(w), _ => false };
            bool disallow = item is IKeywordedGetter<IKeywordGetter> k && ctx.HasKeyword(k, "MagicDisallowEnchanting");
            return !disallow || !enchanted;
        }

        /// <summary>CreateBreakdownFromRecipe: smelt/tan the item back into its main ingredient (one fewer), requires Meltdown.</summary>
        public ConstructibleObject? CreateBreakdown(IConstructibleObjectGetter craft, FormKey? itemOverride = null)
        {
            if (!PoERecords.Require("breakdown recipes", ctx.PoE.PerkMeltdown)) return null;
            var itemFk = itemOverride ?? craft.CreatedObject.FormKey;
            if (!ctx.Cache.TryResolve<IMajorRecordGetter>(itemFk, out var item)) return null;
            if (ctx.Recipes.HasBreakdown(itemFk) || !ctx.CanCopy(item)) return null;

            var main = MainIngredient(craft);
            if (main == null)
            {
                ctx.Log($"     Skipped breakdown for {PatchContext.Show(item)}: no valid main ingredient");
                return null;
            }
            if ((craft.Items?.Count ?? 0) <= 1 && main.Value.Count <= 1)
            {
                ctx.Log($"     Skipped breakdown for {PatchContext.Show(item)}: not enough crafting items");
                return null;
            }

            var mainRec = ctx.Cache.Resolve<IMajorRecordGetter>(main.Value.Item);
            var mainEdid = mainRec.EditorID;
            var mainName = (mainRec as INamedGetter)?.Name;
            bool leathery = mainEdid is "Leather01" or "DLC2NetchLeather" or "IWP_MISC_CleanLinen" or "IWP_MISC_CleanedPelt"
                || new[] { "leather", "fur", "pelt", "cloth", "linen" }.Any(s => ItemChecks.Has(mainEdid, mainName, s));

            FormKey station;
            if (item is IArmorGetter armor && items.IsClothing(armor))
                station = ctx.KeywordEdid(craft.WorkbenchKeyword.FormKey) == "CraftingTanningRack" ? TanningRack : Smelter;
            else
                station = leathery ? TanningRack : Smelter;

            var count = Math.Max(1, main.Value.Count - 1);
            var cobj = NewRecipe(PrefixBreak + PatchContext.StripPrefix(item.EditorID), main.Value.Item, count, new[] { (itemFk, 1) }, station);

            // keep only the original HasPerk conditions (minus Tailor), skip GetItemCount declutter conditions
            foreach (var c in craft.Conditions)
            {
                if (c.Data is not IHasPerkConditionDataGetter d) continue;
                if (ctx.PoE.PerkTailor != null && d.Perk.Link.FormKey == ctx.PoE.PerkTailor.Value) continue;
                cobj.Conditions.Add(c.DeepCopy());
            }
            cobj.Conditions.Add(PerkCondition(ctx.PoE.PerkMeltdown!.Value, true));
            bool enchanted = item switch { IArmorGetter a => ItemChecks.IsEnchanted(a), IWeaponGetter w => ItemChecks.IsEnchanted(w), _ => false };
            if (enchanted && ctx.PoE.PerkArcane != null) cobj.Conditions.Add(PerkCondition(ctx.PoE.PerkArcane.Value, true));
            AddBreakdownItemCountConditions(cobj, itemFk);

            ctx.Log($"     Created breakdown recipe -> {cobj.EditorID}");
            return cobj;
        }

        /// <summary>AddMeltdownCondition on an existing breakdown recipe.</summary>
        public bool AddMeltdownCondition(IConstructibleObjectGetter cobj)
        {
            if (ctx.PoE.PerkMeltdown == null || HasPerkCondition(cobj, ctx.PoE.PerkMeltdown)) return false;
            AddAtTop(Override(cobj), PerkCondition(ctx.PoE.PerkMeltdown.Value, true));
            return true;
        }
    }
}
