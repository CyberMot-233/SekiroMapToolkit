using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SoulsFormats;
using Sap = SoulsAssetPipeline.Animation;

namespace SekiroMapToolkit.Core
{
    /// <summary>一个角色的全部动画（来自 chr\&lt;id&gt;.anibnd.dcx 里的 *.tae）。</summary>
    class AnimEntry
    {
        public string Character;     // c0000
        public string TaeFile;       // a00.tae
        public long Id;              // 动画 ID
        public string Name;          // a000_000010.hkt
        public int Actions;
        public int Tracks;

        public string Display => Name;
        public string Detail => Character + "  ·  " + TaeFile + "  ·  ID " + Id;
    }

    /// <summary>一个角色的动画汇总。</summary>
    class AnimCharacter
    {
        public string Id;                     // c0000
        public string Anibnd;                 // 完整路径
        public string Skeleton = "";
        public int TaeCount;
        public List<AnimEntry> Anims = new List<AnimEntry>();
        public bool Loaded;
        public string Error;
    }

    /// <summary>
    /// 只狼角色动画读取。
    ///
    /// 数据布局：
    ///   chr\&lt;id&gt;.chrbnd.dcx       模型 + 贴图
    ///   chr\&lt;id&gt;.anibnd.dcx       骨骼 hkx\skeleton.hkx + 动画表 tae\*.tae
    ///   chr\&lt;id&gt;_a000_hi.anibnd.dcx  动画关键帧（高精度）
    ///   chr\&lt;id&gt;_a000_lo.anibnd.dcx  动画关键帧（低精度）
    ///
    /// TAE 只存「有哪些动画、每条的时长和事件轨道」，真正的动作曲线在同名 .hkt/.hkx 里。
    /// 只狼的 TAE 是 SDT 格式，由 SoulsAssetPipeline 提供解析。
    /// </summary>
    static class AnimationLibrary
    {
        /// <summary>列出全部有动画的角色（只读包头，很快）。</summary>
        public static List<string> ListCharacters(string root)
        {
            var chrDir = Path.Combine(root, "chr");
            if (!Directory.Exists(chrDir)) return new List<string>();

            return Directory.EnumerateFiles(chrDir, "*.anibnd.dcx", SearchOption.TopDirectoryOnly)
                            .Select(Path.GetFileNameWithoutExtension)      // c0000.anibnd
                            .Select(n => n.Substring(0, n.Length - ".anibnd".Length))
                            .Where(n => n.Length > 0 && !n.Contains("_"))   // 只要主包，不要 _a000_hi / _lo
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }

