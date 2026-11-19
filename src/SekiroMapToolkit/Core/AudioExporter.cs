using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SekiroMapToolkit.Core
{
    /// <summary>一个 FSB 音库：属于哪张地图、哪一类、多少个子音轨。</summary>
    class AudioBank
    {
        public string File;         // 完整路径
        public string Name;         // 不含扩展名，如 sm10 / vm10_jaj / c1010
        public string MapKey;       // 归到哪张地图，如 m10 / c1010 / main
        public string Category;     // 环境 / 场景 / 混响 / 特殊 / 语音 / 角色 / 全局
        public string Language;     // 语音才有：ja / en / fr / it / es / de / 空
        public int Subsongs;        // -1 表示还没探测
        public long Bytes;
    }

    /// <summary>把 文件\sound 下的 FSB 按地图归类，解成 WAV 再转 MP3。</summary>
    static class AudioExporter
    {
        public const string CatAmbience = "环境";
        public const string CatScene = "场景";
        public const string CatReverb = "混响";
        public const string CatSpecial = "特殊";
        public const string CatVoice = "语音";
        public const string CatChar = "角色";
        public const string CatGlobal = "全局";

        static readonly Dictionary<string, string> LangNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "jaj", "ja" }, { "enu", "en" }, { "frf", "fr" },
            { "iti", "it" }, { "ese", "es" }, { "ded", "de" },
        };

        // ---------------------------------------------------------------- 扫描

        /// <summary>扫 <paramref name="soundRoot"/> 下所有 .fsb，按前缀归类。</summary>
        public static List<AudioBank> Scan(string soundRoot)
        {
            var list = new List<AudioBank>();
            if (!Directory.Exists(soundRoot)) return list;
            foreach (var path in Directory.EnumerateFiles(soundRoot, "*.fsb", SearchOption.TopDirectoryOnly)
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var b = new AudioBank
                {
                    File = path,
                    Name = name,
                    MapKey = MapKeyOf(name),
                    Category = CategoryOf(name),
                    Language = LanguageOf(name),
                    Subsongs = -1,
                    Bytes = new FileInfo(path).Length,
                };
                list.Add(b);
            }
            return list;
        }

        /// <summary>sm10 → m10，vm11_jaj → m11，rm20 → m20，c1010 保持，main/smain 归 main。</summary>
        public static string MapKeyOf(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.StartsWith("smain")) return "main";
            if (n == "main") return "main";
            var m = Regex.Match(n, @"^(r|s|v|x)?(m\d+)(?:_[a-z]{3})?$");
            if (m.Success) return m.Groups[2].Value;             // m10 / m11 / m25
            m = Regex.Match(n, @"^(c\d+)$");
            if (m.Success) return m.Groups[1].Value;             // c1010
            return n;
        }

        public static string CategoryOf(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.StartsWith("smain") || n == "main") return CatGlobal;
            if (n.StartsWith("c")) return CatChar;
            if (n.StartsWith("vm") || n.StartsWith("v")) return CatVoice;
            if (n.StartsWith("sm")) return CatScene;
            if (n.StartsWith("rm")) return CatReverb;
            if (n.StartsWith("xm")) return CatSpecial;
            if (n.StartsWith("m")) return CatAmbience;
            return CatSpecial;
        }

        public static string LanguageOf(string name)
        {
            var m = Regex.Match(name.ToLowerInvariant(), @"_([a-z]{3})$");
            if (!m.Success) return "";
            return LangNames.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Groups[1].Value;
        }

        // ---------------------------------------------------------------- 探测

        /// <summary>用 vgmstream 读出每个 bank 有多少子音轨。</summary>
        public static void Probe(AudioBank bank, string vgm, Action<string> log = null)
        {
            try
            {
                var psi = new ProcessStartInfo(vgm)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-I");
                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add(bank.File);
                using var p = Process.Start(psi);
                var json = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(30000);
                var m = Regex.Match(json, "\"total\":\\s*(\\d+)");
                bank.Subsongs = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            }
            catch (Exception ex)
            {
                bank.Subsongs = 0;
                log?.Invoke("[音频] 探测失败 " + bank.Name + "：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 导出

        public sealed class Options
        {
            public string SoundRoot;
            public string OutRoot;
            public string Vgmstream;
            public HashSet<string> MapKeys;          // null / 空 = 全部
            public HashSet<string> Categories;       // null / 空 = 全部
            public int Bitrate = 192;                // kbps
            public bool KeepWav;
            public bool SkipSilent = true;           // 跳过 <0.1 秒的占位音
        }

        public sealed class Result
        {
            public int Banks;
            public int Files;
            public long Bytes;
            public int Failed;
            public double Seconds;
        }

        /// <summary>导出。逐 bank 解 WAV → 转 MP3 → 删 WAV，避免磁盘爆。</summary>
        public static Task<Result> ExportAsync(Options opt, Action<string> log,
                                               Action<int, int, string> progress,
                                               CancellationToken ct)
        {
            var banks = Scan(opt.SoundRoot)
                .Where(b => opt.MapKeys == null || opt.MapKeys.Count == 0 || opt.MapKeys.Contains(b.MapKey))
                .Where(b => opt.Categories == null || opt.Categories.Count == 0 || opt.Categories.Contains(b.Category))
                .ToList();
            return ExportBanksAsync(opt, banks, log, progress, ct);
        }

        /// <summary>只导出明确指定的这几个音库（UI 勾选后走这条）。</summary>
        public static Task<Result> ExportBanksAsync(Options opt, IList<AudioBank> banks, Action<string> log,
                                                    Action<int, int, string> progress,
                                                    CancellationToken ct)
        {
            return Task.Run(() => ExportCore(opt, banks, log, progress, ct), ct);
        }

        static Result ExportCore(Options opt, IList<AudioBank> banks, Action<string> log,
                                 Action<int, int, string> progress, CancellationToken ct)
        {
            var res = new Result();
            var t0 = DateTime.Now;

            log?.Invoke($"[音频] 选中 {banks.Count} 个音库，输出到 {opt.OutRoot}");
            Directory.CreateDirectory(opt.OutRoot);
            var tmpRoot = Path.Combine(Path.GetTempPath(), "smk_audio_" + Environment.ProcessId);
            Directory.CreateDirectory(tmpRoot);

            var done = 0;
            progress?.Invoke(0, banks.Count, "准备中…");

            try
            {
                foreach (var bank in banks)
                {
                    ct.ThrowIfCancellationRequested();
                    var label = bank.MapKey + " / " + bank.Category + " / " + bank.Name;
                    progress?.Invoke(done, banks.Count, "导出 " + label);
                    try
                    {
                        var tmp = Path.Combine(tmpRoot, bank.Name);
                        Directory.CreateDirectory(tmp);
                        if (!DecodeBank(opt.Vgmstream, bank, tmp, log, ct, out var wavs))
                        {
                            res.Failed++;
                            continue;
                        }

                        var dir = Path.Combine(opt.OutRoot, bank.MapKey, bank.Category, bank.Name);
                        Directory.CreateDirectory(dir);

                        int n = 0;
                        foreach (var wav in wavs.OrderBy(w => w, StringComparer.OrdinalIgnoreCase))
                        {
                            ct.ThrowIfCancellationRequested();
                            var baseName = Path.GetFileNameWithoutExtension(wav);
                            if (opt.SkipSilent && IsTiny(wav)) { TryDelete(wav); continue; }
                            var mp3 = Path.Combine(dir, SafeName(baseName) + ".mp3");
                            try
                            {
                                Mp3.Encode(wav, mp3, opt.Bitrate);
                                n++;
                                res.Bytes += new FileInfo(mp3).Length;
                            }
                            catch (Exception ex)
                            {
                                res.Failed++;
                                log?.Invoke("[音频] 编码失败 " + Path.GetFileName(wav) + "：" + ex.Message);
                            }
                            if (opt.KeepWav)
                                File.Move(wav, Path.Combine(dir, SafeName(baseName) + ".wav"), true);
                            else
                                TryDelete(wav);
                        }

                        res.Banks++;
                        res.Files += n;
                        log?.Invoke($"[音频] {label} → {n} 个 mp3");
                        TryDeleteDir(tmp);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        res.Failed++;
                        log?.Invoke("[音频] 失败 " + bank.Name + "：" + ex.Message);
                    }
                    done++;
                    progress?.Invoke(done, banks.Count, "完成 " + label);
                }
            }
            finally
            {
                TryDeleteDir(tmpRoot);
            }

            res.Seconds = (DateTime.Now - t0).TotalSeconds;
            return res;
        }

        static bool IsTiny(string wav)
        {
            try
            {
                using var r = new WaveFileReader(wav);
                return r.TotalTime.TotalSeconds < 0.1;
            }
            catch
            {
                // 无扩展名时 WaveFileReader 也能读；真读不了就当它不短，交给编码器报错
                return new FileInfo(wav).Length < 2048;
            }
        }

        /// <summary>调 vgmstream 把整个 bank 拆成一堆 wav；返回 wav 列表。</summary>
        static bool DecodeBank(string vgm, AudioBank bank, string tmpDir,
                               Action<string> log, CancellationToken ct, out List<string> wavs)
        {
            wavs = new List<string>();
            var psi = new ProcessStartInfo(vgm)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-i");            // 忽略循环，整段播一次
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add("0");             // 全部子音轨
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(Path.Combine(tmpDir, "?03s_?n"));
            psi.ArgumentList.Add(bank.File);

            using var p = Process.Start(psi);
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

            // 注意：?n 带不带 .wav 取决于音库自己的命名（vm*/smain 的轨名就没有扩展名），
            // 所以这里按「全部文件」收，不能只挑 *.wav
            wavs = Directory.EnumerateFiles(tmpDir).Where(x => !x.EndsWith(".log")).ToList();
            if (wavs.Count == 0)
                log?.Invoke("[音频] " + bank.Name + " 没有解出任何子音轨" +
                            (string.IsNullOrWhiteSpace(err) ? "" : "：" + FirstLine(err)));
            return wavs.Count > 0;
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        static string SafeName(string s)
        {
            // vgmstream 的 ?n 会把多个别名拼成 "a.wav; b.wav"，这里压成一个干净名字
            s = s.Replace(".wav", "").Replace("；", "_").Replace(";", "_");
            s = s.Replace("bank_", "").Replace("bank_ntc_", "");
            s = Regex.Replace(s, @"[\s]+", "_");
            s = Regex.Replace(s, @"_+", "_").Trim('_', '.', ' ');

            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }

    /// <summary>WAV → MP3，走 Windows 自带 MediaFoundation，不依赖 ffmpeg。</summary>
    static class Mp3
    {
        static readonly object Gate = new object();
        static bool _started;

        // MP3 只认这几个采样率，其它（如只狼里常见的 11000 / 24000 变体）必须重采样
        static readonly int[] Mp3Rates = { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 };

        static int NearestRate(int hz)
        {
            int best = 44100, bestD = int.MaxValue;
            foreach (var r in Mp3Rates)
            {
                int d = Math.Abs(r - hz);
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        public static void Encode(string wavPath, string mp3Path, int bitrateKbps)
        {
            lock (Gate)
            {
                if (!_started)
                {
                    try { MediaFoundationApi.Startup(); } catch { }
                    _started = true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(mp3Path) ?? ".");
                if (File.Exists(mp3Path)) TryDeleteFile(mp3Path);

                using var reader = new WaveFileReader(wavPath);

                // 快路：已经是 16bit 双声道、采样率 MP3 直接认，就不做任何转换
                var f = reader.WaveFormat;
                if (f.Channels == 2 && f.BitsPerSample == 16 &&
                    f.Encoding == WaveFormatEncoding.Pcm &&
                    Array.IndexOf(Mp3Rates, f.SampleRate) >= 0)
                {
                    EncodeStream(reader, reader.WaveFormat, mp3Path, bitrateKbps);
                    return;
                }

                using var conv = new Pcm16Stereo(reader);
                EncodeStream(conv, conv.WaveFormat, mp3Path, bitrateKbps);
            }
        }

        static void EncodeStream(WaveStream src, WaveFormat fmt, string mp3Path, int bitrateKbps)
        {
            var mt = MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_MP3, fmt, bitrateKbps * 1000);
            if (mt == null)
            {
                // 码率不支持时退到 128k
                mt = MediaFoundationEncoder.SelectMediaType(
                    AudioSubtypes.MFAudioFormat_MP3, fmt, 128000);
            }
            if (mt == null)
                throw new InvalidOperationException("系统没有可用的 MP3 编码器（" +
                    fmt.SampleRate + " Hz / " + bitrateKbps + " kbps）");

            using (var enc = new MediaFoundationEncoder(mt))
                enc.Encode(mp3Path, src);

            if (!File.Exists(mp3Path))
                throw new IOException("编码器没有写出 mp3 文件");
        }

        static void TryDeleteFile(string f) { try { File.Delete(f); } catch { } }

        /// <summary>把任意 PCM WAV 统一成 16bit 双声道 + MP3 支持的采样率。</summary>
        sealed class Pcm16Stereo : WaveStream
        {
            readonly WaveStream _src;
            readonly WaveFormat _inFmt;
            readonly WaveFormat _outFmt;
            readonly int _inCh;
            readonly int _inBytes;
            readonly bool _inFloat;

            readonly double _ratio;          // 输出帧 / 输入帧
            readonly List<short> _pending = new List<short>();
            double _pos;                     // 以输入帧为单位的读指针

            // 环形缓冲：每次把源 WAV 按块读进来
            byte[] _buf = Array.Empty<byte>();
            long _bufStartFrame = -1;
            const int BlockFrames = 8192;

            public Pcm16Stereo(WaveStream src)
            {
                _src = src;
                _inFmt = src.WaveFormat;
                _inCh = Math.Max(1, _inFmt.Channels);
                _inBytes = Math.Max(1, _inFmt.BitsPerSample / 8);
                _inFloat = _inFmt.Encoding == WaveFormatEncoding.IeeeFloat || _inFmt.BitsPerSample == 32;

                int rate = NearestRate(_inFmt.SampleRate);
                _outFmt = new WaveFormat(rate, 16, 2);
                _ratio = (double)rate / _inFmt.SampleRate;
            }

            public override WaveFormat WaveFormat => _outFmt;

            public override long Length
            {
                get
                {
                    long frames = _src.Length / Math.Max(1, _inCh * _inBytes);
                    return (long)(frames * _ratio) * 4;
                }
            }

            public override long Position
            {
                get => (long)(_pos * _ratio) * 4;
                set => _pos = value / 4.0 / _ratio;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int outFrames = count / 4;
                if (outFrames <= 0) return 0;
                if (_pending.Count < outFrames * 2) Resample(outFrames);

                int n = Math.Min(outFrames, _pending.Count / 2);
                for (int i = 0; i < n; i++)
                {
                    short l = _pending[i * 2], r = _pending[i * 2 + 1];
                    int o = offset + i * 4;
                    buffer[o] = (byte)l; buffer[o + 1] = (byte)(l >> 8);
                    buffer[o + 2] = (byte)r; buffer[o + 3] = (byte)(r >> 8);
                }
                _pending.RemoveRange(0, n * 2);
                return n * 4;
            }

            void Resample(int wantFrames)
            {
                int guard = 0;
                while (_pending.Count < wantFrames * 2 && guard++ < 10000)
                {
                    long needA = (long)Math.Floor(_pos);
                    long needB = needA + 1;
                    if (!Ensure(needA) || !Ensure(needB)) break;

                    double t = _pos - needA;
                    short l = (short)Math.Round(Sample(needA, 0) * (1 - t) + Sample(needB, 0) * t);
                    short r = (short)Math.Round(Sample(needA, 1) * (1 - t) + Sample(needB, 1) * t);
                    _pending.Add(l);
                    _pending.Add(r);
                    _pos += 1.0 / _ratio;
                }
            }

            bool Ensure(long frame)
            {
                if (_bufStartFrame >= 0 && frame >= _bufStartFrame && frame < _bufStartFrame + _buf.Length / (_inCh * _inBytes))
                    return true;

                long byteOff = frame * _inCh * _inBytes;
                if (byteOff < 0 || byteOff >= _src.Length) return false;

                _src.Position = byteOff;
                int want = BlockFrames * _inCh * _inBytes;
                if (_buf.Length < want) _buf = new byte[want];
                int got = _src.Read(_buf, 0, want);
                if (got <= 0) return false;
                _bufStartFrame = frame;
                _bufLen = got;
                return true;
            }

            int _bufLen;

            /// <summary>取第 frame 帧、第 ch 声道（0=左，1=右；多声道时奇偶折叠）。</summary>
            double Sample(long frame, int ch)
            {
                int frameBytes = _inCh * _inBytes;
                long rel = frame - _bufStartFrame;
                int off = (int)(rel * frameBytes);
                if (off < 0 || off + frameBytes > _bufLen) return 0;

                double sum = 0;
                int cnt = 0;
                for (int c = ch; c < _inCh; c += 2)
                {
                    int p = off + c * _inBytes;
                    double v;
                    if (_inFloat) v = BitConverter.ToSingle(_buf, p);
                    else if (_inBytes == 2) v = BitConverter.ToInt16(_buf, p) / 32768.0;
                    else if (_inBytes == 3) v = ((_buf[p] | (_buf[p + 1] << 8) | (sbyte)_buf[p + 2] << 16)) / 8388608.0;
                    else v = _buf[p] / 128.0;
                    sum += v;
                    cnt++;
                }
                if (cnt == 0) return 0;
                double ave = sum / cnt;
                return Math.Clamp(ave, -1.0, 1.0) * 32767.0;
            }
        }
    }
}
