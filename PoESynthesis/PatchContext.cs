using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace PoESynthesis
{
    /// <summary>Load-order state shared by every sub-patcher: what may be patched, what is already patched, and lookups.</summary>
    public class PatchContext
    {
        public static readonly ModKey PoEKey = ModKey.FromNameAndExtension("PathsOfEmpyrean.esp");
        public const string Prefix = "PoE_";

        public readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> State;
        public readonly Settings Settings;
        public ISkyrimMod Patch => State.PatchMod;
        public ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache => State.LinkCache;

        /// <summary>PathsOfEmpyrean.esp and every plugin that has it as a master.</summary>
        public readonly HashSet<ModKey> PoEPlugins = new();
        /// <summary>Records overridden (not defined) by a PoE plugin: PoE already handled them.</summary>
        readonly HashSet<FormKey> poeTouched = new();
        readonly HashSet<ModKey> excluded;
        readonly HashSet<ModKey> only;

        readonly Dictionary<FormKey, string> keywordEdids = new();
        readonly Dictionary<string, FormKey> keywordsByEdid = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>EditorIDs of item-ish records in the load order, plus everything this run creates.</summary>
        readonly HashSet<string> edids = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>FLST EditorID -> EditorIDs SkyPatcher adds to it at runtime.</summary>
        readonly Dictionary<string, HashSet<string>> skyPatcherLists = new(StringComparer.OrdinalIgnoreCase);

        public readonly PoERecords PoE;
        public readonly RecipeIndex Recipes;

        public PatchContext(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Settings settings)
        {
            State = state;
            Settings = settings;
            excluded = settings.General.ExcludedPlugins.ToHashSet();
            only = settings.General.OnlyPlugins.ToHashSet();

            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                var mod = listing.Mod;
                // the outgoing patch carries earlier patchers' work in the same group (which may reference PoE); it is not a PoE patch
                if (mod == null || listing.ModKey == state.PatchMod.ModKey) continue;
                if (listing.ModKey == PoEKey || mod.ModHeader.MasterReferences.Any(m => m.Master == PoEKey))
                {
                    PoEPlugins.Add(listing.ModKey);
                }
            }
            foreach (var key in PoEPlugins)
            {
                foreach (var rec in state.LoadOrder[key].Mod!.EnumerateMajorRecords())
                {
                    if (rec.FormKey.ModKey != key) poeTouched.Add(rec.FormKey);
                }
            }
            Console.WriteLine($"PoE plugins: {string.Join(", ", PoEPlugins)} ({poeTouched.Count} overridden records)");

            foreach (var kw in state.LoadOrder.PriorityOrder.Keyword().WinningOverrides())
            {
                if (kw.EditorID == null) continue;
                keywordEdids[kw.FormKey] = kw.EditorID;
                keywordsByEdid.TryAdd(kw.EditorID, kw.FormKey);
            }

            var lo = state.LoadOrder.PriorityOrder;
            AddEdids(lo.Armor().WinningOverrides());
            AddEdids(lo.Weapon().WinningOverrides());
            AddEdids(lo.Ammunition().WinningOverrides());
            AddEdids(lo.MiscItem().WinningOverrides());
            AddEdids(lo.ConstructibleObject().WinningOverrides());
            AddEdids(lo.LeveledItem().WinningOverrides());
            AddEdids(lo.MagicEffect().WinningOverrides());
            AddEdids(lo.Spell().WinningOverrides());
            AddEdids(lo.FormList().WinningOverrides());
            AddEdids(lo.Race().WinningOverrides());
            AddEdids(lo.Projectile().WinningOverrides());
            AddEdids(lo.Static().WinningOverrides());

            if (settings.General.ReadSkyPatcherFormLists) ReadSkyPatcherFormLists();

            PoE = new PoERecords(this);
            Recipes = new RecipeIndex(this);
        }

        void AddEdids(IEnumerable<IMajorRecordGetter> records)
        {
            foreach (var r in records)
            {
                if (r.EditorID != null) edids.Add(r.EditorID);
            }
        }

        void ReadSkyPatcherFormLists()
        {
            var dir = Path.Combine(State.DataFolderPath, "SKSE", "Plugins", "SkyPatcher", "formList");
            if (!Directory.Exists(dir)) return;
            int lines = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*.ini", SearchOption.AllDirectories))
            {
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Split(';')[0].Trim();
                    // filterByFormLists=A,B:formsToAdd=x,y
                    if (!line.StartsWith("filterByFormLists=", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line.Substring("filterByFormLists=".Length).Split(':');
                    var adds = parts.Skip(1).FirstOrDefault(p => p.StartsWith("formsToAdd=", StringComparison.OrdinalIgnoreCase));
                    if (adds == null) continue;
                    var forms = adds.Substring("formsToAdd=".Length).Split(',').Select(SkyPatcherToken).ToList();
                    foreach (var list in parts[0].Split(',').Select(SkyPatcherToken))
                    {
                        if (!skyPatcherLists.TryGetValue(list, out var set))
                        {
                            skyPatcherLists[list] = set = new(StringComparer.OrdinalIgnoreCase);
                        }
                        set.UnionWith(forms);
                    }
                    lines++;
                }
            }
            Console.WriteLine($"Read {lines} SkyPatcher formList entries from {dir}");
        }

        /// <summary>SkyPatcher forms are an EditorID or Plugin.esp|0xFormID; both are normalised to text we can compare.</summary>
        static string SkyPatcherToken(string s)
        {
            s = s.Trim();
            var bar = s.IndexOf('|');
            if (bar < 0) return s;
            var id = s.Substring(bar + 1).Trim();
            if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) id = id.Substring(2);
            return uint.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out var n)
                ? $"{n & 0xFFFFFF:X6}:{s.Substring(0, bar).Trim()}"
                : s;
        }

        // ---------------- scope ----------------

        /// <summary>The record comes from a plugin this patcher works on (as the xEdit patcher's "source file").</summary>
        public bool InScope(FormKey fk)
        {
            var mod = fk.ModKey;
            if (PoEPlugins.Contains(mod) || excluded.Contains(mod)) return false;
            return only.Count == 0 || only.Contains(mod);
        }

        public bool InScope(IMajorRecordGetter r) => InScope(r.FormKey);

        /// <summary>PoE or a PoE patch already overrides this record.</summary>
        public bool HandledByPoE(FormKey fk) => Settings.General.SkipRecordsHandledByPoEPatches && poeTouched.Contains(fk);

        /// <summary>In scope and not already handled by PoE.</summary>
        public bool Patchable(IMajorRecordGetter r) => InScope(r.FormKey) && !HandledByPoE(r.FormKey);

        /// <summary>May this item get copies, new recipes or breakdowns? Scripted items are left alone by default.</summary>
        public bool CanCopy(IMajorRecordGetter r) =>
            !(Settings.General.SkipScriptedItems && r is IHaveVirtualMachineAdapterGetter v && v.VirtualMachineAdapter?.Scripts.Count > 0);

        // ---------------- EditorIDs ----------------

        public bool EdidExists(string edid) => edids.Contains(edid);
        public void RegisterEdid(string edid) => edids.Add(edid);

        /// <summary>StringReplace(edid, 'PoE_', '') like the xEdit scripts.</summary>
        public static string StripPrefix(string? edid) => (edid ?? "").Replace(Prefix, "");

        // ---------------- keywords ----------------

        public string? KeywordEdid(FormKey fk) => keywordEdids.TryGetValue(fk, out var e) ? e : null;

        public FormKey? KeywordByEdid(string edid) => keywordsByEdid.TryGetValue(edid, out var fk) ? fk : null;

        /// <summary>xEdit HasKeyword(rc, 'EditorID'): compares keyword EditorIDs.</summary>
        public bool HasKeyword(IKeywordedGetter<IKeywordGetter> r, string edid)
        {
            if (r.Keywords == null) return false;
            foreach (var k in r.Keywords)
            {
                if (keywordEdids.TryGetValue(k.FormKey, out var e) && e.Equals(edid, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool HasKeyword(IKeywordedGetter<IKeywordGetter> r, FormKey? kw) =>
            kw != null && r.Keywords != null && r.Keywords.Any(k => k.FormKey == kw.Value);

        public bool HasAnyKeyword(IKeywordedGetter<IKeywordGetter> r, IEnumerable<FormKey?> kws) => kws.Any(k => HasKeyword(r, k));

        public static bool AddKeyword(IKeyworded<IKeywordGetter> r, FormKey? kw)
        {
            if (kw == null) return false;
            r.Keywords ??= new();
            if (r.Keywords.Any(k => k.FormKey == kw.Value)) return false;
            r.Keywords.Add(kw.Value.ToLink<IKeywordGetter>());
            return true;
        }

        // ---------------- form lists ----------------

        /// <summary>Is the record already in the list, either in the winning FLST or through a SkyPatcher INI?</summary>
        public bool InFormList(FormKey? list, IMajorRecordGetter rec)
        {
            if (list == null) return false;
            if (Cache.TryResolve<IFormListGetter>(list.Value, out var flst))
            {
                if (flst.Items.Any(i => i.FormKey == rec.FormKey)) return true;
                if (flst.EditorID != null && skyPatcherLists.TryGetValue(flst.EditorID, out var set))
                {
                    if (rec.EditorID != null && set.Contains(rec.EditorID)) return true;
                    if (set.Contains($"{rec.FormKey.ID:X6}:{rec.FormKey.ModKey.FileName}")) return true;
                }
            }
            return false;
        }

        public bool AddToFormList(FormKey? list, IMajorRecordGetter rec)
        {
            if (list == null || InFormList(list, rec)) return false;
            var flst = Patch.FormLists.GetOrAddAsOverride(Cache.Resolve<IFormListGetter>(list.Value));
            flst.Items.Add(rec.FormKey.ToLink<ISkyrimMajorRecordGetter>());
            return true;
        }

        // ---------------- records ----------------

        /// <summary>The newest version of a record: this patch's copy if it has one, else the load-order winner.</summary>
        public TGetter? Current<TGetter>(FormKey fk) where TGetter : class, IMajorRecordGetter =>
            Cache.TryResolve<TGetter>(fk, out var r) ? r : null;

        public string Edid(FormKey fk) =>
            Cache.TryResolve<IMajorRecordGetter>(fk, out var r) ? r.EditorID ?? fk.ToString() : fk.ToString();

        // ---------------- logging ----------------

        public void Log(string msg)
        {
            if (Settings.General.Verbose) Console.WriteLine(msg);
        }

        public static string Show(IMajorRecordGetter r) => $"{r.EditorID ?? "<no edid>"} [{r.FormKey}]";
    }
}
