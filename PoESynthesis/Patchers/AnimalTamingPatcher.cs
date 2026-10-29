using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEAnimalTamingPatcher.pas: tameable '_Tamed' races for Animal Tamer, Well Trained items and riding, exclusions.</summary>
    public class AnimalTamingPatcher
    {
        readonly PatchContext ctx;
        readonly AnimalTamingSettings s;
        int tamed, excludedActors;

        public AnimalTamingPatcher(PatchContext ctx)
        {
            this.ctx = ctx;
            s = ctx.Settings.AnimalTaming;
        }

        bool InvalidActorType(IKeywordedGetter<IKeywordGetter> r) =>
            new[] { "ActorTypeNPC", "ActorTypeDragon", "ActorTypeDaedra", "ActorTypeUndead", "ActorTypeHorse", "ActorTypeDwarven" }.Any(k => ctx.HasKeyword(r, k));

        public void Run()
        {
            Console.WriteLine("---Animal Taming Patcher---");
            var p = ctx.PoE;
            if (s.TameableRaces && PoERecords.Require("tameable races", p.TameAnimal, p.TameCreature, p.RacesFrom, p.RacesTo)) TameableRaces();
            if (s.ExcludedActors.Count > 0 && PoERecords.Require("Animal Tamer exclusions", p.TameExcluded))
            {
                foreach (var link in s.ExcludedActors)
                {
                    if (ctx.Current<INpcGetter>(link.FormKey) is { } npc && ctx.AddToFormList(p.TameExcluded, npc)) excludedActors++;
                }
            }
            Console.WriteLine($"Animal taming: {tamed} tameable races, {excludedActors} excluded actors");
        }

        void TameableRaces()
        {
            var excluded = s.ExcludedRaces.Select(l => l.FormKey).ToHashSet();
            var riding = s.RideableRaces.Where(e => !e.Race.IsNull).ToDictionary(e => e.Race.FormKey, e => e.Skeleton);

            // races that have at least one tameable actor in the patched plugins
            var candidates = new HashSet<FormKey>();
            foreach (var npc in ctx.State.LoadOrder.PriorityOrder.Npc().WinningOverrides().Where(n => ctx.InScope(n)))
            {
                var race = BaseRace(npc, out var baseNpc);
                if (race == null || !ctx.Patchable(race) || candidates.Contains(race.FormKey)) continue;
                if (InvalidActorType(baseNpc) || InvalidActorType(race)) continue;
                if (baseNpc.Factions.Any(f => ctx.Edid(f.Faction.FormKey) == "CurrentFollowerFaction")) continue;
                var flags = baseNpc.Configuration.Flags;
                if ((flags & (NpcConfiguration.Flag.Summonable | NpcConfiguration.Flag.SimpleActor | NpcConfiguration.Flag.IsGhost)) != 0) continue;
                candidates.Add(race.FormKey);
            }

            var skeletons = ReviewedSkeletons();
            foreach (var fk in candidates)
            {
                var race = ctx.Current<IRaceGetter>(fk)!;
                var edid = race.EditorID ?? "";
                if (excluded.Contains(fk) || (race.Flags & Race.Flag.Playable) != 0) continue;
                if (ItemChecks.Has(edid, "beastrace") || ItemChecks.Has(edid, "werewolf") || ItemChecks.Has(edid, "werebear")) continue;
                if (!ctx.HasKeyword(race, "ActorTypeAnimal") && !ctx.HasKeyword(race, "ActorTypeCreature")) continue;
                // option default: clothed races (falmer, goblins, ...) are not tameable
                if (MonsterPatcher.CanPickpocket(ctx, race)) continue;
                if (ctx.InFormList(ctx.PoE.RacesFrom, race) || ctx.EdidExists(edid + "_Tamed")) continue;
                if (s.TrustPoEPatchCoverage && ctx.ReviewedByPoE.Contains(fk.ModKey))
                {
                    ctx.Log($"Not tameable {PatchContext.Show(race)}: its plugin has a PoE patch that left it alone");
                    continue;
                }
                // a variant of a race PoE already judged (same skeleton) follows PoE's call
                var skeleton = SkeletonPath(race);
                if (skeleton != null && skeletons.TryGetValue(skeleton, out var poeTames))
                {
                    if (!poeTames)
                    {
                        ctx.Log($"Not tameable {PatchContext.Show(race)}: PoE doesn't tame the vanilla race with this skeleton");
                        continue;
                    }
                }
                // unknown skeleton: only plain animals; anything also tagged a creature (mimics, tentacle monsters, ...) needs the opt-in
                else if (!s.NewCreatureTypes && (!ctx.HasKeyword(race, "ActorTypeAnimal") || ctx.HasKeyword(race, "ActorTypeCreature")))
                {
                    ctx.Log($"Not tameable {PatchContext.Show(race)}: new creature type (see 'New creature types')");
                    continue;
                }
                CreateTameable(race, riding.GetValueOrDefault(fk));
            }
        }

        static string? SkeletonPath(IRaceGetter r)
        {
            var path = r.SkeletalModel?.Male?.File.GivenPath ?? r.SkeletalModel?.Female?.File.GivenPath;
            return string.IsNullOrWhiteSpace(path) ? null : path.Replace('/', '\\').ToLowerInvariant();
        }

        /// <summary>Skeleton -> does PoE make some race with it tameable, over races from plugins PoE has reviewed (vanilla and patched mods).</summary>
        Dictionary<string, bool> ReviewedSkeletons()
        {
            var map = new Dictionary<string, bool>();
            foreach (var r in ctx.State.LoadOrder.PriorityOrder.Race().WinningOverrides())
            {
                var mod = r.FormKey.ModKey;
                bool reviewed = ctx.PoEPlugins.Contains(mod) || ctx.ReviewedByPoE.Contains(mod) || !ctx.InScope(r.FormKey);
                if (!reviewed || (r.Flags & Race.Flag.Playable) != 0 || r.EditorID?.EndsWith("_Tamed") == true) continue;
                var skeleton = SkeletonPath(r);
                if (skeleton == null) continue;
                bool tame = ctx.InFormList(ctx.PoE.RacesFrom, r);
                map[skeleton] = map.GetValueOrDefault(skeleton) || tame;
            }
            return map;
        }

        /// <summary>Walk the NPC template chain while it inherits traits, like PreloadNPC_AnimalTamingPatcher; leveled templates are skipped.</summary>
        IRaceGetter? BaseRace(INpcGetter npc, out INpcGetter baseNpc)
        {
            baseNpc = npc;
            if (!npc.Template.IsNull && ctx.Cache.TryResolve<ILeveledNpcGetter>(npc.Template.FormKey, out _)) return null;
            for (int guard = 0; guard < 32; guard++)
            {
                if ((baseNpc.Configuration.TemplateFlags & NpcConfiguration.TemplateFlag.Traits) == 0 || baseNpc.Template.IsNull) break;
                if (!ctx.Cache.TryResolve<INpcGetter>(baseNpc.Template.FormKey, out var next)) break;
                baseNpc = next;
            }
            return ctx.Current<IRaceGetter>(baseNpc.Race.FormKey);
        }

        void CreateTameable(IRaceGetter rc, string? skeleton)
        {
            var p = ctx.PoE;
            var edid = rc.EditorID ?? rc.FormKey.ToString();
            var name = rc.Name?.String;
            bool xl = rc.Size == Size.ExtraLarge;

            var t = ctx.Patch.Races.DuplicateInAsNewRecord(rc);
            t.EditorID = edid + "_Tamed";
            t.MorphRace.SetTo(rc);
            t.ArmorRace.SetTo(rc);
            // the xEdit script rebuilds only the first 22 flag bits; the higher ones are kept here
            t.Flags = xl ? t.Flags | Race.Flag.CantOpenDoors : t.Flags & ~Race.Flag.CantOpenDoors;
            t.Flags |= Race.Flag.AllowPcDialog;
            if (!t.Regen.TryGetValue(BasicStat.Health, out var regen) || regen < 0.3f) t.Regen[BasicStat.Health] = 0.3f;

            // rank 2 (exotic creature) when it isn't an animal
            PatchContext.AddKeyword(t, ctx.HasKeyword(rc, "ActorTypeAnimal") ? p.TameAnimal : p.TameCreature);
            if (ItemChecks.Has(edid, name, "spriggan")) PatchContext.AddKeyword(t, p.AlwaysFemale);
            if (!string.IsNullOrEmpty(skeleton) && p.Rideable != null)
            {
                PatchContext.AddKeyword(t, p.Rideable);
                t.SkeletalModel = new GenderedItem<SimpleModel?>(Skeleton(skeleton), Skeleton(skeleton));
            }
            ctx.RegisterEdid(t.EditorID);

            ctx.AddToFormList(p.RacesFrom, rc);
            ctx.AddToFormList(p.RacesTo, t);
            if (s.CollectItems) AddCollectItem(t, edid, name);
            tamed++;
            ctx.Log($"Created tameable race {t.EditorID}");
        }

        static SimpleModel Skeleton(string path)
        {
            var m = new SimpleModel();
            m.File = path;
            return m;
        }

        void AddCollectItem(Race t, string edid, string? name)
        {
            var p = ctx.PoE;
            bool H(params string[] w) => w.Any(x => ItemChecks.Has(edid, name, x));
            var size = t.Size;
            FormKey? kw, list;
            if (H("cow", "goat")) (kw, list) = (p.HasMilk, size is Size.Small or Size.Medium ? p.MilkSm : p.MilkMd);
            else if (H("spider")) (kw, list) = (p.HasVenom, size == Size.Small ? p.VenomSpiderSm : size == Size.Medium ? p.VenomSpiderMd : p.VenomSpiderLg);
            else if (H("chaurus")) (kw, list) = (p.HasVenom, size == Size.Small ? p.VenomChaurusSm : size == Size.Medium ? p.VenomChaurusMd : p.VenomChaurusLg);
            else if (H("netch")) (kw, list) = (p.HasVenom, size == Size.Small ? p.VenomNetchSm : p.VenomNetchLg);
            else if (H("scrib") || ItemChecks.Has(edid, "ashhopper")) (kw, list) = (p.HasVenom, p.VenomScrib);
            else return;
            PatchContext.AddKeyword(t, kw);
            ctx.AddToFormList(list, t);
        }
    }
}
