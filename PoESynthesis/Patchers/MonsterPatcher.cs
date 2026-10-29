using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis.Patchers
{
    /// <summary>PoEMonsterPatcher.pas: pickpocket/no-knockdown/Apex Hunter/Arkay's Judgement/legendary keywords on creature races.</summary>
    public class MonsterPatcher
    {
        readonly PatchContext ctx;
        readonly MonsterSettings s;

        public MonsterPatcher(PatchContext ctx)
        {
            this.ctx = ctx;
            s = ctx.Settings.Monsters;
        }

        public static bool IsCreatureRace(PatchContext ctx, IRaceGetter r) =>
            (r.Flags & Race.Flag.Playable) == 0 && !ctx.HasKeyword(r, "ActorTypeNPC");

        /// <summary>GetCanPickpocket: races that wear clothing (also the races Animal Tamer leaves alone by default).</summary>
        public static bool CanPickpocket(PatchContext ctx, IRaceGetter r)
        {
            var edid = r.EditorID;
            var name = r.Name?.String;
            return new[] { "falmer", "goblin", "riekling", "scamp", "minotaur", "centaur", "armored", "armoured" }.Any(w => ItemChecks.Has(edid, name, w))
                || ctx.HasKeyword(r, "ActorTypeGiant");
        }

        bool NoKnockdowns(IRaceGetter r) =>
            r.Size == Size.ExtraLarge || ItemChecks.Has(r.EditorID, r.Name?.String, "mammoth")
            || ctx.HasKeyword(r, "ActorTypeDragon") || ctx.HasKeyword(r, "ActorTypeGiant");

        bool IsMonster(IRaceGetter r)
        {
            var e = r.EditorID;
            return ctx.HasKeyword(r, "ActorTypeDragon") || ItemChecks.Has(e, "hagraven") || ItemChecks.Has(e, "daedr") || ItemChecks.Has(e, "dremora")
                || new[] { "werewolf", "werebear", "werebeast", "beastrace" }.Any(w => ItemChecks.Has(e, w));
        }

        string? ApexType(IRaceGetter r)
        {
            var e = r.EditorID;
            bool H(params string[] w) => w.Any(x => ItemChecks.Has(e, x));
            if (H("bear")) return "Bear";
            if (H("chaurus")) return "Chaurus";
            if (H("daedr", "dremora", "atronach")) return "Daedra";
            if (H("dragon") || ctx.HasKeyword(r, "ActorTypeDragon")) return "Dragon";
            if (H("draugr", "skeleton", "lich", "undead")) return "Draugr";
            if (H("dwarven", "dwemer", "automaton")) return "Dwarven";
            if (H("falmer")) return "Falmer";
            if (H("gargoyle")) return "Gargoyle";
            if (ctx.HasKeyword(r, "ActorTypeGiant")) return "Giant";
            if (H("hagraven")) return "Hagraven";
            if (H("mammoth")) return "Mammoth";
            if (H("mudcrab")) return "Mudcrab";
            if (H("riekling")) return "Riekling";
            if (H("sabrecat")) return "Sabrecat";
            if (H("spider")) return "Spider";
            if (H("spriggan")) return "Spriggan";
            if (H("troll")) return "Troll";
            if (H("werewolf", "werebear")) return "Werebeast";
            if (H("wisp")) return "Wisp";
            if (H("wolf")) return "Wolf";
            return null;
        }

        public void Run()
        {
            Console.WriteLine("---Monster Patcher---");
            var p = ctx.PoE;
            var excluded = s.ExcludedRaces.Select(l => l.FormKey).ToHashSet();
            var legendary = s.LegendaryRaces.Select(l => l.FormKey).ToHashSet();
            var allApex = p.Apex.Values.Concat(p.ApexPoints).ToList();
            int n = 0;
            foreach (var race in ctx.State.LoadOrder.PriorityOrder.Race().WinningOverrides()
                         .Where(r => ctx.Patchable(r) && !excluded.Contains(r.FormKey) && IsCreatureRace(ctx, r)).ToList())
            {
                var add = new List<FormKey?>();
                if (s.Pickpocket && !ctx.HasKeyword(race, p.CanPickpocket) && CanPickpocket(ctx, race)) add.Add(p.CanPickpocket);
                if (s.NoKnockdowns && !ctx.HasKeyword(race, p.NoKnockdowns) && NoKnockdowns(race)) add.Add(p.NoKnockdowns);
                if (legendary.Contains(race.FormKey) && !ctx.HasKeyword(race, p.Legendary)) add.Add(p.Legendary);
                if (s.ArkaysJudgement && !ctx.HasKeyword(race, p.ArkayMonster) && IsMonster(race)) add.Add(p.ArkayMonster);
                if (s.ApexHunter && !ctx.HasAnyKeyword(race, allApex) && ApexType(race) is { } apex)
                {
                    add.Add(p.Apex[apex]);
                    add.Add(p.ApexPoints[(int)s.ApexHunterPoints]);
                }
                add.RemoveAll(k => k == null);
                if (add.Count == 0) continue;
                var o = ctx.Patch.Races.GetOrAddAsOverride(race);
                foreach (var k in add) PatchContext.AddKeyword(o, k);
                n++;
                ctx.Log($"Tagged race {PatchContext.Show(race)}: {string.Join(", ", add.Select(k => ctx.KeywordEdid(k!.Value)))}");
            }
            Console.WriteLine($"Creature races tagged: {n}");
        }
    }
}
