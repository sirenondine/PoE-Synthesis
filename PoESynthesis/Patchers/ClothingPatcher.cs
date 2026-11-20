using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>
    /// PoEClothingPatcher.pas: Presence keywords, Meltdown conditions, crafting/breakdown recipes. Since v1.4/v1.5 (PoE 1.6+) no
    /// Quality versions are made; a new recipe for rare clothing requires Tailor, other recipes get no Tailor condition.
    /// </summary>
    public class ClothingPatcher
    {
        const int CountBodyHead = 7, CountBody = 5, CountHood = 3, CountBoots = 3, CountOther = 2;

        readonly PatchContext ctx;
        readonly ItemChecks items;
        readonly Crafting crafting;
        readonly ClothingSettings s;
        readonly HashSet<FormKey> notCraftable;
        int tagged, recipes, rareRecipes, conditions, breakdowns;

        public ClothingPatcher(PatchContext ctx, ItemChecks items, Crafting crafting)
        {
            this.ctx = ctx;
            this.items = items;
            this.crafting = crafting;
            s = ctx.Settings.Clothing;
            notCraftable = s.NotCraftable.Select(l => l.FormKey).ToHashSet();
        }

        public void Run()
        {
            Console.WriteLine("---Clothing Patcher---");
            var p = ctx.PoE;
            var clothing = ctx.State.LoadOrder.PriorityOrder.Armor().WinningOverrides()
                .Where(a => ctx.Patchable(a) && ItemChecks.IsValidArmor(a) && items.IsClothing(a)).ToList();

            // existing breakdown recipes first: processing crafting recipes can create new ones
            if (s.MeltdownConditions && p.PerkMeltdown != null)
            {
                foreach (var r in RelevantRecipes(RecipeKind.Breakdown, r => r.FirstInput))
                {
                    if (crafting.AddMeltdownCondition(ctx.Cache.Resolve<IConstructibleObjectGetter>(r.Cobj))) conditions++;
                }
            }

            // existing crafting recipes stay as they are (v1.4: no Tailor condition), they only get a breakdown
            if (s.BreakdownRecipes && p.PerkMeltdown != null)
            {
                foreach (var r in RelevantRecipes(RecipeKind.Crafting, r => r.Output))
                {
                    var item = ctx.Current<IArmorGetter>(r.Output)!;
                    if (crafting.CanCreateBreakdown(item, true) && crafting.CreateBreakdown(ctx.Current<IConstructibleObjectGetter>(r.Cobj)!) != null) breakdowns++;
                }
            }

            // un-enchanted first, so their new recipes can be the model for enchanted items that use them as a template
            foreach (var a in clothing.Where(a => !ItemChecks.IsEnchanted(a)).Concat(clothing.Where(ItemChecks.IsEnchanted)))
            {
                ProcessClothing(ctx.Current<IArmorGetter>(a.FormKey)!);
            }
            Console.WriteLine($"Clothing: {tagged} tagged, {conditions} Meltdown conditions, {breakdowns} breakdowns for existing recipes, {recipes} new crafting recipes ({rareRecipes} of them for rare clothing, requiring Tailor)");
        }

        /// <summary>Clothing recipes whose own plugin or item is patched, and that PoE has not already handled.</summary>
        IEnumerable<RecipeInfo> RelevantRecipes(RecipeKind kind, Func<RecipeInfo, FormKey> item) =>
            ctx.Recipes.All.Where(r => r.Kind == kind && !ctx.HandledByPoE(r.Cobj) && (ctx.InScope(r.Cobj) || ctx.InScope(item(r)))
                && ctx.Current<IMajorRecordGetter>(item(r)) is IArmorGetter a && items.IsClothing(a)).ToList();

        void ProcessClothing(IArmorGetter rc)
        {
            var p = ctx.PoE;
            var edid = rc.EditorID;
            var full = rc.Name?.String;
            bool enchanted = ItemChecks.IsEnchanted(rc);
            var craftInfo = ctx.Recipes.CraftingFor(rc.FormKey);

            if (ItemChecks.HasTemplate(rc))
            {
                // enchanted copy of a craftable item: breakdown from the template's recipe (needs Arcane Blacksmith)
                if (enchanted && s.BreakdownRecipes && p.PerkMeltdown != null)
                {
                    var templateCraft = ctx.Recipes.CraftingFor(rc.TemplateArmor.FormKey);
                    if (templateCraft != null && crafting.CanCreateBreakdown(rc, false))
                        crafting.CreateBreakdown(ctx.Current<IConstructibleObjectGetter>(templateCraft.Cobj)!, rc.FormKey);
                }
                if (craftInfo == null) return;
            }

            if (s.Presence)
            {
                TagPresence(rc, edid, full);
                rc = ctx.Current<IArmorGetter>(rc.FormKey) ?? rc;
            }

            // the xEdit options only offer "Craft" for items without a recipe or template that aren't uniquely enchanted;
            // already-craftable clothing keeps its recipe as it is (v1.4: no Quality versions any more)
            if (craftInfo != null || !s.CreateCraftingRecipes || p.Cloth == null) return;
            if (!ctx.CanCopy(rc) || ItemChecks.HasTemplate(rc) || (enchanted && ctx.HasKeyword(rc, "MagicDisallowEnchanting")) || notCraftable.Contains(rc.FormKey)) return;
            // rare clothing is only craftable with Tailor (v1.5: as itself, no longer as a Quality version)
            bool rare = items.IsRareClothing(rc, edid, full);
            if (rare && p.PerkTailor == null) return;

            IArmorGetter craftItem = rc;
            if (enchanted)
            {
                var made = CreateUnenchantedVersion(rc);
                if (made == null) return;
                craftItem = made;
            }
            var newRecipe = CreateClothingRecipe(craftItem, rare);
            recipes++;
            if (rare) rareRecipes++;
            ctx.Log($"     Created crafting recipe -> {newRecipe.EditorID}");
            if (s.BreakdownRecipes && p.PerkMeltdown != null)
            {
                if (crafting.CanCreateBreakdown(craftItem, true)) crafting.CreateBreakdown(newRecipe);
                // the enchanted original breaks down like its new un-enchanted copy
                if (enchanted && crafting.CanCreateBreakdown(rc, true)) crafting.CreateBreakdown(newRecipe, rc.FormKey);
            }
        }

        void TagPresence(IArmorGetter rc, string? edid, string? full)
        {
            var p = ctx.PoE;
            if (ctx.HasAnyKeyword(rc, new[] { p.Persuasive, p.Intimidating, p.IntimidatingHead })) return;
            var add = new List<FormKey?>();
            if (ItemChecks.SlotBody(rc))
            {
                if (items.IsPersuasive(rc, edid, full)) add.Add(p.Persuasive);
                else if (ItemChecks.IsIntimidating(edid, full))
                {
                    add.Add(p.Intimidating);
                    if (ItemChecks.SlotHeadOrHair(rc)) add.Add(p.IntimidatingHead);
                }
            }
            else if (ItemChecks.SlotHeadOrHair(rc) && ItemChecks.IsIntimidating(edid, full))
            {
                add.Add(p.IntimidatingHead);
            }
            add.RemoveAll(k => k == null);
            if (add.Count == 0) return;
            var o = ctx.Patch.Armors.GetOrAddAsOverride(rc);
            foreach (var k in add) PatchContext.AddKeyword(o, k);
            tagged++;
            ctx.Log($"Tagged {PatchContext.Show(rc)}: {string.Join(", ", add.Select(k => ctx.KeywordEdid(k!.Value)))}");
        }

        /// <summary>CreateUnenchantedVersion: craftable copy without the enchantment.</summary>
        IArmorGetter? CreateUnenchantedVersion(IArmorGetter rc)
        {
            var edid = PatchContext.Prefix + PatchContext.StripPrefix(rc.EditorID) + "_unench";
            if (ctx.EdidExists(edid)) return null;

            var copy = ctx.Patch.Armors.DuplicateInAsNewRecord(rc);
            ctx.RegisterCopy(copy, rc, CopyStats.Identity);
            copy.ObjectEffect.Clear();
            copy.EditorID = edid;
            ctx.RegisterEdid(edid);
            ctx.Log($"     Created unenchanted version for crafting -> {copy.EditorID}");
            return copy;
        }

        /// <summary>CreateClothingRecipe (new recipe): linen by slot, one less for cold/poor or fur items, plus a pelt for fur.</summary>
        ConstructibleObject CreateClothingRecipe(IArmorGetter rc, bool requireTailor)
        {
            var p = ctx.PoE;
            var edid = rc.EditorID;
            var full = rc.Name?.String;
            bool cold = ctx.HasKeyword(rc, "Survival_ArmorCold") || ctx.HasKeyword(rc, "ClothingPoor");
            bool fur = ItemChecks.IsFur(full);

            int count;
            if (ItemChecks.SlotHeadOrHair(rc) && ItemChecks.SlotBody(rc)) count = CountBodyHead;
            else if (ItemChecks.SlotBody(rc)) count = CountBody;
            else if (ItemChecks.SlotHeadOrHair(rc) && ItemChecks.Has(edid, full, "hood")) count = CountHood;
            else if (ItemChecks.SlotFeet(rc) && ItemChecks.Has(edid, full, "boots")) count = CountBoots;
            else count = CountOther;
            if (cold || fur) count--;

            var inputs = new List<(FormKey, int)> { (p.Cloth!.Value, count) };
            if (fur && p.Pelt != null) inputs.Add((p.Pelt.Value, 1));
            var cobj = crafting.NewRecipe(Crafting.PrefixCraft + (edid ?? "").Replace(PatchContext.Prefix, "", StringComparison.OrdinalIgnoreCase),
                rc.FormKey, 1, inputs, Crafting.TanningRack);
            if (requireTailor) Crafting.AddAtTop(cobj, Crafting.PerkCondition(ctx.PoE.PerkTailor!.Value, true));
            return cobj;
        }
    }
}
