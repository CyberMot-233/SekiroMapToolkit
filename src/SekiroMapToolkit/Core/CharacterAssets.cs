using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SoulsFormats;

namespace SekiroMapToolkit.Core
{
    /// <summary>一个角色的网格（已转换到 Y-up）。</summary>
    class ChrMesh
    {
        public string Name;
        public List<Vector3> Positions = new List<Vector3>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<Vector2> Uvs = new List<Vector2>();
        public List<int> Indices = new List<int>();

        /// <summary>每顶点的骨骼权重（最多 4 根）。空表示无蒙皮。</summary>
        public List<BoneWeight> Weights = new List<BoneWeight>();

        public string MaterialName = "";
        public List<string> Textures = new List<string>();
    }

    struct BoneWeight
    {
        public int B0, B1, B2, B3;
        public float W0, W1, W2, W3;
    }

    /// <summary>
    /// 只狼角色资源读取：模型（chrbnd 里的 flver）+ 贴图（TPF）。
    ///
    /// 数据布局：
    ///   chr\&lt;id&gt;.chrbnd.dcx      &lt;id&gt;.flver + &lt;id&gt;.hkx（骨架） + tpf 贴图
    ///   chr\&lt;id&gt;.anibnd.dcx     tae\*.tae + hkx\skeleton.hkx
    /// </summary>
    static class CharacterAssets
    {
        /// <summary>读一个角色的模型网格。</summary>
        /// <summary>读一个角色的 flver 原始对象（蒙皮绑定姿势要从它的 Nodes 取）。</summary>
        public static FLVER2 LoadFlver(string root, string chrId)
        {
            var path = Path.Combine(root, "chr", chrId + ".chrbnd.dcx");
            if (!File.Exists(path)) return null;
            try
            {
                var bnd = BND4.Read(path);
                var f = bnd.Files.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(".flver"));
                if (f == null) return null;
                return FLVER2.Read(new MemoryStream(f.Bytes));
            }
            catch { return null; }
        }

        public static List<ChrMesh> LoadMeshes(string root, string chrId, Action<string> log = null)
        {
            var result = new List<ChrMesh>();
            var path = Path.Combine(root, "chr", chrId + ".chrbnd.dcx");
            if (!File.Exists(path))
            {
                log?.Invoke("[模型] 找不到 " + chrId + ".chrbnd.dcx");
                return result;
            }

            BND4 bnd;
            try { bnd = BND4.Read(path); }
            catch (Exception ex) { log?.Invoke("[模型] " + chrId + " 打不开：" + ex.Message); return result; }

            var flverFile = bnd.Files.FirstOrDefault(f =>
                f.Name.ToLowerInvariant().EndsWith(".flver"));
            if (flverFile == null)
            {
                log?.Invoke("[模型] " + chrId + " 里没有 flver");
                return result;
            }

            FLVER2 fl;
            try { fl = FLVER2.Read(new MemoryStream(flverFile.Bytes)); }
            catch (Exception ex) { log?.Invoke("[模型] " + chrId + " flver 解析失败：" + ex.Message); return result; }

            // 贴图路径靠 MTD 查：FLVER 里只有参数名，真正的贴图文件名在 mtd 里
            var mtds = new MtdBank(root);

            int mi = 0;
            foreach (var mesh in fl.Meshes)
            {
                if (mesh.Vertices.Count == 0) continue;
                var mat = (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < fl.Materials.Count)
                    ? fl.Materials[mesh.MaterialIndex] : null;

                var cm = new ChrMesh
                {
                    Name = chrId + "_mesh" + mi,
                    MaterialName = mat?.Name ?? ("mat" + mi),
                };

                foreach (var v in mesh.Vertices)
                {
                    cm.Positions.Add(Axis.ToBlender(v.Position));
                    cm.Normals.Add(Axis.ToBlenderNormal(v.Normal));
                    cm.Uvs.Add(v.UVs.Count > 0
                        ? new Vector2(v.UVs[0].X, 1f - v.UVs[0].Y)
                        : Vector2.Zero);
                }

                foreach (var fset in mesh.FaceSets)
                {
                    var ind = fset.Indices;
                    if (fset.TriangleStrip)
                    {
                        for (int i = 0; i + 2 < ind.Count; i++)
                        {
                            if ((i & 1) == 0) { cm.Indices.Add(ind[i]); cm.Indices.Add(ind[i + 1]); cm.Indices.Add(ind[i + 2]); }
                            else { cm.Indices.Add(ind[i + 1]); cm.Indices.Add(ind[i]); cm.Indices.Add(ind[i + 2]); }
                        }
                    }
                    else
                    {
                        for (int i = 0; i + 2 < ind.Count; i += 3)
                        {
                            cm.Indices.Add(ind[i]); cm.Indices.Add(ind[i + 1]); cm.Indices.Add(ind[i + 2]);
                        }
                    }
                }

                if (cm.Indices.Count == 0) continue;

                // 顶点骨骼权重
                foreach (var v in mesh.Vertices)
                {
                    var bw = new BoneWeight { W0 = 1f };
                    var bi = v.BoneIndices;
                    var bwts = v.BoneWeights;
                    if (bi.Length >= 4 && bwts.Length >= 4)
                    {
                        bw.B0 = bi[0]; bw.B1 = bi[1]; bw.B2 = bi[2]; bw.B3 = bi[3];
                        bw.W0 = bwts[0]; bw.W1 = bwts[1]; bw.W2 = bwts[2]; bw.W3 = bwts[3];
                    }
                    cm.Weights.Add(bw);
                }

                // 材质引用的贴图：FLVER 只给参数名，真名要从 MTD 拿
                if (mat != null)
                {
                    var mtdName = mat.MTD ?? "";
                    var slash = mtdName.Replace('\\', '/');
                    mtdName = slash.Substring(slash.LastIndexOf('/') + 1);
                    var mtd = mtds.Get(mtdName);

                    foreach (var t in mat.Textures)
                    {
                        var type = t.ParamName ?? "";
                        if (string.IsNullOrEmpty(type)) continue;
                        var texPath = !string.IsNullOrEmpty(t.Path) ? t.Path : MtdBank.PathFor(mtd, type);
                        if (string.IsNullOrEmpty(texPath)) continue;
                        var fn = System.IO.Path.GetFileName(texPath.Replace('\\', '/'));
                        if (!string.IsNullOrEmpty(fn) && !cm.Textures.Contains(fn)) cm.Textures.Add(fn);
                    }
                }

                result.Add(cm);
                mi++;
            }

            log?.Invoke("[模型] " + chrId + "：" + result.Count + " 个网格，"
                + result.Sum(m => m.Positions.Count) + " 顶点，材质贴图 "
                + result.Sum(m => m.Textures.Count) + " 项");
            return result;
        }

