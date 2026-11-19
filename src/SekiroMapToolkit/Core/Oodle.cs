using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SekiroMapToolkit.Core
{
    // SoulsFormats 用裸名字去 LoadLibrary 找 oo2core，搜索路径依当前进程而定：
    // 正式的 SekiroMapToolkit.exe 旁边有 DLL 就没问题，但用 dotnet xxx.dll 跑时
    // 应用目录会变成 dotnet.exe 所在目录，就找不到了。这里提前把它钉死。
    static class Oodle
    {
        static readonly string[] Names = { "oo2core_6_win64.dll", "oo2core_8_win64.dll", "oo2core_9_win64.dll" };

        public static void Ensure()
        {
            try
            {
                var dir = AppContext.BaseDirectory;

                // 1) 程序目录里没有就从游戏目录捞一份过来
                if (!AnyPresent(dir))
                {
                    foreach (var root in CandidateRoots())
                    {
                        foreach (var n in Names)
                        {
                            var hit = Find(root, n, 3);
                            if (hit != null) { File.Copy(hit, Path.Combine(dir, n), true); break; }
                        }
                        if (AnyPresent(dir)) break;
                    }
                }

                // 2) 把程序目录塞进 PATH，裸名字查找才会命中
                try
                {
                    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
                        Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
                }
                catch { }

                // 3) 再按绝对路径预加载一次，已经加载过的模块后续按名字也能命中
                foreach (var n in Names)
                {
                    var full = Path.Combine(dir, n);
                    if (!File.Exists(full)) continue;
                    try { NativeLibrary.Load(full); } catch { }
                }
            }
            catch { }
        }

        static bool AnyPresent(string dir)
        {
            foreach (var n in Names)
                if (File.Exists(Path.Combine(dir, n))) return true;
            return false;
        }

        static IEnumerable<string> CandidateRoots()
        {
            var list = new List<string>();
            try
            {
                var cfg = Config.Load();
                if (!string.IsNullOrEmpty(cfg.GameRoot))
                {
                    var parent = Directory.GetParent(cfg.GameRoot);
                    if (parent != null) list.Add(parent.FullName);
                }
            }
            catch { }
            foreach (var d in new[] { "E:", "D:", "C:", "F:" })
                list.Add(Path.Combine(d + "\\", "SteamLibrary", "steamapps", "common", "Sekiro"));
            return list;
        }

        // breadth-limited search so a 13 GB game folder still answers quickly
        static string Find(string root, string name, int depth)
        {
            if (!Directory.Exists(root)) return null;
            try
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
                if (depth <= 0) return null;
                foreach (var sub in Directory.GetDirectories(root))
                {
                    var hit = Find(sub, name, depth - 1);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }
    }
}