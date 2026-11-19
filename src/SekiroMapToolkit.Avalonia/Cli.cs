using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SekiroMapToolkit.Core;

namespace SekiroMapToolkit
{
    // headless mode: SekiroMapToolkit.exe --cli --root <dir> --out <dir> [--maps a,b] [--all] [--limit N] [--blend]
    static class Cli
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        public static int Run(string[] args)
        {
            AttachConsole(-1);

            if (args.Any(a => a.Equals("--tools", StringComparison.OrdinalIgnoreCase)))
                return RunTools(args);

            if (args.Any(a => a.Equals("--audio", StringComparison.OrdinalIgnoreCase)))
                return RunAudio(args);

            if (args.Any(a => a.Equals("--fevdump", StringComparison.OrdinalIgnoreCase)))
                return RunFevDump(args);

            if (args.Any(a => a.Equals("--events", StringComparison.OrdinalIgnoreCase)))
                return RunEvents(args);

            if (args.Any(a => a.Equals("--tae", StringComparison.OrdinalIgnoreCase)))
                return RunTae(args);

            if (args.Any(a => a.Equals("--anims", StringComparison.OrdinalIgnoreCase)))
                return RunAnims(args);

            string root = null, outRoot = null, blender = null, mapsArg = null;
            int limit = 0;
            bool blend = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--root": if (i + 1 < args.Length) root = args[++i]; break;
                    case "--out": if (i + 1 < args.Length) outRoot = args[++i]; break;
                    case "--maps": if (i + 1 < args.Length) mapsArg = args[++i]; break;
                    case "--blender": if (i + 1 < args.Length) blender = args[++i]; break;
                    case "--limit": if (i + 1 < args.Length) int.TryParse(args[++i], out limit); break;
                    case "--blend": blend = true; break;
                    case "--all": mapsArg = "*"; break;
                }
            }

            var cfg = Config.Load();
            root ??= cfg.GameRoot;
            outRoot ??= cfg.OutputRoot;
            blender ??= cfg.BlenderPath;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Console.Error.WriteLine("解包目录不存在：" + root);
                return 2;
            }
            if (string.IsNullOrEmpty(outRoot)) outRoot = Path.Combine(root, "map", "临时文件夹");
            Directory.CreateDirectory(outRoot);

            List<string> maps;
            if (string.IsNullOrEmpty(mapsArg) || mapsArg == "*")
                maps = SekiroExporter.ListMaps(root);
            else
                maps = mapsArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            if (maps.Count == 0) { Console.Error.WriteLine("没有可导出的地图"); return 3; }

            var sb = new StringBuilder();
            var gate = new object();
            Action<string> log = s =>
            {
                var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
                Console.WriteLine(line);
                lock (gate) sb.AppendLine(line);
            };

            var logFile = Path.Combine(outRoot, "cli_export.log");
            log("ROOT=" + root + "  OUT=" + outRoot + "  maps=" + maps.Count + (blend ? "  +blend" : ""));

            int fail = 0;
            using var cts = new CancellationTokenSource();
            foreach (var map in maps)
            {
                try
                {
                    var stats = SekiroExporter.ExportMap(root, outRoot, map, limit, log, cts.Token);
                    if (stats == null) { fail++; continue; }
                    if (blend)
                    {
                        var dataDir = Path.Combine(outRoot, map);
                        var blendPath = Path.Combine(dataDir, map + ".blend");
                        if (BlenderRunner.BuildBlend(blender, Config.ScriptsDir, dataDir, blendPath, log, cts.Token))
                            log("[BLEND OK] " + blendPath);
                        else { fail++; log("[BLEND FAIL] " + map); }
                    }
                }
                catch (Exception e)
                {
                    fail++;
                    log("[FAIL] " + map + ": " + e.GetType().Name + " " + e.Message);
                }
            }

            File.WriteAllText(logFile, sb.ToString(), new UTF8Encoding(false));
            log("DONE maps=" + maps.Count + " failed=" + fail + " log=" + logFile);
            return fail == 0 ? 0 : 1;
        }

        // SekiroMapToolkit.exe --cli --audio [--sound <dir>] [--audio-out <dir>] [--maps m10,m11]
        //                                [--cats 环境,场景] [--banks sm10,rm11] [--bitrate 192] [--keepwav]
        static int RunAudio(string[] args)
        {
            var cfg = Config.Load();
            string soundRoot = null, outRoot = null, mapsArg = null, catsArg = null, banksArg = null;
            int bitrate = cfg.AudioBitrate > 0 ? cfg.AudioBitrate : 192;
            bool keepWav = false, noSkip = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--sound": if (i + 1 < args.Length) soundRoot = args[++i]; break;
                    case "--audio-out": if (i + 1 < args.Length) outRoot = args[++i]; break;
                    case "--maps": if (i + 1 < args.Length) mapsArg = args[++i]; break;
                    case "--cats": if (i + 1 < args.Length) catsArg = args[++i]; break;
                    case "--banks": if (i + 1 < args.Length) banksArg = args[++i]; break;
                    case "--bitrate": if (i + 1 < args.Length) int.TryParse(args[++i], out bitrate); break;
                    case "--keepwav": keepWav = true; break;
                    case "--noskip": noSkip = true; break;
                    case "--all": mapsArg = "*"; break;
                }
            }

            soundRoot ??= cfg.AudioSourceRoot;
            if (string.IsNullOrEmpty(soundRoot) && !string.IsNullOrEmpty(cfg.GameRoot))
                soundRoot = Path.Combine(cfg.GameRoot, "sound");
            outRoot ??= cfg.AudioOutputRoot;
            if (string.IsNullOrEmpty(outRoot))
                outRoot = Path.Combine(cfg.OutputRoot ?? Path.Combine(cfg.GameRoot ?? "", "map", "临时文件夹"), "音频");

            if (string.IsNullOrEmpty(soundRoot) || !Directory.Exists(soundRoot))
            {
                Console.Error.WriteLine("音库目录不存在：" + soundRoot);
                return 2;
            }

            var def = ToolCatalog.Find("vgmstream");
            var vgm = def?.ResolveExe();
            if (vgm == null)
            {
                Console.WriteLine("[音频] 缺 vgmstream，正在下载…");
                vgm = ToolCatalog.InstallAsync(def, cfg.GitHubToken, s => Console.WriteLine(s), CancellationToken.None)
                                  .GetAwaiter().GetResult();
                if (vgm == null) { Console.Error.WriteLine("vgmstream 安装失败"); return 4; }
            }

            Directory.CreateDirectory(outRoot);

            var all = AudioExporter.Scan(soundRoot);
            IEnumerable<AudioBank> sel = all;

            if (!string.IsNullOrEmpty(banksArg) && banksArg != "*")
            {
                var want = new HashSet<string>(banksArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                        .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
                sel = sel.Where(b => want.Contains(b.Name));
            }
            if (!string.IsNullOrEmpty(mapsArg) && mapsArg != "*")
            {
                var want = new HashSet<string>(mapsArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                      .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
                sel = sel.Where(b => want.Contains(b.MapKey));
            }
            if (!string.IsNullOrEmpty(catsArg))
            {
                var want = new HashSet<string>(catsArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                      .Select(s => s.Trim()));
                sel = sel.Where(b => want.Contains(b.Category));
            }

            var banks = sel.ToList();
            if (banks.Count == 0) { Console.Error.WriteLine("没有匹配的音库"); return 3; }

            var sb = new StringBuilder();
            var gate = new object();
            Action<string> log = s =>
            {
                var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
                Console.WriteLine(line);
                lock (gate) sb.AppendLine(line);
            };

            log("SOUND=" + soundRoot + "  OUT=" + outRoot + "  banks=" + banks.Count + "  bitrate=" + bitrate);

            var opt = new AudioExporter.Options
            {
                SoundRoot = soundRoot,
                OutRoot = outRoot,
                Vgmstream = vgm,
                Bitrate = bitrate,
                KeepWav = keepWav,
                SkipSilent = !noSkip,
            };

            var res = AudioExporter.ExportBanksAsync(opt, banks, log, null, CancellationToken.None)
                                   .GetAwaiter().GetResult();

            var logFile = Path.Combine(outRoot, "cli_audio.log");
            File.WriteAllText(logFile, sb.ToString(), new UTF8Encoding(false));
            log(string.Format("DONE banks={0} files={1} size={2:F1}MB seconds={3:F1} failed={4} log={5}",
                res.Banks, res.Files, res.Bytes / 1048576.0, res.Seconds, res.Failed, logFile));
            return res.Failed == 0 ? 0 : 1;
        }

        // SekiroMapToolkit.exe --cli --anims [--root <dir>] [--chr c0000,c1000]
        //        [--find a000_0000] [--out <dir>] [--raw] [--dry]
        //        [--fbx] [--mesh] [--tex] [--with-audio] [--rest]
        static int RunAnims(string[] args)
        {
            var cfg = Config.Load();
            string root = cfg.GameRoot, chrArg = null, find = null, outDir = cfg.OutputRoot;
            bool raw = false, dry = false;
            bool fbx = false, mesh = false, tex = false, audio = false, rest = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--root": if (i + 1 < args.Length) root = args[++i]; break;
                    case "--chr": if (i + 1 < args.Length) chrArg = args[++i]; break;
                    case "--find": if (i + 1 < args.Length) find = args[++i]; break;
                    case "--out": if (i + 1 < args.Length) outDir = args[++i]; break;
                    case "--raw": raw = true; break;
                    case "--dry": dry = true; break;
                    case "--fbx": fbx = true; break;
                    case "--mesh": mesh = true; break;
                    case "--tex": tex = true; break;
                    case "--with-audio": audio = true; break;
                    case "--rest": rest = true; break;
                }
            }

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Console.Error.WriteLine("游戏目录不存在：" + root);
                return 2;
            }

            // ---- FBX 路线：走 CharacterExport 一体化导出
            if (fbx || mesh || tex || audio || rest)
            {
                return RunAnimsFbx(args, root, chrArg, find, outDir,
                    fbx, mesh, tex, audio, rest);
            }

            var all = AnimationLibrary.ListCharacters(root);
            Console.WriteLine("有动画的角色 " + all.Count + " 个：" + string.Join(", ", all.Take(12)));

            var pick = string.IsNullOrEmpty(chrArg)
                ? all
                : chrArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).ToList();

            int totalAnims = 0;
            var keep = new List<AnimEntry>();
            foreach (var c in pick)
            {
                var a = AnimationLibrary.Load(root, c, s => Console.WriteLine(s));
                totalAnims += a.Anims.Count;
                foreach (var e in a.Anims)
                    if (string.IsNullOrEmpty(find) ||
                        e.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        e.Id.ToString().Contains(find))
                        keep.Add(e);
            }

            Console.WriteLine("选中动画 " + keep.Count + " / " + totalAnims);
            foreach (var e in keep.Take(15))
                Console.WriteLine(string.Format("    {0,-24} {1,-14} ID {2,-8} 动作 {3} 轨 {4}",
                    e.Name, e.TaeFile, e.Id, e.Actions, e.Tracks));

            if (dry) return 0;

            var dir = Path.Combine(outDir, "动画");
            int n = raw
                ? AnimationLibrary.ExportRaw(root, pick[0], dir, s => Console.WriteLine(s))
                : AnimationLibrary.ExportSelected(root, keep, dir, s => Console.WriteLine(s));

            Console.WriteLine("DONE 动画文件=" + n + " → " + dir);
            return 0;
        }

        /// <summary>
        /// FBX 一体化导出：动画 + 模型 + 贴图 + 音频。
        /// </summary>
        static int RunAnimsFbx(string[] args, string root, string chrArg, string find,
                               string outDir, bool fbx, bool mesh, bool tex, bool audio, bool rest)
        {
            bool dry = args.Any(a => a.Equals("--dry", StringComparison.OrdinalIgnoreCase));

            var chars = AnimationLibrary.ListCharacters(root);
            var pick = string.IsNullOrEmpty(chrArg)
                ? chars
                : chrArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).ToList();

            Console.WriteLine("目标角色 " + pick.Count + " 个：" + string.Join(", ", pick.Take(12)));

            var opt = new ChrExportOptions
            {
                Anim = fbx,
                Mesh = mesh || fbx,
                Tex = tex || mesh,
                Audio = audio,
                RestPose = rest,
            };
            Console.WriteLine("导出内容：" + opt);

            if (dry) return 0;

            var dir = Path.Combine(outDir, "动画");
            int bad = 0;
            foreach (var c in pick)
            {
                IEnumerable<string> want = null;
                if (!string.IsNullOrEmpty(find))
                {
                    var hk = CharacterAssets.LoadAnimHkx(root, c);
                    want = hk.Select(h => h.file)
                             .Where(n => n.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0)
                             .ToList();
                    if (!want.Any())
                    {
                        Console.WriteLine("  " + c + "：没有匹配 " + find + " 的动画");
                        continue;
                    }
                }

                var r = CharacterExport.Export(root, c, want, dir, opt, s => Console.WriteLine(s));
                if (r.Errors.Count > 0) bad++;
                foreach (var e in r.Errors.Take(3)) Console.WriteLine("    错误 " + e);
            }

            Console.WriteLine("DONE 输出 → " + Path.Combine(dir));
            return bad == 0 ? 0 : 1;
        }

        // SekiroMapToolkit.exe --cli --tae [--root <dir>] [--chr c0000] [--find a000] [--sound-only]
        //        [--csv <file>]
        // 把角色的 TAE 事件表打出来（图形编辑器那份数据的命令行版）。
        static int RunTae(string[] args)
        {
            var cfg = Config.Load();
            string root = cfg.GameRoot;
            string chrArg = null, find = null, csv = null;
            bool soundOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--root": if (i + 1 < args.Length) root = args[++i]; break;
                    case "--chr": if (i + 1 < args.Length) chrArg = args[++i]; break;
                    case "--find": if (i + 1 < args.Length) find = args[++i]; break;
                    case "--csv": if (i + 1 < args.Length) csv = args[++i]; break;
                    case "--sound-only": soundOnly = true; break;
                }
            }

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Console.Error.WriteLine("解包目录不存在：" + root);
                return 2;
            }

            var chrs = string.IsNullOrEmpty(chrArg)
                ? new List<string> { "c0000" }
                : chrArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();

            var template = File.Exists(Path.Combine(AppContext.BaseDirectory, "Res", "TAE.Template.SDT.xml"))
                ? Path.Combine(AppContext.BaseDirectory, "Res", "TAE.Template.SDT.xml")
                : null;

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(csv))
                sb.AppendLine("character,anim,type,name,start,end," +
                              "params");

            int totalAnim = 0, totalEv = 0, totalParam = 0;

            foreach (var chr in chrs)
            {
                var path = Path.Combine(root, "chr", chr + ".anibnd.dcx");
                if (!File.Exists(path)) { Console.Error.WriteLine("跳过 " + chr + "（没有 anibnd）"); continue; }

                var bnd = SoulsFormats.BND4.Read(path);
                var taeFiles = bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)).ToList();
                if (taeFiles.Count == 0) { Console.Error.WriteLine("跳过 " + chr + "（没有 tae）"); continue; }

                foreach (var tf in taeFiles)
                {
                    List<TaeAnimInfo> anims;
                    try { anims = TaeEvents.Load(tf.Bytes, template); }
                    catch (Exception ex) { Console.Error.WriteLine(chr + " 解析失败：" + ex.Message); continue; }

                    foreach (var a in anims)
                    {
                        if (!string.IsNullOrEmpty(find) &&
                            a.Id.ToString().IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var evs = a.Events.AsEnumerable();
                        if (soundOnly) evs = evs.Where(e => e.Name != null &&
                            e.Name.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0);

                        var list = evs.OrderBy(e => e.Start).ToList();
                        if (list.Count == 0) continue;

                        totalAnim++;
                        Console.WriteLine();
                        Console.WriteLine("== " + chr + "  动画 " + a.Id + "  (" + list.Count + " 条事件)");

                        foreach (var e in list)
                        {
                            totalEv++;
                            if (e.Params.Count > 0) totalParam++;

                            var pv = string.Join("  ", e.Params.Select(kv => kv.Key + "=" + kv.Value));
                            Console.WriteLine(string.Format("   {0,8:F3} ~ {1,8:F3}  type {2,-6} {3,-34} {4}",
                                e.Start, e.End, e.Type, e.Name, pv));

                            if (!string.IsNullOrEmpty(csv))
                            {
                                const char Q = (char)34;                 // 双引号
                                var dq = new string(Q, 2);               // 两个双引号（CSV 转义）
                                string Esc(string v) => (v ?? "").Replace(dq, dq + dq);

                                sb.Append(chr).Append(',').Append(a.Id).Append(',')
                                  .Append(e.Type).Append(',')
                                  .Append(Q).Append(Esc(e.Name)).Append(Q).Append(',')
                                  .Append(e.Start.ToString("F3")).Append(',')
                                  .Append(e.End.ToString("F3")).Append(',')
                                  .Append(Q).Append(Esc(pv)).Append(Q)
                                  .AppendLine();
                            }
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("共 " + totalAnim + " 条动画 / " + totalEv + " 个事件，其中 " + totalParam + " 个带参数");

            if (!string.IsNullOrEmpty(csv))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(csv));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(csv, sb.ToString(), new System.Text.UTF8Encoding(true));
                Console.WriteLine("已写出 " + csv);
            }

            return 0;
        }

        // SekiroMapToolkit.exe --cli --events [--sound <dir>] [--audio-out <dir>]
        //        [--banks sm10,c1010] [--cats 场景] [--find a100000001] [--dry] [--bitrate 192]
        static int RunEvents(string[] args)
        {
            var cfg = Config.Load();
            string soundRoot = cfg.AudioSourceRoot, outRoot = cfg.AudioOutputRoot;
            string banksArg = null, catsArg = null, find = null;
            int bitrate = cfg.AudioBitrate > 0 ? cfg.AudioBitrate : 192;
            bool dry = false, keepWav = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--sound": if (i + 1 < args.Length) soundRoot = args[++i]; break;
                    case "--audio-out": if (i + 1 < args.Length) outRoot = args[++i]; break;
                    case "--banks": if (i + 1 < args.Length) banksArg = args[++i]; break;
                    case "--cats": if (i + 1 < args.Length) catsArg = args[++i]; break;
                    case "--find": if (i + 1 < args.Length) find = args[++i]; break;
                    case "--bitrate": if (i + 1 < args.Length) int.TryParse(args[++i], out bitrate); break;
                    case "--keepwav": keepWav = true; break;
                    case "--dry": dry = true; break;
                }
            }

            if (string.IsNullOrEmpty(soundRoot) || !Directory.Exists(soundRoot))
            {
                Console.Error.WriteLine("音库目录不存在：" + soundRoot);
                return 2;
            }

            Action<string> log = s => Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
            var idx = EventLibrary.Build(soundRoot, log);

            var sel = idx.Events.AsEnumerable();
            if (!string.IsNullOrEmpty(banksArg))
            {
                var set = new HashSet<string>(banksArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                      .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
                sel = sel.Where(e => set.Contains(e.Bank));
            }
            if (!string.IsNullOrEmpty(catsArg))
            {
                var set = new HashSet<string>(catsArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(s => s.Trim()));
                sel = sel.Where(e => set.Contains(e.Category));
            }
            if (!string.IsNullOrEmpty(find))
                sel = sel.Where(e => e.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     e.Path.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = sel.ToList();
            Console.WriteLine("选中 " + list.Count + " 个事件");
            foreach (var e in list.Take(20))
                Console.WriteLine(string.Format("    {0,-12} {1,-28} {2}", e.Bank, e.Name, e.Subsong >= 0 ? "轨 " + e.Subsong : "整库匹配"));

            if (dry) return 0;

            var def = ToolCatalog.Find("vgmstream");
            var vgm = def?.ResolveExe();
            if (vgm == null)
            {
                Console.WriteLine("[事件] 缺 vgmstream，正在下载…");
                vgm = ToolCatalog.InstallAsync(def, cfg.GitHubToken, s => Console.WriteLine(s), CancellationToken.None)
                                  .GetAwaiter().GetResult();
                if (vgm == null) { Console.Error.WriteLine("vgmstream 安装失败"); return 4; }
            }

            var opt = new EventExporter.Options
            {
                SoundRoot = soundRoot,
                OutRoot = outRoot,
                Vgmstream = vgm,
                Bitrate = bitrate,
                KeepWav = keepWav,
            };
            var res = EventExporter.ExportAsync(opt, list, log,
                (d, t, msg) => { }, CancellationToken.None).GetAwaiter().GetResult();

            Console.WriteLine(string.Format("DONE events={0} files={1} size={2:F1}MB seconds={3:F1} failed={4}",
                res.Events, res.Files, res.Bytes / 1048576.0, res.Seconds, res.Failed));
            return res.Failed == 0 ? 0 : 1;
        }

        // SekiroMapToolkit.exe --cli --fevdump [--sound <dir>] [--fev sm10,c1010]
        static int RunFevDump(string[] args)
        {
            var cfg = Config.Load();
            var soundRoot = cfg.AudioSourceRoot;
            string only = null;
            int limit = 0;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--sound": if (i + 1 < args.Length) soundRoot = args[++i]; break;
                    case "--fev": if (i + 1 < args.Length) only = args[++i]; break;
                    case "--limit": if (i + 1 < args.Length) int.TryParse(args[++i], out limit); break;
                }
            }

            if (string.IsNullOrEmpty(soundRoot) || !Directory.Exists(soundRoot))
            {
                Console.Error.WriteLine("音库目录不存在：" + soundRoot);
                return 2;
            }

            var projs = FevParser.Scan(soundRoot);
            var want = string.IsNullOrEmpty(only)
                ? null
                : new HashSet<string>(only.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                             .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);

            int shown = 0;
            long total = 0;
            foreach (var p in projs)
            {
                total += p.Sounds.Count;
                if (want != null && !want.Contains(p.Name)) continue;
                var fsb = Path.Combine(soundRoot, p.Name + ".fsb");
                var fi = File.Exists(fsb) ? FsbNames.Read(fsb) : new FsbNames.Info();
                int matched = 0;
                foreach (var s in p.Sounds)
                    if (FsbNames.IndexOf(fi, s.ShortName) != null) matched++;

                Console.WriteLine(string.Format("{0}  [{1}]  事件 {2}  fsb轨 {3}  匹配 {4}  {5} KB",
                    p.Name, p.Category, p.Sounds.Count, fi.Subsongs, matched, p.Bytes / 1024));
                foreach (var s in p.Sounds.Take(limit > 0 ? limit : int.MaxValue))
                {
                    var idx = FsbNames.IndexOf(fi, s.ShortName);
                    Console.WriteLine(string.Format("      {0,-32} -> {1}", s.Path, idx?.ToString() ?? "未匹配"));
                }
                shown++;
                if (limit > 0 && shown >= limit) break;
            }
            Console.WriteLine(string.Format("共 {0} 个 fev / {1} 个音频事件", projs.Count, total));
            return 0;
        }

        // SekiroMapToolkit.exe --cli --tools [--only key1,key2] [--list]
        static int RunTools(string[] args)
        {
            var cfg = Config.Load();
            string only = null;
            bool list = args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < args.Length; i++)
                if (args[i].Equals("--only", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    only = args[++i];

            if (list || args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var d in ToolCatalog.All)
                {
                    var exe = d.ResolveExe();
                    Console.WriteLine(string.Format("  {0,-16} {1,-8} {2}", d.Key,
                        exe != null ? "已安装" : (d.AssetUrl == null ? "仅源码" : "未安装"),
                        exe ?? d.RepoUrl));
                }
                return 0;
            }

            var keys = string.IsNullOrEmpty(only)
                ? ToolCatalog.All.Where(t => t.AssetUrl != null).Select(t => t.Key).ToList()
                : only.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

            Action<string> log = s => Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
            log("工具目录：" + ToolCatalog.Root);

            int fail = 0;
            foreach (var key in keys)
            {
                var def = ToolCatalog.Find(key);
                if (def == null) { log("[跳过] 未知工具 " + key); fail++; continue; }
                if (def.Installed) { log("[已有] " + def.Name + " → " + (def.ResolveExe() ?? def.Dir)); continue; }

                var exe = ToolCatalog.InstallAsync(def, cfg.GitHubToken, log, CancellationToken.None)
                                     .GetAwaiter().GetResult();
                if (exe == null) fail++;
            }

            log("TOOLS DONE installed=" + ToolCatalog.InstalledCount + "/" + ToolCatalog.All.Length + " failed=" + fail);
            return fail == 0 ? 0 : 1;
        }
    }
}