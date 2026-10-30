using System;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis
{
    /// <summary>Dragonblood Masterwork versions, ported from PoEMasterworkVariant.pas.</summary>
    public class Masterwork
    {
        const int AddPriceBody = 75, AddPriceHeadShield = 50, AddPriceOther = 30, AddPrice1H = 50, AddPrice2H = 75;
        const int AddArmorBody = 3, AddArmorShield = 3, AddArmorOther = 2, AddDamage1H = 2, AddDamage2H = 3;

        readonly PatchContext ctx;
        readonly Crafting crafting;
        public int Created;

        public Masterwork(PatchContext ctx, Crafting crafting)
        {
            this.ctx = ctx;
            this.crafting = crafting;
        }

        public bool Available => PoERecords.Require("Masterwork versions", ctx.PoE.Masterwork, ctx.PoE.MasterworkActive);

        public static string EdidFor(string? baseEdid) => PatchContext.Prefix + PatchContext.StripPrefix(baseEdid) + "_M";

        /// <summary>A Masterwork version already exists, or the base recipe is already switched off while Masterwork is active.</summary>
        public bool AlreadyDone(IMajorRecordGetter item, IConstructibleObjectGetter? craft) =>
            ctx.EdidExists(EdidFor(item.EditorID)) || (craft != null && Crafting.HasGlobalCondition(craft, ctx.PoE.MasterworkActive));

        /// <summary>ProcessItemForMasterwork: Masterwork item plus crafting, breakdown and tempering recipes.</summary>
        public IMajorRecordGetter? Process(FormKey item, IConstructibleObjectGetter? craft, IConstructibleObjectGetter? temper)
        {
            if (craft == null) return null;
            var current = ctx.Current<IMajorRecordGetter>(item);
            if (current == null || !ctx.CanCopy(current) || AlreadyDone(current, craft)) return null;

            IMajorRecordGetter? mw = current switch
            {
                IArmorGetter a => CreateArmor(a),
                IWeaponGetter w => CreateWeapon(w),
                _ => null,
            };
            if (mw == null) return null;
            CreateRecipes(mw, craft, temper, "", createBreakdown: true);
            return mw;
        }

        /// <summary>CreateRecipesForMasterwork.</summary>
        public void CreateRecipes(IMajorRecordGetter mw, IConstructibleObjectGetter craft, IConstructibleObjectGetter? temper, string suffix, bool createBreakdown)
        {
            craft = ctx.Current<IConstructibleObjectGetter>(craft.FormKey) ?? craft;
            var mwCraft = crafting.CopyRecipe(craft, Crafting.PrefixCraft + PatchContext.StripPrefix(mw.EditorID) + suffix, mw.FormKey);
            AddMasterworkCondition(mwCraft, active: true);
            if (createBreakdown) crafting.CreateBreakdown(mwCraft);

            if (temper != null)
            {
                temper = ctx.Current<IConstructibleObjectGetter>(temper.FormKey) ?? temper;
                crafting.CopyRecipe(temper, Crafting.PrefixTemper + PatchContext.StripPrefix(mw.EditorID) + suffix, mw.FormKey);
            }
            else
            {
                ctx.Log($"     WARNING: no tempering recipe for the Masterwork version of {mw.EditorID}: the base item has none to copy");
            }

            // base recipe is suppressed while Masterwork is active
            if (!Crafting.HasGlobalCondition(craft, ctx.PoE.MasterworkActive))
            {
                AddMasterworkCondition(crafting.Override(craft), active: false);
            }
        }

        void AddMasterworkCondition(ConstructibleObject cobj, bool active) =>
            Crafting.AddAtTop(cobj, Crafting.GlobalCondition(ctx.PoE.MasterworkActive!.Value, active ? CompareOperator.GreaterThan : CompareOperator.EqualTo, 0));

        IArmorGetter? CreateArmor(IArmorGetter rc)
        {
            if (!ItemChecks.IsValidArmor(rc))
            {
                ctx.Log($"     Skipped Masterwork version of {rc.EditorID}: not a valid armor");
                return null;
            }
            int addPrice, addArmor;
            if (ctx.HasKeyword(rc, "ArmorCuirass")) (addPrice, addArmor) = (AddPriceBody, AddArmorBody);
            else if (ctx.HasKeyword(rc, "ArmorHelmet")) (addPrice, addArmor) = (AddPriceHeadShield, AddArmorOther);
            else if (ctx.HasKeyword(rc, "ArmorShield")) (addPrice, addArmor) = (AddPriceHeadShield, AddArmorShield);
            else (addPrice, addArmor) = (AddPriceOther, AddArmorOther);

            var mw = ctx.Patch.Armors.DuplicateInAsNewRecord(rc);
            ctx.RegisterCopy(mw, rc, CopyStats.MasterworkArmor(addPrice, addArmor));
            mw.EditorID = EdidFor(rc.EditorID);
            PatchContext.AddKeyword(mw, ctx.PoE.Masterwork);
            Finish(mw);
            return mw;
        }

        IWeaponGetter? CreateWeapon(IWeaponGetter rc)
        {
            int addDamage, addPrice;
            switch (rc.Data?.Skill)
            {
                case Skill.OneHanded: (addDamage, addPrice) = (AddDamage1H, AddPrice1H); break;
                case Skill.TwoHanded:
                case Skill.Archery: (addDamage, addPrice) = (AddDamage2H, AddPrice2H); break;
                default:
                    ctx.Log($"     Skipped Masterwork version of {rc.EditorID}: not a valid weapon skill");
                    return null;
            }
            // weapons have no Cuirass/Helmet/Shield keyword, so the xEdit script ends up on the "other" price
            addPrice = AddPriceOther;

            var mw = ctx.Patch.Weapons.DuplicateInAsNewRecord(rc);
            ctx.RegisterCopy(mw, rc, CopyStats.MasterworkWeapon(addPrice, addDamage));
            mw.EditorID = EdidFor(rc.EditorID);
            PatchContext.AddKeyword(mw, ctx.PoE.Masterwork);
            Finish(mw);
            return mw;
        }

        void Finish(IMajorRecord mw)
        {
            ctx.RegisterEdid(mw.EditorID!);
            Created++;
            ctx.Log($"     Created Masterwork version: {mw.EditorID}");
        }
    }
}
