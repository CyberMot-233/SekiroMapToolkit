using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SoulsFormats;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;

namespace SekiroMapToolkit.Core
{
    // Converts Sekiro (MSBS + FLVER2) map assets into a Blender-importable scene bundle:
    //   <out>/<map>/meshes.bin  - raw mesh buffers (already converted to Blender Z-up space)
    //   <out>/<map>/scene.json  - object placement (model instance transforms) + materials
    //   <out>/<map>/tex/*.dds   - textures referenced by the materials
    // build_blend.py then turns that into a .blend file.
    static class SekiroExporter
    {
        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ---------------- public entry ----------------

        public static StatsDef ExportMap(string root, string outRoot, string mapName, int limitModels,
                                         Action<string> log, CancellationToken ct, bool skipFog = true)
        {
            log ??= delegate { };
            var sw = Stopwatch.StartNew();
            var msbPath = Path.Combine(root, "map", "mapstudio", mapName + ".msb.dcx");
            if (!File.Exists(msbPath))
            {
                log("[跳过] " + mapName + "：找不到 " + msbPath);
                return null;
            }

            var outDir = Path.Combine(outRoot, mapName);
            var texDir = Path.Combine(outDir, "tex");
            Directory.CreateDirectory(texDir);

            var msb = MSBS.Read(msbPath);

            var mapPieceFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in msb.Models.MapPieces)
                mapPieceFolder[m.Name] = FolderFromSib(m.SibPath);
            var objModels = new HashSet<string>(msb.Models.Objects.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);

            var placements = new List<(string PutName, string Model, Vector3 Pos, Vector3 Rot, Vector3 Scale)>();
            foreach (var p in msb.Parts.MapPieces)
                placements.Add((p.Name, p.ModelName, p.Position, p.Rotation, p.Scale));
            foreach (var p in msb.Parts.Objects)
            {
                if (objModels.Count > 0 && !objModels.Contains(p.ModelName) && mapPieceFolder.ContainsKey(p.ModelName))
                    continue;
                placements.Add((p.Name, p.ModelName, p.Position, p.Rotation, p.Scale));
            }

