using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEScrollCrafting.pas: scrolls of tome-taught spells get a "Memorize:" template and scroll-scribing recipes.</summary>
    public class ScrollCrafting
    {
        const string PrefixCraft = "PoE_ScrollScribe_", PrefixTemplate = "PoE_ScrollScribeTemplate_";

        readonly PatchContext ctx;
        readonly Crafting crafting;
        int scrolls;

        public ScrollCrafting(PatchContext ctx, Crafting crafting)
        {
            this.ctx = ctx;
            this.crafting = crafting;
        }

        public void Run()
        {
            var p = ctx.PoE;
            if (!PoERecords.Require("scroll scribing", p.CraftingScroll, p.ScrollTemplateKeyword, p.ScrollTemplate, p.PaperRoll, p.SoulGemDust, p.PerkScrollScribe2)) return;
            var lo = ctx.State.LoadOrder.PriorityOrder;

            // main effect of each patched spell a tome teaches -> that spell (ProcessBOOK_ScrollCrafting)
            var valid = new Dictionary<FormKey, FormKey>();
            foreach (var book in lo.Book().WinningOverrides())
            {
                if (book.Type != Book.BookType.BookOrTome || book.Teaches is not IBookSpellGetter teach) continue;
                if (ctx.Current<ISpellGetter>(teach.Spell.FormKey) is not { } spell || !ctx.InScope(spell) || !SpellPatcher.IsNormalSpell(ctx, spell)) continue;
                if (HighestCostEffect(spell) is { } main) valid.TryAdd(main.FormKey, spell.FormKey);
            }

            foreach (var scroll in lo.Scroll().WinningOverrides().Where(r => SpellPatcher.Owns(ctx, r)).ToList())
            {
                var match = scroll.Effects.Select(e => e.BaseEffect.FormKey).LastOrDefault(valid.ContainsKey);
                if (match.IsNull)
                {
                    ctx.Log($"Skipped scroll crafting for {PatchContext.Show(scroll)}: no effect of a spell a tome teaches");
                    continue;
                }
                var edid = scroll.EditorID ?? scroll.FormKey.ToString();
                if (ctx.EdidExists(PrefixCraft + edid) || ctx.Recipes.RecipesFor(scroll.FormKey).Any(r =>
                        ctx.Current<IConstructibleObjectGetter>(r.Cobj)?.WorkbenchKeyword.FormKey == p.CraftingScroll!.Value))
                {
                    continue;
                }
                Scribe(scroll, edid, ctx.Current<IMagicEffectGetter>(match)!, valid[match]);
            }
            Console.WriteLine($"Scroll scribing: {scrolls} scrolls made craftable");
        }

        void Scribe(IScrollGetter scroll, string edid, IMagicEffectGetter effect, FormKey spell)
        {
            var p = ctx.PoE;
            var name = scroll.Name?.String ?? "";

            // the learnable template: give up one scroll to memorize it
            var baseTemplate = ctx.Current<IMiscItemGetter>(p.ScrollTemplate!.Value)!;
            var templateEdid = baseTemplate.EditorID + "_" + edid;
            if (ctx.EdidExists(templateEdid)) return;
            var template = ctx.Patch.MiscItems.DuplicateInAsNewRecord(baseTemplate);
            template.EditorID = templateEdid;
            template.Name = (baseTemplate.Name?.String ?? "") + name;
            PatchContext.AddKeyword(template, p.ScrollTemplateKeyword);
            ctx.RegisterEdid(templateEdid);
            if (ctx.Settings.Spells.ScrollTemplateDescriptions) ctx.AddDescription(template, $"Learn how to enscribe {name} from memory.");
            var learn = crafting.NewRecipe(PrefixTemplate + edid, template.FormKey, 1, new[] { (scroll.FormKey, 1) }, p.CraftingScroll!.Value);
            learn.Conditions.Add(Crafting.ItemCountCondition(scroll.FormKey, CompareOperator.GreaterThanOrEqualTo, 1));
            learn.Conditions.Add(Crafting.ItemCountCondition(template.FormKey, CompareOperator.EqualTo, 0));

            var o = ctx.Patch.Scrolls.GetOrAddAsOverride(scroll);
            PatchContext.AddKeyword(o, effect.MagicSkill switch
            {
                ActorValue.Alteration => p.SkillAlteration,
                ActorValue.Conjuration => p.SkillConjuration,
                ActorValue.Destruction => p.SkillDestruction,
                ActorValue.Illusion => p.SkillIllusion,
                ActorValue.Restoration => p.SkillRestoration,
                _ => null,
            });

            var level = effect.MinimumSkillLevel;
            var (paper, dust) = level >= 100 ? (3, 15) : level >= 75 ? (2, 10) : level >= 50 ? (2, 6) : level >= 25 ? (1, 3) : (1, 1);
            var inputs = new[] { (p.PaperRoll!.Value, paper), (p.SoulGemDust!.Value, dust) };
            // 2 scrolls, or 3 with Scroll Scribe 2; needs the spell, a scroll or the template
            foreach (var (suffix, count, perk) in new[] { ("", 2, false), ("2", 3, true) })
            {
                var cobj = crafting.NewRecipe(PrefixCraft + edid + suffix, scroll.FormKey, count, inputs, p.CraftingScroll.Value);
                cobj.Conditions.Add(Crafting.PerkCondition(p.PerkScrollScribe2!.Value, perk));
                var hasSpell = new HasSpellConditionData();
                hasSpell.Spell.Link.SetTo(spell);
                cobj.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = hasSpell, Flags = Condition.Flag.OR });
                cobj.Conditions.Add(Crafting.ItemCountCondition(scroll.FormKey, CompareOperator.GreaterThanOrEqualTo, 1, or: true));
                cobj.Conditions.Add(Crafting.ItemCountCondition(template.FormKey, CompareOperator.GreaterThanOrEqualTo, 1, or: true));
            }
            scrolls++;
            ctx.Log($"Added scroll-scribing recipes for {PatchContext.Show(scroll)}");
        }

        /// <summary>GetHighestCostMagicEffect: the effect with the highest base cost x magnitude x area x duration, skipping skill-less effects.</summary>
        IMagicEffectGetter? HighestCostEffect(ISpellGetter spell)
        {
            IMagicEffectGetter? best = null;
            float bestCost = -100;
            foreach (var entry in spell.Effects)
            {
                if (ctx.Current<IMagicEffectGetter>(entry.BaseEffect.FormKey) is not { } eff || eff.MagicSkill == ActorValue.None) continue;
                // a lone zero-cost effect still counts
                if (eff.BaseCost <= 0 && best != null) continue;
                float cost = eff.BaseCost;
                var d = entry.Data;
                if (d != null)
                {
                    if (d.Magnitude > 0) cost *= d.Magnitude;
                    if (d.Area > 0) cost *= d.Area;
                    if (d.Duration > 0) cost *= d.Duration;
                }
                if (cost > bestCost)
                {
                    bestCost = cost;
                    best = eff;
                }
            }
            return best;
        }
    }
}
