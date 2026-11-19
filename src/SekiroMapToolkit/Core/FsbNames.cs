using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 直接读 FSB5 头里的 name table，拿到每个子音轨的名字。
    ///
    /// 只狼的音库大部分是标准 FSB5（魔数 "FSB5" 明文），只有一部分数据段被混淆过，
    /// 但 name table 始终是明文，所以不需要 vgmstream 也能建索引。
    /// 少量音库开头是重复的 8 字节混淆模式、认不出 FSB5，这时返回空列表，调用方回退到按序号处理。
    /// </summary>
    static class FsbNames
    {
        public sealed class Info
        {
            public int Subsongs = -1;
            public List<string> Names = new List<string>();
            /// <summary>名字是否可用；false 表示要用序号导出。</summary>
            public bool HasNames => Names.Count > 0;
        }

        /// <summary>读一个 FSB 的子音轨名；失败返回空 Info（Subsongs = -1）。</summary>
        public static Info Read(string path)
        {
            var info = new Info();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var head = new byte[64];
                if (fs.Read(head, 0, head.Length) < 32) return info;
                if (head[0] != 'F' || head[1] != 'S' || head[2] != 'B' || head[3] != '5') return info;

                uint numSamples = BitConverter.ToUInt32(head, 8);
                uint sampleHeadersSize = BitConverter.ToUInt32(head, 12);
                uint nameTableSize = BitConverter.ToUInt32(head, 16);

                info.Subsongs = (int)numSamples;
                if (nameTableSize == 0 || nameTableSize > 8 * 1024 * 1024) return info;

                long nameStart = 28L + sampleHeadersSize;   // FSB5 固定头 28 字节
                if (nameStart <= 0 || nameStart + nameTableSize > fs.Length) return info;

                fs.Position = nameStart;
                var seg = new byte[nameTableSize];
                int got = 0;
                while (got < seg.Length)
                {
                    int n = fs.Read(seg, got, seg.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < seg.Length) Array.Resize(ref seg, got);

                info.Names = Extract(seg);
                return info;
            }
            catch
            {
                return info;
            }
        }

        /// <summary>
        /// name table 的布局是 [标志字节] + 一段 u32 偏移表 + null 分隔字符串区。
        /// 那段偏移表里也可能出现可打印字节，扫出来会是 <c>"  "</c>、<c>", "</c>、<c>"8 "</c> 这种垃圾。
        /// 真正的音轨名有两个硬特征：首字符是字母，且整串只由 [A-Za-z0-9_.-/] 组成。
        /// </summary>
        static List<string> Extract(byte[] seg)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < seg.Length; i++)
            {
                byte b = seg[i];
                if (IsNameChar(b))
                {
                    sb.Append((char)b);
                }
                else
                {
                    Flush(sb, list);
                }
            }
            Flush(sb, list);
            return list;
        }

        static void Flush(StringBuilder sb, List<string> list)
        {
            if (sb.Length >= 3)
            {
                var s = sb.ToString();
                var c0 = s[0];
                bool startsAlpha = (c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z') || c0 == '_';
                if (startsAlpha && !IsAllDigits(s)) list.Add(s);
            }
            sb.Clear();
        }

        static bool IsNameChar(byte b)
        {
            if (b >= '0' && b <= '9') return true;
            if (b >= 'a' && b <= 'z') return true;
            if (b >= 'A' && b <= 'Z') return true;
            return b == '_' || b == '-' || b == '.' || b == '/';
        }

        static bool IsAllDigits(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>
        /// 把 FEV 里的音轨路径（<c>/ntc_sm10/a100000001</c>）对到 FSB 名字表的下标。
        /// 只狼的 name 是短名（<c>a100000001</c>），变体在后面加 b / c / d。
        /// 返回 null 表示这一轨在 FSB 里找不到。
        /// </summary>
        public static int? IndexOf(Info info, string trackShortName)
        {
            if (!info.HasNames || string.IsNullOrEmpty(trackShortName)) return null;

            for (int i = 0; i < info.Names.Count; i++)
                if (string.Equals(info.Names[i], trackShortName, StringComparison.OrdinalIgnoreCase))
                    return i;

            // 退一步：允许变体后缀（a100000001 / a100000001b / a100000001c）
            for (int i = 0; i < info.Names.Count; i++)
            {
                var n = info.Names[i];
                if (n.Length >= trackShortName.Length &&
                    n.StartsWith(trackShortName, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = n.Substring(trackShortName.Length);
                    if (rest.Length <= 3 && IsVariantSuffix(rest)) return i;
                }
            }
            return null;
        }

        static bool IsVariantSuffix(string s)
        {
            if (s.Length == 0) return false;
            foreach (var c in s)
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9')) return false;
            return true;
        }

        /// <summary>找出某个事件音轨对应的全部 FSB 下标（含变体）。</summary>
        public static List<int> IndexesOf(Info info, string trackShortName)
        {
            var list = new List<int>();
            if (!info.HasNames || string.IsNullOrEmpty(trackShortName)) return list;

            for (int i = 0; i < info.Names.Count; i++)
            {
                var n = info.Names[i];
                if (string.Equals(n, trackShortName, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(i);
                    continue;
                }
                if (n.Length > trackShortName.Length &&
                    n.StartsWith(trackShortName, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = n.Substring(trackShortName.Length);
                    if (rest.Length <= 3 && IsVariantSuffix(rest)) list.Add(i);
                }
            }
            return list;
        }
    }
}