            var modelOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pl in placements)
                if (seen.Add(pl.Model)) modelOrder.Add(pl.Model);
            if (limitModels > 0) modelOrder = modelOrder.Take(limitModels).ToList();
            var modelIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < modelOrder.Count; i++) modelIndex[modelOrder[i]] = i;

            log("[" + mapName + "] 部件=" + placements.Count + " 模型=" + modelOrder.Count +
                "（地图块=" + msb.Models.MapPieces.Count + " 物件=" + msb.Models.Objects.Count + "）");

            var tex = new TextureResolver(root, texDir);
            var mtds = new MtdBank(root);
            var materials = new List<MatDef>();
            var matKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            var missingModels = new List<string>();
            int fogSkipped = 0;
            int meshWritten = 0;
            int totalVerts = 0;

            var binPath = Path.Combine(outDir, "meshes.bin");
            using (var fs = new FileStream(binPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(new[] { (byte)'S', (byte)'K', (byte)'M', (byte)'S' });
                bw.Write(1);
                bw.Write(modelOrder.Count);

                foreach (var model in modelOrder)
                {
                    ct.ThrowIfCancellationRequested();

                    var bndPath = ResolveModelBnd(root, model, mapPieceFolder, out bool isMapPiece);
                    if (bndPath == null)
                    {
                        missingModels.Add(model);
                        WriteEmptyMesh(bw, model);
                        meshWritten++;
                        continue;
                    }
                    try
                    {
                        var bnd = BND4.Read(bndPath);
                        var flverBytes = PickFlver(bnd, model);
                        if (flverBytes == null)
                        {
                            missingModels.Add(model);
                            WriteEmptyMesh(bw, model);
                            meshWritten++;
                            continue;
                        }
                        var fl = FLVER2.Read(new MemoryStream(flverBytes));

                        if (!isMapPiece)
                            tex.AddEmbeddedTpf(bnd);

                        var subs = new List<(int matIdx, List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv, List<int> idx)>();
                        foreach (var mesh in fl.Meshes)
                        {
                            if (mesh.Vertices.Count == 0) continue;
                            var mat = (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < fl.Materials.Count)
                                ? fl.Materials[mesh.MaterialIndex] : null;

                            if (skipFog && IsFogShell(mat, mtds)) { fogSkipped++; continue; }

                            var matKey = MaterialKey(mat, mtds);
                            if (!matKeys.TryGetValue(matKey, out int mi))
                            {
                                mi = materials.Count;
                                matKeys[matKey] = mi;
                                materials.Add(BuildMatDef(mat, mi, tex, mtds));
                            }

                            var pos = new List<Vector3>(mesh.Vertices.Count);
                            var nrm = new List<Vector3>(mesh.Vertices.Count);
                            var uv = new List<Vector2>(mesh.Vertices.Count);
                            foreach (var v in mesh.Vertices)
                            {
                                pos.Add(Axis.ToBlender(v.Position));
                                nrm.Add(Axis.ToBlenderNormal(v.Normal));
                                var t = v.UVs.Count > 0 ? new Vector2(v.UVs[0].X, 1f - v.UVs[0].Y) : new Vector2(0, 0);
                                uv.Add(t);
                            }
                            var idx = new List<int>();
                            foreach (var fset in mesh.FaceSets)
                            {
                                var ind = fset.Indices;
                                if (fset.TriangleStrip)
                                {
                                    for (int i = 0; i + 2 < ind.Count; i++)
                                    {
                                        if ((i & 1) == 0) { idx.Add(ind[i]); idx.Add(ind[i + 1]); idx.Add(ind[i + 2]); }
                                        else { idx.Add(ind[i + 1]); idx.Add(ind[i]); idx.Add(ind[i + 2]); }
                                    }
                                }
                                else
                                {
                                    for (int i = 0; i + 2 < ind.Count; i += 3)
                                    { idx.Add(ind[i]); idx.Add(ind[i + 1]); idx.Add(ind[i + 2]); }
                                }
                            }
                            if (idx.Count == 0) continue;
                            subs.Add((mi, pos, nrm, uv, idx));
                            totalVerts += pos.Count;
                        }

                        WriteName(bw, model);
                        bw.Write(subs.Count);
                        foreach (var s in subs)
                        {
                            bw.Write(s.matIdx);
                            bw.Write(s.pos.Count);
                            bw.Write(s.idx.Count);
                            var buf = new float[s.pos.Count * 8];
                            for (int i = 0; i < s.pos.Count; i++)
                            {
                                buf[i * 8 + 0] = s.pos[i].X; buf[i * 8 + 1] = s.pos[i].Y; buf[i * 8 + 2] = s.pos[i].Z;
                                buf[i * 8 + 3] = s.nrm[i].X; buf[i * 8 + 4] = s.nrm[i].Y; buf[i * 8 + 5] = s.nrm[i].Z;
                                buf[i * 8 + 6] = s.uv[i].X; buf[i * 8 + 7] = s.uv[i].Y;
                            }
                            var bytes = new byte[buf.Length * 4];
                            Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
                            bw.Write(bytes);
                            var ib = new byte[s.idx.Count * 4];
                            Buffer.BlockCopy(s.idx.ToArray(), 0, ib, 0, ib.Length);
                            bw.Write(ib);
                        }
                        meshWritten++;
                        if ((meshWritten % 100) == 0)
                            log("   .. " + meshWritten + "/" + modelOrder.Count + " 模型, 顶点=" + totalVerts.ToString("N0") + ", 贴图=" + tex.FoundCount);
                    }
                    catch (Exception e)
                    {
                        log("   [警告] 模型 " + model + "：" + e.GetType().Name + " " + e.Message);
                        missingModels.Add(model);
                        WriteEmptyMesh(bw, model);
                        meshWritten++;
                    }
                }

                tex.ResolveMapTextures();

                foreach (var md in materials)
                {
                    md.albedo = tex.FileNameOf(md.albedoPath);
                    md.normal = tex.FileNameOf(md.normalPath);
                }

                var objects = new List<ObjDef>();
                int placed = 0;
                foreach (var pl in placements)
                {
                    if (!modelIndex.TryGetValue(pl.Model, out int mi)) continue;
                    var m3 = Axis.PartMatrix(pl.Rot, pl.Scale);
                    var q = Axis.QuaternionFrom(m3);
                    var pos = Axis.ToBlender(pl.Pos);
                    var scale = new Vector3(pl.Scale.X, pl.Scale.Z, pl.Scale.Y);
                    objects.Add(new ObjDef
                    {
                        name = pl.PutName,
                        mesh = mi,
                        loc = new[] { Rf(pos.X), Rf(pos.Y), Rf(pos.Z) },
                        quat = new[] { Rf(q.W), Rf(q.X), Rf(q.Y), Rf(q.Z) },
                        scale = new[] { Rf(scale.X), Rf(scale.Y), Rf(scale.Z) }
                    });
                    placed++;
                    if (placed >= 200000) break;
                }

                var scene = new SceneDef
                {
                    map = mapName,
                    meshCount = modelOrder.Count,
                    materials = materials,
                    objects = objects,
                    missingModels = missingModels,
                    missingTextures = tex.Missing.ToList(),
                    stats = new StatsDef
                    {
                        parts = placements.Count,
                        models = modelOrder.Count,
                        objectsPlaced = objects.Count,
                        vertices = totalVerts,
                        textures = tex.FoundCount,
                        seconds = (int)sw.Elapsed.TotalSeconds,
                        fogSkipped = fogSkipped
                    }
                };
                File.WriteAllText(Path.Combine(outDir, "scene.json"), JsonSerializer.Serialize(scene, JsonOpts), new UTF8Encoding(false));

                log("[完成] " + mapName + "：云雾跳过=" + fogSkipped + " 物件=" + objects.Count + " 网格=" + modelOrder.Count +
                    " 顶点=" + totalVerts.ToString("N0") + " 贴图=" + tex.FoundCount + " 缺贴图=" + tex.Missing.Count +
                    " 缺模型=" + missingModels.Count + " 用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + " 秒");
                return scene.stats;
            }
        }

        // every map that has a mapstudio msb
        public static List<string> ListMaps(string root)
        {
            var dir = Path.Combine(root ?? "", "map", "mapstudio");
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.msb.dcx")
                            .Select(f => Path.GetFileName(f))
                            .Select(n => n.Substring(0, n.Length - ".msb.dcx".Length))
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }

        public static SceneDef LoadScene(string dataDir)
        {
            var p = Path.Combine(dataDir, "scene.json");
            if (!File.Exists(p)) return null;
            try { return JsonSerializer.Deserialize<SceneDef>(File.ReadAllText(p, Encoding.UTF8)); }
            catch { return null; }
        }

        // ---------------- internals ----------------

        static void WriteName(BinaryWriter bw, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            bw.Write(b.Length);
            bw.Write(b);
        }

        static void WriteEmptyMesh(BinaryWriter bw, string model)
        {
            WriteName(bw, model);
            bw.Write(0);
        }

        static string FolderFromSib(string sib)
        {
            if (string.IsNullOrEmpty(sib)) return null;
            var parts = sib.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], "map", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                    return parts[i + 1];
            return null;
        }

        static string ResolveModelBnd(string root, string model, Dictionary<string, string> mapPieceFolder, out bool isMapPiece)
        {
            isMapPiece = false;
            if (model.Length > 1 && (model[0] == 'm' || model[0] == 'M'))
            {
                var suffix = model.Substring(1);
                var folders = new List<string>();
                if (mapPieceFolder.TryGetValue(model, out var f) && !string.IsNullOrEmpty(f)) folders.Add(f);
                if (!folders.Contains("m11_00_00_00")) folders.Add("m11_00_00_00");
                folders.AddRange(new[] { "m10_00_00_00", "m11_01_00_00", "m11_02_00_00", "m13_00_00_00", "m15_00_00_00", "m17_00_00_00", "m20_00_00_00", "m25_00_00_00", "m89_00_00_99" });
                foreach (var folder in folders)
                {
                    var p = Path.Combine(root, "map", folder, folder + "_" + suffix + ".mapbnd.dcx");
                    if (File.Exists(p)) { isMapPiece = true; return p; }
                }
            }
            var objPath = Path.Combine(root, "obj", model + ".objbnd.dcx");
            if (File.Exists(objPath)) return objPath;
            return null;
        }

        static byte[] PickFlver(BND4 bnd, string model)
        {
            var cands = bnd.Files.Where(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase) ||
                                             f.Name.EndsWith(".flver.dcx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cands.Count == 0) return null;
            var main = cands.FirstOrDefault(f => !Path.GetFileName(f.Name).Replace(".dcx", "").Contains("_S.flver")) ?? cands[0];
            return main.Bytes;
        }

        static readonly Regex SlotRe = new Regex(@"Texture2D_(\d+)", RegexOptions.Compiled);

        static (string albedo, string normal) PickTextures(FLVER2.Material mat, MtdBank mtds)
        {
            if (mat?.Textures == null) return (null, null);
            var mtd = mtds?.Get(ShortMtd(mat.MTD));
            string albedo = null, normal = null, albedo2 = null, normal2 = null;
            foreach (var t in mat.Textures)
            {
                var type = t.ParamName ?? "";
                var path = !string.IsNullOrEmpty(t.Path) ? t.Path : MtdBank.PathFor(mtd, type);
                if (string.IsNullOrEmpty(path)) continue;
                var up = type.ToUpperInvariant();
                bool isA2 = up.Contains("ALBEDO_2") || up == "G_DIFFUSETEXTURE2" || up == "G_DIFFUSE2" || up == "G_DIFFUSE_2";
                bool isA1 = !isA2 && (up.Contains("ALBEDO") || up == "G_DIFFUSE" || up == "G_DIFFUSETEXTURE");
                bool isN2 = up.Contains("NORMAL_2") || up == "G_BUMPMAP2" || up == "G_BUMPMAP_2" || up == "G_BUMPMAPTEXTURE2";
                bool isN1 = !isN2 && (up.Contains("NORMAL") || up.Contains("BUMPMAP"));
                if (isA2) { if (albedo2 == null) albedo2 = path; }
                else if (isA1) { if (albedo == null) albedo = path; }
                else if (isN2) { if (normal2 == null) normal2 = path; }
                else if (isN1) { if (normal == null) normal = path; }
            }
            return (albedo ?? albedo2, normal ?? normal2);
        }

        static bool IsFogShell(FLVER2.Material mat, MtdBank mtds)
        {
            if (mat == null) return false;
            var (a, n) = PickTextures(mat, mtds);
            if (a != null) return false;
            var mtdName = ShortMtd(mat.MTD).ToLowerInvariant();
            if (mtdName.Contains("cloud") || mtdName.Contains("fog")) return true;
            var name = mat.Name ?? "";
            return name.Contains("雲") || name.Contains("云") || name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string MaterialKey(FLVER2.Material mat, MtdBank mtds)
        {
            if (mat == null) return "<none>";
            var (a, n) = PickTextures(mat, mtds);
            return (mat.Name ?? "") + "|" + (a ?? "") + "|" + (n ?? "");
        }

        static MatDef BuildMatDef(FLVER2.Material mat, int idx, TextureResolver tex, MtdBank mtds)
        {
            var (a, n) = PickTextures(mat, mtds);
            var def = new MatDef { index = idx, name = mat?.Name ?? ("mat" + idx), albedo = "", normal = "", mtd = ShortMtd(mat?.MTD) };
            if (a != null) { tex.Request(a); def.albedoPath = a; }
            if (n != null) { tex.Request(n); def.normalPath = n; }
            return def;
        }

        static string ShortMtd(string mtd)
        {
            if (string.IsNullOrEmpty(mtd)) return "";
            var p = mtd.Replace('\\', '/');
            return p.Substring(p.LastIndexOf('/') + 1);
        }

        static float Rf(float v) => (float)Math.Round(v, 6);
    }
}