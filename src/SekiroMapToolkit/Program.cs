using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SekiroMapToolkit.Core;
using SekiroMapToolkit.UI;

namespace SekiroMapToolkit
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            EnsureOodle();

            if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
                return RunCli(args);

            // self-test: dump the main window into a png and exit
            int shotIdx = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
            if (shotIdx >= 0 && shotIdx + 1 < args.Length)
                return RunShot(args[shotIdx + 1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new MainForm());
            return 0;
        }

        // ---------------- ui self-test ----------------

        static int RunShot(string path)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using (var f = new MainForm())
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(600);
                Application.DoEvents();
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                f.Close();
            }
            return 0;
        }

        // ---------------- oodle bootstrap ----------------

        // SoulsFormats needs oo2core to inflate Sekiro's DCX files; keep a copy next to the exe.
        static void EnsureOodle()
        {
            const string name = "oo2core_6_win64.dll";
            var here = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(here)) return;

            var cands = new List<string>();
            var cfg = Config.Load();
            if (!string.IsNullOrEmpty(cfg.GameRoot))
            {
                var parent = Directory.GetParent(cfg.GameRoot)?.FullName;
                if (parent != null) cands.Add(Path.Combine(parent, name));
            }
            foreach (var drive in new[] { "E:", "D:", "C:", "F:" })
                cands.Add(Path.Combine(drive + "\\", "SteamLibrary", "steamapps", "common", "Sekiro", name));

            foreach (var c in cands)
            {
                try
                {
                    if (!File.Exists(c)) continue;
                    File.Copy(c, here, true);
                    return;
                }
                catch { }
            }
        }

        // ---------------- headless ----------------

        static int RunCli(string[] args)
        {
            AttachConsole(-1);

            string root = null, outRoot = null, blender = null, mapsArg = null;
            int limit = 0;
            bool blend = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--root": root = args[++i]; break;
                    case "--out": outRoot = args[++i]; break;
                    case "--maps": mapsArg = args[++i]; break;
                    case "--map": mapsArg = args[++i]; break;
                    case "--limit": int.TryParse(args[++i], out limit); break;
                    case "--blender": blender = args[++i]; break;
                    case "--blend": blend = true; break;
                }
            }

            var cfg = Config.Load();
            if (string.IsNullOrEmpty(root)) root = cfg.GameRoot;
            if (string.IsNullOrEmpty(outRoot)) outRoot = cfg.OutputRoot;
            if (string.IsNullOrEmpty(blender)) blender = cfg.BlenderPath;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(outRoot))
            {
                Console.Error.WriteLine("missing --root / --out");
                return 2;
            }

            Directory.CreateDirectory(outRoot);
            var logFile = Path.Combine(outRoot, "cli_export.log");
            var sb = new StringBuilder();
            void Log(string s)
            {
                var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
                sb.AppendLine(line);
                try { Console.WriteLine(line); } catch { }
            }

            var maps = string.IsNullOrEmpty(mapsArg)
                ? SekiroExporter.ListMaps(root)
                : mapsArg.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            int fail = 0;
            foreach (var map in maps)
            {
                using (var cts = new CancellationTokenSource())
                {
                    try
                    {
                        var stats = SekiroExporter.ExportMap(root, outRoot, map, limit, Log, cts.Token);
                        if (stats == null) { fail++; continue; }
                        if (blend)
                        {
                            var dataDir = Path.Combine(outRoot, map);
                            var blendPath = Path.Combine(dataDir, map + ".blend");
                            if (BlenderRunner.Build(dataDir, blendPath, blender, Log, cts.Token))
                                Log("[BLEND OK] " + blendPath);
                            else
                            { fail++; Log("[BLEND FAIL] " + map); }
                        }
                    }
                    catch (Exception e)
                    {
                        fail++;
                        Log("[FAIL] " + map + ": " + e.GetType().Name + " " + e.Message);
                    }
                }
            }

            File.WriteAllText(logFile, sb.ToString(), new UTF8Encoding(false));
            Log("DONE maps=" + maps.Count + " failed=" + fail + " log=" + logFile);
            return fail == 0 ? 0 : 1;
        }
    }
}