using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SoulsFormats;

namespace SekiroMapToolkit.Core
{
    /// <summary>一次角色导出的选项。</summary>
    public class ChrExportOptions
    {
        public bool Anim = true;        // 导出动画 FBX
        public bool Mesh = true;        // 连带导出模型网格
        public bool Tex = true;         // 连带导出贴图
        public bool Audio = true;       // 连带导出这些动画事件用到的音频
        public bool RestPose;           // 只出静置姿势（不要动画数据）

        public override string ToString()
        {
            var parts = new List<string>();
            if (Anim) parts.Add("FBX");
            if (Mesh) parts.Add("模型");
            if (Tex) parts.Add("贴图");
            if (Audio) parts.Add("音频");
            if (RestPose) parts.Add("静置");
            return string.Join("+", parts);
        }
    }

    /// <summary>一次角色导出的结果统计。</summary>
    public class ChrExportResult
    {
        public string Character = "";
        public int AnimCount;          // 导出的 FBX 个数
        public int MeshCount;          // 网格数（累计）
        public int VertexCount;        // 顶点数（累计）
        public int TexCount;           // 贴图张数
        public int AudioCount;         // 音频个数
        public long Bytes;             // 总体积
        public string OutputDir = "";
        public List<string> Errors = new List<string>();
        public double Seconds;

        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Character).Append("：");
            sb.Append(AnimCount).Append(" 个 FBX");
            if (MeshCount > 0) sb.Append("，").Append(MeshCount).Append(" 网格/").Append(VertexCount.ToString("N0")).Append(" 顶点");
            if (TexCount > 0) sb.Append("，").Append(TexCount).Append(" 张贴图");
            if (AudioCount > 0) sb.Append("，").Append(AudioCount).Append(" 个音频");
            sb.Append("，").Append((Bytes / 1048576.0).ToString("F1")).Append(" MB");
            sb.Append("，用时 ").Append(Seconds.ToString("F1")).Append(" 秒");
            return sb.ToString();
        }
    }

    /// <summary>把动画、模型、贴图、音频一次性导出去的统一管线。</summary>
    public static class CharacterExport
    {
        /// <summary>
        /// 导出一个角色的若干条动画。
        /// </summary>
        /// <param name="root">解包根目录</param>
        /// <param name="chrId">角色 ID，如 c0000</param>
        /// <param name="animFiles">要出的 hkx 文件名；空 = 全部</param>
        /// <param name="outDir">输出目录</param>
        /// <param name="opt">导出选项</param>
        /// <param name="log">日志回调</param>
        public static ChrExportResult Export(string root, string chrId, IEnumerable<string> animFiles,
                                             string outDir, ChrExportOptions opt, Action<string> log = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new ChrExportResult { Character = chrId };
            opt = opt ?? new ChrExportOptions();

            var chrOut = Path.Combine(outDir, chrId);
            Directory.CreateDirectory(chrOut);
            r.OutputDir = chrOut;

            // ---- 骨架（没有骨架什么都出不了）
            var skel = CharacterAssets.LoadSkeleton(root, chrId, log);
            if (skel == null || skel.BoneCount == 0)
            {
                r.Errors.Add("骨架读取失败，无法导出");
                log?.Invoke("[导出] " + chrId + " 骨架读取失败");
                r.Seconds = sw.Elapsed.TotalSeconds;
                return r;
            }

            var comp = CharacterAssets.GetCompendium(root, chrId);
            if (comp == null)
            {
                r.Errors.Add("缺少 compendium，动画解不出来");
                log?.Invoke("[导出] " + chrId + " 缺少 compendium");
                r.Seconds = sw.Elapsed.TotalSeconds;
                return r;
            }

            // ---- 模型（可选，一次读完反复用）
            List<ChrMesh> meshes = null;
            if (opt.Mesh)
            {
                meshes = CharacterAssets.LoadMeshes(root, chrId, log);
                r.MeshCount = meshes.Count;
                r.VertexCount = meshes.Sum(m => m.Positions.Count);
                if (meshes.Count == 0) log?.Invoke("[导出] " + chrId + " 没有网格，只出骨骼动画");
            }

            // ---- 贴图（可选）
            if (opt.Tex)
                r.TexCount = CharacterAssets.ExportTextures(root, chrId, outDir, log);

            // ---- 选动画
            var all = CharacterAssets.LoadAnimHkx(root, chrId, log);
            var pick = new List<(string file, byte[] bytes)>();
            if (animFiles == null)
            {
                pick.AddRange(all);
            }
            else
            {
                var want = new HashSet<string>(animFiles.Select(f => f ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var a in all)
                    if (want.Contains(a.file) || want.Contains(CharacterAssets.StripExtPublic(a.file)))
                        pick.Add(a);
            }

            if (pick.Count == 0)
            {
                log?.Invoke("[导出] " + chrId + " 没选中动画，只出模型/贴图");
            }

            // ---- 动画 FBX
            var fbxDir = Path.Combine(chrOut, "fbx");
            if (opt.Anim || opt.Mesh)
            {
                Directory.CreateDirectory(fbxDir);

                if (opt.RestPose || pick.Count == 0)
                {
                    // 没有动画也要出模型：造一个单帧静置姿势
                    var rest = RestAnimation(skel);
                    var p = Path.Combine(fbxDir, chrId + "_rest.fbx");
                    SafeWrite(() => FbxAnimWriter.Write(rest, skel, meshes, p), r, log, p);
                    if (!r.Errors.Any(e => e.Contains(p)))
                    {
                        r.AnimCount++;
                        SafeSize(r, p);
                    }
                }
                else
                {
                    foreach (var pair in pick)
                    {
                        var anim = HavokAnimation.Decode(pair.bytes, comp,
                            CharacterAssets.StripExtPublic(pair.file), 0);
                        if (anim == null || !anim.Ok)
                        {
                            r.Errors.Add(pair.file + " 解码失败");
                            continue;
                        }
                        var p = Path.Combine(fbxDir, CharacterAssets.StripExtPublic(pair.file) + ".fbx");
                        SafeWrite(() => FbxAnimWriter.Write(anim, skel, meshes, p), r, log, p);
                        if (!r.Errors.Any(e => e.Contains(p)))
                        {
                            r.AnimCount++;
                            SafeSize(r, p);
                        }
                    }
                }
            }

            // ---- 音频（可选）：这些动画事件里用到的音
            if (opt.Audio && pick.Count > 0)
            {
                try
                {
                    var names = pick.Select(p => CharacterAssets.StripExtPublic(p.file)).ToList();
                    var n = EventAudio.ExportForAnimations(root, chrId, names,
                        Path.Combine(chrOut, "audio"), log);
                    r.AudioCount = n;
                }
                catch (Exception ex)
                {
                    r.Errors.Add("音频导出失败：" + ex.Message);
                    log?.Invoke("[音频] 失败：" + ex.Message);
                }
            }

            r.Seconds = sw.Elapsed.TotalSeconds;
            log?.Invoke("[导出] " + r.Summary() + " → " + chrOut);
            return r;
        }

        /// <summary>只出模型 + 贴图（不带任何动画）。</summary>
        public static ChrExportResult ExportModel(string root, string chrId, string outDir,
                                                  bool withTex, Action<string> log = null)
        {
            var opt = new ChrExportOptions
            {
                Anim = false,
                Mesh = true,
                Tex = withTex,
                Audio = false,
                RestPose = true,
            };
            return Export(root, chrId, null, outDir, opt, log);
        }

        /// <summary>造一个单帧的静置姿势动画（就是骨架的参考姿势）。</summary>
        internal static DecodedAnimation RestAnimation(DecodedSkeleton skel)
        {
            int n = skel.BoneCount;
            var anim = new DecodedAnimation
            {
                Name = skel.Name + "_rest",
                SkeletonName = skel.Name,
                FrameCount = 1,
                FrameDuration = 1f / 30f,
                TrackCount = n,
                TrackToBone = Enumerable.Range(0, n).ToArray(),
            };
            var frame = new DecodedTransform[n];
            for (int i = 0; i < n; i++)
            {
                var m = skel.ReferencePose != null && i < skel.ReferencePose.Length
                    ? skel.ReferencePose[i] : Matrix4x4.Identity;
                Matrix4x4.Decompose(m, out var sc, out var rot, out var tr);
                frame[i] = new DecodedTransform { Translation = tr, Rotation = rot, Scale = sc };
            }
            anim.Frames.Add(frame);
            return anim;
        }

        static void SafeWrite(Action a, ChrExportResult r, Action<string> log, string path)
        {
            try { a(); }
            catch (Exception ex)
            {
                r.Errors.Add(path + "：" + ex.Message);
                log?.Invoke("[导出] 失败 " + Path.GetFileName(path) + "：" + ex.Message);
            }
        }

        static void SafeSize(ChrExportResult r, string path)
        {
            try { r.Bytes += new FileInfo(path).Length; } catch { }
        }
    }
}
