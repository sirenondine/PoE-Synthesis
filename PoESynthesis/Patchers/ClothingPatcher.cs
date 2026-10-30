using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEClothingPatcher.pas: Presence keywords, Tailor/Meltdown conditions, crafting/breakdown recipes, Quality versions.</summary>
    public class ClothingPatcher
    {
        const int CountBodyHead = 7, CountBody = 5, CountHood = 3, CountBoots = 3, CountOther = 2;
        const int QualityAddBody = 26, QualityAddOther = 18;

        readonly PatchContext ctx;
        readonly ItemChecks items;
        readonly Crafting crafting;
        readonly ClothingSettings s;
        readonly HashSet<FormKey> notCraftable;
        int tagged, recipes, quality, conditions;

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

            if (p.PerkTailor != null && (s.TailorConditions || s.BreakdownRecipes))
            {
                foreach (var r in RelevantRecipes(RecipeKind.Crafting, r => r.Output))
                {
                    var cobj = ctx.Cache.Resolve<IConstructibleObjectGetter>(r.Cobj);
                    if (s.TailorConditions && !Crafting.HasPerkCondition(cobj, p.PerkTailor))
                    {
                        AddTailorCondition(crafting.Override(cobj));
                        conditions++;
                    }
                    var item = ctx.Current<IArmorGetter>(r.Output)!;
                    if (s.BreakdownRecipes && crafting.CanCreateBreakdown(item, true)) crafting.CreateBreakdown(ctx.Current<IConstructibleObjectGetter>(r.Cobj)!);
                }
            }

            // un-enchanted first, so their new recipes can be the model for enchanted items that use them as a template
            foreach (var a in clothing.Where(a => !ItemChecks.IsEnchanted(a)).Concat(clothing.Where(ItemChecks.IsEnchanted)))
            {
                ProcessClothing(ctx.Current<IArmorGetter>(a.FormKey)!);
            }
            Console.WriteLine($"Clothing: {tagged} tagged, {conditions} recipe conditions, {recipes} new crafting recipes, {quality} Quality versions");
        }

        /// <summary>Clothing recipes whose own plugin or item is patched, and that PoE has not already handled.</summary>
        IEnumerable<RecipeInfo> RelevantRecipes(RecipeKind kind, Func<RecipeInfo, FormKey> item) =>
            ctx.Recipes.All.Where(r => r.Kind == kind && !ctx.HandledByPoE(r.Cobj) && (ctx.InScope(r.Cobj) || ctx.InScope(item(r)))
                && ctx.Current<IMajorRecordGetter>(item(r)) is IArmorGetter a && items.IsClothing(a)).ToList();

        void AddTailorCondition(ConstructibleObject cobj)
        {
            var output = ctx.Current<IArmorGetter>(cobj.CreatedObject.FormKey);
            bool isQuality = output != null && ctx.HasKeyword(output, ctx.PoE.QualityClothing);
            Crafting.AddAtTop(cobj, Crafting.PerkCondition(ctx.PoE.PerkTailor!.Value, isQuality));
        }

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

            // the xEdit options only offer "Craft" for items without a template that aren't uniquely enchanted
            bool canCraft = ctx.CanCopy(rc) && !ItemChecks.HasTemplate(rc) && !(enchanted && ctx.HasKeyword(rc, "MagicDisallowEnchanting")) && !notCraftable.Contains(rc.FormKey);
            if (!canCraft || p.Cloth == null || p.PerkTailor == null) return;
            // already-craftable clothing is never "rare": it keeps its normal recipe and gets a Quality version
            bool rare = craftInfo == null && items.IsRareClothing(rc, edid, full);

            IConstructibleObjectGetter? cobj = null;
            IArmorGetter craftItem = rc;
            if (craftInfo == null)
            {
                if (!s.CreateCraftingRecipes) return;
                if (enchanted || rare)
                {
                    var made = CreateUnenchantedVersion(rc, enchanted, rare);
                    if (made == null) return;
                    craftItem = made;
                }
                var newRecipe = CreateClothingRecipe(craftItem);
                cobj = newRecipe;
                recipes++;
                ctx.Log($"     Created crafting recipe -> {newRecipe.EditorID}");
                if (s.BreakdownRecipes && p.PerkMeltdown != null)
                {
                    if (crafting.CanCreateBreakdown(craftItem, true)) crafting.CreateBreakdown(newRecipe);
                    // the enchanted original breaks down like its new un-enchanted copy
                    if (enchanted && crafting.CanCreateBreakdown(rc, true)) crafting.CreateBreakdown(newRecipe, rc.FormKey);
                }
            }
            else
            {
                cobj = ctx.Current<IConstructibleObjectGetter>(craftInfo.Cobj);
            }
            if (cobj == null) return;

            // rare clothing is only craftable in Quality form, which it already is
            if (s.QualityVersions && !rare && p.QualityClothing != null)
            {
                var q = CreateQualityVersion(ctx.Current<IArmorGetter>(craftItem.FormKey) ?? craftItem, overwrite: null);
                if (q != null)
                {
                    var qRecipe = crafting.CopyRecipe(cobj, cobj.EditorID + "_Q", q.FormKey);
                    var tailor = qRecipe.Conditions.FirstOrDefault(c => c.Data is IHasPerkConditionDataGetter d && d.Perk.Link.FormKey == p.PerkTailor.Value);
                    if (tailor is ConditionFloat cf) cf.ComparisonValue = 1;
                    else Crafting.AddAtTop(qRecipe, Crafting.PerkCondition(p.PerkTailor.Value, true));
                    if (s.BreakdownRecipes && p.PerkMeltdown != null) crafting.CreateBreakdown(qRecipe);
                }
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

        /// <summary>CreateUnenchantedVersion: craftable copy without the enchantment; rare clothing becomes its Quality version directly.</summary>
        IArmorGetter? CreateUnenchantedVersion(IArmorGetter rc, bool enchanted, bool rare)
        {
            var edid = enchanted ? PatchContext.Prefix + PatchContext.StripPrefix(rc.EditorID) + "_unench" : rc.EditorID;
            if (edid != null && enchanted && ctx.EdidExists(edid)) return null;
            if (rare && ctx.EdidExists(QualityEdid(edid))) return null;

            var copy = ctx.Patch.Armors.DuplicateInAsNewRecord(rc);
            ctx.RegisterCopy(copy, rc, CopyStats.Identity);
            copy.ObjectEffect.Clear();
            copy.EditorID = edid;
            if (rare) return CreateQualityVersion(copy, overwrite: copy);
            ctx.RegisterEdid(copy.EditorID!);
            ctx.Log($"     Created unenchanted version for crafting -> {copy.EditorID}");
            return copy;
        }

        static string QualityEdid(string? edid) => PatchContext.Prefix + PatchContext.StripPrefix(edid) + "_Q";

        /// <summary>CreateQualityVersion: 'Quality ...' copy (or in-place conversion) worth more, tagged SF_K_QualityClothing.</summary>
        IArmorGetter? CreateQualityVersion(IArmorGetter rc, Armor? overwrite)
        {
            var edid = QualityEdid(rc.EditorID);
            if (overwrite == null && ctx.EdidExists(edid)) return null;
            var q = overwrite ?? ctx.Patch.Armors.DuplicateInAsNewRecord(rc);
            var add = ctx.HasKeyword(rc, "ClothingBody") ? QualityAddBody : QualityAddOther;
            if (overwrite == null) ctx.RegisterCopy(q, rc, CopyStats.Quality(add));
            else ctx.ExtendCopy(q, CopyStats.Quality(add));
            q.EditorID = edid;
            PatchContext.AddKeyword(q, ctx.PoE.QualityClothing);
            ctx.RegisterEdid(edid);
            quality++;
            ctx.Log($"     Created Quality version -> {edid}");
            return q;
        }

        /// <summary>CreateClothingRecipe (new recipe): linen by slot, one less for cold/poor or fur items, plus a pelt for fur.</summary>
        ConstructibleObject CreateClothingRecipe(IArmorGetter rc)
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
            AddTailorCondition(cobj);
            return cobj;
        }
    }
}
