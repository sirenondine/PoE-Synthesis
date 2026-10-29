using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEAmmoPatcher.pas: Lucky arrows/bolts and Barbed/Broadhead bolts.</summary>
    public class AmmoPatcher
    {
        const string PrefixNameLucky = "Lucky ", SuffixLucky = "_lucky", PrefixTokenLucky = "PoE_LuckyArrowToken_";
        const string DescBarbed = "Target bleeds for <6> seconds, taking extra damage.";
        const string DescBroadhead = "Ignores <50>% of armor.";

        readonly PatchContext ctx;
        readonly Crafting crafting;
        readonly AmmoSettings s;
        int lucky, bolts;

        public AmmoPatcher(PatchContext ctx, Crafting crafting)
        {
            this.ctx = ctx;
            this.crafting = crafting;
            s = ctx.Settings.Ammo;
        }

        public void Run()
        {
            Console.WriteLine("---Ammo Patcher---");
            var p = ctx.PoE;
            bool doLucky = s.LuckyVariants && PoERecords.Require("Lucky ammo", p.Lucky, p.LuckyAmmoList, p.LuckyAmmoRegList, p.LuckyTokenList);
            bool doBolts = s.AdvancedBolts && PoERecords.Require("Barbed/Broadhead bolts", p.Barbed, p.Broadhead, p.PerkAdvancedBolts);

            foreach (var ammo in ctx.State.LoadOrder.PriorityOrder.Ammunition().WinningOverrides().Where(a => ctx.Patchable(a) && ItemChecks.IsValidAmmo(a)).ToList())
            {
                var edid = ammo.EditorID ?? ammo.FormKey.ToString();
                if (doLucky) TryLucky(ammo, edid);

                var craftInfo = ctx.Recipes.CraftingFor(ammo.FormKey) ?? ctx.Recipes.RecipesFor(ammo.FormKey).FirstOrDefault(r => r.Kind != RecipeKind.Temper);
                if (!doBolts || !ItemChecks.IsBolt(ammo) || craftInfo == null) continue;
                var craft = ctx.Cache.Resolve<IConstructibleObjectGetter>(craftInfo.Cobj);
                foreach (var (name, suffix, desc, kw) in new[] { ("Barbed ", "_barbed", DescBarbed, p.Barbed), ("Broadhead ", "_broadhead", DescBroadhead, p.Broadhead) })
                {
                    if (ctx.EdidExists(PatchContext.Prefix + edid + suffix)) continue;
                    var bolt = CreateAdvancedBolt(ammo, craft, name, suffix, desc, kw!.Value);
                    if (bolt != null && doLucky) TryLucky(bolt, edid + suffix);
                }
            }
            Console.WriteLine($"Ammo: {lucky} Lucky versions, {bolts} Barbed/Broadhead bolts");
        }

        void TryLucky(IAmmunitionGetter ammo, string edid)
        {
            var p = ctx.PoE;
            if (!IsRetrievable(ammo))
            {
                ctx.Log($"     Skipped Lucky version of {edid}: can't be picked up");
                return;
            }
            if (ctx.HasKeyword(ammo, p.Lucky) || ctx.InFormList(p.LuckyAmmoRegList, ammo) || ctx.EdidExists(PatchContext.Prefix + edid + SuffixLucky)) return;
            CreateLucky(ammo, edid);
        }

        bool IsRetrievable(IAmmunitionGetter ammo) =>
            ctx.Current<IProjectileGetter>(ammo.Projectile.FormKey) is { } proj && (proj.Flags & Projectile.Flag.CanBePickedUp) != 0;

        void CreateLucky(IAmmunitionGetter rc, string edid)
        {
            var p = ctx.PoE;
            var full = rc.Name?.String ?? "";
            var copy = ctx.Patch.Ammunitions.DuplicateInAsNewRecord(rc);
            copy.EditorID = PatchContext.Prefix + edid + SuffixLucky;
            PatchContext.AddKeyword(copy, p.Lucky);
            copy.Name = PrefixNameLucky + full;
            if (copy.Value > 0) copy.Value += 3;
            CopyProjectile(copy, SuffixLucky, PrefixNameLucky + full);

            var token = ctx.Patch.MiscItems.AddNew(PrefixTokenLucky + edid);
            token.Name = PrefixNameLucky + full + " Token";
            token.Model = new Model();
            token.Model.File = "Clutter\\Coin01.nif";
            token.MajorFlags |= MiscItem.MajorFlag.NonPlayable;
            ctx.RegisterEdid(copy.EditorID);
            ctx.RegisterEdid(token.EditorID!);

            // the three lists are parallel: regular[i] <-> lucky[i] <-> token[i], so always append all three
            AppendToList(p.LuckyAmmoRegList!.Value, rc.FormKey);
            AppendToList(p.LuckyAmmoList!.Value, copy.FormKey);
            AppendToList(p.LuckyTokenList!.Value, token.FormKey);
            lucky++;
            ctx.Log($"     Created Lucky version of {rc.EditorID}");
        }

        void AppendToList(FormKey list, FormKey item)
        {
            var flst = ctx.Patch.FormLists.GetOrAddAsOverride(ctx.Cache.Resolve<IFormListGetter>(list));
            flst.Items.Add(item.ToLink<ISkyrimMajorRecordGetter>());
        }

        void CopyProjectile(Ammunition ammo, string suffix, string name)
        {
            if (ctx.Current<IProjectileGetter>(ammo.Projectile.FormKey) is not { } proj) return;
            var proj2 = ctx.Patch.Projectiles.DuplicateInAsNewRecord(proj);
            proj2.EditorID = PatchContext.Prefix + proj.EditorID + suffix;
            proj2.Name = name;
            ammo.Projectile.SetTo(proj2);
            ctx.RegisterEdid(proj2.EditorID);
        }

        /// <summary>CreateAdvancedBoltVariant: 1 main material + N bolts -> N Barbed/Broadhead bolts, requires Advanced Bolts.</summary>
        IAmmunitionGetter? CreateAdvancedBolt(IAmmunitionGetter rc, IConstructibleObjectGetter craft, string preName, string suffix, string desc, FormKey keyword)
        {
            var main = crafting.MainIngredient(craft);
            if (main == null) return null;
            var edid = rc.EditorID ?? rc.FormKey.ToString();
            var full = rc.Name?.String ?? "";

            var copy = ctx.Patch.Ammunitions.DuplicateInAsNewRecord(rc);
            copy.EditorID = PatchContext.Prefix + edid + suffix;
            copy.Name = preName + full;
            var oldDesc = rc.Description?.String ?? "";
            copy.Description = (oldDesc != "" ? oldDesc + " " : "") + desc;
            PatchContext.AddKeyword(copy, keyword);
            CopyProjectile(copy, suffix, preName + full);
            ctx.RegisterEdid(copy.EditorID);

            int outCount = craft.CreatedObjectCount ?? 1;
            var cobj = crafting.NewRecipe(Crafting.PrefixCraft + edid + suffix, copy.FormKey, outCount,
                new[] { (main.Value.Item, 1), (rc.FormKey, outCount) }, craft.WorkbenchKeyword.FormKey);
            cobj.Conditions.Add(Crafting.PerkCondition(ctx.PoE.PerkAdvancedBolts!.Value, true));
            bolts++;
            ctx.Log($"     Created {preName}version of {edid}");
            return copy;
        }
    }
}
