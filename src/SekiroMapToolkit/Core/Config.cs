using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SekiroMapToolkit.Core
{
    class ToolEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    class Config
    {
        public string GameRoot { get; set; }
        public string OutputRoot { get; set; }
        public string BlenderPath { get; set; }
        public bool SkipFog { get; set; } = true;
        public bool BuildBlend { get; set; } = true;
        public bool OpenWhenDone { get; set; }
        public int LimitModels { get; set; }
        public List<string> LastMaps { get; set; } = new List<string>();
        public List<ToolEntry> Tools { get; set; } = new List<ToolEntry>();

        /// <summary>GitHub PAT，用于下载内置工具（私有仓库 / 避开匿名限速）。</summary>
        public string GitHubToken { get; set; } = "";

        /// <summary>背景图模式：art（内置只狼二次元壁纸） / render（自己导出的地图渲染图） / off。</summary>
        public string BackgroundMode { get; set; } = "art";

        /// <summary>自定义背景图路径，非空时优先于 BackgroundMode。</summary>
        public string BackgroundImage { get; set; } = "";

        /// <summary>背景暗化强度 0.25–0.95，越大越暗。</summary>
        public double BackgroundDim { get; set; } = 0.70;

        /// <summary>是否每次启动轮换背景（一轮内不重复）。</summary>
        public bool BackgroundRotate { get; set; } = true;

        /// <summary>挂机时每 4 分钟自动换一张壁纸。</summary>
        public bool BackgroundAuto { get; set; } = true;

        /// <summary>当前壁纸序号，-1 = 随机。</summary>
        public int BackgroundIndex { get; set; } = -1;

        /// <summary>音库目录（含 *.fsb），默认 &lt;解包目录&gt;\sound。</summary>
        public string AudioSourceRoot { get; set; } = "";

        /// <summary>音频输出目录，默认 &lt;解包目录&gt;\map\临时文件夹\音频。</summary>
        public string AudioOutputRoot { get; set; } = "";

        /// <summary>MP3 码率（kbps）。</summary>
        public int AudioBitrate { get; set; } = 192;

        /// <summary>上次勾选的音库名。</summary>
        public List<string> LastAudioBanks { get; set; } = new List<string>();

        /// <summary>动画输出目录，默认 &lt;解包目录&gt;\map\临时文件夹\动画。</summary>
        public string AnimOutputRoot { get; set; } = "";

        public static string Dir => AppContext.BaseDirectory;

        public static string ScriptsDir
        {
            get
            {
                var d = Path.Combine(Dir, "scripts");
                if (Directory.Exists(d)) return d;
                // dev fallback: ...\src\SekiroMapToolkit\bin\Release\net9.0-windows -> repo\scripts
                var up = Path.GetFullPath(Path.Combine(Dir, "..", "..", "..", "..", "..", "scripts"));
                return Directory.Exists(up) ? up : d;
            }
        }

        public static Config Load()
        {
            var p = Path.Combine(Dir, "config.json");
            Config c = null;
            if (File.Exists(p))
            {
                try { c = JsonSerializer.Deserialize<Config>(File.ReadAllText(p, Encoding.UTF8)); }
                catch { c = null; }
            }
            c ??= new Config();
            c.FillDefaults();
            return c;
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                File.WriteAllText(Path.Combine(Dir, "config.json"), JsonSerializer.Serialize(this, opts), new UTF8Encoding(false));
            }
            catch { }
        }

        void FillDefaults()
        {
            if (string.IsNullOrEmpty(GameRoot) || !Directory.Exists(GameRoot))
            {
                foreach (var drive in new[] { "E:", "D:", "C:", "F:" })
                {
                    var p = Path.Combine(drive + "\\", "SteamLibrary", "steamapps", "common", "Sekiro", "文件");
                    if (Directory.Exists(p)) { GameRoot = p; break; }
                }
            }
            if (!string.IsNullOrEmpty(GameRoot))
            {
                if (string.IsNullOrEmpty(OutputRoot) || !Directory.Exists(OutputRoot))
                    OutputRoot = Path.Combine(GameRoot, "map", "临时文件夹");
                Directory.CreateDirectory(OutputRoot);

                if (string.IsNullOrEmpty(AudioSourceRoot) || !Directory.Exists(AudioSourceRoot))
                {
                    var s = Path.Combine(GameRoot, "sound");
                    if (Directory.Exists(s)) AudioSourceRoot = s;
                }
                if (string.IsNullOrEmpty(AudioOutputRoot))
                    AudioOutputRoot = Path.Combine(OutputRoot, "音频");
                if (string.IsNullOrEmpty(AnimOutputRoot))
                    AnimOutputRoot = Path.Combine(OutputRoot, "动画");
            }

            if (string.IsNullOrEmpty(BlenderPath) || !File.Exists(BlenderPath))
            {
                var cands = new List<string>
                {
                    @"E:\blender-4.5.5-windows-x64\blender.exe",
                    @"C:\Program Files\Blender Foundation\Blender 4.5\blender.exe",
                    @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
                    @"C:\Program Files\Blender Foundation\Blender 4.0\blender.exe"
                };
                if (!string.IsNullOrEmpty(GameRoot))
                {
                    var parent = Directory.GetParent(GameRoot)?.FullName;
                    if (parent != null)
                        cands.AddRange(Directory.GetDirectories(parent, "blender*", SearchOption.TopDirectoryOnly)
                                                .Select(d => Path.Combine(d, "blender.exe"))
                                                .Where(File.Exists));
                }
                BlenderPath = cands.FirstOrDefault(File.Exists) ?? "";
            }

            // 内置工具目录（<exe>\tools\*）优先；找不到再扫游戏根的 tools\
            foreach (var def in ToolCatalog.All)
            {
                var hit = def.ResolveExe();
                if (hit == null) continue;
                var e = Tools.FirstOrDefault(t => string.Equals(t.Name, def.Name, StringComparison.OrdinalIgnoreCase));
                if (e == null)
                {
                    e = new ToolEntry { Name = def.Name };
                    Tools.Add(e);
                }
                if (string.IsNullOrEmpty(e.Path) || !File.Exists(e.Path)) e.Path = hit;
            }

            EnsureTool("DSMapStudio", "DSMapStudio.exe", @"tools\DSMapStudio\DSMapStudio.exe");
            EnsureTool("FLVER Editor", "FLVER_Editor.exe", @"tools\FLVER_Editor_X2.55nr\FLVER_Editor.exe");
            EnsureTool("Yabber", "Yabber.exe", @"tools\Yabber 1.3.1\Yabber.exe");
            EnsureTool("UXM 解包器", "UXM.exe", @"tools\UXM 2.4\UXM.exe");
            EnsureTool("Nuxe 解包器", "Nuxe.exe", @"tools\Nuxe-main\Nuxe 1.3.0\Nuxe.exe");
            EnsureTool("DS Anim Studio", "DS ANIM STUDIO.exe", @"tools\DSAnimStudio-v5-RC6.1-zh-CN-win64\DS ANIM STUDIO.exe");

            // 去重：同名只留一条，优先保留路径有效的那条
            var merged = new List<ToolEntry>();
            foreach (var t in Tools)
            {
                var old = merged.FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
                if (old == null) { merged.Add(t); continue; }
                var oldOk = !string.IsNullOrEmpty(old.Path) && File.Exists(old.Path);
                var newOk = !string.IsNullOrEmpty(t.Path) && File.Exists(t.Path);
                if (!oldOk && newOk) old.Path = t.Path;
            }
            Tools.Clear();
            Tools.AddRange(merged.Where(t => !string.IsNullOrEmpty(t.Path) && File.Exists(t.Path)));
        }

        // adds / repairs one tool entry, looking for the exe below <game>\tools
        void EnsureTool(string name, string exeName, string defaultRel)
        {
            var e = Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                e = new ToolEntry { Name = name };
                Tools.Add(e);
            }
            if (!string.IsNullOrEmpty(e.Path) && File.Exists(e.Path)) return;

            if (string.IsNullOrEmpty(GameRoot)) return;
            var parent = Directory.GetParent(GameRoot)?.FullName;
            if (parent == null) return;

            var hit = SafeEnumerate(parent, exeName).OrderBy(p => p.Length).FirstOrDefault();
            if (hit == null)
            {
                var d = Path.Combine(parent, defaultRel);
                if (File.Exists(d)) hit = d;
            }
            if (hit != null) e.Path = hit;
        }

        static IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            int guard = 0;
            while (stack.Count > 0 && guard < 20000)
            {
                var d = stack.Pop();
                guard++;
                string[] files = null, dirs = null;
                try { files = Directory.GetFiles(d, pattern); } catch { }
                try { dirs = Directory.GetDirectories(d); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (dirs != null) foreach (var sub in dirs) stack.Push(sub);
            }
        }
    }
}