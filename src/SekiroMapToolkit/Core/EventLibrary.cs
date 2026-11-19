using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SekiroMapToolkit.Core
{
    /// <summary>一个可勾选、可单独导出的音频事件。</summary>
    class EventEntry
    {
        public string Bank;          // sm10
        public string MapKey;        // m10
        public string Category;      // 场景
        public string Path;          // /ntc_sm10/a100000001 或 bank#12
        public string Name;          // a100000001
        public string Group;         // ntc_sm10 / 语音 ja / 空
        public string Source;        // fev / fsb
        public int Subsong = -1;     // 已知序号（-1 表示要按名字匹配）
        public bool Resolvable = true;   // false = fev 引用了这一轨，但 fsb 里找不到，导不出来

        /// <summary>界面主标题。</summary>
        public string Display => Name;

        /// <summary>界面副标题：音库 · 分组（分组和音库同名就不重复显示）。</summary>
        public string Detail
        {
            get
            {
                var parts = new List<string> { Bank };
                if (!string.IsNullOrEmpty(Group) &&
                    !string.Equals(Group, Bank, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Group, "音轨", StringComparison.Ordinal))
                    parts.Add(Group);
                if (!Resolvable) parts.Add("音库中缺失");
                else if (Subsong >= 0) parts.Add("轨 " + Subsong);
                return string.Join("  ·  ", parts);
            }
        }

        /// <summary>导出时用的唯一键。</summary>
        public string Key => Bank + "|" + Path;
    }

    /// <summary>索引结果。</summary>
    class EventIndex
    {
        public List<EventEntry> Events = new List<EventEntry>();
        public int BanksWithFev;
        public int BanksOnlyFsb;
        public int Unreadable;

        public int BankCount => Events.Select(e => e.Bank).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    /// <summary>
    /// 「事件 → 音轨」总索引 + 按事件导出。
    ///
    /// 只狼的声音组织方式：
    ///   sound\&lt;bank&gt;.fev   事件工程，STRR 块里存着 /ntc_sm10/a100000001 这样的音轨路径
    ///   sound\&lt;bank&gt;.fsb   实际音库，一个 bank 里有 N 个子音轨
    ///
    /// 有 fev 的 bank：按 fev 里的事件列条目 —— 这才是「事件」粒度。
    /// 没有 fev 的 bank（主要是 vm* 语音）：按子音轨序号列。
    /// </summary>
    static class EventLibrary
    {
        /// <summary>扫 sound 目录，建「事件 → 音轨」索引。</summary>
        public static EventIndex Build(string soundRoot, Action<string> log = null)
        {
            var idx = new EventIndex();
            if (!Directory.Exists(soundRoot)) return idx;

            var fevs = FevParser.Scan(soundRoot);
            var fevNames = new HashSet<string>(fevs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            idx.BanksWithFev = fevs.Count;

            // 1) 有 fev 的 bank：按事件列
            foreach (var proj in fevs)
            {
                var fsbPath = Path.Combine(soundRoot, proj.Name + ".fsb");
                var fi = File.Exists(fsbPath) ? FsbNames.Read(fsbPath) : new FsbNames.Info();
                if (fi.Subsongs < 0) idx.Unreadable++;

                foreach (var s in proj.Sounds)
                {
                    var e = new EventEntry
                    {
                        Bank = proj.Name,
                        MapKey = proj.MapKey,
                        Category = proj.Category,
                        Path = s.Path,
                        Name = s.ShortName,
                        Group = string.IsNullOrEmpty(s.Group) ? s.Folder : s.Group,
                        Source = "fev",
                    };
                    var hits = FsbNames.IndexesOf(fi, s.ShortName);
                    if (hits.Count == 1) e.Subsong = hits[0];
                    // 名字表读不出来时无法判断，保守地认为「可能能导」（后面走整库匹配再确认）
                    if (fi.HasNames && hits.Count == 0) e.Resolvable = false;
                    idx.Events.Add(e);
                }
            }

            // 2) 没有 fev 的 fsb：按子音轨序号列
            foreach (var fsbPath in Directory.EnumerateFiles(soundRoot, "*.fsb", SearchOption.TopDirectoryOnly)
                                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var bank = Path.GetFileNameWithoutExtension(fsbPath);
                if (fevNames.Contains(bank)) continue;

                var fi = FsbNames.Read(fsbPath);
                if (fi.Subsongs < 0) { idx.Unreadable++; continue; }
                if (fi.Subsongs == 0) continue;
                idx.BanksOnlyFsb++;

                var mapKey = AudioExporter.MapKeyOf(bank);
                var cat = AudioExporter.CategoryOf(bank);
                var lang = AudioExporter.LanguageOf(bank);

                for (int i = 0; i < fi.Subsongs; i++)
                {
                    var nm = fi.Names.Count > i ? fi.Names[i] : null;
                    idx.Events.Add(new EventEntry
                    {
                        Bank = bank,
                        MapKey = mapKey,
                        Category = cat,
                        Path = "bank#" + i,
                        Name = nm ?? ("#" + i),
                        Group = string.IsNullOrEmpty(lang) ? "音轨" : "语音 " + lang,
                        Source = "fsb",
                        Subsong = i,
                    });
                }
            }

            log?.Invoke(string.Format("[事件] 共 {0} 个事件，来自 {1} 个音库（{2} 个带 fev，{3} 个只有 fsb，{4} 个头部读不出）",
                idx.Events.Count, idx.BankCount, idx.BanksWithFev, idx.BanksOnlyFsb, idx.Unreadable));
            return idx;
        }
    }

    /// <summary>按事件导出：只解所选事件对应的子音轨，不碰整个音库。</summary>
    static class EventExporter
    {
        public sealed class Options
        {
            public string SoundRoot;
            public string OutRoot;
            public string Vgmstream;
            public int Bitrate = 192;
            public bool KeepWav;
        }

        public sealed class Result
        {
            public int Events;
            public int Files;
            public long Bytes;
            public int Failed;
            public double Seconds;
        }

        /// <summary>
        /// 导出选中的事件。
        ///
        /// 两类走法：
        ///   Subsong >= 0 → vgmstream -s N 单轨精确解，最省时间；
        ///   Subsong <  0 → 该事件在 FSB 里对不上唯一轨（可能是变体组 / 名字读不出），
        ///                  只好整库解一遍再按名字挑。
        /// </summary>
        public static Task<Result> ExportAsync(Options opt, IList<EventEntry> events,
                                               Action<string> log, Action<int, int, string> progress,
                                               CancellationToken ct)
        {
            return Task.Run(() => ExportCore(opt, events, log, progress, ct), ct);
        }

        static Result ExportCore(Options opt, IList<EventEntry> events, Action<string> log,
                                 Action<int, int, string> progress, CancellationToken ct)
        {
            var res = new Result();
            var t0 = DateTime.Now;
            Directory.CreateDirectory(opt.OutRoot);

            var tmpRoot = Path.Combine(Path.GetTempPath(), "smk_event_" + Environment.ProcessId);
            Directory.CreateDirectory(tmpRoot);

            try
            {
                var byBank = events.GroupBy(e => e.Bank, StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                                   .ToList();

                int done = 0;
                progress?.Invoke(0, byBank.Count, "准备中…");

                foreach (var g in byBank)
                {
                    ct.ThrowIfCancellationRequested();
                    var bank = g.Key;
                    var fsb = Path.Combine(opt.SoundRoot, bank + ".fsb");
                    if (!File.Exists(fsb))
                    {
                        res.Failed += g.Count();
                        log?.Invoke("[事件] 找不到音库 " + bank + ".fsb");
                        continue;
                    }

                    progress?.Invoke(done, byBank.Count, "导出 " + bank);
                    var tmp = Path.Combine(tmpRoot, bank);
                    Directory.CreateDirectory(tmp);

                    var fi = FsbNames.Read(fsb);
                    var single = new List<EventEntry>();
                    var whole = new List<EventEntry>();
                    int missing = 0;
                    foreach (var e in g)
                    {
                        ct.ThrowIfCancellationRequested();
                        // 已知在音库里找不到的，直接跳过，不浪费解码时间也不算失败
                        if (!e.Resolvable) { missing++; continue; }
                        if (e.Subsong >= 0) single.Add(e); else whole.Add(e);
                    }
                    if (missing > 0)
                        log?.Invoke("[事件] " + bank + " 有 " + missing + " 个事件在音库里没有对应音轨，已跳过");

                    // 单轨精确解
                    foreach (var e in single)
                    {
                        ct.ThrowIfCancellationRequested();
                        var wav = Path.Combine(tmp, "s" + e.Subsong.ToString("D3") + ".wav");
                        if (!DecodeOne(opt.Vgmstream, fsb, e.Subsong, wav, log, ct))
                        {
                            res.Failed++;
                            continue;
                        }
                        if (Emit(opt, e, wav, log))
                        {
                            res.Files++;
                            res.Events++;
                            res.Bytes += LastSize;
                        }
                        else res.Failed++;
                        TryDelete(wav);
                    }

                    // 整库解再按名字挑
                    if (whole.Count > 0)
                    {
                        var bag = Path.Combine(tmp, "all");
                        Directory.CreateDirectory(bag);
                        if (DecodeAll(opt.Vgmstream, fsb, bag, log, ct))
                        {
                            NormalizeBag(bag);
                            var files = Directory.EnumerateFiles(bag).Where(f => !f.EndsWith(".log")).ToList();
                            foreach (var e in whole)
                            {
                                ct.ThrowIfCancellationRequested();
                                var hit = files.FirstOrDefault(f => NameMatches(Path.GetFileNameWithoutExtension(f), e));
                                if (hit == null) { res.Failed++; continue; }
                                if (Emit(opt, e, hit, log))
                                {
                                    res.Files++;
                                    res.Events++;
                                    res.Bytes += LastSize;
                                }
                                else res.Failed++;
                                // 不要在这里删 hit：一个子音轨可能被多个事件共享
                                // （如 a100000011 和 a100000020 合并在同一个 wav 里），
                                // 删了后面的事件就找不到文件了。临时目录最后由 TryDeleteDir 统一清。
                            }
                        }
                        else
                        {
                            res.Failed += whole.Count;
                        }
                        TryDeleteDir(bag);
                    }

                    TryDeleteDir(tmp);
                    done++;
                    progress?.Invoke(done, byBank.Count, "完成 " + bank);
                }
            }
            finally
            {
                TryDeleteDir(tmpRoot);
            }

            res.Seconds = (DateTime.Now - t0).TotalSeconds;
            return res;
        }

        [ThreadStatic] static long _lastSize;
        static long LastSize => _lastSize;

        /// <summary>把解出来的 wav 转成 mp3 放到最终位置。</summary>
        static bool Emit(Options opt, EventEntry e, string wav, Action<string> log)
        {
            try
            {
                var dir = Path.Combine(opt.OutRoot, e.MapKey, e.Category, e.Bank);
                Directory.CreateDirectory(dir);

                var baseName = SafeName(e.Name);
                var mp3 = Path.Combine(dir, baseName + ".mp3");
                Mp3.Encode(wav, mp3, opt.Bitrate);
                _lastSize = new FileInfo(mp3).Length;

                if (opt.KeepWav)
                    File.Move(wav, Path.Combine(dir, baseName + ".wav"), true);

                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("[事件] 转码失败 " + e.Name + "：" + ex.Message);
                _lastSize = 0;
                return false;
            }
        }

        /// <summary>
        /// vgmstream 的 ?n 遇到「一个子音轨挂了多个名字」时会输出
        /// <c>006_bank_ntc_sm10_a100000011.wav; bank_ntc_sm10_a100000020.wav</c>，
        /// 名字里带 <c>; </c> 和第二个 <c>.wav</c>，在 Windows 上是非法文件名。
        /// 这里统一改成 <c>006_ntc_sm10_a100000011_ntc_sm10_a100000020</c>。
        /// </summary>
        static void NormalizeBag(string bag)
        {
            foreach (var f in Directory.EnumerateFiles(bag).ToList())
            {
                var name = Path.GetFileName(f);
                if (name.IndexOf(';') < 0 && name.IndexOf(".wav", 1, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var clean = name.Replace(".wav", "")
                                .Replace(";", "_")
                                .Replace("bank_", "")
                                .Replace(" ", "");
                clean = System.Text.RegularExpressions.Regex.Replace(clean, "_+", "_").Trim('_', '.');

                var dst = Path.Combine(bag, clean);
                if (string.Equals(f, dst, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(f, dst);
                }
                catch { }
            }
        }

        /// <summary>vgmstream 输出的名字形如 <c>007_ntc_sm10_a000810108</c>，抠掉序号后比对。</summary>
        static bool NameMatches(string fileBase, EventEntry e)
        {
            // 去掉 "001_" 前缀
            var s = fileBase;
            var us = s.IndexOf('_');
            if (us > 0 && us < 6 && s.Substring(0, us).All(char.IsDigit)) s = s.Substring(us + 1);

            // vgmstream 把 bank 前缀也塞进来了，统一去掉
            s = s.Replace("bank_", "");

            if (string.Equals(s, e.Name, StringComparison.OrdinalIgnoreCase)) return true;

            // 事件路径 /ntc_sm10/a100000001 → ntc_sm10_a100000001
            var flat = e.Path.TrimStart('/').Replace('/', '_');
            if (string.Equals(s, flat, StringComparison.OrdinalIgnoreCase)) return true;

            // 只认尾部短名（变体后缀允许）
            if (s.EndsWith(e.Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Length > e.Name.Length && s.Contains(e.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool DecodeOne(string vgm, string fsb, int subsong, string outWav,
                              Action<string> log, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(vgm)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(subsong.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outWav);
            psi.ArgumentList.Add(fsb);

            using var p = Process.Start(psi);
            if (p == null) return false;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            while (!p.WaitForExit(200))
            {
                ct.ThrowIfCancellationRequested();
                if (p.HasExited) break;
            }
            p.WaitForExit();
            var err = errTask.GetAwaiter().GetResult();
            outTask.GetAwaiter().GetResult();

            if (File.Exists(outWav) && new FileInfo(outWav).Length > 44) return true;
            if (!string.IsNullOrWhiteSpace(err))
                log?.Invoke("[事件] 单轨解码失败 #" + subsong + "：" + FirstLine(err));
            return false;
        }

        static bool DecodeAll(string vgm, string fsb, string outDir, Action<string> log, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(vgm)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(Path.Combine(outDir, "?03s_?n"));
            psi.ArgumentList.Add(fsb);

            using var p = Process.Start(psi);
            if (p == null) return false;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            while (!p.WaitForExit(200))
            {
                ct.ThrowIfCancellationRequested();
                if (p.HasExited) break;
            }
            p.WaitForExit();
            var err = errTask.GetAwaiter().GetResult();
            outTask.GetAwaiter().GetResult();

            bool any = Directory.EnumerateFiles(outDir).Any(f => !f.EndsWith(".log"));
            if (!any && !string.IsNullOrWhiteSpace(err))
                log?.Invoke("[事件] 整库解码失败：" + FirstLine(err));
            return any;
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        static string SafeName(string s)
        {
            s = s.Replace(".wav", "");
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 100 ? s.Substring(0, 100) : s;
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }
}

