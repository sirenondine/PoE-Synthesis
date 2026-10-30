using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PoESynthesis
{
    /// <summary>
    /// SkyPatcher can rewrite an original's stats in game (e.g. Requiem's armor ratings and weapon damage). A PoE copy is built from the
    /// plugin values, and keyword rules like "ArmorMaterialIron+ArmorCuirass: damageResist=138" catch the copy too, wiping its
    /// Masterwork bonus. This replays the SkyPatcher armor/weapon/ammo rules on each original (after a KID pass for runtime keywords),
    /// derives every copy from the result with the same formulas used to build it, and writes those final stats as SkyPatcher lines
    /// that load after every other rule.
    /// </summary>
    public class RuntimeStats
    {
        /// <summary>An original's in-game state while the rules are replayed.</summary>
        class State
        {
            public required IMajorRecordGetter Record;
            public required string Type;
            public required List<FormKey> Copies;
            public required HashSet<string> Keywords; // lowercase EditorIDs
            public required ItemStats Stats;
            public bool Touched;
            public string Edid => Record.EditorID ?? "";
            public string EdidLower = "";
            public string NameLower = "";
        }

        static readonly Dictionary<string, (string Folder, string Objects)> Types = new()
        {
            ["Armor"] = ("armor", "filterByArmors"), ["Weapon"] = ("weapon", "filterByWeapons"), ["Ammo"] = ("ammo", "filterByAmmos"),
        };
        static readonly HashSet<string> KnownFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            "filterByArmors", "filterByWeapons", "filterByAmmos", "filterByArmorsExcluded", "filterByWeaponsExcluded", "filterByAmmosExcluded",
            "filterByModNames", "hasPlugins", "hasPluginsOr",
            "filterByKeywords", "filterByKeywordsOr", "filterByKeywordsExcluded", "restrictToKeywords",
            "filterByEditorIdContains", "filterByEditorIdContainsOr", "filterByEditorIdContainsExcluded",
            "filterByNameContains", "filterByNameContainsOr", "filterByNameContainsExcluded",
            "filterByArmorTypes", "filterByArmorTypesExcluded", "filterByBipedSlots", "filterByBipedSlotsOr", "filterByBipedSlotsExcluded", "restrictToBipedSlots",
            "filterByArmorAddons", "filterByArmorAddonsOr", "filterByArmorAddonsExcluded",
            "filterByAnimationTypeOr", "restrictToFlags", "restrictToBolts",
        };

        readonly PatchContext ctx;
        readonly Dictionary<FormKey, State> states = new();
        readonly Dictionary<string, Dictionary<string, FormKey>> edidIndex = new();
        int skippedLines;
        /// <summary>Per type: keyword changes that reached an original by identity (object list or plugin), rewritten for its copies.</summary>
        readonly Dictionary<string, List<string>> keywordLines = new();

        public RuntimeStats(PatchContext ctx) => this.ctx = ctx;

        public void Run()
        {
            Console.WriteLine("---SkyPatcher replay for PoE copies (keywords, stats)---");
            foreach (var copy in ctx.CopiedFrom.Keys)
            {
                var root = ctx.OriginalOf(copy);
                if (root == copy || !ctx.Cache.TryResolve<IMajorRecordGetter>(root, out var rec)) continue;
                var type = rec switch { IArmorGetter => "Armor", IWeaponGetter => "Weapon", IAmmunitionGetter => "Ammo", _ => null };
                if (type == null) continue;
                if (!states.TryGetValue(root, out var st))
                {
                    var kws = (rec as IKeywordedGetter<IKeywordGetter>)?.Keywords?.Select(k => ctx.KeywordEdid(k.FormKey)).OfType<string>()
                        .Select(k => k.ToLowerInvariant()).ToHashSet() ?? new HashSet<string>();
                    var stats = CopyStats.Read(rec);
                    states[root] = st = new State
                    {
                        Record = rec, Type = type, Copies = new(), Keywords = kws, Stats = stats,
                        EdidLower = (rec.EditorID ?? "").ToLowerInvariant(), NameLower = stats.Name.ToLowerInvariant(),
                    };
                }
                st.Copies.Add(copy);
            }
            if (states.Count == 0) return;

            TrackMatchSources();
            KidPass();
            foreach (var (type, (folder, objects)) in Types) Replay(type, folder, objects);
            Write();
        }

        // ---------------- KID: keywords the originals have in game ----------------

        enum AtomKind { Form, Plugin, Wild, Exact }
        record KidAtom(bool Neg, AtomKind Kind, FormKey Form, string Text);
        record KidRule(string Kw, string Type, List<List<KidAtom>> Alts, string Traits);

        static KidAtom CompileAtom(string raw)
        {
            var atom = raw.Trim();
            bool neg = atom.StartsWith('-');
            if (neg) atom = atom.Substring(1).Trim();
            if (atom.StartsWith('*')) return new(neg, AtomKind.Wild, FormKey.Null, atom.Substring(1).ToLowerInvariant());
            var m = Regex.Match(atom, @"^(?:0x)?([0-9a-fA-F]+)~(.+)$");
            if (m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out var id) && ModKey.TryFromFileName(m.Groups[2].Value.Trim(), out var mk))
                return new(neg, AtomKind.Form, new FormKey(mk, id & 0xFFFFFF), "");
            if (Regex.IsMatch(atom, @"\.(esp|esm|esl)$", RegexOptions.IgnoreCase)) return new(neg, AtomKind.Plugin, FormKey.Null, atom.ToLowerInvariant());
            return new(neg, AtomKind.Exact, FormKey.Null, atom.ToLowerInvariant());
        }

        void KidPass()
        {
            var rules = new List<KidRule>();
            foreach (var file in Directory.EnumerateFiles(ctx.State.DataFolderPath, "*_KID.ini").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Split(';')[0].Trim();
                    var eq = line.IndexOf('=');
                    if (eq < 0 || !line.Substring(0, eq).Trim().Equals("Keyword", StringComparison.OrdinalIgnoreCase)) continue;
                    var p = line.Substring(eq + 1).Split('|').Select(x => x.Trim()).ToArray();
                    if (p.Length < 2) continue;
                    var type = p[1].ToLowerInvariant() switch { "armor" => "Armor", "weapon" => "Weapon", "ammo" => "Ammo", _ => null };
                    if (type == null) continue;
                    // a rule with a chance below 100 may or may not land; leave it out
                    if (p.Length > 4 && float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var chance) && chance < 100) continue;
                    var filter = p.Length > 2 ? p[2] : "";
                    var alts = filter.Length == 0 || filter.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                        ? new List<List<KidAtom>> { new() }
                        : filter.Split(',').Select(a => a.Split('+').Where(x => x.Trim().Length > 0).Select(CompileAtom).ToList()).ToList();
                    rules.Add(new(p[0].ToLowerInvariant(), type, alts, p.Length > 3 ? p[3] : ""));
                }
            }
            // two passes so rules that filter on keywords other rules add get their turn, like KID's own ordering
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var rule in rules)
                {
                    foreach (var st in states.Values)
                    {
                        if (st.Type != rule.Type || st.Keywords.Contains(rule.Kw)) continue;
                        if (rule.Alts.Any(alt => alt.All(a => AtomHolds(a, st) != a.Neg)) && KidTraits(rule.Traits, st)) st.Keywords.Add(rule.Kw);
                    }
                }
            }
        }

        static bool AtomHolds(KidAtom a, State st) => a.Kind switch
        {
            AtomKind.Form => st.Record.FormKey == a.Form,
            AtomKind.Plugin => st.Record.FormKey.ModKey.FileName.String.Equals(a.Text, StringComparison.OrdinalIgnoreCase),
            AtomKind.Wild => st.EdidLower.Contains(a.Text) || st.NameLower.Contains(a.Text) || st.Keywords.Any(k => k.Contains(a.Text)),
            _ => st.EdidLower == a.Text || st.NameLower == a.Text || st.Keywords.Contains(a.Text),
        };

        static bool KidTraits(string traits, State st)
        {
            if (traits.Length == 0 || traits.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var raw in traits.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                bool neg = raw.StartsWith('-');
                var t = raw.TrimStart('-').ToUpperInvariant();
                bool? v = null;
                if (st.Record is IArmorGetter a)
                {
                    if (int.TryParse(t, out var slot) && slot >= 30 && slot <= 61)
                        v = a.BodyTemplate != null && ((uint)a.BodyTemplate.FirstPersonFlags & (1u << (slot - 30))) != 0;
                    else if (t is "HEAVY" or "LIGHT" or "CLOTHING")
                        v = a.BodyTemplate?.ArmorType == (t == "HEAVY" ? ArmorType.HeavyArmor : t == "LIGHT" ? ArmorType.LightArmor : ArmorType.Clothing);
                    else if (t == "E") v = !a.ObjectEffect.IsNull;
                    else if (t == "T") v = !a.TemplateArmor.IsNull;
                }
                else if (st.Record is IWeaponGetter w)
                {
                    if (t == "E") v = !w.ObjectEffect.IsNull;
                    else if (t == "T") v = !w.Template.IsNull;
                }
                if (v != null && v.Value == neg) return false; // traits we don't model count as passing
            }
            return true;
        }

        // ---------------- SkyPatcher ----------------

        /// <summary>A SkyPatcher line with its lists split and references resolved once.</summary>
        class Rule
        {
            public required Dictionary<string, string> Parts;
            public required Dictionary<string, List<string>> Lists;
            public required HashSet<FormKey> Objects;
            /// <summary>The line names forms, whether or not they exist: an object-only line never falls through to "every form".</summary>
            public required bool HasObjectList;
            public required HashSet<FormKey> Excluded;
            public required bool HasGroups;
            public List<string> L(string key) => Lists.TryGetValue(key, out var l) ? l : Empty;
            static readonly List<string> Empty = new();
        }

        Rule Compile(Dictionary<string, string> parts, string type, string objectsKey)
        {
            var lists = parts.ToDictionary(kv => kv.Key, kv => kv.Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(), StringComparer.OrdinalIgnoreCase);
            // keyword references become lowercase EditorIDs up front
            foreach (var key in new[] { "filterByKeywords", "filterByKeywordsOr", "filterByKeywordsExcluded", "restrictToKeywords" })
            {
                if (lists.TryGetValue(key, out var l)) lists[key] = l.Select(KeywordName).OfType<string>().ToList();
            }
            HashSet<FormKey> Forms(string key) => (lists.TryGetValue(key, out var l) ? l : new()).Select(r => Resolve(r, type)).OfType<FormKey>().ToHashSet();
            var groupKeys = new[] { "filterByEditorIdContains", "filterByEditorIdContainsOr", "filterByNameContains", "filterByNameContainsOr", "filterByKeywords",
                "filterByKeywordsOr", "filterByArmorTypes", "filterByBipedSlots", "filterByBipedSlotsOr", "filterByArmorAddons", "filterByArmorAddonsOr", "filterByAnimationTypeOr" };
            return new Rule
            {
                Parts = parts, Lists = lists, Objects = Forms(objectsKey), Excluded = Forms(objectsKey + "Excluded"),
                HasObjectList = lists.TryGetValue(objectsKey, out var named) && named.Count > 0,
                HasGroups = groupKeys.Any(k => lists.TryGetValue(k, out var l) && l.Count > 0),
            };
        }

        List<string> Files(string folder) =>
            ctx.DataDirs("SKSE", "Plugins", "SkyPatcher", folder)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.ini", SearchOption.AllDirectories).Select(f => (Dir: d, File: f)))
                .GroupBy(x => Path.GetRelativePath(x.Dir, x.File).ToLowerInvariant()).Select(g => g.First())
                .Where(x => !Path.GetFileName(x.File).StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase))
                // breadth-first like SkyPatcher's own folder walk; within a level the real order is the filesystem's, alphabetical here
                .OrderBy(x => Path.GetRelativePath(x.Dir, x.File).Count(c => c == '/' || c == '\\'))
                .ThenBy(x => Path.GetRelativePath(x.Dir, x.File), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.File)
                // "Plugin.esp.ini" only runs when that plugin is loaded
                .Where(f => !Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"\.(esp|esm|esl)$", RegexOptions.IgnoreCase) || Loaded(Path.GetFileNameWithoutExtension(f)))
                .ToList();

        static readonly string[] MatchKeys = { "dwMatch", "valueMatch", "damageResistMatch" };

        /// <summary>Items a *Match key copies stats from are replayed too, so the copy sees their in-game values, not their plugin ones.</summary>
        void TrackMatchSources()
        {
            foreach (var (type, (folder, _)) in Types)
            {
                foreach (var file in Files(folder))
                {
                    foreach (var raw in File.ReadLines(file))
                    {
                        if (raw.TrimStart().StartsWith(';') || !raw.Contains("Match", StringComparison.OrdinalIgnoreCase)) continue;
                        var parts = Parse(raw.Trim());
                        foreach (var key in MatchKeys)
                        {
                            if (!parts.TryGetValue(key, out var r) || Resolve(r, type) is not { } fk || states.ContainsKey(fk)) continue;
                            if (!ctx.Cache.TryResolve<IMajorRecordGetter>(fk, out var rec) || rec is not (IArmorGetter or IWeaponGetter or IAmmunitionGetter)) continue;
                            var stats = CopyStats.Read(rec);
                            var kws = (rec as IKeywordedGetter<IKeywordGetter>)?.Keywords?.Select(k => ctx.KeywordEdid(k.FormKey)).OfType<string>()
                                .Select(k => k.ToLowerInvariant()).ToHashSet() ?? new HashSet<string>();
                            states[fk] = new State
                            {
                                Record = rec, Type = type, Copies = new(), Keywords = kws, Stats = stats,
                                EdidLower = (rec.EditorID ?? "").ToLowerInvariant(), NameLower = stats.Name.ToLowerInvariant(),
                            };
                        }
                    }
                }
            }
        }

        void Replay(string type, string folder, string objectsKey)
        {
            var targets = states.Values.Where(s => s.Type == type).ToList();
            if (targets.Count == 0) return;
            foreach (var file in Files(folder))
            {
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(';')) continue;
                    var parts = Parse(line);
                    if (!parts.Keys.Any(IsSetter)) continue;
                    if (parts.Keys.Any(k => IsFilterLike(k) && !KnownFilters.Contains(k)))
                    {
                        skippedLines++;
                        continue;
                    }
                    var rule = Compile(parts, type, objectsKey);
                    // a pure object list only touches the listed forms
                    var scan = rule.HasObjectList && !rule.HasGroups
                        ? rule.Objects.Select(fk => states.TryGetValue(fk, out var x) && x.Type == type ? x : null).OfType<State>()
                        : targets;
                    var kwParts = parts.Where(kv => kv.Key.Equals("keywordsToAdd", StringComparison.OrdinalIgnoreCase)
                        || kv.Key.Equals("keywordsToRemove", StringComparison.OrdinalIgnoreCase)).Select(kv => $"{kv.Key}={kv.Value}").ToList();
                    // a line that picks forms by identity doesn't reach the copies on its own
                    bool identity = rule.HasObjectList || rule.L("filterByModNames").Count > 0;
                    var reached = new List<FormKey>();
                    foreach (var st in scan)
                    {
                        if (!Matches(rule, st)) continue;
                        Apply(parts, st, type);
                        if (identity && kwParts.Count > 0) reached.AddRange(st.Copies);
                    }
                    if (reached.Count > 0)
                    {
                        // only the copies, and no other filter: with an object list, any other filter would run over every form
                        var list = keywordLines.TryGetValue(type, out var l) ? l : keywordLines[type] = new();
                        list.AddRange(reached.Chunk(100).Select(chunk =>
                            $"{objectsKey}=" + string.Join(",", chunk.Select(c => $"{c.ModKey.FileName}|{c.ID:X}")) + ":" + string.Join(":", kwParts)));
                    }
                }
            }
        }

        bool Loaded(string plugin) => ModKey.TryFromFileName(plugin, out var mk) && ctx.State.LoadOrder.ContainsKey(mk);

        static bool IsFilterLike(string k) =>
            k.StartsWith("filterBy", StringComparison.OrdinalIgnoreCase) || k.StartsWith("restrictTo", StringComparison.OrdinalIgnoreCase) || k.StartsWith("hasPlugin", StringComparison.OrdinalIgnoreCase);

        static readonly HashSet<string> Setters = new(StringComparer.OrdinalIgnoreCase)
        {
            "keywordsToAdd", "keywordsToRemove", "fullName", "damageResist", "damageResistMultiply", "damageResistMatch", "dwMatch", "valueMatch",
            "attackDamage", "attackDamageToAdd", "attackDamageMult", "critDamage", "critDamageToAdd", "critDamageMult", "speed", "speedMult", "reach",
            "value", "valueMult", "weight", "weightMult",
        };

        static bool IsSetter(string k) => Setters.Contains(k);

        static Dictionary<string, string> Parse(string line)
        {
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // fullName=~...~ may contain ':'
            var name = Regex.Match(line, @"fullName\s*=\s*~([^~]*)~", RegexOptions.IgnoreCase);
            if (name.Success)
            {
                parts["fullName"] = name.Groups[1].Value;
                line = line.Remove(name.Index, name.Length);
            }
            foreach (var p in line.Split(':'))
            {
                var eq = p.IndexOf('=');
                if (eq > 0) parts[p.Substring(0, eq).Trim()] = p.Substring(eq + 1).Trim();
            }
            return parts;
        }

        static List<string> List(Dictionary<string, string> parts, string key) =>
            parts.TryGetValue(key, out var v) ? v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new();

        bool Matches(Rule r, State st)
        {
            if (r.Objects.Contains(st.Record.FormKey)) return true;
            // an object list with no other positive filter only patches the listed forms
            if (r.HasObjectList && !r.HasGroups) return false;

            var groups = new List<bool>();
            void Group(string and, string or, Func<string, bool> holds)
            {
                var a = r.L(and);
                var o = r.L(or);
                if (a.Count == 0 && o.Count == 0) return;
                groups.Add((a.Count == 0 || a.All(holds)) && (o.Count == 0 || o.Any(holds)));
            }
            bool Kw(string k) => st.Keywords.Contains(k);
            bool InEdid(string x) => st.EdidLower.Contains(x, StringComparison.OrdinalIgnoreCase);
            bool InName(string x) => st.Stats.Name.Contains(x, StringComparison.OrdinalIgnoreCase);
            Group("filterByEditorIdContains", "filterByEditorIdContainsOr", InEdid);
            Group("filterByNameContains", "filterByNameContainsOr", InName);
            Group("filterByKeywords", "filterByKeywordsOr", Kw);
            if (st.Record is IArmorGetter a)
            {
                Group("", "filterByArmorTypes", t => ArmorTypeIs(a, t));
                Group("filterByBipedSlots", "filterByBipedSlotsOr", x => SlotIs(a, x));
                Group("filterByArmorAddons", "filterByArmorAddonsOr", x => a.Armature.Any(m => Resolve(x) == m.FormKey));
            }
            if (st.Record is IWeaponGetter w)
            {
                Group("", "filterByAnimationTypeOr", t => string.Equals(w.Data?.AnimationType.ToString(), t, StringComparison.OrdinalIgnoreCase)
                    || (t.Equals("crossbow", StringComparison.OrdinalIgnoreCase) && w.Data?.AnimationType == WeaponAnimationType.Crossbow)
                    || (t.Equals("bow", StringComparison.OrdinalIgnoreCase) && w.Data?.AnimationType == WeaponAnimationType.Bow));
            }

            if (r.L("hasPlugins").Any(x => !Loaded(x))) return false;
            var anyPlugins = r.L("hasPluginsOr");
            if (anyPlugins.Count > 0 && !anyPlugins.Any(Loaded)) return false;
            var mods = r.L("filterByModNames");
            if (mods.Count > 0 && !mods.Any(m => m.Equals(st.Record.FormKey.ModKey.FileName, StringComparison.OrdinalIgnoreCase))) return false;

            if (groups.Count > 0 && !groups.Any(g => g)) return false;

            if (r.L("filterByKeywordsExcluded").Any(Kw)) return false;
            if (r.L("filterByEditorIdContainsExcluded").Any(InEdid)) return false;
            if (r.L("filterByNameContainsExcluded").Any(InName)) return false;
            if (r.Excluded.Contains(st.Record.FormKey)) return false;
            var restrict = r.L("restrictToKeywords");
            if (restrict.Count > 0 && !restrict.Any(Kw)) return false;
            if (st.Record is IArmorGetter arm)
            {
                if (r.L("filterByArmorTypesExcluded").Any(t => ArmorTypeIs(arm, t))) return false;
                if (r.L("filterByBipedSlotsExcluded").Any(x => SlotIs(arm, x))) return false;
                var rs = r.L("restrictToBipedSlots");
                if (rs.Count > 0 && !rs.Any(x => SlotIs(arm, x))) return false;
                if (r.L("filterByArmorAddonsExcluded").Any(x => arm.Armature.Any(m => Resolve(x) == m.FormKey))) return false;
            }
            if (st.Record is IWeaponGetter wep && r.Parts.TryGetValue("restrictToFlags", out var flags))
            {
                if (flags.Contains("boundweapon", StringComparison.OrdinalIgnoreCase) && !ItemChecks.IsBound(wep)) return false;
            }
            if (st.Record is IAmmunitionGetter ammo && r.Parts.TryGetValue("restrictToBolts", out var bolts))
            {
                if (bolts.Equals("true", StringComparison.OrdinalIgnoreCase) != ItemChecks.IsBolt(ammo)) return false;
            }
            return true;
        }

        static bool ArmorTypeIs(IArmorGetter a, string t) => t.ToLowerInvariant() switch
        {
            "lightarmor" => a.BodyTemplate?.ArmorType == ArmorType.LightArmor,
            "heavyarmor" => a.BodyTemplate?.ArmorType == ArmorType.HeavyArmor,
            "clothing" => a.BodyTemplate?.ArmorType == ArmorType.Clothing,
            _ => false,
        };

        static bool SlotIs(IArmorGetter a, string s) =>
            int.TryParse(s, out var slot) && slot >= 30 && slot <= 61 && a.BodyTemplate != null && ((uint)a.BodyTemplate.FirstPersonFlags & (1u << (slot - 30))) != 0;

        /// <summary>"Plugin.esp|ID" or an EditorID.</summary>
        FormKey? Resolve(string reference, string? type = null)
        {
            var bar = reference.IndexOf('|');
            if (bar >= 0)
            {
                var id = reference.Substring(bar + 1).Trim();
                if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) id = id.Substring(2);
                return uint.TryParse(id, NumberStyles.HexNumber, null, out var n) && ModKey.TryFromFileName(reference.Substring(0, bar).Trim(), out var mk)
                    ? new FormKey(mk, n & 0xFFFFFF) : null;
            }
            if (type == null) return null;
            if (!edidIndex.TryGetValue(type, out var map))
            {
                var lo = ctx.State.LoadOrder.PriorityOrder;
                IEnumerable<IMajorRecordGetter> recs = type switch
                {
                    "Armor" => lo.Armor().WinningOverrides(),
                    "Weapon" => lo.Weapon().WinningOverrides(),
                    _ => lo.Ammunition().WinningOverrides(),
                };
                edidIndex[type] = map = new(StringComparer.OrdinalIgnoreCase);
                foreach (var r in recs) if (r.EditorID != null) map.TryAdd(r.EditorID, r.FormKey);
            }
            return map.TryGetValue(reference.Trim(), out var fk) ? fk : null;
        }

        bool RefIs(string reference, FormKey fk, string? type) => Resolve(reference, type) == fk;

        /// <summary>Stats of another item a *Match key copies from: its replayed state if we track it, else its record.</summary>
        ItemStats? StatsOf(string reference, string type)
        {
            if (Resolve(reference, type) is not { } fk) return null;
            if (states.TryGetValue(fk, out var s)) return s.Stats;
            return ctx.Cache.TryResolve<IMajorRecordGetter>(fk, out var r) && r is IArmorGetter or IWeaponGetter or IAmmunitionGetter ? CopyStats.Read(r) : null;
        }

        static float? Num(Dictionary<string, string> parts, string key) =>
            parts.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;

        void Apply(Dictionary<string, string> parts, State st, string type)
        {
            foreach (var k in List(parts, "keywordsToAdd")) if (KeywordName(k) is { } e) st.Keywords.Add(e);
            foreach (var k in List(parts, "keywordsToRemove")) if (KeywordName(k) is { } e) st.Keywords.Remove(e);

            var s = st.Stats;
            var before = s;
            if (parts.TryGetValue("fullName", out var name) && name.Length > 0) s = s with { Name = name };
            if (type == "Armor")
            {
                // SkyPatcher keeps armor rating x100 as an integer: damageResist goes through stoi
                if (Num(parts, "damageResist") is { } dr) s = s with { Rating = MathF.Truncate(dr) };
                if (Num(parts, "damageResistMultiply") is { } drm) s = s with { Rating = MathF.Truncate(s.Rating * 100 * drm) / 100 };
                if (parts.TryGetValue("damageResistMatch", out var drMatch) && StatsOf(drMatch, type) is { } m1) s = s with { Rating = m1.Rating };
            }
            else
            {
                if (Num(parts, "attackDamage") is { } d) s = s with { Rating = d };
                if (Num(parts, "attackDamageToAdd") is { } da) s = s with { Rating = s.Rating + da };
                if (Num(parts, "attackDamageMult") is { } dm) s = s with { Rating = s.Rating * dm };
                if (Num(parts, "critDamage") is { } c) s = s with { Crit = c };
                if (Num(parts, "critDamageToAdd") is { } ca) s = s with { Crit = s.Crit + ca };
                if (Num(parts, "critDamageMult") is { } cm) s = s with { Crit = s.Crit * cm };
                if (type == "Weapon")
                {
                    if (Num(parts, "speed") is { } sp) s = s with { Speed = sp };
                    if (Num(parts, "speedMult") is { } spm) s = s with { Speed = s.Speed * spm };
                    if (Num(parts, "reach") is { } r) s = s with { Reach = r };
                }
            }
            if (Num(parts, "weight") is { } w) s = s with { Weight = w };
            if (Num(parts, "value") is { } v) s = s with { Value = MathF.Truncate(v) };
            if (Num(parts, "weightMult") is { } wm) s = s with { Weight = s.Weight * wm };
            if (Num(parts, "valueMult") is { } vm) s = s with { Value = MathF.Ceiling(s.Value * vm) };
            if (parts.TryGetValue("dwMatch", out var dw) && StatsOf(dw, type) is { } m2) s = s with { Rating = m2.Rating, Weight = m2.Weight };
            if (parts.TryGetValue("valueMatch", out var vmatch) && StatsOf(vmatch, type) is { } m3) s = s with { Value = m3.Value };

            if (s != before)
            {
                st.Stats = s;
                st.Touched = true;
            }
        }

        string? KeywordName(string reference) =>
            (reference.Contains('|') ? (Resolve(reference) is { } fk ? ctx.KeywordEdid(fk) : null) : reference)?.ToLowerInvariant();

        // ---------------- output ----------------

        string OwnPrefix => Path.GetFileNameWithoutExtension(ctx.Patch.ModKey.FileName) + "_PoECop";
        string Stem => Path.GetFileNameWithoutExtension(ctx.Patch.ModKey.FileName) + "_PoECopyStats";
        string KeywordStem => Path.GetFileNameWithoutExtension(ctx.Patch.ModKey.FileName) + "_PoECopies";

        static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);

        void Write()
        {
            var g = ctx.Settings.General;
            foreach (var (type, (folder, _)) in Types)
            {
                var path = Path.Combine(ctx.State.DataFolderPath, "SKSE", "Plugins", "SkyPatcher", folder, KeywordStem + ".ini");
                var lines = g.InheritSkyPatcherKeywords && keywordLines.TryGetValue(type, out var l) ? l.Distinct().ToList() : new();
                foreach (var old in ctx.DataDirs("SKSE", "Plugins", "SkyPatcher", folder).Select(d => Path.Combine(d, KeywordStem + ".ini")).Where(File.Exists)) File.Delete(old);
                if (lines.Count == 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Join("\r\n", new[]
                {
                    "; Generated by the Paths of Empyrean Synthesis patcher: keyword changes SkyPatcher makes to specific originals",
                    "; (by object list or plugin), mirrored onto their PoE copies. Overwritten every run.",
                }.Concat(lines)) + "\r\n");
                Console.WriteLine($"Wrote {path} ({lines.Count} keyword lines)");
            }
            if (!g.CopiesFollowSkyPatcherStats) return;

            int originals = 0, copies = 0;
            foreach (var (type, (folder, objects)) in Types)
            {
                var lines = new List<string>
                {
                    "; Generated by the Paths of Empyrean Synthesis patcher: final stats of PoE copies (Masterwork, Quality, variants, ...)",
                    "; derived from their originals' stats after the other SkyPatcher rules. Kept deeper than any other SkyPatcher file so it",
                    "; loads last (SkyPatcher walks folders breadth-first). Overwritten every run.",
                };
                foreach (var st in states.Values.Where(s => s.Type == type && s.Touched && s.Copies.Count > 0).OrderBy(s => s.Edid, StringComparer.OrdinalIgnoreCase))
                {
                    originals++;
                    var plain = CopyStats.Read(st.Record);
                    foreach (var copy in st.Copies)
                    {
                        var d = ctx.DeriveCopy(copy, st.Stats);
                        var sets = new List<string> { $"{objects}={copy.ModKey.FileName}|{copy.ID:X}" };
                        if (type == "Armor") sets.Add($"damageResist={(int)MathF.Round(d.Rating)}");
                        else
                        {
                            sets.Add($"attackDamage={F(MathF.Truncate(d.Rating))}");
                            if (type == "Weapon") sets.AddRange(new[] { $"critDamage={F(MathF.Truncate(d.Crit))}", $"speed={F(d.Speed)}", $"reach={F(d.Reach)}" });
                        }
                        sets.Add($"value={(int)MathF.Round(d.Value)}");
                        sets.Add($"weight={F(d.Weight)}");
                        // names only when SkyPatcher renamed the original, so the copy follows
                        if (st.Stats.Name != plain.Name && !d.Name.Contains('~')) sets.Add($"fullName=~{d.Name}~");
                        lines.Add(string.Join(":", sets));
                        copies++;
                    }
                }
                WriteDeep(type, folder, lines);
            }
            if (skippedLines > 0) Console.WriteLine($"SkyPatcher: {skippedLines} stat lines use filters this patcher can't evaluate and were not replayed");
            Console.WriteLine($"SkyPatcher changes the stats of {originals} originals; {copies} copies re-derived from them");
        }

        /// <summary>Write one folder deeper than any SkyPatcher INI of this type, so a breadth-first walk reaches it last.</summary>
        void WriteDeep(string type, string folder, List<string> lines)
        {
            var roots = ctx.DataDirs("SKSE", "Plugins", "SkyPatcher", folder).ToList();
            int depth = roots.SelectMany(r => Directory.EnumerateFiles(r, "*.ini", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith(Stem, StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetRelativePath(r, f).Count(c => c == '/' || c == '\\')))
                .DefaultIfEmpty(0).Max() + 1;
            var baseDir = Path.Combine(ctx.State.DataFolderPath, "SKSE", "Plugins", "SkyPatcher", folder);
            // remove an earlier run's file wherever it sat
            foreach (var r in roots)
            {
                foreach (var old in Directory.EnumerateFiles(r, Stem + ".ini", SearchOption.AllDirectories)) File.Delete(old);
            }
            if (lines.Count <= 3) return;
            var dir = baseDir;
            for (int i = 0; i < depth; i++) dir = Path.Combine(dir, "zzzz_PoE_Synthesis_last");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Stem + ".ini");
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
            Console.WriteLine($"Wrote {path}");
        }
    }
}
