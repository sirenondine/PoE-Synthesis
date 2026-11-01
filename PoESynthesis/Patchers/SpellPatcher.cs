using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace PoESynthesis.Patchers
{
    /// <summary>
    /// PoESpellPatcher.pas: PoE perk scripts and keywords on spell effects, Sun/Rite/Potent copies, Expanded Glyphs for runes,
    /// non-tome spell tags and the staff enchantment list. Scroll scribing (PoEScrollCrafting.pas) runs from here too.
    /// </summary>
    public class SpellPatcher
    {
        const NpcConfiguration.TemplateFlag PotentTemplateFlags = NpcConfiguration.TemplateFlag.Factions | NpcConfiguration.TemplateFlag.AIData
            | NpcConfiguration.TemplateFlag.AIPackages | NpcConfiguration.TemplateFlag.ModelAnimation | NpcConfiguration.TemplateFlag.DefPackList
            | NpcConfiguration.TemplateFlag.AttackData;

        readonly PatchContext ctx;
        readonly Crafting crafting;
        readonly SpellSettings s;
        readonly Dictionary<FormKey, MagicEffectType> typeOverrides;
        readonly HashSet<FormKey> alwaysPlayer, excluded;
        /// <summary>Spells, scrolls and staff enchantments outside PoE that use each magic effect.</summary>
        readonly Dictionary<FormKey, List<IMajorRecordGetter>> users = new();
        /// <summary>Spells a spell tome teaches.</summary>
        readonly HashSet<FormKey> tomeSpells = new();
        /// <summary>Rune projectile/explosion/decal/impact records -> their 1.5x "_big" copy.</summary>
        readonly Dictionary<FormKey, FormKey> bigCopies = new();
        bool doElements, doSun, doPoison, doSummon, doPotent, doRunes, doTag;
        int patched, copies, potent, runes, tagged, staffs;

        public SpellPatcher(PatchContext ctx, Crafting crafting)
        {
            this.ctx = ctx;
            this.crafting = crafting;
            s = ctx.Settings.Spells;
            typeOverrides = s.EffectTypes.Where(e => !e.Effect.IsNull).GroupBy(e => e.Effect.FormKey).ToDictionary(g => g.Key, g => g.Last().Type);
            alwaysPlayer = s.CasterAlwaysPlayer.Select(l => l.FormKey).ToHashSet();
            excluded = s.ExcludedEffects.Select(l => l.FormKey).ToHashSet();
        }

        /// <summary>Patchable, and not from a plugin an official PoE patch covers (when trusted).</summary>
        public static bool Owns(PatchContext ctx, IMajorRecordGetter r) =>
            ctx.Patchable(r) && !(ctx.Settings.Spells.TrustPoEPatchCoverage && ctx.ReviewedByPoE.Contains(r.FormKey.ModKey));

        public void Run()
        {
            Console.WriteLine("---Spell Patcher---");
            var p = ctx.PoE;
            doElements = s.ElementEffects && PoERecords.Require("Fire/Ice/Shock effects", p.Sear, p.ChainFire, p.ChainFireFrostCool, p.ChainFrost, p.ChainShock,
                p.ChainShockFireCool, p.Chill, p.ChainFrostShockCool, p.Seizure, p.PerkDustToDust, p.SpellDustToDust);
            doSun = s.SunEffects && PoERecords.Require("Sun effects", p.MagicDamageSun, p.PerkDustToDust, p.SpellDustToDust, p.PerkArkaysJudgement, p.ArkayMonster);
            doPoison = s.PoisonDiseaseEffects && PoERecords.Require("Poison/Disease effects", p.MagicDamagePoison, p.MagicDamageDisease, p.RiteOfTheOldGods);
            doSummon = s.SummonEffects && PoERecords.Require("Summon effects", p.MagicSummonFire, p.MagicSummonFrost, p.MagicSummonShock, p.MagicSummonUndead,
                p.MagicSummonFamiliar, p.MagicSummonDaedra, p.AtronachFire, p.AtronachFrost, p.AtronachShock);
            doPotent = doSummon && s.PotentSummons && PoERecords.Require("Potent summons", p.PotentDaedra, p.ElementalPotency);
            doRunes = s.RuneEffects && PoERecords.Require("Rune effects", p.ExpandedGlyphsEffect, p.ExpandedGlyphsAbility);
            doTag = s.TagNonTomeSpells && PoERecords.Require("non-tome spell tags", p.CarefulPrepExclude, p.SpellMasteryExclude);

            IndexUsers();
            IndexTomes();
            foreach (var (fk, type, spell, index) in FindEffects()) Process(fk, type, spell, index);
            Console.WriteLine($"Spell effects: {patched} patched, {copies} Sun/Rite copies, {potent} Potent summons, {runes} runes, {tagged} tagged as not from a tome");

            if (s.ScrollCrafting) new ScrollCrafting(ctx, crafting).Run();
            if (s.StaffEnchantments && PoERecords.Require("staff enchantment list", p.StaffEnchantments)) StaffEnchantments();
        }

        // ---------------- discovery ----------------

        void IndexUsers()
        {
            var lo = ctx.State.LoadOrder.PriorityOrder;
            var all = lo.Spell().WinningOverrides().Select(r => ((IMajorRecordGetter)r, r.Effects))
                .Concat(lo.Scroll().WinningOverrides().Select(r => ((IMajorRecordGetter)r, r.Effects)))
                .Concat(lo.ObjectEffect().WinningOverrides().Where(r => r.EnchantType == ObjectEffect.EnchantTypeEnum.StaffEnchantment)
                    .Select(r => ((IMajorRecordGetter)r, r.Effects)));
            foreach (var (rec, effects) in all)
            {
                if (ctx.PoEPlugins.Contains(rec.FormKey.ModKey) || ctx.HandledByPoE(rec.FormKey)) continue;
                foreach (var fk in effects.Select(e => e.BaseEffect.FormKey).Where(fk => !fk.IsNull).Distinct())
                {
                    users.GetOrAdd(fk).Add(rec);
                }
            }
        }

        void IndexTomes()
        {
            foreach (var book in ctx.State.LoadOrder.PriorityOrder.Book().WinningOverrides())
            {
                if (book.Type != Book.BookType.BookOrTome || book.Teaches is not IBookSpellGetter teach) continue;
                if (ctx.Current<ISpellGetter>(teach.Spell.FormKey) is { } spell && IsNormalSpell(ctx, spell)) tomeSpells.Add(spell.FormKey);
            }
        }

        /// <summary>GetIsNormalSpell: a named, player-castable hand spell with a half-cost perk.</summary>
        public static bool IsNormalSpell(PatchContext ctx, ISpellGetter sp)
        {
            if (sp.Type != SpellType.Spell || string.IsNullOrEmpty(sp.Name?.String) || sp.HalfCostPerk.IsNull) return false;
            var equip = sp.EquipmentType.IsNull ? null : ctx.Edid(sp.EquipmentType.FormKey);
            return equip is "EitherHand" or "BothHands";
        }

        /// <summary>
        /// The options page's list: effects of patched spells, each with the spell it is configured from.
        /// A spell a tome teaches wins over others using the same effect, then the first by EditorID like the xEdit list.
        /// </summary>
        List<(FormKey Effect, MagicEffectType Type, ISpellGetter Spell, int Index)> FindEffects()
        {
            var spells = ctx.State.LoadOrder.PriorityOrder.Spell().WinningOverrides()
                .Where(sp => sp.Type == SpellType.Spell && Owns(ctx, sp))
                .OrderBy(sp => sp.EditorID ?? "", StringComparer.OrdinalIgnoreCase).ToList();
            var order = new List<FormKey>();
            var from = new Dictionary<FormKey, (ISpellGetter Spell, int Index)>();
            foreach (var sp in spells)
            {
                for (int j = 0; j < sp.Effects.Count; j++)
                {
                    var fk = sp.Effects[j].BaseEffect.FormKey;
                    if (fk.IsNull || excluded.Contains(fk)) continue;
                    if (ctx.Current<IMagicEffectGetter>(fk) is not { } eff || !Owns(ctx, eff)) continue;
                    if (!from.TryGetValue(fk, out var cur))
                    {
                        order.Add(fk);
                        from[fk] = (sp, j);
                    }
                    else if (!tomeSpells.Contains(cur.Spell.FormKey) && tomeSpells.Contains(sp.FormKey))
                    {
                        from[fk] = (sp, j);
                    }
                }
            }

            var result = new List<(FormKey, MagicEffectType, ISpellGetter, int)>();
            foreach (var fk in order)
            {
                var eff = ctx.Current<IMagicEffectGetter>(fk)!;
                var type = typeOverrides.TryGetValue(fk, out var t) ? t : Detect(eff);
                if (type == null) continue;
                result.Add((fk, type.Value, from[fk].Spell, from[fk].Index));
            }
            return result;
        }

        // ---------------- processing ----------------

        void Process(FormKey fk, MagicEffectType type, ISpellGetter spell, int index)
        {
            var eff = ctx.Current<IMagicEffectGetter>(fk)!;
            var data = spell.Effects[index].Data;
            bool conc = eff.CastType == CastType.Concentration || (data?.Duration ?? 0) >= 3;
            bool player = alwaysPlayer.Contains(fk);
            ctx.Log($"Processing {PatchContext.Show(eff)} ({type}, from {PatchContext.Show(spell)})");

            // not learned from a tome: likely has special casting conditions (tagged first, so Sun/Rite/Potent copies carry it)
            if (doTag && !users.GetValueOrDefault(fk, new()).Any(u => tomeSpells.Contains(u.FormKey)))
            {
                var o = Eff(fk);
                if (PatchContext.AddKeyword(o, ctx.PoE.CarefulPrepExclude) | PatchContext.AddKeyword(o, ctx.PoE.SpellMasteryExclude))
                {
                    tagged++;
                    ctx.Log("     Not from a tome -> tagged for Careful Preparation/Spell Mastery exclusion");
                }
            }

            bool done = type switch
            {
                MagicEffectType.Fire or MagicEffectType.Ice or MagicEffectType.Shock when doElements => Element(fk, type, conc, player, data?.Magnitude ?? 0),
                MagicEffectType.Sun when doSun => Sun(fk, conc, player),
                MagicEffectType.Poison or MagicEffectType.Disease when doPoison => PoisonDisease(fk, type == MagicEffectType.Disease),
                >= MagicEffectType.SummonUndead when doSummon => Summon(fk, type, spell),
                _ => false,
            };
            if (done) patched++;

            if (doRunes && IsRune(eff)) Rune(fk, spell);
        }

        MagicEffect Eff(FormKey fk) => ctx.Patch.MagicEffects.GetOrAddAsOverride(ctx.Current<IMagicEffectGetter>(fk)!);

        static string EdidOf(IMajorRecordGetter r) => r.EditorID ?? r.FormKey.ToString();

        bool Element(FormKey fk, MagicEffectType type, bool conc, bool player, float magnitude)
        {
            var p = ctx.PoE;
            var eff = Eff(fk);
            ScriptEntry sc;
            if (type == MagicEffectType.Fire)
            {
                sc = Script(eff, "IMP_DES__Fire");
                Obj(sc, "PoE_K_SearingPain", p.Sear);
                Obj(sc, "PoE_K_SpellChaining_Fire", p.ChainFire);
                Obj(sc, "PoE_K_SpellChaining_FireFrost_cool", p.ChainFireFrostCool);
                Obj(sc, "PoE_K_SpellChaining_Frost", p.ChainFrost);
                Obj(sc, "PoE_K_SpellChaining_Shock", p.ChainShock);
                Obj(sc, "PoE_K_SpellChaining_ShockFire_cool", p.ChainShockFireCool);
                Obj(sc, "PoE_PERK_DES_CatchFire", p.PerkCatchFire);
                Obj(sc, "PoE_PERK_DES_ForceOfNature", p.PerkForceNature);
                Obj(sc, "PoE_PERK_DES_Immolate", p.PerkImmolate);
                Obj(sc, "PoE_PERK_DES_SearingPain", p.PerkSear);
                Obj(sc, "PoE_SPELL_DES_CatchFire", p.SpellCatchFire);
                Obj(sc, "PoE_SPELL_DES_Immolate", p.SpellImmolate);
                Obj(sc, "PoE_SPELL_DES_SearingPain", p.SpellSear);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_FireIce", p.SpellChainFireIce);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_FireTimer", p.SpellChainFireTimer);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_ShockFire", p.SpellChainShockFire);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_ShockTimer", p.SpellChainShockTimer);
            }
            else if (type == MagicEffectType.Ice)
            {
                sc = Script(eff, "IMP_DES__Frost");
                Obj(sc, "PoE_K_ChilledToTheBone", p.Chill);
                Obj(sc, "PoE_K_SpellChaining_Fire", p.ChainFire);
                Obj(sc, "PoE_K_SpellChaining_FireFrost_cool", p.ChainFireFrostCool);
                Obj(sc, "PoE_K_SpellChaining_FrostElectric_cool", p.ChainFrostShockCool);
                Obj(sc, "PoE_K_SpellChaining_Shock", p.ChainShock);
                Obj(sc, "PoE_PERK_DES_ChilledToTheBone", p.PerkChill);
                Obj(sc, "PoE_PERK_DES_ForceOfNature", p.PerkForceNature);
                Obj(sc, "PoE_PERK_DES_Frostborn", p.PerkFrostborn);
                Obj(sc, "PoE_PERK_DES_PiercingCold1", p.PerkPierceCold);
                Obj(sc, "PoE_SPELL_DES_ChilledToTheBone", p.SpellChill);
                Obj(sc, "PoE_SPELL_DES_Frostborn", p.SpellFrostborn);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_FireIce", p.SpellChainFireIce);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_FrostTimer", p.SpellChainFrostTimer);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_IceShock", p.SpellChainIceShock);
                Float(sc, "spellMagnitude", magnitude);
            }
            else
            {
                sc = Script(eff, "IMP_DES__Shock");
                Obj(sc, "PoE_K_Seizure_cooldown", p.Seizure);
                Obj(sc, "PoE_K_SpellChaining_Fire", p.ChainFire);
                Obj(sc, "PoE_K_SpellChaining_Frost", p.ChainFrost);
                Obj(sc, "PoE_K_SpellChaining_FrostElectric_cool", p.ChainFrostShockCool);
                Obj(sc, "PoE_K_SpellChaining_ShockFire_cool", p.ChainShockFireCool);
                Obj(sc, "PoE_PERK_DES_ForceOfNature", p.PerkForceNature);
                Obj(sc, "PoE_PERK_DES_Seizure", p.PerkSeizure);
                Obj(sc, "PoE_PERK_DES_StaticDrain", p.PerkStatic);
                Obj(sc, "PoE_SPELL_DES_Seizure", p.SpellSeizure);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_FireTimer", p.SpellChainFireTimer);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_IceShock", p.SpellChainIceShock);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_ShockFire", p.SpellChainShockFire);
                Obj(sc, "PoE_SPELL_DES_SpellChaining_ShockTimer", p.SpellChainShockTimer);
            }
            if (conc) Bool(sc, "IsContinuousSpell", true);
            if (player) Bool(sc, "casterAlwaysPlayer", true);
            if (type == MagicEffectType.Fire) ApplySpell(eff, conc, player);
            ctx.Log($"     Processed for {type}");
            return true;
        }

        void ApplySpell(MagicEffect eff, bool conc, bool player)
        {
            var sc = Script(eff, "IMP_MISC__ApplySpell");
            Obj(sc, "IMP_Perk", ctx.PoE.PerkDustToDust);
            Obj(sc, "IMP_SPELL", ctx.PoE.SpellDustToDust);
            if (conc) Bool(sc, "doRenew", true);
            if (player) Bool(sc, "casterAlwaysPlayer", true);
        }

        /// <summary>Sun keyword and Dust to Dust, plus a half-cost copy that only hits Arkay's Judgement monsters.</summary>
        bool Sun(FormKey fk, bool conc, bool player)
        {
            var p = ctx.PoE;
            var eff = Eff(fk);
            PatchContext.AddKeyword(eff, p.MagicDamageSun);
            ApplySpell(eff, conc, player);

            var edid = EdidOf(eff) + "_monster";
            if (!ctx.EdidExists(edid))
            {
                eff.BaseCost /= 2;
                var m = ctx.Patch.MagicEffects.DuplicateInAsNewRecord(eff);
                m.EditorID = edid;
                m.Description = "";
                m.Conditions.Clear();
                m.Conditions.Add(Cond(PerkData(p.PerkArkaysJudgement!.Value), 1, Condition.RunOnType.Target));
                m.Conditions.Add(Cond(KeywordData(p.ArkayMonster!.Value), 1, or: true));
                foreach (var kw in new[] { p.ActorTypeDaedra, p.ActorTypeDragon }.Where(k => k != null))
                {
                    m.Conditions.Add(Cond(KeywordData(kw!.Value), 1, or: true));
                }
                foreach (var race in new[] { p.RaceWerewolf, p.RaceWerebear, p.RaceHagraven }.Where(r => r != null))
                {
                    var d = new GetIsRaceConditionData();
                    d.Race.Link.SetTo(race!.Value);
                    m.Conditions.Add(Cond(d, 1, or: true));
                }
                ctx.RegisterEdid(edid);
                AddEffectToUsers(fk, m.FormKey);
                copies++;
            }
            ctx.Log("     Processed for Sun");
            return true;
        }

        /// <summary>Damage keyword, no undead/dwarven/daedra restrictions, plus a half-cost copy that ignores resistance under Rite of the Old Gods.</summary>
        bool PoisonDisease(FormKey fk, bool disease)
        {
            var p = ctx.PoE;
            var eff = Eff(fk);
            if (disease)
            {
                PatchContext.AddKeyword(eff, p.MagicDamageDisease);
                eff.Keywords?.RemoveAll(k => k.FormKey == p.MagicDamagePoison!.Value);
            }
            else
            {
                PatchContext.AddKeyword(eff, p.MagicDamagePoison);
            }

            var edid = EdidOf(eff) + "_rite";
            if (!ctx.EdidExists(edid))
            {
                eff.BaseCost /= 2;
                // undead/daedra/machines with resistance >= 100 aren't affected anyway
                for (int i = eff.Conditions.Count - 1; i >= 0; i--)
                {
                    var d = eff.Conditions[i].Data;
                    if (d.RunOnType != Condition.RunOnType.Subject) continue;
                    bool remove = d switch
                    {
                        IHasKeywordConditionDataGetter hk => ctx.KeywordEdid(hk.Keyword.Link.FormKey) is "ActorTypeUndead" or "ActorTypeDwarven" or "ActorTypeDaedra",
                        IIsUndeadConditionDataGetter => true,
                        IGetActorValueConditionDataGetter av => av.ActorValue is ActorValue.PoisonResist or ActorValue.ResistDisease,
                        _ => false,
                    };
                    if (remove) eff.Conditions.RemoveAt(i);
                    else if (d is IGetIsRaceConditionDataGetter)
                    {
                        Console.WriteLine($"WARNING: {PatchContext.Show(eff)} only targets certain races; check it works with Rite of the Old Gods.");
                    }
                }

                var r = ctx.Patch.MagicEffects.DuplicateInAsNewRecord(eff);
                r.EditorID = edid;
                r.Description = "";
                r.ResistValue = ActorValue.None;
                eff.Conditions.Insert(0, Cond(RiteData(), 0, Condition.RunOnType.Target));
                r.Conditions.Insert(0, Cond(RiteData(), 1, Condition.RunOnType.Target));
                ctx.RegisterEdid(edid);
                AddEffectToUsers(fk, r.FormKey);
                copies++;
            }
            ctx.Log("     Processed for Poison/Disease");
            return true;
        }

        HasMagicEffectKeywordConditionData RiteData()
        {
            var d = new HasMagicEffectKeywordConditionData();
            d.Keyword.Link.SetTo(ctx.PoE.RiteOfTheOldGods!.Value);
            return d;
        }

        bool Summon(FormKey fk, MagicEffectType type, ISpellGetter spell)
        {
            var p = ctx.PoE;
            var eff = Eff(fk);
            var sc = Script(eff, "IMP_CON__Summon");
            Obj(sc, "ActorTypeUndead", p.ActorTypeUndead);
            Obj(sc, "PoE_GLO_CON_SpiritGuideActive", p.SpiritGuideActive);
            Obj(sc, "PoE_LIST_CON_SpiritGuide_storedSpellScrollEnch", p.SpiritGuideStoredSpells);
            Obj(sc, "PoE_PERK_CON_Atromancy", p.PerkAtromancy);
            Obj(sc, "PoE_PERK_CON_DarkSouls", p.PerkDarkSouls);
            Obj(sc, "PoE_PERK_CON_FeelNoPain", p.PerkFeelPain);
            Obj(sc, "PoE_PERK_CON_PlagueCarrier", p.PerkPlagueCarrier);
            Obj(sc, "PoE_PERK_CON_SpiritGuide", p.PerkSpiritGuide);
            Obj(sc, "PoE_PERK_CON_TheRelentless", p.PerkRelentless);
            Obj(sc, "PoE_SPELL_CON_DarkSouls", p.SpellDarkSouls);
            Obj(sc, "PoE_SPELL_CON_FeelNoPain", p.SpellFeelPain);
            Obj(sc, "PoE_SPELL_CON_PlagueCarrier", p.SpellPlagueCarrier);
            Obj(sc, "PoE_SPELL_CON_TheRelentless", p.SpellRelentless);
            if (type is MagicEffectType.SummonUndead or MagicEffectType.SummonUndeadGross) Bool(sc, "IsSummonedUndead", true);
            if (type == MagicEffectType.SummonUndeadGross) Bool(sc, "IsGrossZombie", true);

            // familiars and daedra lose the undead/elemental summon keywords
            if (type is MagicEffectType.SummonFamiliar or MagicEffectType.SummonDaedra)
            {
                PatchContext.AddKeyword(eff, type == MagicEffectType.SummonFamiliar ? p.MagicSummonFamiliar : p.MagicSummonDaedra);
                var drop = new[] { p.MagicSummonUndead, p.MagicSummonFire, p.MagicSummonFrost, p.MagicSummonShock }.Select(k => k!.Value).ToHashSet();
                eff.Keywords?.RemoveAll(k => drop.Contains(k.FormKey));
            }
            if (type == MagicEffectType.SummonAtronachFire) PatchContext.AddKeyword(eff, p.AtronachFire);
            if (type == MagicEffectType.SummonAtronachIce) PatchContext.AddKeyword(eff, p.AtronachFrost);
            if (type == MagicEffectType.SummonAtronachShock) PatchContext.AddKeyword(eff, p.AtronachShock);
            ctx.Log("     Processed for Summon");

            if (type < MagicEffectType.SummonFamiliar) return true;
            // a vanilla or PoE-handled actor is shared with spells this patcher doesn't own
            if (ctx.Current<INpcGetter>(eff.Archetype.AssociationKey.FormKey) is not { } npc || !Owns(ctx, npc)) return true;

            var actor = ctx.Patch.Npcs.GetOrAddAsOverride(npc);
            actor.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            var asc = Script(actor.VirtualMachineAdapter, "PoE_SummonedFamiliarActor");
            Obj(asc, "mySpell", spell.FormKey);
            Obj(asc, "MyScroll", FirstUser<IScrollGetter>(fk));
            Obj(asc, "MyStaffEnchant", FirstUser<IObjectEffectGetter>(fk));

            // atronachs with more than one effect are assumed to have their Potent setup already
            if (!doPotent || (type > MagicEffectType.SummonFamiliar && spell.Effects.Count > 1)) return true;
            if (eff.Archetype is not MagicEffectSummonCreatureArchetype) return true;
            var actorEdid = EdidOf(actor) + "_potent";
            var effEdid = EdidOf(eff) + "_potent";
            if (ctx.EdidExists(actorEdid) || ctx.EdidExists(effEdid)) return true;

            var actor2 = ctx.Patch.Npcs.DuplicateInAsNewRecord(actor);
            actor2.EditorID = actorEdid;
            actor2.Name = "Potent " + (actor.Name?.String ?? "");
            PatchContext.AddKeyword(actor2, p.PotentDaedra);
            actor2.Template.SetTo(actor.FormKey);
            actor2.Configuration.TemplateFlags = PotentTemplateFlags;

            eff.BaseCost /= 2;
            var eff2 = ctx.Patch.MagicEffects.DuplicateInAsNewRecord(eff);
            eff2.EditorID = effEdid;
            eff2.Description = "";
            ((MagicEffectSummonCreatureArchetype)eff2.Archetype).Association.SetTo(actor2.FormKey);
            ctx.RegisterEdid(actorEdid);
            ctx.RegisterEdid(effEdid);

            // the normal summon without Daedric Potency, the Potent one with it (the xEdit script leaves this to you)
            AddEffectToUsers(fk, eff2.FormKey, (orig, added) =>
            {
                SetPotencyCondition(orig, 0);
                SetPotencyCondition(added, 1);
            });
            potent++;
            Console.WriteLine($"ATTENTION: created Potent actor {PatchContext.Show(actor2)}, a plain copy of {PatchContext.Show(actor)}: raise its level/health by hand.");
            return true;
        }

        void SetPotencyCondition(Effect e, float value)
        {
            var perk = ctx.PoE.ElementalPotency!.Value;
            e.Conditions.RemoveAll(c => c.Data is IHasPerkConditionDataGetter d && d.Perk.Link.FormKey == perk);
            e.Conditions.Insert(0, Cond(PerkData(perk), value));
        }

        FormKey? FirstUser<T>(FormKey effect) where T : IMajorRecordGetter =>
            users.GetValueOrDefault(effect)?.OfType<T>().OrderByDescending(u => ctx.InScope(u)).Select(u => (FormKey?)u.FormKey).FirstOrDefault();

        /// <summary>
        /// Append <paramref name="added"/> next to every use of <paramref name="orig"/>, keeping the entry's magnitude, area, duration and conditions.
        /// The xEdit script only reaches the first spell, scroll and staff and can't copy the conditions.
        /// </summary>
        void AddEffectToUsers(FormKey orig, FormKey added, Action<Effect, Effect>? tweak = null)
        {
            if (!users.TryGetValue(orig, out var list)) return;
            foreach (var u in list)
            {
                ExtendedList<Effect>? effects = u switch
                {
                    ISpellGetter sp => ctx.Patch.Spells.GetOrAddAsOverride(sp).Effects,
                    IScrollGetter sc => ctx.Patch.Scrolls.GetOrAddAsOverride(sc).Effects,
                    IObjectEffectGetter oe => ctx.Patch.ObjectEffects.GetOrAddAsOverride(oe).Effects,
                    _ => null,
                };
                if (effects == null || effects.Any(e => e.BaseEffect.FormKey == added)) continue;
                foreach (var e in effects.Where(e => e.BaseEffect.FormKey == orig).ToList())
                {
                    var n = e.DeepCopy();
                    n.BaseEffect.SetTo(added);
                    tweak?.Invoke(e, n);
                    effects.Add(n);
                }
                ctx.Log($"     Added {ctx.Edid(added)} to {PatchContext.Show(u)}");
            }
        }

        // ---------------- runes ----------------

        /// <summary>TryProcessMagicEffectRune: a 1.5x bigger projectile/explosion/decal set and the Expanded Glyphs equip ability.</summary>
        void Rune(FormKey fk, ISpellGetter spell)
        {
            var p = ctx.PoE;
            var eff = ctx.Current<IMagicEffectGetter>(fk)!;
            var glyphBase = ctx.Current<IMagicEffectGetter>(p.ExpandedGlyphsEffect!.Value)!;
            var abilityBase = ctx.Current<ISpellGetter>(p.ExpandedGlyphsAbility!.Value)!;
            var glyphEdid = EdidOf(glyphBase) + "_" + EdidOf(spell);
            if (ctx.EdidExists(glyphEdid)) return;

            var proj = ctx.Current<IProjectileGetter>(eff.Projectile.FormKey);
            var exp = proj == null ? null : ctx.Current<IExplosionGetter>(proj.Explosion.FormKey);
            var txt = proj == null ? null : ctx.Current<ITextureSetGetter>(proj.DecalData.FormKey);
            var imds = ctx.Current<IImpactDataSetGetter>(eff.ImpactData.FormKey);
            var missing = proj == null ? "Projectile" : exp == null ? "explosion on its Projectile" : txt?.Decal == null ? "decal data on its Projectile" : imds == null ? "Impact Data" : null;
            if (missing != null)
            {
                Console.WriteLine($"WARNING: {PatchContext.Show(eff)} is tagged MagicRune but has no {missing}; skipped Expanded Glyphs.");
                return;
            }

            var txt2 = Big(txt!, () =>
            {
                var t = ctx.Patch.TextureSets.DuplicateInAsNewRecord(txt!);
                var size = t.Decal!.MinWidth * 1.5f;
                t.Decal.MinWidth = t.Decal.MaxWidth = t.Decal.MinHeight = t.Decal.MaxHeight = size;
                return t;
            });
            var exp2 = Big(exp!, () =>
            {
                var x = ctx.Patch.Explosions.DuplicateInAsNewRecord(exp!);
                x.Radius *= 1.5f;
                x.ISRadius *= 1.5f;
                return x;
            });
            var proj2 = Big(proj!, () =>
            {
                var x = ctx.Patch.Projectiles.DuplicateInAsNewRecord(proj!);
                x.Explosion.SetTo(exp2);
                x.DecalData.SetTo(txt2);
                return x;
            });
            var imds2 = Big(imds!, () =>
            {
                var x = ctx.Patch.ImpactDataSets.DuplicateInAsNewRecord(imds!);
                foreach (var entry in x.Impacts)
                {
                    if (ctx.Current<IImpactGetter>(entry.Impact.FormKey) is not { } imd) continue;
                    entry.Impact.SetTo(Big(imd, () =>
                    {
                        var y = ctx.Patch.Impacts.DuplicateInAsNewRecord(imd);
                        y.TextureSet.SetTo(txt2);
                        return y;
                    }));
                }
                return x;
            });

            var glyph = ctx.Patch.MagicEffects.DuplicateInAsNewRecord(glyphBase);
            glyph.EditorID = glyphEdid;
            ctx.RegisterEdid(glyphEdid);
            var gs = Script(glyph, "PoE_DES_ExpandedGlyphsAb");
            Obj(gs, "runeImpactDataSetBig", imds2);
            Obj(gs, "runeProjectileBig", proj2);
            Obj(gs, "targetRuneSpell", spell.FormKey);

            var o = Eff(fk);
            if (o.EquipAbility.IsNull)
            {
                var ab = ctx.Patch.Spells.DuplicateInAsNewRecord(abilityBase);
                ab.EditorID = EdidOf(abilityBase) + "_" + EdidOf(spell);
                ctx.RegisterEdid(ab.EditorID);
                // PoE's template ability has no effect entry (the xEdit script's write to entry 0 does nothing); PoE's own runes carry one
                if (ab.Effects.Count == 0) ab.Effects.Add(new Effect { Data = new EffectData() });
                ab.Effects[0].BaseEffect.SetTo(glyph);
                o.EquipAbility.SetTo(ab);
            }
            else if (ctx.Current<ISpellGetter>(o.EquipAbility.FormKey) is { } existing)
            {
                var ab = ctx.Patch.Spells.GetOrAddAsOverride(existing);
                var e = new Effect { Data = new EffectData() };
                e.BaseEffect.SetTo(glyph);
                ab.Effects.Add(e);
            }
            runes++;
            ctx.Log("     Processed for Rune (Expanded Glyphs)");
        }

        /// <summary>The record's "_big" copy, made once per run (or the one already in the load order).</summary>
        FormKey Big<TGetter>(TGetter src, Func<IMajorRecord> make) where TGetter : class, IMajorRecordGetter
        {
            if (bigCopies.TryGetValue(src.FormKey, out var fk)) return fk;
            var edid = EdidOf(src) + "_big";
            if (ctx.Cache.TryResolve<TGetter>(edid, out var existing)) return bigCopies[src.FormKey] = existing.FormKey;
            var r = make();
            r.EditorID = edid;
            ctx.RegisterEdid(edid);
            return bigCopies[src.FormKey] = r.FormKey;
        }

        // ---------------- staff enchantments ----------------

        void StaffEnchantments()
        {
            foreach (var oe in ctx.State.LoadOrder.PriorityOrder.ObjectEffect().WinningOverrides())
            {
                if (oe.EnchantType != ObjectEffect.EnchantTypeEnum.StaffEnchantment || oe.Effects.Count == 0 || !Owns(ctx, oe)) continue;
                if (ctx.AddToFormList(ctx.PoE.StaffEnchantments, oe))
                {
                    staffs++;
                    ctx.Log($"Added {PatchContext.Show(oe)} to the staff enchantment list");
                }
            }
            Console.WriteLine($"Staff enchantments: {staffs} added to {ctx.Edid(ctx.PoE.StaffEnchantments!.Value)}");
        }

        // ---------------- type detection (GetMagicEffType) ----------------

        MagicEffectType? Detect(IMagicEffectGetter e)
        {
            var summon = SummonType(e);
            if (summon != null) return summon;
            if (IsElement(e, "MagicDamageFire")) return MagicEffectType.Fire;
            if (IsElement(e, "MagicDamageFrost")) return MagicEffectType.Ice;
            if (IsElement(e, "MagicDamageShock")) return MagicEffectType.Shock;
            if (IsPoison(e)) return MagicEffectType.Poison;
            if (IsDisease(e)) return MagicEffectType.Disease;
            if (IsSun(e)) return MagicEffectType.Sun;
            if (IsRune(e)) return MagicEffectType.NotApplicable;
            return null;
        }

        static bool Detrimental(IMagicEffectGetter e) => (e.Flags & MagicEffect.Flag.Detrimental) != 0;

        static bool ValueModifier(IMagicEffectGetter e) => e.Archetype.Type is MagicEffectArchetype.TypeEnum.ValueModifier
            or MagicEffectArchetype.TypeEnum.DualValueModifier or MagicEffectArchetype.TypeEnum.PeakValueModifier;

        bool IsElement(IMagicEffectGetter e, string keyword) =>
            ctx.HasKeyword(e, keyword) && Detrimental(e) && e.MagicSkill == ActorValue.Destruction && ValueModifier(e) && e.Archetype.ActorValue == ActorValue.Health;

        bool IsPoison(IMagicEffectGetter e)
        {
            if (e.ResistValue != ActorValue.PoisonResist) return false;
            if (ctx.HasKeyword(e, "MAG_MagicDamagePoison") || ctx.HasKeyword(e, "GRIM_K_MagicDamagePoison")) return true;
            // a Touch poison is likely a creature bite or sting
            return Detrimental(e) && e.TargetType != TargetType.Touch && ValueModifier(e) && e.Archetype.ActorValue == ActorValue.Health;
        }

        bool IsDisease(IMagicEffectGetter e)
        {
            if (e.ResistValue != ActorValue.ResistDisease) return false;
            if (ctx.HasKeyword(e, "GRIM_K_MagicDamageDisease")) return true;
            return Detrimental(e) && e.TargetType != TargetType.Touch && (ValueModifier(e) || e.Archetype.Type == MagicEffectArchetype.TypeEnum.Script);
        }

        bool IsSun(IMagicEffectGetter e)
        {
            if (ctx.HasKeyword(e, "MAG_MagicDamageSun") || ctx.HasKeyword(e, "GRIM_K_MagicDamageSun")) return true;
            if (!Detrimental(e) || e.MagicSkill != ActorValue.Restoration || e.ResistValue != ActorValue.None) return false;
            if (!ValueModifier(e) || e.Archetype.ActorValue != ActorValue.Health) return false;
            // only hits undead
            return e.Conditions.Any(c => c.Data.RunOnType == Condition.RunOnType.Subject && c is IConditionFloatGetter f && f.ComparisonValue >= 1
                && (c.Data is IIsUndeadConditionDataGetter || (c.Data is IHasKeywordConditionDataGetter hk && ctx.KeywordEdid(hk.Keyword.Link.FormKey) == "ActorTypeUndead")));
        }

        bool IsRune(IMagicEffectGetter e) => ctx.HasKeyword(e, "MagicRune") && e.TargetType == TargetType.TargetLocation;

        /// <summary>TryGetMagEffSummonType: null when not a summon, NotApplicable when the summon type can't be told.</summary>
        MagicEffectType? SummonType(IMagicEffectGetter e)
        {
            if (e.MagicSkill != ActorValue.Conjuration || e.Archetype.Type != MagicEffectArchetype.TypeEnum.SummonCreature || e.TargetType != TargetType.TargetLocation) return null;
            if (ctx.Current<INpcGetter>(e.Archetype.AssociationKey.FormKey) is not { } creature) return null;
            var edid = creature.EditorID;
            var full = creature.Name?.String;
            var undead = ItemChecks.Has(edid, full, "zombie") ? MagicEffectType.SummonUndeadGross : MagicEffectType.SummonUndead;

            MagicEffectType? FromKeywords(IKeywordedGetter<IKeywordGetter> r)
            {
                if (ctx.HasKeyword(r, "ActorTypeUndead") || ctx.HasKeyword(r, "ActorTypeGhost")) return undead;
                if (ctx.HasKeyword(r, "ActorTypeFamiliar")) return MagicEffectType.SummonFamiliar;
                if (ctx.HasKeyword(r, "ActorTypeDaedra")) return DaedraType(edid, full);
                return null;
            }

            var type = FromKeywords(creature);
            if (type != null) return type;

            // dig through templates that supply traits down to the base actor's race
            var cur = creature;
            for (int guard = 0; guard < 16 && (cur.Configuration.TemplateFlags & NpcConfiguration.TemplateFlag.Traits) != 0; guard++)
            {
                if (cur.Template.IsNull || ctx.Current<INpcGetter>(cur.Template.FormKey) is not { } next) break;
                cur = next;
            }
            if (ctx.Current<IRaceGetter>(cur.Race.FormKey) is { } race && (type = FromKeywords(race)) != null) return type;

            if (ctx.HasKeyword(e, "MagicSummonUndead")) return undead;
            if (ctx.HasKeyword(e, "MagicSummonFamiliar")) return MagicEffectType.SummonFamiliar;
            if (ctx.HasKeyword(e, "MagicSummonFire")) return MagicEffectType.SummonAtronachFire;
            if (ctx.HasKeyword(e, "MagicSummonFrost")) return MagicEffectType.SummonAtronachIce;
            if (ctx.HasKeyword(e, "MagicSummonShock")) return MagicEffectType.SummonAtronachShock;
            Console.WriteLine($"WARNING: {PatchContext.Show(e)} is a summon, but its type can't be told; set it under Spells -> Magic effect types.");
            return MagicEffectType.NotApplicable;
        }

        static MagicEffectType DaedraType(string? edid, string? full)
        {
            bool Has(params string[] subs) => subs.Any(sub => ItemChecks.Has(edid, full, sub));
            if (!Has("atronach", "golem")) return MagicEffectType.SummonDaedra;
            if (Has("fire", "flame")) return MagicEffectType.SummonAtronachFire;
            if (Has("frost", "ice")) return MagicEffectType.SummonAtronachIce;
            if (Has("shock", "lightning", "storm")) return MagicEffectType.SummonAtronachShock;
            return MagicEffectType.SummonAtronachOther;
        }

        // ---------------- scripts and conditions ----------------

        static ScriptEntry Script(MagicEffect r, string name)
        {
            r.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            return Script(r.VirtualMachineAdapter, name);
        }

        /// <summary>GetOrAddScript.</summary>
        static ScriptEntry Script(VirtualMachineAdapter vm, string name)
        {
            var sc = vm.Scripts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (sc == null)
            {
                sc = new ScriptEntry { Name = name };
                vm.Scripts.Add(sc);
            }
            return sc;
        }

        static T Property<T>(ScriptEntry sc, string name) where T : ScriptProperty, new()
        {
            var existing = sc.Properties.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is T t) return t;
            if (existing != null) sc.Properties.Remove(existing);
            t = new T { Name = name };
            sc.Properties.Add(t);
            return t;
        }

        /// <summary>SetScriptPropertyObjectS; a record that isn't in the load order leaves the property alone.</summary>
        static void Obj(ScriptEntry sc, string name, FormKey? value)
        {
            if (value != null) Property<ScriptObjectProperty>(sc, name).Object.SetTo(value.Value);
        }

        static void Bool(ScriptEntry sc, string name, bool value) => Property<ScriptBoolProperty>(sc, name).Data = value;
        static void Float(ScriptEntry sc, string name, float value) => Property<ScriptFloatProperty>(sc, name).Data = value;

        static ConditionFloat Cond(ConditionData data, float value, Condition.RunOnType runOn = Condition.RunOnType.Subject, bool or = false)
        {
            data.RunOnType = runOn;
            return new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = value, Data = data, Flags = or ? Condition.Flag.OR : 0 };
        }

        static HasPerkConditionData PerkData(FormKey perk)
        {
            var d = new HasPerkConditionData();
            d.Perk.Link.SetTo(perk);
            return d;
        }

        static HasKeywordConditionData KeywordData(FormKey kw)
        {
            var d = new HasKeywordConditionData();
            d.Keyword.Link.SetTo(kw);
            return d;
        }
    }
}
