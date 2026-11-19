using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 「导出动画时顺带把音频也带上」的实现。
    ///
    /// 只狼的声音是按角色/场景分音库的：
    ///   sound\&lt;bank&gt;.fev   事件工程（事件名形如 a100000001）
    ///   sound\&lt;bank&gt;.fsb   实际音库
    /// 角色的音库名和角色 ID 同名，比如 c1010 的音就在 sound\c1010.fev / .fsb 里。
    ///
    /// 所以导出角色 c1010 的动画时，把 c1010 音库里的音频一并带出来就是「这个角色的配音」。
    /// 动画名（a000_000210）和事件名（a100000001）编号体系不同，没法一一对应，
    /// 因此这里做的是「按音库整包带出该角色的音频」，而不是逐动画精确配对 —— 这条要说清楚。
    /// </summary>
    static class EventAudio
    {
        /// <summary>
        /// 把与这些动画同属一个角色的音频导出来。
        /// 返回导出的音频文件个数。
        /// </summary>
        public static int ExportForAnimations(string root, string chrId,
                                              IEnumerable<string> animNames,
                                              string outDir, Action<string> log = null)
        {
            var soundRoot = Path.Combine(root ?? "", "sound");
            if (!Directory.Exists(soundRoot))
            {
                log?.Invoke("[音频] 找不到 sound 目录，跳过");
                return 0;
            }

            // 角色自己的音库；没有就退一步找前缀相同的
            var fev = Path.Combine(soundRoot, chrId + ".fev");
            var fsb = Path.Combine(soundRoot, chrId + ".fsb");
            if (!File.Exists(fev) && !File.Exists(fsb))
            {
                log?.Invoke("[音频] " + chrId + " 没有同名音库，跳过");
                return 0;
            }

            Directory.CreateDirectory(outDir);

            // 复用事件索引：建一次是全量的，这里只挑属于本音库的条目
            EventIndex idx;
            try { idx = EventLibrary.Build(soundRoot, log); }
            catch (Exception ex) { log?.Invoke("[音频] 索引失败：" + ex.Message); return 0; }

            var mine = idx.Events
                .Where(e => string.Equals(e.Bank, chrId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Resolvable)
                .ToList();

            if (mine.Count == 0)
            {
                log?.Invoke("[音频] " + chrId + " 音库里没有可导出条目");
                return 0;
            }

            var opt = new EventExporter.Options
            {
                SoundRoot = soundRoot,
                OutRoot = outDir,
                Vgmstream = FindVgmstream(),
                Bitrate = 192,
            };

            int n = 0;
            try
            {
                var res = EventExporter.ExportAsync(opt, mine, log, null,
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                n = res != null ? res.Files : 0;
            }
            catch (Exception ex)
            {
                log?.Invoke("[音频] 导出失败：" + ex.Message);
            }

            log?.Invoke("[音频] " + chrId + "：" + n + " 个 → " + outDir);
            return n;
        }

        static string FindVgmstream()
        {
            // 工具自带一份；找不到就让 EventLibrary 自己去找
            var cands = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "tools", "vgmstream", "vgmstream-cli.exe"),
                Path.Combine(AppContext.BaseDirectory, "vgmstream-cli.exe"),
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return null;
        }
    }
}