        /// <summary>读一个角色的全部 TAE，把动画列出来。</summary>
        public static AnimCharacter Load(string root, string chrId, Action<string> log = null)
        {
            var a = new AnimCharacter { Id = chrId };
            var path = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
            if (!File.Exists(path)) { a.Error = "找不到 " + chrId + ".anibnd.dcx"; return a; }
            a.Anibnd = path;

            try
            {
                var bnd = BND4.Read(path);
                foreach (var f in bnd.Files)
                {
                    var low = f.Name.ToLowerInvariant();
                    if (!low.EndsWith(".tae")) continue;
                    a.TaeCount++;
                    var taeName = Path.GetFileName(f.Name.Replace('\\', '/'));
                    try
                    {
                        var tae = Sap.TAE.Read(new MemoryStream(f.Bytes));
                        if (string.IsNullOrEmpty(a.Skeleton)) a.Skeleton = tae.SkeletonName ?? "";
                        foreach (var an in tae.Animations)
                        {
                            a.Anims.Add(new AnimEntry
                            {
                                Character = chrId,
                                TaeFile = taeName,
                                Id = an.ID,
                                Name = an.Header?.AnimFileName ?? (chrId + "_" + an.ID.ToString("D6")),
                                Actions = an.Actions?.Count ?? 0,
                                Tracks = an.ActionTracks?.Count ?? 0,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke("[动画] " + chrId + " / " + taeName + " 解析失败：" + ex.Message);
                    }
                }
                a.Loaded = true;
            }
            catch (Exception ex)
            {
                a.Error = ex.Message;
            }

            log?.Invoke(string.Format("[动画] {0}：{1} 个 tae，{2} 条动画，骨骼 {3}",
                chrId, a.TaeCount, a.Anims.Count, string.IsNullOrEmpty(a.Skeleton) ? "?" : a.Skeleton));
            return a;
        }

        /// <summary>
        /// 把一个角色 anibnd 里的全部原始文件（tae / hkx / hkt）落到磁盘。
        /// 这些是只狼的原始二进制，可以直接喂给 DSAnimStudio 之类的工具。
        /// </summary>
        public static int ExportRaw(string root, string chrId, string outDir, Action<string> log)
        {
            var path = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
            if (!File.Exists(path)) { log?.Invoke("[动画] 找不到 " + chrId); return 0; }

            var bnd = BND4.Read(path);
            var dir = Path.Combine(outDir, chrId);
            Directory.CreateDirectory(dir);

            int n = 0;
            foreach (var f in bnd.Files)
            {
                var name = Path.GetFileName(f.Name.Replace('\\', '/'));
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    File.WriteAllBytes(Path.Combine(dir, name), f.Bytes);
                    n++;
                }
                catch (Exception ex) { log?.Invoke("[动画] 写出 " + name + " 失败：" + ex.Message); }
            }

            log?.Invoke("[动画] " + chrId + " 导出 " + n + " 个原始文件 → " + dir);
            return n;
        }

        /// <summary>只导出选中的那几条动画对应的原始文件（按名字匹配）。</summary>
        public static int ExportSelected(string root, IList<AnimEntry> picked, string outDir, Action<string> log)
        {
            int total = 0;
            foreach (var g in picked.GroupBy(e => e.Character, StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(root, "chr", g.Key + ".anibnd.dcx");
                if (!File.Exists(path)) { log?.Invoke("[动画] 找不到 " + g.Key); continue; }

                BND4 bnd;
                try { bnd = BND4.Read(path); }
                catch (Exception ex) { log?.Invoke("[动画] " + g.Key + " 打不开：" + ex.Message); continue; }

                var dir = Path.Combine(outDir, g.Key);
                Directory.CreateDirectory(dir);

                // 需要的 tae 文件名 + 动画短名
                var wantTae = new HashSet<string>(g.Select(e => e.TaeFile), StringComparer.OrdinalIgnoreCase);
                var wantAnim = new HashSet<string>(g.Select(e => StripExt(e.Name)), StringComparer.OrdinalIgnoreCase);

                foreach (var f in bnd.Files)
                {
                    var name = Path.GetFileName(f.Name.Replace('\\', '/'));
                    if (string.IsNullOrEmpty(name)) continue;
                    var low = name.ToLowerInvariant();
                    var baseName = StripExt(name).ToLowerInvariant();

                    bool keep = false;
                    if (low.EndsWith(".tae")) keep = wantTae.Contains(name);
                    else if (low.EndsWith(".hkt") || low.EndsWith(".hkx"))
                        keep = wantAnim.Contains(baseName) || low.Contains("skeleton");
                    else if (low.EndsWith(".hkx"))
                        keep = wantAnim.Contains(baseName);

                    if (!keep) continue;
                    try { File.WriteAllBytes(Path.Combine(dir, name), f.Bytes); total++; }
                    catch (Exception ex) { log?.Invoke("[动画] 写出 " + name + " 失败：" + ex.Message); }
                }
            }
            log?.Invoke("[动画] 共导出 " + total + " 个文件 → " + outDir);
            return total;
        }

        static string StripExt(string s)
        {
            var i = s.LastIndexOf('.');
            return i > 0 ? s.Substring(0, i) : s;
        }
    }
}
