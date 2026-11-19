using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace SekiroMapToolkit.Core
{
    // ---------------- texture resolution ----------------
    // Walks the textures the FLVER materials ask for and pulls the actual DDS out of
    // either the object binder (embedded TPF) or the split map tpf binders.
    class TextureResolver
    {
        readonly string root;
        readonly string texDir;
        readonly Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Missing { get; } = new List<string>();
        public int FoundCount => resolved.Count;

        public TextureResolver(string root, string texDir) { this.root = root; this.texDir = texDir; }

        public static string Normalize(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return "";
            var s = pathOrName.Replace('\\', '/');
            s = s.Substring(s.LastIndexOf('/') + 1).ToLowerInvariant();
            foreach (var ext in new[] { ".tif", ".tga", ".dds", ".png", ".bmp", ".dcx", ".tpf", ".jpg" })
                while (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - ext.Length);
            return s;
        }

        static string PrefixHint(string name)
        {
            var mm = Regex.Match(name, @"^(m\d+)_");
            return mm.Success ? mm.Groups[1].Value : null;
        }

        static string DirHintOf(string path)
        {
            var s = path.Replace('\\', '/').ToLowerInvariant();
            var mm = Regex.Match(s, @"/map/(m\d+)/");
            return mm.Success ? mm.Groups[1].Value : null;
        }

        public string Request(string texPath)
        {
            var name = Normalize(texPath);
            if (name.Length == 0) return null;
            if (resolved.TryGetValue(name, out var f)) return f;
            if (!pending.ContainsKey(name)) pending[name] = DirHintOf(texPath) ?? PrefixHint(name);
            return null;
        }

        public string FileNameOf(string texPath)
        {
            var name = Normalize(texPath);
            if (name.Length == 0) return "";
            return resolved.TryGetValue(name, out var f) ? f : "";
        }

        public void AddEmbeddedTpf(BND4 bnd)
        {
            foreach (var file in bnd.Files)
            {
                if (!file.Name.Replace(".dcx", "").EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var tpf = TPF.Read(new MemoryStream(file.Bytes));
                    foreach (var t in tpf.Textures)
                    {
                        if (t.Bytes == null || t.Bytes.Length < 128) continue;
                        if (t.Bytes[0] != 'D' || t.Bytes[1] != 'D' || t.Bytes[2] != 'S') continue;
                        var key = Normalize(t.Name);
                        if (key.Length == 0) continue;
                        var fn = key + ".dds";
                        if (!File.Exists(Path.Combine(texDir, fn))) File.WriteAllBytes(Path.Combine(texDir, fn), t.Bytes);
                        resolved[key] = fn;
                        foreach (var alias in Aliases(file.Name)) resolved[alias] = fn;
                        foreach (var alias in Aliases(t.Name)) resolved[alias] = fn;
                    }
                }
                catch { }
            }
        }

        // every plausible spelling a flver path may use for this texture
        static IEnumerable<string> Aliases(string pathOrName)
        {
            var b = Normalize(pathOrName);
            if (b.Length == 0) yield break;
            yield return b;
            if (b.Length > 1 && b[0] == 'm' && char.IsDigit(b[1])) yield return b.Substring(1);
            var mm = Regex.Match(b, @"^m\d+_(.+)$");
            if (mm.Success) yield return mm.Groups[1].Value;
        }

        // resolve every still-pending texture from the split map tpf binders
        public void ResolveMapTextures()
        {
            if (pending.Count == 0) return;
            var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in pending)
            {
                if (resolved.ContainsKey(kv.Key)) continue;
                foreach (var alias in Aliases(kv.Key))
                    if (!want.ContainsKey(alias)) want[alias] = kv.Key;
            }
            if (want.Count == 0) return;

            var dirs = new List<string>();
            foreach (var kv in pending)
            {
                var hint = kv.Value;
                if (!string.IsNullOrEmpty(hint))
                {
                    var p = Path.Combine(root, "map", hint);
                    if (Directory.Exists(p) && !dirs.Contains(p)) dirs.Add(p);
                }
            }
            var mapRoot = Path.Combine(root, "map");
            if (Directory.Exists(mapRoot))
            {
                foreach (var d in Directory.GetDirectories(mapRoot))
                {
                    var name = Path.GetFileName(d).ToLowerInvariant();
                    if (Regex.IsMatch(name, @"^m\d+$") && !dirs.Contains(d)) dirs.Add(d);
                }
            }

            foreach (var dir in dirs)
            {
                if (want.Count == 0) break;
                foreach (var bhd in Directory.GetFiles(dir, "*.tpfbhd"))
                {
                    var bdt = Path.ChangeExtension(bhd, ".tpfbdt");
                    if (!File.Exists(bdt)) continue;
                    BXF4 bxf = null;
                    try
                    {
                        bxf = BXF4.Read(bhd, bdt);
                        foreach (var f in bxf.Files)
                        {
                            string primary = null;
                            foreach (var alias in Aliases(f.Name))
                                if (want.TryGetValue(alias, out var pk)) { primary = pk; break; }
                            if (primary == null) continue;
                            try
                            {
                                var tpf = TPF.Read(new MemoryStream(f.Bytes));
                                var t = tpf.Textures.FirstOrDefault(x => x.Bytes != null && x.Bytes.Length > 128
                                                                        && x.Bytes[0] == 'D' && x.Bytes[1] == 'D' && x.Bytes[2] == 'S');
                                if (t == null) continue;
                                var fn = primary + ".dds";
                                if (!File.Exists(Path.Combine(texDir, fn))) File.WriteAllBytes(Path.Combine(texDir, fn), t.Bytes);
                                resolved[primary] = fn;
                                foreach (var a in Aliases(f.Name)) if (!resolved.ContainsKey(a)) resolved[a] = fn;
                                foreach (var a in Aliases(t.Name)) if (!resolved.ContainsKey(a)) resolved[a] = fn;
                                var done = Aliases(f.Name).Concat(Aliases(t.Name)).ToList();
                                foreach (var a in done) want.Remove(a);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    finally { bxf = null; }
                }
            }

            foreach (var kv in pending)
                if (!resolved.ContainsKey(kv.Key) && !Missing.Contains(kv.Key)) Missing.Add(kv.Key);
        }
    }

    // ---------------- mtd defaults (same fallback DSMapStudio uses) ----------------
    class MtdBank
    {
        readonly string dir;
        readonly Dictionary<string, MTD> cache = new Dictionary<string, MTD>(StringComparer.OrdinalIgnoreCase);

        public MtdBank(string gameRoot)
        {
            var a = Path.Combine(gameRoot, "mtd", "_allmaterialbnd_unpacked");
            dir = Directory.Exists(a) ? a : (Directory.Exists(Path.Combine(gameRoot, "mtd")) ? Path.Combine(gameRoot, "mtd") : null);
        }

        public MTD Get(string mtdFileName)
        {
            if (string.IsNullOrEmpty(mtdFileName) || dir == null) return null;
            if (cache.TryGetValue(mtdFileName, out var hit)) return hit;
            MTD mtd = null;
            var p = Path.Combine(dir, mtdFileName);
            if (File.Exists(p)) { try { mtd = MTD.Read(p); } catch { mtd = null; } }
            cache[mtdFileName] = mtd;
            return mtd;
        }

        public static string PathFor(MTD mtd, string type)
        {
            var t = mtd?.Textures?.FirstOrDefault(x => x.Type == type);
            if (t == null || !t.Extended || string.IsNullOrEmpty(t.Path)) return null;
            return t.Path;
        }
    }
}