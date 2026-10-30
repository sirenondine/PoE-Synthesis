using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEWeaponArmorPatcher.pas: armor tagging, Meltdown, breakdown, Masterwork, crossbow/longbow variants, bound weapons.</summary>
    public class WeaponArmorPatcher
    {
        readonly PatchContext ctx;
        readonly ItemChecks items;
        readonly Crafting crafting;
        readonly Masterwork masterwork;
        readonly WeaponArmorSettings s;
        readonly List<string> descriptions = new();

        public WeaponArmorPatcher(PatchContext ctx, ItemChecks items, Crafting crafting, Masterwork masterwork)
        {
            this.ctx = ctx;
            this.items = items;
            this.crafting = crafting;
            this.masterwork = masterwork;
            s = ctx.Settings.WeaponsArmor;
        }

        public void Run()
        {
            Console.WriteLine("---Weapon/Armor Patcher---");
            var lo = ctx.State.LoadOrder.PriorityOrder;
            var armors = lo.Armor().WinningOverrides().Where(a => ctx.Patchable(a) && ItemChecks.IsValidArmor(a)).ToList();
            var weapons = lo.Weapon().WinningOverrides().Where(w => ctx.Patchable(w)).ToList();

            if (s.MaterialMastery || s.Presence || s.RoyalHeadgear || s.Conductive) TagArmor(armors);
            if (s.MeltdownConditions) Meltdown();

            // craftable armor / weapons: recipe from anywhere in the load order, item from a patched plugin
            var craftArmor = armors.Where(a => !items.IsClothing(a) && !items.IsJewelry(a) && ctx.Recipes.CraftingFor(a.FormKey) != null).ToList();
            var craftWeapons = weapons.Where(w => ItemChecks.IsValidWeapon(w) && !ItemChecks.IsBound(w) && ctx.Recipes.CraftingFor(w.FormKey) != null).ToList();

            foreach (var a in craftArmor) BreakdownAndMasterwork(a.FormKey);
            if (s.Longbows) Longbows(weapons);
            foreach (var w in craftWeapons) BreakdownAndMasterwork(w.FormKey);
            if (s.CrossbowVariants) Crossbows(weapons);
            if (s.BoundWeapons) BoundWeapons(weapons);

            if (s.CrossbowDescriptions && descriptions.Count > 0) WriteDescriptions();
        }

        // ---------------- keywords ----------------

        void TagArmor(List<IArmorGetter> armors)
        {
            var poe = ctx.PoE;
            var allMastery = poe.MaterialMastery.Values.SelectMany(v => new[] { v.Heavy, v.Light }).Append(poe.MaterialMasteryStealth).ToList();
            int tagged = 0;
            foreach (var a in armors)
            {
                if (items.IsClothing(a) || items.IsJewelry(a) || ItemChecks.HasTemplate(a)) continue;
                var edid = a.EditorID;
                var name = a.Name?.String;
                var add = new List<FormKey?>();

                if (s.Presence && !ctx.HasAnyKeyword(a, new[] { poe.Persuasive, poe.Intimidating, poe.IntimidatingHead }))
                {
                    // armor only gets Intimidating by default: GetIsPersuasive needs clothing
                    if (ItemChecks.SlotBody(a) && items.IsPersuasive(a, edid, name)) add.Add(poe.Persuasive);
                    else if ((ItemChecks.SlotBody(a) || ItemChecks.SlotHeadOrHair(a)) && ItemChecks.IsIntimidating(edid, name))
                    {
                        if (ItemChecks.SlotHeadOrHair(a)) add.Add(poe.IntimidatingHead);
                        if (ItemChecks.SlotBody(a)) add.Add(poe.Intimidating);
                    }
                }
                if (s.Conductive && !ctx.HasKeyword(a, poe.Conductive) && items.IsConductive(a, edid, name)) add.Add(poe.Conductive);
                if (s.RoyalHeadgear && ItemChecks.SlotHeadOrHair(a) && !ctx.HasKeyword(a, poe.Royal) && ItemChecks.IsRoyalHead(edid, name)) add.Add(poe.Royal);
                if (s.MaterialMastery && !ctx.HasAnyKeyword(a, allMastery))
                {
                    var type = items.MaterialMasteryType(a, edid, name);
                    if (type == "Stealth") add.Add(ItemChecks.IsLight(a) ? poe.MaterialMasteryStealth : null);
                    else if (type != null) add.Add(ItemChecks.IsHeavy(a) ? poe.MaterialMastery[type].Heavy : ItemChecks.IsLight(a) ? poe.MaterialMastery[type].Light : null);
                }

                add.RemoveAll(k => k == null);
                if (add.Count == 0) continue;
                var o = ctx.Patch.Armors.GetOrAddAsOverride(a);
                foreach (var k in add) PatchContext.AddKeyword(o, k);
                tagged++;
                ctx.Log($"Tagged {PatchContext.Show(a)}: {string.Join(", ", add.Select(k => ctx.KeywordEdid(k!.Value)))}");
            }
            Console.WriteLine($"Armor tagged with PoE keywords: {tagged}");
        }

        void BoundWeapons(List<IWeaponGetter> weapons)
        {
            if (!PoERecords.Require("bound weapon keyword", ctx.PoE.BoundWeapon)) return;
            int n = 0;
            foreach (var w in weapons.Where(ItemChecks.IsBound))
            {
                if (ctx.HasKeyword(w, ctx.PoE.BoundWeapon)) continue;
                PatchContext.AddKeyword(ctx.Patch.Weapons.GetOrAddAsOverride(w), ctx.PoE.BoundWeapon);
                n++;
            }
            Console.WriteLine($"Bound weapons tagged: {n}");
        }

        // ---------------- recipes ----------------

        void Meltdown()
        {
            int n = 0;
            foreach (var r in ctx.Recipes.All.Where(r => r.Kind == RecipeKind.Breakdown).ToList())
            {
                // recipe or the item it breaks down must come from a patched plugin
                if (ctx.HandledByPoE(r.Cobj) || !(ctx.InScope(r.Cobj) || ctx.InScope(r.FirstInput))) continue;
                var input = ctx.Current<IMajorRecordGetter>(r.FirstInput);
                if (input is IArmorGetter a && (items.IsClothing(a) || items.IsJewelry(a))) continue;
                if (crafting.AddMeltdownCondition(ctx.Cache.Resolve<IConstructibleObjectGetter>(r.Cobj))) n++;
            }
            Console.WriteLine($"Weapon/armor breakdown recipes given the Meltdown condition: {n}");
        }

        void BreakdownAndMasterwork(FormKey item)
        {
            var craftInfo = ctx.Recipes.CraftingFor(item);
            if (craftInfo == null) return;
            var craft = ctx.Cache.Resolve<IConstructibleObjectGetter>(craftInfo.Cobj);
            var temperInfo = ctx.Recipes.TemperFor(item);
            var temper = temperInfo == null ? null : ctx.Cache.Resolve<IConstructibleObjectGetter>(temperInfo.Cobj);
            var rec = ctx.Current<IMajorRecordGetter>(item)!;

            if (s.BreakdownRecipes && crafting.CanCreateBreakdown(rec, false)) crafting.CreateBreakdown(craft);
            if (s.Masterwork && masterwork.Available) masterwork.Process(item, craft, temper);
        }

        // ---------------- longbows (PoELongbowVariant.pas) ----------------

        void Longbows(List<IWeaponGetter> weapons)
        {
            if (!PoERecords.Require("longbow versions", ctx.PoE.Longbow)) return;
            var picked = s.LongbowBows.Where(e => !e.Bow.IsNull).ToDictionary(e => e.Bow.FormKey, e => e.Mesh);
            var bows = weapons.Where(w => w.Data?.AnimationType == WeaponAnimationType.Bow && ItemChecks.IsValidWeapon(w)
                && !ItemChecks.IsEnchanted(w) && !ItemChecks.HasTemplate(w)
                && ctx.CanCopy(w) && (picked.Count == 0 || picked.ContainsKey(w.FormKey))).ToList();
            int n = 0;
            foreach (var bow in bows)
            {
                if (ctx.EdidExists(PatchContext.Prefix + bow.EditorID + "_L") || ctx.HasKeyword(bow, ctx.PoE.Longbow)) continue;
                if (CreateLongbow(bow, picked.GetValueOrDefault(bow.FormKey) ?? "")) n++;
            }
            Console.WriteLine($"Longbow versions created: {n}");
        }

        bool CreateLongbow(IWeaponGetter e, string mesh)
        {
            var craftInfo = ctx.Recipes.CraftingFor(e.FormKey);
            var temperInfo = ctx.Recipes.TemperFor(e.FormKey);
            var edid = e.EditorID;
            var full = e.Name?.String ?? "";

            var shortbow = ctx.Patch.Weapons.GetOrAddAsOverride(e);
            shortbow.Name = ReplaceFirst(full, " Bow", " Shortbow");

            var lb = ctx.Patch.Weapons.DuplicateInAsNewRecord(shortbow);
            ctx.RegisterCopy(lb, e);
            lb.EditorID = PatchContext.Prefix + edid + "_L";
            lb.Name = ReplaceFirst(full, " Bow", " Longbow");
            if (lb.BasicStats != null)
            {
                if (lb.BasicStats.Value > 0) lb.BasicStats.Value = (uint)Math.Truncate(lb.BasicStats.Value * 1.3f + 100);
                if (lb.BasicStats.Weight > 0) lb.BasicStats.Weight = MathF.Truncate(lb.BasicStats.Weight + 4);
                lb.BasicStats.Damage = (ushort)(lb.BasicStats.Damage + 3);
            }
            if (lb.Data != null) lb.Data.Speed = 0.2f + (lb.Data.Speed - 0.5f) / 0.5f * 0.18f;
            if (lb.Critical != null) lb.Critical.Damage = (ushort)(lb.Critical.Damage + 1);
            PatchContext.AddKeyword(lb, ctx.PoE.Longbow);

            if (!lb.FirstPersonModel.IsNull && ctx.Cache.TryResolve<IStaticGetter>(lb.FirstPersonModel.FormKey, out var wnam))
            {
                var wnam2 = ctx.Patch.Statics.DuplicateInAsNewRecord(wnam);
                wnam2.EditorID = wnam.EditorID + "_L";
                if (mesh != "" && wnam2.Model != null) wnam2.Model.File = mesh;
                lb.FirstPersonModel.SetTo(wnam2);
            }
            if (mesh != "" && lb.Model != null) lb.Model.File = mesh;
            ctx.RegisterEdid(lb.EditorID);
            ctx.Log($"     Created longbow version {lb.EditorID}");

            if (craftInfo == null)
            {
                ctx.Log($"     WARNING: no crafting recipe for the longbow version of {edid}: the bow has none");
                return true;
            }
            var craft = ctx.Cache.Resolve<IConstructibleObjectGetter>(craftInfo.Cobj);
            var craft2 = crafting.CopyRecipe(craft, Crafting.PrefixCraft + edid + "_L", lb.FormKey);
            var main = crafting.MainIngredient(craft2);
            if (main != null && craft2.Items != null) craft2.Items[main.Value.Index].Item.Count = main.Value.Count + 1;
            crafting.CreateBreakdown(craft2);

            IConstructibleObjectGetter temper2;
            if (temperInfo != null)
            {
                temper2 = crafting.CopyRecipe(ctx.Cache.Resolve<IConstructibleObjectGetter>(temperInfo.Cobj), Crafting.PrefixTemper + edid + "_L", lb.FormKey);
            }
            else
            {
                // the xEdit script makes an empty stub here; a stub needs an input, so the bow's main material is used
                var input = main?.Item ?? lb.FormKey;
                temper2 = crafting.NewRecipe(Crafting.PrefixTemper + lb.EditorID, lb.FormKey, 1, new[] { (input, 1) }, Crafting.SharpeningWheel);
            }
            if (s.Masterwork && masterwork.Available) masterwork.Process(lb.FormKey, craft2, temper2);
            return true;
        }

        static string ReplaceFirst(string s, string from, string to)
        {
            var i = s.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            return i < 0 ? s : s.Substring(0, i) + to + s.Substring(i + from.Length);
        }

        // ---------------- crossbows (PoECrossbowVariant.pas) ----------------

        const string DescR = "More powerful than a normal crossbow.";
        const string DescSL = "Fired bolts have a chance to knock down the target. Longer reload time.";
        const string DescLW = "Weighs less than a normal crossbow. Faster reload time.";
        const string DescS = "Fires silently and deals additional sneak attack damage. Longer reload time.";
        const string DescRSL = "More powerful than a normal crossbow with a chance to knock down the target. Longer reload time.";
        const string DescRLW = "Lighter and more powerful than a normal crossbow. Faster reload time.";
        const string DescRS = "More powerful than a normal crossbow. Fires silently and deals additional sneak attack damage. Longer reload time.";
        const string DescSLLW = "Weighs less than a normal crossbow. Fired bolts have a chance to knock down the target.";
        const string DescSLS = "Fires silently and deals additional sneak attack damage, with a chance to knock down the target. Much longer reload time.";
        const string DescLWS = "Weighs less than a normal crossbow. Fires silently and deals additional sneak attack damage.";

        [Flags]
        enum Upgrade { None = 0, Recurve = 1, SpringLoaded = 2, LightWeight = 4, Silenced = 8 }

        void Crossbows(List<IWeaponGetter> weapons)
        {
            var p = ctx.PoE;
            if (!PoERecords.Require("crossbow variants", p.Recurve, p.SpringLoaded, p.LightWeight, p.Silenced, p.PerkArbalester, p.PerkMasterRanger,
                    p.Fittings, p.IngotSteel, p.IngotQuicksilver, p.VampireDust, p.DwarvenOil)) return;
            var variantKeywords = new[] { p.Recurve, p.SpringLoaded, p.LightWeight, p.Silenced };
            int n = 0;
            foreach (var cb in weapons.Where(w => w.Data?.AnimationType == WeaponAnimationType.Crossbow && ItemChecks.IsValidWeapon(w)
                         && !ItemChecks.IsEnchanted(w) && !ItemChecks.HasTemplate(w) && ctx.CanCopy(w)))
            {
                if (ctx.HasAnyKeyword(cb, variantKeywords) || ctx.EdidExists(PatchContext.Prefix + cb.EditorID + "_R")) continue;
                var craftInfo = ctx.Recipes.CraftingFor(cb.FormKey);
                if (craftInfo == null)
                {
                    ctx.Log($"WARNING: no crossbow variants for {PatchContext.Show(cb)}: it has no crafting recipe");
                    continue;
                }
                var temperInfo = ctx.Recipes.TemperFor(cb.FormKey);
                CreateCrossbowVariants(ctx.Current<IWeaponGetter>(cb.FormKey)!,
                    ctx.Current<IConstructibleObjectGetter>(craftInfo.Cobj)!,
                    temperInfo == null ? null : ctx.Current<IConstructibleObjectGetter>(temperInfo.Cobj));
                n++;
            }
            Console.WriteLine($"Crossbows given variants: {n}");
        }

        void CreateCrossbowVariants(IWeaponGetter rc, IConstructibleObjectGetter craft, IConstructibleObjectGetter? temper)
        {
            var edid = rc.EditorID ?? rc.FormKey.ToString();
            var p = ctx.PoE;
            var recurveItems = new List<(FormKey, int)>();
            var main = crafting.MainIngredient(craft);
            if (main != null) recurveItems.Add((main.Value.Item, 1));
            var springItems = new List<(FormKey, int)> { (p.Fittings!.Value, 2), (p.IngotSteel!.Value, 1) };
            var lightItems = new List<(FormKey, int)> { (p.IngotQuicksilver!.Value, 1) };
            var silentItems = new List<(FormKey, int)> { (p.DwarvenOil!.Value, 1), (p.VampireDust!.Value, 1) };
            List<(FormKey, int)> ItemsFor(Upgrade u) =>
                (u.HasFlag(Upgrade.Recurve) ? recurveItems : new()).Concat(u.HasFlag(Upgrade.SpringLoaded) ? springItems : new())
                .Concat(u.HasFlag(Upgrade.LightWeight) ? lightItems : new()).Concat(u.HasFlag(Upgrade.Silenced) ? silentItems : new()).ToList();

            var singles = new (Upgrade U, string Suffix, string Name, string Desc)[]
            {
                (Upgrade.Recurve, "_R", "Recurve ", DescR),
                (Upgrade.SpringLoaded, "_SL", "Spring-Loaded ", DescSL),
                (Upgrade.LightWeight, "_LW", "Light-Weight ", DescLW),
                (Upgrade.Silenced, "_S", "Silenced ", DescS),
            };
            var made = new Dictionary<Upgrade, IWeaponGetter>();
            foreach (var (u, suffix, name, desc) in singles)
            {
                var v = CreateVariant(rc, suffix, name, u);
                made[u] = v;
                var c2 = CreateVariantCraftRecipe(rc.FormKey, v, ItemsFor(u), edid, suffix, false);
                var t2 = CreateVariantTemperRecipe(v, temper, edid, suffix);
                crafting.CreateBreakdown(c2, v.FormKey);
                if (s.Masterwork && masterwork.Available) masterwork.Process(v.FormKey, c2, t2);
                descriptions.Add(Describe(v, desc));
            }

            // Master Ranger dual upgrades: craftable from the base crossbow, or from either single upgrade
            var duals = new (Upgrade A, Upgrade B, string Suffix, string Name, string Desc)[]
            {
                (Upgrade.Recurve, Upgrade.SpringLoaded, "_RSL", "Spring-Loaded Recurve ", DescRSL),
                (Upgrade.Recurve, Upgrade.LightWeight, "_RLW", "Light-Weight Recurve ", DescRLW),
                (Upgrade.Recurve, Upgrade.Silenced, "_RS", "Silenced Recurve ", DescRS),
                (Upgrade.SpringLoaded, Upgrade.LightWeight, "_SLLW", "Light-Weight Spring-Loaded ", DescSLLW),
                (Upgrade.SpringLoaded, Upgrade.Silenced, "_SLS", "Silenced Spring-Loaded ", DescSLS),
                (Upgrade.LightWeight, Upgrade.Silenced, "_LWS", "Silenced Light-Weight ", DescLWS),
            };
            foreach (var (a, b, suffix, name, desc) in duals)
            {
                var v = CreateVariant(rc, suffix, name, a | b);
                var c2 = CreateVariantCraftRecipe(rc.FormKey, v, ItemsFor(a | b), edid, suffix, true);
                var t2 = CreateVariantTemperRecipe(v, temper, edid, suffix);
                crafting.CreateBreakdown(c2, v.FormKey);
                var mw = s.Masterwork && masterwork.Available ? masterwork.Process(v.FormKey, c2, t2) : null;

                var fromA = CreateVariantCraftRecipe(made[a].FormKey, v, ItemsFor(b), edid, suffix + "1", true);
                if (mw != null) masterwork.CreateRecipes(mw, fromA, null, "_1", createBreakdown: false);
                var fromB = CreateVariantCraftRecipe(made[b].FormKey, v, ItemsFor(a), edid, suffix + "2", true);
                if (mw != null) masterwork.CreateRecipes(mw, fromB, null, "_2", createBreakdown: false);
                descriptions.Add(Describe(v, desc));
            }
            ctx.Log($"     Created crossbow variants of {edid}");
        }

        IWeaponGetter CreateVariant(IWeaponGetter e, string suffix, string namePrefix, Upgrade u)
        {
            var w = ctx.Patch.Weapons.DuplicateInAsNewRecord(e);
            ctx.RegisterCopy(w, e);
            w.EditorID = PatchContext.Prefix + e.EditorID + suffix;
            w.Name = namePrefix + (e.Name?.String ?? "");
            var p = ctx.PoE;
            if (u.HasFlag(Upgrade.Recurve))
            {
                AddValue(w, 40);
                PatchContext.AddKeyword(w, p.Recurve);
            }
            if (u.HasFlag(Upgrade.SpringLoaded))
            {
                if (w.BasicStats != null && w.BasicStats.Weight > 0) w.BasicStats.Weight = MathF.Truncate(w.BasicStats.Weight + 1);
                AddValue(w, 50);
                if (w.Data != null) w.Data.Speed *= 0.85f;
                PatchContext.AddKeyword(w, p.SpringLoaded);
            }
            // the xEdit script applies Silenced before Light-Weight on the silenced light-weight crossbow; the result is the same
            if (u.HasFlag(Upgrade.LightWeight))
            {
                if (w.BasicStats != null && w.BasicStats.Weight > 0) w.BasicStats.Weight = MathF.Truncate(w.BasicStats.Weight * 0.66f);
                AddValue(w, 30);
                if (w.Data != null) w.Data.Speed *= 1.15f;
                PatchContext.AddKeyword(w, p.LightWeight);
            }
            if (u.HasFlag(Upgrade.Silenced))
            {
                AddValue(w, 40);
                if (w.Data != null) w.Data.Speed *= 0.85f;
                PatchContext.AddKeyword(w, p.Silenced);
                AddSilencedScript(w);
            }
            ctx.RegisterEdid(w.EditorID);
            return w;
        }

        static void AddValue(Weapon w, int add)
        {
            if (w.BasicStats != null && w.BasicStats.Value > 0) w.BasicStats.Value = (uint)(w.BasicStats.Value + add);
        }

        void AddSilencedScript(Weapon w)
        {
            w.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            var script = w.VirtualMachineAdapter.Scripts.FirstOrDefault(sc => sc.Name == "IWP_ARCH_CrossbowSilenced");
            if (script == null)
            {
                script = new ScriptEntry { Name = "IWP_ARCH_CrossbowSilenced" };
                w.VirtualMachineAdapter.Scripts.Add(script);
            }
            script.Properties.RemoveAll(pr => pr.Name == "PoE_K_CrossbowSilenced");
            var prop = new ScriptObjectProperty { Name = "PoE_K_CrossbowSilenced", Flags = ScriptProperty.Flag.Edited };
            prop.Object.SetTo(ctx.PoE.Silenced!.Value);
            script.Properties.Add(prop);
        }

        IConstructibleObjectGetter CreateVariantCraftRecipe(FormKey from, IWeaponGetter to, List<(FormKey, int)> addItems, string origEdid, string suffix, bool dual)
        {
            var inputs = new List<(FormKey, int)> { (from, 1) };
            inputs.AddRange(addItems);
            var cobj = crafting.NewRecipe(Crafting.PrefixCraft + origEdid + suffix, to.FormKey, 1, inputs, Crafting.Forge);
            cobj.Conditions.Add(Crafting.PerkCondition(ctx.PoE.PerkArbalester!.Value, true));
            if (dual) cobj.Conditions.Add(Crafting.PerkCondition(ctx.PoE.PerkMasterRanger!.Value, true));
            cobj.Conditions.Add(Crafting.ItemCountCondition(from, CompareOperator.GreaterThanOrEqualTo, 1));
            return cobj;
        }

        IConstructibleObjectGetter? CreateVariantTemperRecipe(IWeaponGetter to, IConstructibleObjectGetter? temper, string origEdid, string suffix)
        {
            if (temper == null)
            {
                ctx.Log($"     WARNING: {origEdid} has no tempering recipe, so its {suffix} variant gets none either");
                return null;
            }
            return crafting.CopyRecipe(temper, Crafting.PrefixTemper + origEdid + suffix, to.FormKey);
        }

        string Describe(IWeaponGetter v, string desc) => $"0x{v.FormKey.ID:X}~{v.FormKey.ModKey.FileName}|{desc}";

        void WriteDescriptions()
        {
            var path = Path.Combine(ctx.State.DataFolderPath, Path.GetFileNameWithoutExtension(ctx.State.PatchMod.ModKey.FileName) + "_DESC.ini");
            File.WriteAllLines(path, descriptions);
            Console.WriteLine($"Wrote {descriptions.Count} crossbow descriptions to {path}");
        }
    }
}
