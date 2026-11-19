using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SekiroMapToolkit.Core
{
    static class BlenderRunner
    {
        public static string ScriptPath(string scriptsDir, string name) => Path.Combine(scriptsDir, name);

        // build one exported map bundle into a .blend
        public static bool BuildBlend(string blender, string scriptsDir, string dataDir, string blendOut,
                                      Action<string> log, CancellationToken ct)
        {
            var script = ScriptPath(scriptsDir, "build_blend.py");
            if (!File.Exists(script)) { log("[错误] 找不到 " + script); return false; }
            var code = Run(blender, new[]
            {
                "-b", "--factory-startup", "--python", script, "--", dataDir, blendOut
            }, log, ct);
            return code == 0 && File.Exists(blendOut);
        }

        // convenience: locate the bundled scripts next to the exe
        public static bool Build(string dataDir, string blendOut, string blender, Action<string> log, CancellationToken ct)
            => BuildBlend(blender, Config.ScriptsDir, dataDir, blendOut, log, ct);

        // dds -> opaque png, alpha dropped, for a whole folder
        public static bool ConvertDdsFolder(string blender, string scriptsDir, string texDir,
                                            Action<string> log, CancellationToken ct)
        {
            var script = ScriptPath(scriptsDir, "convert_dds.py");
            if (!File.Exists(script)) { log("[错误] 找不到 " + script); return false; }
            var code = Run(blender, new[]
            {
                "-b", "--factory-startup", "--python", script, "--", texDir
            }, log, ct);
            return code == 0;
        }

        // generic blender -b script run
        public static int Run(string blender, IEnumerable<string> args, Action<string> log, CancellationToken ct)
        {
            log ??= delegate { };
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender))
            {
                log("[错误] Blender 路径无效：" + blender);
                return -1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = blender,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(blender) ?? Environment.CurrentDirectory
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var errLines = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) log(e.Data); };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                log(e.Data);
                if (errLines.Length < 8000) errLines.AppendLine(e.Data);
            };

            try { p.Start(); }
            catch (Exception e) { log("[错误] 启动 Blender 失败：" + e.Message); return -1; }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            while (!p.WaitForExit(400))
            {
                if (ct.IsCancellationRequested)
                {
                    try { p.Kill(true); } catch { }
                    log("[已取消] Blender 进程已结束");
                    return -2;
                }
            }
            p.WaitForExit();
            return p.ExitCode;
        }

        // create a 4x4 grid preview thumbnail of the map with blender (optional helper)
        public static bool RenderPreview(string blender, string scriptsDir, string blendFile, string pngOut,
                                         Action<string> log, CancellationToken ct)
        {
            var script = ScriptPath(scriptsDir, "render_preview.py");
            if (!File.Exists(script)) { log("[错误] 找不到 " + script); return false; }
            var code = Run(blender, new[] { "-b", blendFile, "--python", script, "--", pngOut }, log, ct);
            return code == 0 && File.Exists(pngOut);
        }
    }
}