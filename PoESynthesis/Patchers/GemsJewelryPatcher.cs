using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEGemsJewelryPatcher.pas: Jeweler conditions, Lithomancy/royal keywords, gem &lt;-&gt; flawless gem recipes.</summary>
    public class GemsJewelryPatcher
    {
        const int AmountCraft = 3, AmountBreak = 2;

        readonly PatchContext ctx;
        readonly ItemChecks items;
        readonly Crafting crafting;
        readonly GemsJewelrySettings s;

        public GemsJewelryPatcher(PatchContext ctx, ItemChecks items, Crafting crafting)
        {
            this.ctx = ctx;
            this.items = items;
            this.crafting = crafting;
            s = ctx.Settings.GemsJewelry;
        }

        public void Run()
        {
            Console.WriteLine("---Gems/Jewelry Patcher---");
            if (s.JewelerConditions) JewelerConditions();
            if (s.GemKeywords || s.CircletRoyal) TagJewelry();
            if (s.GemRecipes) GemRecipes();
        }

        bool IsGem(FormKey fk) => ctx.Current<IMiscItemGetter>(fk) is { } m && items.IsGem(m);

        void JewelerConditions()
        {
            if (!PoERecords.Require("Jeweler conditions", ctx.PoE.PerkJeweler)) return;
            int n = 0;
            foreach (var r in ctx.Recipes.All.Where(r => r.Kind == RecipeKind.Crafting && !ctx.HandledByPoE(r.Cobj)
                         && (ctx.InScope(r.Cobj) || ctx.InScope(r.Output))).ToList())
            {
                if (ctx.Current<IArmorGetter>(r.Output) is not { } jewel || !items.IsJewelry(jewel)) continue;
                var cobj = ctx.Cache.Resolve<IConstructibleObjectGetter>(r.Cobj);
                if (cobj.Items == null || !cobj.Items.Any(i => IsGem(i.Item.Item.FormKey))) continue;
                if (Crafting.HasPerkCondition(cobj, ctx.PoE.PerkJeweler)) continue;
                Crafting.AddAtTop(crafting.Override(cobj), Crafting.PerkCondition(ctx.PoE.PerkJeweler!.Value, true));
                n++;
            }
            Console.WriteLine($"Jewelry recipes with gems given the Jeweler condition: {n}");
        }

        void TagJewelry()
        {
            var p = ctx.PoE;
            var gemKeywords = new[] { p.Ruby, p.Sapphire, p.Emerald, p.Amethyst, p.Garnet, p.Diamond };
            int n = 0;
            foreach (var j in ctx.State.LoadOrder.PriorityOrder.Armor().WinningOverrides()
                         .Where(a => ctx.Patchable(a) && ItemChecks.IsValidArmor(a) && !ItemChecks.HasTemplate(a) && items.IsJewelry(a)).ToList())
            {
                var edid = j.EditorID;
                var name = j.Name?.String;
                var add = new List<FormKey?>();
                if (s.CircletRoyal && ItemChecks.SlotCirclet(j) && !ctx.HasKeyword(j, p.Royal)) add.Add(p.Royal);
                if (s.GemKeywords && !ctx.HasAnyKeyword(j, gemKeywords))
                {
                    if (ItemChecks.Has(edid, name, "ruby")) add.Add(p.Ruby);
                    else if (ItemChecks.Has(edid, name, "sapphire")) add.Add(p.Sapphire);
                    else if (ItemChecks.Has(edid, name, "emerald")) add.Add(p.Emerald);
                    else if (ItemChecks.Has(edid, name, "amethyst") || ItemChecks.Has(edid, name, "moonstone")) add.Add(p.Amethyst);
                    else if (ItemChecks.Has(edid, name, "garnet") || ItemChecks.Has(edid, name, "onyx")) add.Add(p.Garnet);
                    else if (ItemChecks.Has(edid, name, "diamond")) add.Add(p.Diamond);
                    else ctx.Log($"     WARNING: {PatchContext.Show(j)} not tagged for Lithomancy, no gem type in its name or EditorID");
                }
                add.RemoveAll(k => k == null);
                if (add.Count == 0) continue;
                var o = ctx.Patch.Armors.GetOrAddAsOverride(j);
                foreach (var k in add) PatchContext.AddKeyword(o, k);
                n++;
            }
            Console.WriteLine($"Jewelry tagged: {n}");
        }

        void GemRecipes()
        {
            if (!PoERecords.Require("gem recipes", ctx.PoE.PerkJeweler)) return;
            var gems = ctx.State.LoadOrder.PriorityOrder.MiscItem().WinningOverrides().Where(m => ctx.Patchable(m) && items.IsGem(m)).ToList();
            // gems that some recipe already converts to or from another gem are left alone
            bool HasGemRecipe(IMiscItemGetter g) =>
                ctx.Recipes.RecipesFor(g.FormKey).Any(r => IsGem(r.FirstInput)) || ctx.Recipes.RecipesUsing(g.FormKey).Any(r => IsGem(r.Output));
            var regular = gems.Where(g => !ItemChecks.Has(g.EditorID, "flawless")).ToList();
            int n = 0;
            foreach (var flawless in gems.Where(g => ItemChecks.Has(g.EditorID, "flawless")))
            {
                var plain = (flawless.EditorID ?? "").Replace("flawless", "", StringComparison.OrdinalIgnoreCase);
                var gem = regular.FirstOrDefault(g => string.Equals(g.EditorID, plain, StringComparison.OrdinalIgnoreCase));
                if (gem == null || HasGemRecipe(flawless) || HasGemRecipe(gem)) continue;
                if (ctx.EdidExists(Crafting.PrefixCraft + flawless.EditorID)) continue;

                var craft = crafting.NewRecipe(Crafting.PrefixCraft + flawless.EditorID, flawless.FormKey, 1, new[] { (gem.FormKey, AmountCraft) }, Crafting.Smelter);
                craft.Conditions.Add(Crafting.PerkCondition(ctx.PoE.PerkJeweler!.Value, true));
                var brk = crafting.NewRecipe(Crafting.PrefixBreak + flawless.EditorID, gem.FormKey, AmountBreak, new[] { (flawless.FormKey, 1) }, Crafting.Smelter);
                brk.Conditions.Add(Crafting.PerkCondition(ctx.PoE.PerkJeweler!.Value, true));
                ctx.Log($"Created gem recipes {gem.EditorID} <-> {flawless.EditorID}");
                n++;
            }
            Console.WriteLine($"Gem pairs given recipes: {n}");
        }
    }
}
