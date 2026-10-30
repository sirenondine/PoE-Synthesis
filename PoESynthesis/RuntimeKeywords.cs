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
    /// Gives PoE copies (Masterwork, Quality, unenchanted, crossbow/longbow, Lucky/Barbed, ...) the keywords their originals get at
    /// runtime from KID (SkyPatcher keyword changes are handled by <see cref="RuntimeStats"/>, which replays SkyPatcher). A copy already carries the original's plugin keywords, and rules that select by keyword,
    /// EditorID/name substring or traits reach it at runtime anyway. Rules that select the original by identity don't:
    /// its EditorID, FormID, exact name or whole plugin. Those rules are mirrored onto the copies as generated INIs, so KID's
    /// traits and chance (and keywords that only exist at runtime) behave exactly as they do for the original.
    /// </summary>
    public class RuntimeKeywords
    {
        record Original(IMajorRecordGetter Record, string Type, List<FormKey> Copies);

        static readonly Dictionary<string, string> KidTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Armor"] = "Armor", ["Weapon"] = "Weapon", ["Ammo"] = "Ammo",
        };
        static readonly Regex KidFormId = new(@"^(?:0x)?([0-9a-fA-F]+)~(.+)$");
        const int AltsPerLine = 100;

        readonly PatchContext ctx;
        readonly string stem;
        readonly Dictionary<FormKey, Original> originals = new();
        readonly Dictionary<ModKey, List<Original>> byPlugin = new();
        readonly Dictionary<string, List<Original>> byText = new(StringComparer.OrdinalIgnoreCase);

        public RuntimeKeywords(PatchContext ctx)
        {
            this.ctx = ctx;
            stem = Path.GetFileNameWithoutExtension(ctx.Patch.ModKey.FileName) + "_PoECopies";
        }

        string KidFile => stem + "_KID.ini";

        public void Run()
        {
            Console.WriteLine("---Runtime keywords for PoE copies---");
            foreach (var copy in ctx.CopiedFrom.Keys)
            {
                var root = ctx.OriginalOf(copy);
                if (root == copy || !ctx.Cache.TryResolve<IMajorRecordGetter>(root, out var rec)) continue;
                var type = rec switch { IArmorGetter => "Armor", IWeaponGetter => "Weapon", IAmmunitionGetter => "Ammo", _ => null };
                if (type == null) continue;
                if (!originals.TryGetValue(root, out var o))
                {
                    originals[root] = o = new Original(rec, type, new());
                    (byPlugin.TryGetValue(root.ModKey, out var l) ? l : byPlugin[root.ModKey] = new()).Add(o);
                    foreach (var text in new[] { rec.EditorID, (rec as INamedGetter)?.Name }.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        (byText.TryGetValue(text!, out var t) ? t : byText[text!] = new()).Add(o);
                    }
                }
                o.Copies.Add(copy);
            }
            Console.WriteLine($"{ctx.CopiedFrom.Count} copies of {originals.Count} originals");
            if (originals.Count == 0) return;
            Kid();
        }

        // ---------------- KID ----------------

        /// <summary>Does this filter atom pick out exactly this original (not a keyword, not a substring)?</summary>
        bool IsIdentity(string atom, Original o)
        {
            if (atom.StartsWith('*')) return false;
            var m = KidFormId.Match(atom);
            if (m.Success)
            {
                return uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out var id)
                    && (id & 0xFFFFFF) == o.Record.FormKey.ID && m.Groups[2].Value.Trim().Equals(o.Record.FormKey.ModKey.FileName, StringComparison.OrdinalIgnoreCase);
            }
            if (atom.StartsWith('*')) return false;
            if (IsPluginName(atom)) return atom.Equals(o.Record.FormKey.ModKey.FileName, StringComparison.OrdinalIgnoreCase);
            bool sameText = atom.Equals(o.Record.EditorID, StringComparison.OrdinalIgnoreCase)
                || atom.Equals((o.Record as INamedGetter)?.Name, StringComparison.OrdinalIgnoreCase);
            // a keyword the original carries is also on the copy, so it already matches
            return sameText && !(o.Record is IKeywordedGetter<IKeywordGetter> k && ctx.HasKeyword(k, atom));
        }

        static bool IsPluginName(string s) => Regex.IsMatch(s, @"\.(esp|esm|esl)$", RegexOptions.IgnoreCase);

        /// <summary>Originals a positive filter atom could be identifying.</summary>
        IEnumerable<Original> Candidates(string atom)
        {
            if (atom.StartsWith('*')) return Enumerable.Empty<Original>();
            var m = KidFormId.Match(atom);
            if (m.Success)
            {
                if (!uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out var id)
                    || !ModKey.TryFromFileName(m.Groups[2].Value.Trim(), out var mod)) return Enumerable.Empty<Original>();
                var fk = new FormKey(mod, id & 0xFFFFFF);
                return originals.TryGetValue(fk, out var o) ? new[] { o } : Enumerable.Empty<Original>();
            }
            if (IsPluginName(atom)) return ModKey.TryFromFileName(atom, out var pm) && byPlugin.TryGetValue(pm, out var l) ? l : Enumerable.Empty<Original>();
            return byText.TryGetValue(atom, out var t) ? t : Enumerable.Empty<Original>();
        }

        static string KidRef(FormKey fk) => $"0x{fk.ID:X}~{fk.ModKey.FileName}";

        void Kid()
        {
            var output = new Dictionary<string, List<string>>(); // "kw|Type" + "|traits|chance" tail -> filter alternatives
            var order = new List<string>();
            int rules = 0, files = 0;
            foreach (var file in Directory.EnumerateFiles(ctx.State.DataFolderPath, "*_KID.ini")
                         .Where(f => !Path.GetFileName(f).Equals(KidFile, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                files++;
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Split(';')[0].Trim();
                    var eq = line.IndexOf('=');
                    if (eq < 0 || !line.Substring(0, eq).Trim().Equals("Keyword", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line.Substring(eq + 1).Split('|').Select(p => p.Trim()).ToArray();
                    if (parts.Length < 3 || !KidTypes.TryGetValue(parts[1], out var type)) continue;
                    var filter = parts[2];
                    if (filter.Length == 0 || filter.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;

                    var alts = new List<string>();
                    foreach (var alt in filter.Split(','))
                    {
                        var atoms = alt.Split('+').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                        var positives = atoms.Where(a => !a.StartsWith('-')).ToList();
                        var hits = positives.SelectMany(Candidates).Distinct().Where(o => o.Type == type
                            && positives.Any(a => IsIdentity(a, o))).ToList();
                        foreach (var o in hits)
                        {
                            foreach (var copy in o.Copies)
                            {
                                alts.Add(string.Join("+", atoms.Select(a =>
                                {
                                    bool neg = a.StartsWith('-');
                                    var bare = neg ? a.Substring(1).Trim() : a;
                                    return IsIdentity(bare, o) ? (neg ? "-" : "") + KidRef(copy) : a;
                                })));
                            }
                        }
                    }
                    if (alts.Count == 0) continue;
                    rules++;
                    var tail = string.Join("|", parts.Skip(3));
                    var key = parts[0] + "|" + type + "\u0000" + tail;
                    if (!output.TryGetValue(key, out var list))
                    {
                        output[key] = list = new();
                        order.Add(key);
                    }
                    list.AddRange(alts.Where(a => !list.Contains(a)));
                }
            }

            var lines = new List<string>
            {
                "; Generated by the Paths of Empyrean Synthesis patcher: KID rules that pick an armor/weapon/ammo by EditorID, FormID,",
                "; exact name or plugin, mirrored onto the PoE copies (Masterwork, Quality, variants, ...) made from it. Overwritten every run.",
            };
            foreach (var key in order)
            {
                var split = key.Split('\u0000');
                var tail = split[1].TrimEnd('|');
                foreach (var chunk in output[key].Chunk(AltsPerLine))
                {
                    lines.Add($"Keyword = {split[0]}|{string.Join(",", chunk)}" + (tail.Length > 0 ? "|" + tail : ""));
                }
            }
            Write(Path.Combine(ctx.State.DataFolderPath, KidFile), lines, order.Count > 0);
            Console.WriteLine($"KID: {rules} rules from {files} files mirrored onto copies ({order.Count} keyword lines)");
        }

        static void Write(string path, List<string> lines, bool any)
        {
            if (!any)
            {
                // nothing to mirror: drop a stale file from an earlier run
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
            Console.WriteLine($"Wrote {path}");
        }
    }
}
