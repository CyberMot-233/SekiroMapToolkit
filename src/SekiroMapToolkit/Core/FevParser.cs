using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SekiroMapToolkit.Core
{
    /// <summary>FMOD Designer 事件工程里的一个声音定义（一个事件引用的最小音频单元）。</summary>
    class FevSound
    {
        public string Bank;         // 所属音库名，如 sm10
        public string Path;         // FSB 里的音轨路径，如 /ntc_sm10/a100000001
        public string Group;        // 分组（master / music / Menu / SE / Voice / Default）
        public float PlayDistance;
        public float StopDistance;
        public bool NoHdr;

        /// <summary>音轨短名，如 a100000001 —— 和 FSB 里的子音轨名尾部一致。</summary>
        public string ShortName
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return "";
                var i = Path.LastIndexOf('/');
                return i < 0 ? Path : Path.Substring(i + 1);
            }
        }

        /// <summary>事件容器里的中间目录，如 ntc_sm10。</summary>
        public string Folder
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return "";
                var parts = Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? parts[parts.Length - 2] : "";
            }
        }
    }

    /// <summary>一个 .fev 文件解析出来的东西。</summary>
    class FevProject
    {
        public string File;
        public string Name;                       // sm10 / c1010 / main ...
        public string Category;                   // 复用 AudioExporter 的 7 分类
        public List<FevSound> Sounds = new List<FevSound>();
        public List<string> Groups = new List<string>();
        public long Bytes;

        public string MapKey => AudioExporter.MapKeyOf(Name);
    }

    /// <summary>
    /// 解析只狼的 FMOD Designer 工程文件（<c>sound\*.fev</c>）。
    ///
    /// 只狼的 .fev 是标准 RIFF：<c>RIFF/FEV </c> → <c>FMT </c> + <c>LIST/PROJ</c>{ OBCT / PROP / LGCY / EPRP / STRR / LANG }。
    /// 真正有用的是 STRR（字符串表）：里面存着 <c>/ntc_sm10/a100000001</c> 这样的音轨路径，
    /// 和 FSB 音库里的子音轨名一一对应 —— 这就是「事件 → 音频」的桥。
    /// LGCY 里是声音定义的分组与 DSP 参数，只取分组名和 playDistance / stopDistance。
    /// </summary>
    static class FevParser
    {
        /// <summary>扫目录下所有 .fev，解析出事件索引。</summary>
        public static List<FevProject> Scan(string soundRoot)
        {
            var list = new List<FevProject>();
            if (!Directory.Exists(soundRoot)) return list;
            foreach (var path in Directory.EnumerateFiles(soundRoot, "*.fev", SearchOption.TopDirectoryOnly)
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var p = Parse(path);
                    if (p != null && p.Sounds.Count > 0) list.Add(p);
                }
                catch
                {
                    // 个别损坏 / 非标准工程直接跳过，不影响其它
                }
            }
            return list;
        }

        public static FevProject Parse(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 16) return null;
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return null;
            if (data[8] != 'F' || data[9] != 'E' || data[10] != 'V' || data[11] != ' ') return null;

            var proj = new FevProject
            {
                File = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Bytes = data.Length,
            };
            proj.Category = AudioExporter.CategoryOf(proj.Name);

            // 顶层：FMT + LIST/PROJ，LIST 里面才是 OBCT/STRR/...
            var chunks = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.Ordinal);
            WalkProj(data, chunks);

            if (chunks.TryGetValue("STRR", out var strr))
                proj.Sounds = ReadSounds(data, strr.Key, strr.Value, proj.Name);

            if (chunks.TryGetValue("LGCY", out var lgcy))
                proj.Groups = ReadGroups(data, lgcy.Key, lgcy.Value);

            foreach (var s in proj.Sounds)
                s.Bank = proj.Name;

            // 尽量把 LGCY 里的分组套到声音上：只狼的顺序是「一个分组下面挂若干声音」，
            // 但两个块的索引并非严格一一对应，所以只在数量吻合时才关联，否则留默认分组。
            AttachGroupsIfPairable(proj);

            return proj;
        }

        // ------------------------------------------------------------ chunk 遍历

        static void WalkProj(byte[] d, Dictionary<string, KeyValuePair<int, int>> outChunks)
        {
            int i = 12;   // 跳过 "RIFF"+size+"FEV "
            while (i + 8 <= d.Length)
            {
                var id = Encoding.ASCII.GetString(d, i, 4);
                int size = BitConverter.ToInt32(d, i + 4);
                if (size < 0 || i + 8 + size > d.Length) return;

                if (id == "LIST")
                {
                    // LIST 头 4 字节是 type（PROJ），从里面继续走
                    WalkProj(d, outChunks, i + 12, i + 8 + size);
                }
                else
                {
                    var key = id.Trim();
                    if (key.Length > 0 && !outChunks.ContainsKey(key))
                        outChunks[key] = new KeyValuePair<int, int>(i + 8, size);
                }
                i += 8 + size + (size & 1);
            }
        }

        static void WalkProj(byte[] d, Dictionary<string, KeyValuePair<int, int>> outChunks, int start, int end)
        {
            int i = start;
            while (i + 8 <= end && i + 8 <= d.Length)
            {
                var id = Encoding.ASCII.GetString(d, i, 4);
                int size = BitConverter.ToInt32(d, i + 4);
                if (size < 0 || i + 8 + size > d.Length) return;

                if (id == "LIST")
                {
                    WalkProj(d, outChunks, i + 12, i + 8 + size);
                }
                else
                {
                    var key = id.Trim();
                    if (key.Length > 0 && !outChunks.ContainsKey(key))
                        outChunks[key] = new KeyValuePair<int, int>(i + 8, size);
                }
                i += 8 + size + (size & 1);
            }
        }

        // ------------------------------------------------------------ STRR

        /// <summary>
        /// STRR 是「4 字节 count + count 个 u32 累积长度 + null 分隔字符串区」。
        /// 里面既有 a100000001 这样的短名，也有 /ntc_sm10/a100000001 这样的完整音轨路径。
        /// 只认带 / 的完整路径（它才对得上 FSB 的 stream name）。
        /// </summary>
        static List<FevSound> ReadSounds(byte[] d, int off, int size, string bank)
        {
            var result = new List<FevSound>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (off + size > d.Length) size = d.Length - off;
            if (size <= 0) return result;

            var raw = new byte[size];
            Buffer.BlockCopy(d, off, raw, 0, size);

            foreach (var s in SplitStrings(raw))
            {
                if (s.Length < 3) continue;
                if (!s.StartsWith("/", StringComparison.Ordinal)) continue;
                if (!s.Contains("/")) continue;
                var slash = s.IndexOf('/', 1);
                if (slash < 0 || slash == s.Length - 1) continue;
                if (!seen.Add(s)) continue;
                result.Add(new FevSound { Bank = bank, Path = s, Group = "" });
            }
            return result;
        }

        /// <summary>把一段二进制里所有可打印的 null/长度分隔串抠出来。</summary>
        static IEnumerable<string> SplitStrings(byte[] raw)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                var b = raw[i];
                if (b >= 0x20 && b < 0x7F)
                {
                    sb.Append((char)b);
                }
                else
                {
                    if (sb.Length >= 2) yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length >= 2) yield return sb.ToString();
        }

        // ------------------------------------------------------------ LGCY

        /// <summary>从 LGCY 里捞分组名（master / music / Menu / SE / Voice / Default）。</summary>
        static List<string> ReadGroups(byte[] d, int off, int size)
        {
            var known = new[] { "master", "music", "Menu", "SE", "Voice", "Default", "System", "Ambience" };
            var hits = new List<string>();
            if (off + size > d.Length) size = d.Length - off;
            if (size <= 0) return hits;

            var sub = new byte[size];
            Buffer.BlockCopy(d, off, sub, 0, size);
            foreach (var s in SplitStrings(sub))
            {
                // 分组名是短标识符，且严格等于已知集合（LGCY 里还有一堆属性名要排除）
                foreach (var k in known)
                    if (string.Equals(s, k, StringComparison.Ordinal) && !hits.Contains(k))
                        hits.Add(k);
            }
            return hits;
        }

        /// <summary>
        /// 分组和声音没有可靠的公开索引关系，只在「按顺序数量刚好吻合」时贴上去，
        /// 避免瞎猜把声音分错组。
        /// </summary>
        static void AttachGroupsIfPairable(FevProject proj)
        {
            if (proj.Sounds.Count == 0) return;

            // 只狼里绝大多数工程所有事件都挂在同一个组下；找不到就标成「默认」
            if (proj.Groups.Count == 1)
            {
                foreach (var s in proj.Sounds) s.Group = proj.Groups[0];
                return;
            }

            foreach (var s in proj.Sounds)
            {
                if (string.IsNullOrEmpty(s.Group))
                {
                    // 用音轨路径里的目录名当分组，比空着更可读（ntc_sm10 → sm10）
                    var folder = s.Folder;
                    s.Group = folder.StartsWith("ntc_", StringComparison.OrdinalIgnoreCase)
                        ? folder.Substring(4)
                        : folder;
                }
            }
        }
    }
}
