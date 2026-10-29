using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEAlchemyPatcher.pas: harvest overhaul for flora/trees, Adaptive Mutation lists, Grim Harvest death items.</summary>
    public class AlchemyPatcher
    {
        readonly PatchContext ctx;
        readonly AlchemySettings s;
        readonly Dictionary<string, FormKey> leveledLists = new(StringComparer.OrdinalIgnoreCase);
        int harvest, ingredients, abilities, deathItems;

        public AlchemyPatcher(PatchContext ctx)
        {
            this.ctx = ctx;
            s = ctx.Settings.Alchemy;
        }

        public void Run()
        {
            Console.WriteLine("---Alchemy Patcher---");
            if (s.HarvestOverhaul && PoERecords.Require("harvest overhaul", ctx.PoE.GreenThumb, ctx.PoE.GrimHarvest)) Harvestables();
            if (s.AdaptiveMutationLists || s.NewAdaptiveMutationAbilities) Ingredients();
            if (s.DeathItems && PoERecords.Require("Grim Harvest death items", ctx.PoE.GrimHarvest)) DeathItems();
            Console.WriteLine($"Alchemy: {harvest} harvestables, {ingredients} ingredient list entries, {abilities} new Adaptive Mutation abilities, {deathItems} death items");
        }

        // ---------------- leveled lists ----------------

        /// <summary>Existing UseAll leveled list with this EditorID (from this run or the load order), else a new one.</summary>
        FormKey LeveledList(string edid, IEnumerable<(FormKey Item, short Count)> entries, FormKey? global, byte chanceNone)
        {
            if (leveledLists.TryGetValue(edid, out var known)) return known;
            var existing = ctx.EdidExists(edid)
                ? ctx.State.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides().FirstOrDefault(l => edid.Equals(l.EditorID, StringComparison.OrdinalIgnoreCase))
                : null;
            if (existing != null) return leveledLists[edid] = existing.FormKey;

            var lvli = ctx.Patch.LeveledItems.AddNew(edid);
            lvli.Flags = LeveledItem.Flag.UseAll;
            lvli.ChanceNone = new Percent(chanceNone / 100.0);
            if (global != null) lvli.Global.SetTo(global.Value);
            lvli.Entries = new();
            foreach (var (item, count) in entries)
            {
                lvli.Entries.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Reference = item.ToLink<IItemGetter>(), Level = 1, Count = count } });
            }
            ctx.RegisterEdid(edid);
            return leveledLists[edid] = lvli.FormKey;
        }

        static void AddEntry(LeveledItem list, FormKey item) =>
            (list.Entries ??= new()).Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Reference = item.ToLink<IItemGetter>(), Level = 1, Count = 1 } });

        // ---------------- flora / trees ----------------

        void Harvestables()
        {
            var excluded = s.ExcludedHarvestables.ToHashSet();
            var lo = ctx.State.LoadOrder.PriorityOrder;
            var targets = lo.Flora().WinningOverrides().Select(f => ((IMajorRecordGetter)f, f.Ingredient.FormKey, f.VirtualMachineAdapter != null))
                .Concat(lo.Tree().WinningOverrides().Select(t => ((IMajorRecordGetter)t, t.Ingredient.FormKey, t.VirtualMachineAdapter != null)))
                .Where(x => ctx.Patchable(x.Item1) && !excluded.Contains(x.Item1.FormKey)).ToList();

            foreach (var (rec, pfigKey, hasScript) in targets)
            {
                if (pfigKey.IsNull || !ctx.Cache.TryResolve<IMajorRecordGetter>(pfigKey, out var pfig)) continue;
                if (pfig is ILeveledItemGetter)
                {
                    ctx.Log($"Skipped {PatchContext.Show(rec)}: harvest already gives a leveled list, probably overhauled already");
                    continue;
                }
                bool plantable = PlantableFlora(rec);
                if (pfig is not IIngredientGetter && !plantable) continue;
                if (hasScript || ItemChecks.Has(rec.EditorID, "single") || ItemChecks.Has(rec.EditorID, "nirnroot")) continue;

                // option defaults: animal sources (nests, eggs, dead fish, ...) are extra small and use Grim Harvest
                bool animal = new[] { "egg", "nest", "skeever", "fish", "mudcrab", "dead" }.Any(w => ItemChecks.Has(rec.EditorID, w));
                int size = animal ? 0 : ItemChecks.Has(rec.EditorID, "small") ? 1 : ItemChecks.Has(rec.EditorID, "large") ? 3 : 2;
                var global = animal ? ctx.PoE.GrimHarvest!.Value : ctx.PoE.GreenThumb!.Value;
                if (ProcessHarvestable(rec, pfig, plantable, size, global)) harvest++;
            }
        }

        bool PlantableFlora(IMajorRecordGetter rec) =>
            ctx.PoE.PlantableFlora != null && ctx.Current<IFormListGetter>(ctx.PoE.PlantableFlora.Value) is { } l && l.Items.Any(i => i.FormKey == rec.FormKey);

        bool ProcessHarvestable(IMajorRecordGetter rec, IMajorRecordGetter pfig, bool plantable, int size, FormKey global)
        {
            // Hearthfire plantables whose harvest isn't a plain ingredient only get the Green Thumb bonus
            if (plantable && pfig is not IIngredientGetter)
            {
                if (pfig is ILeveledItemGetter) return false; // filtered above; the xEdit branch for this is unreachable too
                if (pfig is IMiscItemGetter misc)
                {
                    var ingr = ScriptObjectProperty(misc, "BYOHHiddenObjectScript", "itemToAddIngredient")
                        ?? ScriptObjectProperty(misc, "BYOHHiddenObjectScript", "itemToAddPotion");
                    if (ingr == null || !ctx.Cache.TryResolve<IMajorRecordGetter>(ingr.Value, out var found)) return false;
                    pfig = found;
                }
                else return false;
            }

            var (baseAmt, suffix) = size switch { 0 => (1, "_XS"), 1 => (1, "_S"), 3 => (2, "_L"), _ => (2, "") };
            var ingrEdid = pfig.EditorID ?? pfig.FormKey.ToString();
            bool grim = global == ctx.PoE.GrimHarvest;

            var entries = new List<(FormKey, short)> { (pfig.FormKey, (short)baseAmt) };
            if (size > 0)
            {
                var half = LeveledList(PatchContext.Prefix + "LVL_" + ingrEdid + "50", new[] { (pfig.FormKey, (short)1) }, null, 50);
                entries.Add((half, 1));
                if (size == 3) entries.Add((half, 1));
            }
            var bonus = LeveledList(PatchContext.Prefix + "LVL_" + ingrEdid + "P" + suffix + (grim ? "_Grim" : ""), new[] { (pfig.FormKey, (short)1) }, global, 100);
            entries.Add((bonus, 1));
            if (plantable) suffix += "_BYOH";
            var main = LeveledList(PatchContext.Prefix + "LVL_" + rec.EditorID + suffix, entries, null, 0);

            switch (rec)
            {
                case IFloraGetter f: ctx.Patch.Florae.GetOrAddAsOverride(f).Ingredient.SetTo(main); break;
                case ITreeGetter t: ctx.Patch.Trees.GetOrAddAsOverride(t).Ingredient.SetTo(main); break;
            }
            ctx.Log($"Harvest overhaul for {PatchContext.Show(rec)} -> {ctx.Edid(main)}");
            return true;
        }

        FormKey? ScriptObjectProperty(IHaveVirtualMachineAdapterGetter rec, string script, string property)
        {
            var entry = rec.VirtualMachineAdapter?.Scripts.FirstOrDefault(sc => sc.Name.Equals(script, StringComparison.OrdinalIgnoreCase));
            var prop = entry?.Properties.OfType<IScriptObjectPropertyGetter>().FirstOrDefault(pr => pr.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
            return prop == null || prop.Object.IsNull ? null : prop.Object.FormKey;
        }

        // ---------------- ingredients ----------------

        void Ingredients()
        {
            var p = ctx.PoE;
            foreach (var ingr in ctx.State.LoadOrder.PriorityOrder.Ingredient().WinningOverrides().Where(i => ctx.Patchable(i)).ToList())
            {
                foreach (var effect in ingr.Effects)
                {
                    if (!ctx.Cache.TryResolve<IMagicEffectGetter>(effect.BaseEffect.FormKey, out var mgef)) continue;
                    if (!ctx.HasKeyword(mgef, "MagicAlchBeneficial")) continue;

                    bool done = false;
                    if (s.AdaptiveMutationLists)
                    {
                        foreach (var (kw, list) in p.AdaptiveMutationLists)
                        {
                            if (!ctx.HasKeyword(mgef, kw)) continue;
                            if (ctx.AddToFormList(list, ingr)) ingredients++;
                            done = true;
                            break;
                        }
                    }
                    var archetype = mgef.Archetype;
                    if (archetype.ActorValue == ActorValue.ResistDisease || archetype.Type == MagicEffectArchetype.TypeEnum.CureDisease)
                    {
                        if (s.AdaptiveMutationLists && ctx.AddToFormList(p.ResistDiseaseList, ingr)) ingredients++;
                        continue;
                    }
                    if (done || !s.NewAdaptiveMutationAbilities) continue;
                    // new abilities only for effects that come with the ingredient's own plugin
                    if (mgef.FormKey.ModKey != ingr.FormKey.ModKey) continue;
                    NewAdaptiveMutation(ingr, mgef, effect.Data?.Magnitude ?? 0);
                }
            }
        }

        void NewAdaptiveMutation(IIngredientGetter ingr, IMagicEffectGetter eff, float magnitude)
        {
            var av = eff.Archetype.ActorValue;
            if (av is ActorValue.WeaponSpeedMult or ActorValue.ShoutRecoveryMult || eff.VirtualMachineAdapter != null) return;
            var flags = eff.Flags;
            bool fits = eff.Archetype.Type == MagicEffectArchetype.TypeEnum.PeakValueModifier && av != ActorValue.None && magnitude > 0
                && (flags & (MagicEffect.Flag.PowerAffectsMagnitude | MagicEffect.Flag.PowerAffectsDuration)) != 0
                && (flags & (MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoMagnitude)) == 0;
            if (!fits)
            {
                ctx.Log($"     {ingr.EditorID}: effect {eff.EditorID} doesn't fit the Adaptive Mutation ability format");
                return;
            }

            var avName = av.ToString();
            var listEdid = PatchContext.Prefix + "LIST_AdaptiveMutationFilter_" + avName;
            var existingList = FindByEdid<IFormListGetter>(listEdid);
            if (existingList != null)
            {
                if (ctx.AddToFormList(existingList.FormKey, ingr)) ingredients++;
                return;
            }

            var mgefEdid = PatchContext.Prefix + "MGEF_AdaptiveMutation_Fortify" + avName;
            var spellEdid = PatchContext.Prefix + "AB_AdaptiveMutation_Fortify" + avName;
            var mgef = FindByEdid<IMagicEffectGetter>(mgefEdid);
            ISpellGetter? ability = FindByEdid<ISpellGetter>(spellEdid);
            if (mgef == null)
            {
                var m = ctx.Patch.MagicEffects.DuplicateInAsNewRecord(eff);
                m.EditorID = mgefEdid;
                m.CastType = CastType.ConstantEffect;
                m.Keywords = null;
                m.Sounds = null;
                m.HitShader.Clear();
                m.HitEffectArt.Clear();
                m.EnchantShader.Clear();
                m.EnchantVisuals.Clear();
                m.EquipAbility.Clear();
                m.ImageSpaceModifier.Clear();
                var desc = eff.Description?.String ?? "";
                m.Description = desc.Replace(" for <dur> seconds", "", StringComparison.OrdinalIgnoreCase);
                ctx.RegisterEdid(mgefEdid);
                mgef = m;

                var sp = ctx.Patch.Spells.AddNew(spellEdid);
                sp.Name = "Adaptive Mutation";
                sp.MenuDisplayObject.SetTo(ctx.PoE.MagicHatMarker);
                sp.EquipmentType.SetTo(ctx.PoE.EitherHand);
                sp.Flags = SpellDataFlag.IgnoreResistance | SpellDataFlag.NoAbsorbOrReflect;
                sp.Type = SpellType.Ability;
                sp.CastType = CastType.ConstantEffect;
                sp.TargetType = TargetType.Self;
                var spellEffect = new Effect { Data = new EffectData { Magnitude = 10 } };
                spellEffect.BaseEffect.SetTo(m);
                sp.Effects.Add(spellEffect);
                ctx.RegisterEdid(spellEdid);
                ability = sp;
                abilities++;
            }

            var flst = ctx.Patch.FormLists.AddNew(listEdid);
            flst.Items.Add(ingr.FormKey.ToLink<ISkyrimMajorRecordGetter>());
            ctx.RegisterEdid(listEdid);
            ingredients++;
            ctx.AddToFormList(ctx.PoE.AdaptiveMutationAllFilters, flst);
            if (ability != null) ctx.AddToFormList(ctx.PoE.AdaptiveMutationAllAbilities, ability);
            ctx.Log($"     {ingr.EditorID}: new Adaptive Mutation list for {avName}");
        }

        T? FindByEdid<T>(string edid) where T : class, IMajorRecordGetter =>
            ctx.EdidExists(edid) ? ctx.State.LoadOrder.PriorityOrder.WinningOverrides<T>().Concat(ctx.Patch.EnumerateMajorRecords<T>())
                .FirstOrDefault(r => edid.Equals(r.EditorID, StringComparison.OrdinalIgnoreCase)) : null;

        // ---------------- death items ----------------

        void DeathItems()
        {
            var excluded = s.ExcludedDeathItems.Select(l => l.FormKey).ToHashSet();
            var grim = ctx.PoE.GrimHarvest!.Value;
            var seen = new HashSet<FormKey>();
            foreach (var npc in ctx.State.LoadOrder.PriorityOrder.Npc().WinningOverrides().Where(n => ctx.InScope(n) && !n.DeathItem.IsNull).ToList())
            {
                var lvliKey = npc.DeathItem.FormKey;
                // only death items that come from the patched plugins, not vanilla ones PoE already covers
                if (!ctx.InScope(lvliKey) || ctx.HandledByPoE(lvliKey) || excluded.Contains(lvliKey) || !seen.Add(lvliKey)) continue;
                if (!ValidCreature(npc)) { seen.Remove(lvliKey); continue; }
                if (ctx.Current<ILeveledItemGetter>(lvliKey) is not { } lvli) continue;

                // already has a Grim Harvest bonus list
                if (lvli.Entries?.Any(e => ctx.Current<ILeveledItemGetter>(e.Data?.Reference.FormKey ?? FormKey.Null) is { } sub && sub.Global.FormKey == grim) == true) continue;
                var ingrs = lvli.Entries?.Select(e => e.Data?.Reference.FormKey ?? FormKey.Null)
                    .Where(fk => ctx.Current<IIngredientGetter>(fk) != null).ToList() ?? new();
                if (ingrs.Count == 0)
                {
                    ctx.Log($"     Skipped death item {PatchContext.Show(lvli)}: no top-level ingredients, might need manual patching");
                    continue;
                }
                var o = ctx.Patch.LeveledItems.GetOrAddAsOverride(lvli);
                foreach (var ingr in ingrs)
                {
                    var bonus = LeveledList(PatchContext.Prefix + "LVL_" + ctx.Edid(ingr) + "P_Grim", new[] { (ingr, (short)1) }, grim, 100);
                    AddEntry(o, bonus);
                }
                deathItems++;
                ctx.Log($"Grim Harvest added to death item {PatchContext.Show(lvli)}");
            }
        }

        bool ValidCreature(INpcGetter npc)
        {
            if ((npc.Configuration.TemplateFlags & NpcConfiguration.TemplateFlag.Traits) != 0) return false;
            if ((npc.Configuration.Flags & (NpcConfiguration.Flag.Summonable | NpcConfiguration.Flag.IsGhost)) != 0) return false;
            if (ctx.Current<IRaceGetter>(npc.Race.FormKey) is not { } race) return true;
            return !(ctx.HasKeyword(race, "ActorTypeNPC") || ctx.HasKeyword(race, "ActorTypeDwarven") || ctx.HasKeyword(race, "ActorTypeDragon"));
        }
    }
}
