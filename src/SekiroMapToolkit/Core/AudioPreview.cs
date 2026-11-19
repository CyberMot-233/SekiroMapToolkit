using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 事件试听：解出单个子音轨 → 用 Windows 默认输出设备播 → 播完自动清理。
    ///
    /// 只解一条子音轨（vgmstream -s N），所以点一下 1 秒内就能响，不用等整个音库。
    /// </summary>
    static class AudioPreview
    {
        static readonly object Gate = new object();
        static IWavePlayer _out;
        static WaveStream _reader;
        static string _tempFile;
        static int _token;

        /// <summary>当前是否在放。</summary>
        public static bool IsPlaying { get { lock (Gate) { return _out != null && _out.PlaybackState == PlaybackState.Playing; } } }

        /// <summary>正在放的那条（用于 UI 显示），没在放返回 null。</summary>
        public static string Current { get; private set; }

        /// <summary>停止并清理。</summary>
        public static void Stop()
        {
            lock (Gate)
            {
                _token++;          // 让还在解码的那次回调失效
                Cleanup();
            }
        }

        static void Cleanup()
        {
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;

            try { _reader?.Dispose(); } catch { }
            _reader = null;

            if (_tempFile != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    try { if (File.Exists(_tempFile)) File.Delete(_tempFile); _tempFile = null; break; }
                    catch { Thread.Sleep(60); }
                }
            }
            Current = null;
        }

        /// <summary>
        /// 试听一个事件。同一条再点一次就是停止。
        /// </summary>
        /// <param name="vgm">vgmstream-cli.exe</param>
        /// <param name="fsb">音库完整路径</param>
        /// <param name="e">要听的事件</param>
        public static Task<bool> PlayAsync(string vgm, string fsb, EventEntry e, Action<string> log)
        {
            return Task.Run(() =>
            {
                lock (Gate)
                {
                    // 正在放同一条 → 当成停止
                    if (Current == e.Key && _out != null && _out.PlaybackState == PlaybackState.Playing)
                    {
                        _token++;
                        Cleanup();
                        return false;
                    }
                }

                string wav = null;
                try
                {
                    var tmpDir = Path.Combine(Path.GetTempPath(), "smk_preview");
                    Directory.CreateDirectory(tmpDir);
                    wav = Path.Combine(tmpDir, Sanitize(e.Bank + "_" + e.Name) + ".wav");

                    if (!Decode(vgm, fsb, e, wav, log)) return false;

                    lock (Gate)
                    {
                        _token++;
                        var myToken = _token;
                        Cleanup();
                        _tempFile = wav;
                        Current = e.Key;

                        _reader = new MediaFoundationReader(wav);
                        _out = new WaveOutEvent { DesiredLatency = 220 };
                        _out.Init(_reader);
                        _out.PlaybackStopped += (_, __) =>
                        {
                            // 播完自动收拾，但别把下一次播放的对象拆了
                            lock (Gate)
                            {
                                if (myToken == _token) Cleanup();
                            }
                        };
                        _out.Play();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    log?.Invoke("[试听] 失败：" + ex.Message);
                    try { if (wav != null && File.Exists(wav)) File.Delete(wav); } catch { }
                    return false;
                }
            });
        }

        /// <summary>解一条子音轨。Subsong 未知时整库解一遍再按名字挑。</summary>
        static bool Decode(string vgm, string fsb, EventEntry e, string outWav, Action<string> log)
        {
            if (File.Exists(outWav)) { try { File.Delete(outWav); } catch { } }

            if (e.Subsong >= 0)
                return RunVgm(vgm, new[] { "-i", "-s", e.Subsong.ToString(CultureInfo.InvariantCulture), "-o", outWav, fsb }, log);

            // 整库解到临时目录，再按名字挑一条
            var bag = Path.Combine(Path.GetTempPath(), "smk_preview_bag");
            try
            {
                if (Directory.Exists(bag)) Directory.Delete(bag, true);
            }
            catch { }
            Directory.CreateDirectory(bag);

            bool ok = RunVgm(vgm, new[] { "-i", "-S", "0", "-o", Path.Combine(bag, "?03s_?n"), fsb }, log);
            if (!ok) return false;

            foreach (var f in Directory.EnumerateFiles(bag))
            {
                var b = Path.GetFileNameWithoutExtension(f).Replace(".wav", "");
                var us = b.IndexOf('_');
                if (us > 0 && us < 6 && b.Substring(0, us).All(char.IsDigit)) b = b.Substring(us + 1);
                b = b.Replace("bank_", "").Replace(";", "_").Replace(" ", "");

                if (b.IndexOf(e.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { File.Copy(f, outWav, true); } catch { }
                    break;
                }
            }

            try { Directory.Delete(bag, true); } catch { }
            return File.Exists(outWav) && new FileInfo(outWav).Length > 44;
        }

        static bool RunVgm(string vgm, string[] args, Action<string> log)
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
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (!string.IsNullOrWhiteSpace(err))
            {
                var first = err.Split('\n')[0].Trim();
                if (first.Length > 0 && first.Contains("error", StringComparison.OrdinalIgnoreCase))
                    log?.Invoke("[试听] vgmstream：" + first);
            }
            return true;
        }

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Replace('|', '_');
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}