        /// <summary>收 chrbnd 里所有 TPF 的贴图名。</summary>
        static List<string> CollectTextureNames(BND4 bnd, Action<string> log)
        {
            var names = new List<string>();
            foreach (var f in bnd.Files)
            {
                if (!f.Name.Replace(".dcx", "").EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var tpf = TPF.Read(new MemoryStream(f.Bytes));
                    foreach (var t in tpf.Textures)
                        if (!string.IsNullOrEmpty(t.Name)) names.Add(t.Name);
                }
                catch (Exception ex) { log?.Invoke("[模型] TPF 读取失败：" + ex.Message); }
            }
            return names;
        }

        /// <summary>
        /// 把角色要用的贴图复制出来。
        ///
        /// 只狼角色贴图不在 chrbnd 里，而是散在 parts 的 TPF 里。本工具的解包步骤
        /// 会把它们摊到 &lt;root&gt;\mtd\textures\（文件名形如 chr_c1010_tex_c1010_xxx_a.dds），
        /// 这里按材质引用的基名去那儿找，找不到再退回去用 TextureResolver 现场解包。
        /// </summary>
        public static int ExportTextures(string root, string chrId, string outDir, Action<string> log = null)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in LoadMeshes(root, chrId, null))
                foreach (var t in m.Textures)
                    wanted.Add(t);

            if (wanted.Count == 0) { log?.Invoke("[贴图] " + chrId + " 没有材质贴图引用"); return 0; }

            var dir = Path.Combine(outDir, chrId, "tex");
            Directory.CreateDirectory(dir);

            // 先看已解好的贴图池
            var pool = Path.Combine(root, "mtd", "textures");
            var poolFiles = Directory.Exists(pool)
                ? Directory.GetFiles(pool, "*.dds")
                : Array.Empty<string>();

            int n = 0;
            var missing = new List<string>();
            foreach (var w in wanted)
            {
                var baseName = StripExt(Path.GetFileName(w.Replace('\\', '/')));   // c1010_katana_rope_a
                var hit = poolFiles.FirstOrDefault(f =>
                    StripExt(Path.GetFileName(f)).EndsWith(baseName, StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                {
                    try
                    {
                        File.Copy(hit, Path.Combine(dir, baseName + ".dds"), true);
                        n++;
                        continue;
                    }
                    catch { }
                }
                missing.Add(w);
            }

            // 池子里没有的，现场用 TextureResolver 解
            if (missing.Count > 0)
            {
                try
                {
                    var tex = new TextureResolver(root, dir);
                    foreach (var m in missing) tex.Request(m);
                }
                catch (Exception ex) { log?.Invoke("[贴图] 现场解包失败：" + ex.Message); }
            }

            var got = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dds").Length : 0;
            log?.Invoke("[贴图] " + chrId + "：要 " + wanted.Count + " 张，出 " + got + " 张 → " + dir);
            return got;
        }

        /// <summary>把角色要用的贴图名列出来（不解码，快）。</summary>
        public static List<string> ListTextureNames(string root, string chrId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in LoadMeshes(root, chrId, null))
                foreach (var t in m.Textures)
                    set.Add(t);
            return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string StripExt(string s)
        {
            var i = s.LastIndexOf('.');
            return i > 0 ? s.Substring(0, i) : s;
        }

        /// <summary>去掉扩展名（给外部用）。</summary>
        public static string StripExtPublic(string s) => StripExt(s);

        /// <summary>
        /// 读一个角色的骨架。
        ///
        /// 关键点：&lt;id&gt;.anibnd.dcx 自己就带 skeleton.hkx + *.compendium，
        /// 不需要额外去 _hi 包借。Havok 的 TAG0 里 TCRF 段必须有 compendium 才能解出字段，
        /// 这是硬条件 —— 少了它只会得到「Unexpected signature」。
        /// </summary>
        public static DecodedSkeleton LoadSkeleton(string root, string chrId, Action<string> log = null)
        {
            var anibnd = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
            if (!File.Exists(anibnd)) { log?.Invoke("[骨架] 找不到 " + chrId + ".anibnd.dcx"); return null; }

            BND4 bnd;
            try { bnd = BND4.Read(anibnd); }
            catch (Exception ex) { log?.Invoke("[骨架] " + chrId + " 打不开：" + ex.Message); return null; }

            var skf = bnd.Files.FirstOrDefault(f =>
            {
                var n = Path.GetFileName(f.Name.Replace('\\', '/'));
                return n.StartsWith("skeleton", StringComparison.OrdinalIgnoreCase)
                    && n.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase);
            });
            if (skf == null) { log?.Invoke("[骨架] " + chrId + " 里没有 skeleton.hkx"); return null; }

            var comp = bnd.Files.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(".compendium"))?.Bytes;
            if (comp == null) comp = CompendiumFromHi(root, chrId);
            if (comp == null) { log?.Invoke("[骨架] " + chrId + " 找不到 compendium，TAG0 解不出来"); return null; }

            var sk = HavokAnimation.DecodeSkeleton(skf.Bytes, comp);
            log?.Invoke("[骨架] " + chrId + "：骨骼 " + sk.BoneCount + "（" + sk.Name + "）");
            return sk;
        }

        /// <summary>从 &lt;id&gt;_a000_hi / _lo / 其它 anibnd 分包里借 compendium。</summary>
        static byte[] CompendiumFromHi(string root, string chrId)
        {
            var chrDir = Path.Combine(root, "chr");
            if (!Directory.Exists(chrDir)) return null;

            foreach (var f in Directory.GetFiles(chrDir, chrId + "*.anibnd.dcx"))
            {
                try
                {
                    var b = BND4.Read(f);
                    var c = b.Files.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(".compendium"));
                    if (c != null) return c.Bytes;
                }
                catch { }
            }
            return null;
        }

        /// <summary>读一个角色全部关键帧 HKX（主包里的 a000_*.hkx）。</summary>
        public static List<(string file, byte[] bytes)> LoadAnimHkx(string root, string chrId, Action<string> log = null)
        {
            var list = new List<(string, byte[])>();
            var path = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
            if (!File.Exists(path)) return list;

            BND4 bnd;
            try { bnd = BND4.Read(path); }
            catch (Exception ex) { log?.Invoke("[动画] " + chrId + " 打不开：" + ex.Message); return list; }

            foreach (var f in bnd.Files)
            {
                var n = Path.GetFileName(f.Name.Replace('\\', '/'));
                if (!n.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StartsWith("skeleton", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((n, f.Bytes));
            }
            return list;
        }

        /// <summary>
        /// 解一条动画。file 是 a000_000210.hkx 这种名字，id 取文件名里的十进制数。
        /// </summary>
        public static DecodedAnimation DecodeAnim(string root, string chrId, string file,
                                                  byte[] compendium, Action<string> log = null)
        {
            var path = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
            if (!File.Exists(path)) return null;

            BND4 bnd;
            try { bnd = BND4.Read(path); }
            catch { return null; }

            var hit = bnd.Files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f.Name.Replace('\\', '/')), file, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;

            long id = 0;
            var b = StripExt(file);
            var us = b.LastIndexOf('_');
            if (us >= 0 && us + 1 < b.Length) long.TryParse(b.Substring(us + 1), out id);

            return HavokAnimation.Decode(hit.Bytes, compendium, StripExt(file), id);
        }

        /// <summary>取一个角色的 compendium。</summary>
        public static byte[] GetCompendium(string root, string chrId)
        {
            var path = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
            if (File.Exists(path))
            {
                try
                {
                    var b = BND4.Read(path);
                    var c = b.Files.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(".compendium"));
                    if (c != null) return c.Bytes;
                }
                catch { }
            }
            return CompendiumFromHi(root, chrId);
        }
    }
}
