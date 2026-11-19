using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SekiroMapToolkit.Core
{
    /// <summary>一个内置工具：从 GitHub Release 拉 zip / 7z，解到 tools\&lt;Key&gt;，再启动。</summary>
    class ToolDef
    {
        public string Key;          // tools\ 下的子目录名
        public string Name;         // 显示名
        public string Desc;         // 一句话说明
        public string Repo;         // owner/repo
        public string AssetUrl;     // 直链；null = 只有源码仓库
        public string AssetName;    // 下载后的文件名
        public string[] ExeHints;   // 解压后可能的 exe 文件名（按优先级）
        public string Tag;          // 分类：解包 / 地图 / 模型 / 贴图 / 运行库
        public bool NeededNow;      // 导出流程直接依赖
        public bool NoExe;          // 是库/工具集，不产出可双击的 exe

        public string RepoUrl => "https://github.com/" + Repo;

        public string Dir => Path.Combine(ToolCatalog.Root, Key);

        /// <summary>已安装且能找到 exe 时返回 exe 全路径，否则 null。</summary>
        public string ResolveExe()
        {
            if (!Directory.Exists(Dir)) return null;
            foreach (var hint in ExeHints ?? Array.Empty<string>())
            {
                var direct = Path.Combine(Dir, hint);
                if (File.Exists(direct)) return direct;
            }
            foreach (var hint in ExeHints ?? Array.Empty<string>())
            {
                try
                {
                    var hit = Directory.EnumerateFiles(Dir, hint, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
                catch { }
            }
            return null;
        }

        public bool Installed => NoExe
            ? Directory.Exists(Dir) && Directory.EnumerateFileSystemEntries(Dir).Any()
            : ResolveExe() != null;
    }

    static class ToolCatalog
    {
        public static string Root => Path.Combine(Config.Dir, "tools");

        public static readonly ToolDef[] All =
        {
            new ToolDef
            {
                Key = "DSMapStudio",
                Name = "DSMapStudio",
                Desc = "地图编辑器，用来对照摆放、验证导出结果（汉化版）",
                Repo = "Hoshino486/DSMapStudio",
                AssetUrl = "https://github.com/Hoshino486/DSMapStudio/releases/download/v1.11.1-zh-CN/DSMapStudio-1.11.1-zh-CN-win-x64.zip",
                AssetName = "DSMapStudio-1.11.1-zh-CN-win-x64.zip",
                ExeHints = new[] { "DSMapStudio.exe" },
                Tag = "地图",
                NeededNow = false,
            },
            new ToolDef
            {
                Key = "Nuxe",
                Name = "Nuxe 解包器",
                Desc = "把 Data*.bdt 拆成 文件\\ 目录，界面干净、支持只选部分文件",
                Repo = "Hoshino486/Nuxe",
                AssetUrl = "https://github.com/Hoshino486/Nuxe/releases/download/v1.3.0/Nuxe-1.3.0-win-x64.zip",
                AssetName = "Nuxe-1.3.0-win-x64.zip",
                ExeHints = new[] { "Nuxe.exe" },
                Tag = "解包",
                NeededNow = true,
            },
            new ToolDef
            {
                Key = "UXM",
                Name = "UXM Selective Unpack",
                Desc = "老牌解包器，配合 UXM 打补丁可直接读散文件（仓库只有源码，需自编）",
                Repo = "Hoshino486/UXM-Selective-Unpack",
                AssetUrl = null,
                ExeHints = new[] { "UXM.exe" },
                Tag = "解包",
                NeededNow = true,
            },
            new ToolDef
            {
                Key = "FLVER_Editor",
                Name = "FLVER Editor",
                Desc = "直接开 .flver / .flver.dcx，看骨骼、材质槽、贴图路径",
                Repo = "asasasasasbc/FLVER_Editor",
                AssetUrl = "https://github.com/asasasasasbc/FLVER_Editor/releases/download/X2.9/FLVER_Editor_X2.91.7z",
                AssetName = "FLVER_Editor_X2.91.7z",
                ExeHints = new[] { "FLVER_Editor.exe", "MySFformat.exe" },
                Tag = "模型",
                NeededNow = false,
            },
            new ToolDef
            {
                Key = "Yabber",
                Name = "Yabber",
                Desc = "通用容器解包 / 重打包：BND、TPF、DCX 一把梭",
                Repo = "JKAnderson/Yabber",
                AssetUrl = "https://github.com/JKAnderson/Yabber/releases/download/1.3.1/Yabber.1.3.1.zip",
                AssetName = "Yabber.1.3.1.zip",
                ExeHints = new[] { "Yabber.exe" },
                Tag = "解包",
                NeededNow = false,
            },
            new ToolDef
            {
                Key = "DSAnimStudio",
                Name = "DS Anim Studio",
                Desc = "看 TAE 动作与骨骼、验证物件朝向（汉化版）",
                Repo = "Hoshino486/DSAnimStudio",
                AssetUrl = "https://github.com/Hoshino486/DSAnimStudio/releases/download/v5-RC6.1-zh-CN/DSAnimStudio-v5-RC6.1-zh-CN-win64.zip",
                AssetName = "DSAnimStudio-v5-RC6.1-zh-CN-win64.zip",
                ExeHints = new[] { "DS ANIM STUDIO.exe", "DSAnimStudio.exe" },
                Tag = "模型",
                NeededNow = false,
            },
            new ToolDef
            {
                Key = "SoulsFormats",
                Name = "SoulsFormats",
                Desc = "本工具内部用的解析库（MSB / BND / FLVER / TPF），这里放源码仓库",
                Repo = "JKAnderson/SoulsFormats",
                AssetUrl = null,
                ExeHints = new string[0],
                Tag = "运行库",
                NeededNow = false,
            },
            new ToolDef
            {
                Key = "HavokLib",
                Name = "Havok Toolset",
                Desc = "拆 .hkx 碰撞 / 布娃娃，想连物理一起看时用（命令行工具集）",
                Repo = "Hoshino486/HavokLib",
                AssetUrl = "https://github.com/Hoshino486/HavokLib/releases/download/v0.10.2/HavokToolset-0.10.2-win64.zip",
                AssetName = "HavokToolset-0.10.2-win64.zip",
                ExeHints = new[] { "havok_toolset.exe", "HavokToolset.exe" },
                Tag = "运行库",
                NeededNow = false,
            },
            new ToolDef
            {
                Key = "vgmstream",
                Name = "vgmstream 音频解码器",
                Desc = "专治 FMOD FSB5（含只狼的加密音库），一拆一批子音轨；音频导出靠它",
                Repo = "vgmstream/vgmstream",
                AssetUrl = "https://github.com/vgmstream/vgmstream/releases/download/r2168/vgmstream-win64.zip",
                AssetName = "vgmstream-win64.zip",
                ExeHints = new[] { "vgmstream-cli.exe" },
                Tag = "音频",
                NeededNow = true,
            },
        };

        public static ToolDef Find(string key) =>
            All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

        public static int InstalledCount => All.Count(t => t.Installed);

        // ---------------------------------------------------------------- download

        static HttpClient MakeClient(string token)
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("SekiroMapToolkit/1.1");
            if (!string.IsNullOrWhiteSpace(token))
                h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            return h;
        }

        /// <summary>下载并解压一个工具。返回 exe 路径；无 exe 的库返回解压目录；失败返回 null。</summary>
        public static async Task<string> InstallAsync(ToolDef def, string token, Action<string> log,
                                                      CancellationToken ct)
        {
            if (def.AssetUrl == null)
            {
                log("[跳过] " + def.Name + " 仓库里没有 Release 资产（只有源码）");
                return null;
            }

            Directory.CreateDirectory(Root);
            var tmp = Path.Combine(Root, def.AssetName);

            try
            {
                using (var http = MakeClient(token))
                {
                    log("[下载] " + def.Name + " ← " + def.AssetUrl);
                    using var resp = await http.GetAsync(def.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log("[失败] 404：Release 资产不存在，去 " + def.RepoUrl + "/releases 看看改名没有");
                        return null;
                    }
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        log("[失败] " + (int)resp.StatusCode + "：GitHub 拒绝访问，检查 PAT 是否过期 / 有没有 repo 权限");
                        return null;
                    }
                    resp.EnsureSuccessStatusCode();

                    long total = resp.Content.Headers.ContentLength ?? 0;
                    await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                    await using (var dst = File.Create(tmp))
                    {
                        var buf = new byte[81920];
                        long done = 0;
                        int n;
                        int lastPct = -1;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n, ct);
                            done += n;
                            if (total > 0)
                            {
                                int pct = (int)(done * 100 / total);
                                if (pct / 10 != lastPct / 10)
                                {
                                    lastPct = pct;
                                    log("  " + pct + "%  " + (done / 1048576.0).ToString("F1") + " / " +
                                        (total / 1048576.0).ToString("F1") + " MB");
                                }
                            }
                        }
                    }
                }

                log("[解压] " + Path.GetFileName(tmp) + " → " + def.Dir);
                Extract(tmp, def.Dir, log);
            }
            catch (OperationCanceledException) { log("[取消] " + def.Name); return null; }
            catch (Exception ex)
            {
                log("[失败] " + def.Name + "：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            if (def.NoExe)
            {
                log("[完成] " + def.Name + " → " + def.Dir);
                return def.Dir;
            }

            var exe = def.ResolveExe();
            log(exe != null ? "[完成] " + def.Name + " → " + exe
                            : "[提示] " + def.Name + " 已解压到 " + def.Dir + "，但没找到可执行文件");
            return exe;
        }

        static void Extract(string archive, string dest, Action<string> log)
        {
            Directory.CreateDirectory(dest);

            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var e in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                    if (!target.StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(e.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
                return;
            }

            // .7z / .tar.* 交给系统 bsdtar（libarchive 支持 7z）。
            // 不用 PowerShell 传参，避免中文路径在控制台代码页里被打断。
            var psi = new ProcessStartInfo
            {
                FileName = "tar.exe",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(archive);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dest);

            using var p = Process.Start(psi);
            if (p == null) throw new IOException("无法启动 tar.exe");
            string err = p.StandardError.ReadToEnd();
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                // tar 读不了 7z 时，退回到 7-Zip 命令行（如果机器装了）
                var sevenZip = new[]
                {
                    @"C:\Program Files\7-Zip\7z.exe",
                    @"C:\Program Files (x86)\7-Zip\7z.exe",
                }.FirstOrDefault(File.Exists);

                if (sevenZip != null)
                {
                    log("[解压] tar 不支持，改用 7z.exe");
                    var zpsi = new ProcessStartInfo
                    {
                        FileName = sevenZip,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    zpsi.ArgumentList.Add("x");
                    zpsi.ArgumentList.Add("-y");
                    zpsi.ArgumentList.Add("-o" + dest);
                    zpsi.ArgumentList.Add(archive);
                    using var zp = Process.Start(zpsi);
                    zp.WaitForExit();
                    if (zp.ExitCode == 0) return;
                }

                throw new IOException("解压失败（" + p.ExitCode + "）：" + err.Trim() + " " + outp.Trim());
            }
        }
    }
}
