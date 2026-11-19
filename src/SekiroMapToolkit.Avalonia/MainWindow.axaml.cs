using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SekiroMapToolkit.Core;
using System.Numerics;
using SoulsFormats;

namespace SekiroMapToolkit.UI
{
    public partial class MainWindow : Window
    {
        class MapEntry
        {
            public string Name;
            public CheckBox Box;
        }

        class ToolRow
        {
            public ToolDef Def;
            public Button Install;
            public Button Launch;
            public TextBlock State;
        }

        class AudioBankRow
        {
            public AudioBank Bank;
            public CheckBox Box;
        }

        class AudioEventRow
        {
            public EventEntry Event;
            public CheckBox Box;
        }

        class AnimRow
        {
            public AnimEntry Entry;
            public CheckBox Box;
        }

        Config _cfg = Config.Load();
        readonly List<MapEntry> _maps = new List<MapEntry>();
        readonly List<string> _logLines = new List<string>();
        readonly List<string> _previewMaps = new List<string>();
        readonly List<ToolRow> _toolRows = new List<ToolRow>();
        CancellationTokenSource _cts;
        bool _busy;
        bool _installing;

        readonly List<AudioBankRow> _audioRows = new List<AudioBankRow>();
        readonly List<AudioEventRow> _audioEventRows = new List<AudioEventRow>();
        CancellationTokenSource _audioCts;
        bool _audioBusy;
        bool _audioEventMode = true;          // true = 按事件，false = 按音库
        EventIndex _eventIndex;

        readonly List<AnimRow> _animRows = new List<AnimRow>();
        AnimCharacter _animCurrent;
        string _animChrSelected;
        bool _animBusy;
        bool _animLoading;

        static readonly string[] Titles =
        {
            "主页", "地图导出", "解包与校验", "贴图工具", "场景预览", "音频导出", "角色动画", "模型贴图", "内置工具", "使用说明"
        };

        static readonly string[] Subs =
        {
            "把只狼的 MSB 摆放与 FLVER 模型，原样搬进 Blender",
            "勾选地图 → 解析 MSB / mapbnd / objbnd / mtd → 写出 meshes.bin + scene.json + tex",
            "内置解包器一键下载，8 项自检确认资源齐全",
            "DDS 批量转不透明 PNG（丢 alpha，透明区做颜色扩散）",
            "看导出统计，或用 Blender 渲一张缩略图（可设为背景）",
            "按事件浏览音库，一条一条听、一条一条导，不用整包拆",
            "读 anibnd 的动画，导出 hkx / FBX，动画曲线 + 骨骼 + 模型蒙皮打包带走",
            "看角色的模型网格与材质贴图，单独导出模型 FBX 或贴图",
            "解包 / 地图 / 模型 / 音频工具全部从 GitHub 直连下载，无需另装",
            "坐标约定、命令行参数与已知限制"
        };

        // 内置壁纸池：20 张网络手绘插画（绘画质感，非游戏截图、非 AI 生成），已统一压暗
        static readonly string[] Wallpapers =
        {
            "bg_01.jpg", "bg_02.jpg", "bg_03.jpg", "bg_04.jpg",
            "bg_05.jpg", "bg_06.jpg", "bg_07.jpg", "bg_08.jpg",
            "bg_09.jpg", "bg_10.jpg", "bg_11.jpg", "bg_12.jpg",
            "bg_13.jpg", "bg_14.jpg", "bg_15.jpg", "bg_16.jpg",
            "bg_17.jpg", "bg_18.jpg", "bg_19.jpg", "bg_20.jpg"
        };

        static readonly Random Rng = new Random();

        // 壁纸自动轮换（4 分钟一张），纯本机计时器，不耗资源
        readonly DispatcherTimer _bgTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };

        public MainWindow()
        {
            InitializeComponent();

            try { WireGraphEditor(); } catch { }
            try { WireViewport(); } catch { }

            try
            {
                using var s = AssetLoader.Open(new Uri("avares://SekiroMapToolkit/Assets/app.png"));
                Icon = new WindowIcon(s);
            }
            catch { }

            RootBox.Text = _cfg.GameRoot ?? "";
            OutBox.Text = _cfg.OutputRoot ?? "";
            BlenderBox.Text = _cfg.BlenderPath ?? "";
            SkipFogBox.IsChecked = _cfg.SkipFog;
            BuildBlendBox.IsChecked = _cfg.BuildBlend;
            OpenDoneBox.IsChecked = _cfg.OpenWhenDone;
            DeleteDdsBox.IsChecked = false;
            TokenBox.Text = _cfg.GitHubToken ?? "";
            UpdateTokenState();

            AudioSrcBox.Text = _cfg.AudioSourceRoot ?? "";
            AudioOutBox.Text = _cfg.AudioOutputRoot ?? "";
            AudioBitrateBox.Value = _cfg.AudioBitrate > 0 ? _cfg.AudioBitrate : 192;

            AnimOutBox.Text = _cfg.AnimOutputRoot ?? "";

            RotateBackdropOnStart();
            ShowPage(0);

            _bgTimer.Tick += (_, __) => AutoRotateBackdrop();
            if (_cfg.BackgroundAuto) _bgTimer.Start();
            BuildToolList();
            RefreshMapsSilent();
            UpdateHomeStats();

            Opened += OnOpened;
        }

        async void OnOpened(object sender, EventArgs e)
        {
            Log("就绪。解包目录：" + (string.IsNullOrEmpty(RootBox.Text) ? "未设置" : RootBox.Text));
            Log("内置工具已装 " + ToolCatalog.InstalledCount + " / " + ToolCatalog.All.Length +
                "，工具目录：" + ToolCatalog.Root);

            if (!string.IsNullOrEmpty(Program.ShotPath))
            {
                if (Program.ShotPage >= 0) ShowPage(Program.ShotPage);

                if (!string.IsNullOrEmpty(Program.ShotChr))
                {
                    try
                    {
                        LoadAnimCharacter(Program.ShotChr);
                        // 等这个角色彻底就绪（tae 解析 + 视口渲染）
                        for (int k = 0; k < 300; k++)
                        {
                            await Task.Delay(200);
                            bool ok = _animCurrent?.Id == Program.ShotChr
                                   && _taeLoadedChr == Program.ShotChr
                                   && _autoTaeTask != null && _autoTaeTask.IsCompleted;
                            if (ok) break;
                        }
                        // 验证实时播放：播 1.5 秒再截
                        if (Program.ShotPlay)
                        {
                            StartPlayback();
                            await Task.Delay(1500);
                            StopPlayback();
                            await Task.Delay(300);
                        }
                        if (Program.ShotTab >= 0)
                        {
                            if (Program.ShotTab == 1) TabEntityBtn?.RaiseEvent(
                                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                            else if (Program.ShotTab == 2) TabLogBtn?.RaiseEvent(
                                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                            else TabParamBtn?.RaiseEvent(
                                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                            await Task.Delay(400);
                        }
                        await Task.Delay(400);
                    }
                    catch (Exception ex) { Log("[截图] 选角色失败：" + ex.Message); }
                }
                // 等 tae 解析完再截图，否则图形编辑器是空的
                for (int k = 0; k < 40; k++)
                {
                    await Task.Delay(250);
                    if (_autoTaeTask != null && _autoTaeTask.IsCompleted) break;
                }
                await Task.Delay(600);
                try
                {
                    var w = Math.Max(1200, (int)ClientSize.Width);
                    var h = Math.Max(720, (int)ClientSize.Height);
                    var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96));
                    rtb.Render(Shell);
                    rtb.Save(Program.ShotPath);
                }
                catch (Exception ex) { File.WriteAllText(Program.ShotPath + ".err", ex.ToString()); }
                Close();
            }
        }

        // ---------------------------------------------------------- chrome

        void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            if (WindowState == WindowState.Normal) BeginMoveDrag(e);
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void AboutAvatar_Click(object sender, RoutedEventArgs e) => ShowPage(7);

        void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                Shell.CornerRadius = new CornerRadius(14);
            }
            else
            {
                WindowState = WindowState.Maximized;
                Shell.CornerRadius = new CornerRadius(0);
            }
            MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        void Shell_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) return;
            if (e.Source is Button) return;

            var p = e.GetPosition(Shell);
            var w = Shell.Bounds.Width;
            var h = Shell.Bounds.Height;
            const double grip = 6;

            bool left = p.X <= grip, right = p.X >= w - grip, top = p.Y <= grip, bottom = p.Y >= h - grip;
            if (!(left || right || top || bottom)) return;

            WindowEdge edge;
            if (left && top) edge = WindowEdge.NorthWest;
            else if (right && top) edge = WindowEdge.NorthEast;
            else if (left && bottom) edge = WindowEdge.SouthWest;
            else if (right && bottom) edge = WindowEdge.SouthEast;
            else if (left) edge = WindowEdge.West;
            else if (right) edge = WindowEdge.East;
            else if (top) edge = WindowEdge.North;
            else edge = WindowEdge.South;

            BeginResizeDrag(edge, e);
        }

        // ---------------------------------------------------------- nav

        void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int i))
                ShowPage(i);
            QuickMenuPanel.IsVisible = false;
        }

        void NavAbout_Click(object sender, RoutedEventArgs e) => ShowPage(7);

        void StartFromHome_Click(object sender, RoutedEventArgs e) => ShowPage(1);

        void QuickMenu_Click(object sender, RoutedEventArgs e) => QuickMenuPanel.IsVisible = !QuickMenuPanel.IsVisible;

        void ShowPage(int i)
        {
            PageHome.IsVisible = i == 0;
            PageExport.IsVisible = i == 1;
            PageUnpack.IsVisible = i == 2;
            PageTex.IsVisible = i == 3;
            PagePreview.IsVisible = i == 4;
            PageAudio.IsVisible = i == 5;
            PageAnim.IsVisible = i == 6;
            PageModel.IsVisible = i == 7;
            PageTools.IsVisible = i == 8;
            PageAbout.IsVisible = i == 9;

            PageTitle.Text = Titles[i];
            PageSub.Text = Subs[i];

            var navs = new[] { NavHome, NavExport, NavUnpack, NavTex, NavPreview, NavAudio, NavAnim, NavModel, NavTools, NavAbout };
            for (int k = 0; k < navs.Length; k++)
                navs[k].Classes.Set("active", k == i);

            ApplyBackground(i);

            if (i == 4) RefreshPreviewList();
            if (i == 6) RefreshAnimCharacters();
            if (i == 7) RefreshModelCharacters();
            if (i == 2) RefreshUnpackButtons();
            if (i == 5) RefreshAudioList();
            if (i == 8) RefreshToolStates();
        }

        // ---------------------------------------------------------- background

        void ApplyBackground(int page)
        {
            try
            {
                // 1) 用户自定义图优先
                if (!string.IsNullOrWhiteSpace(_cfg.BackgroundImage) && File.Exists(_cfg.BackgroundImage))
                {
                    PageBg.Source = LoadBitmap(_cfg.BackgroundImage);
                    PageDim.Opacity = _cfg.BackgroundDim;
                    return;
                }

                // 2) 关掉背景
                if (string.Equals(_cfg.BackgroundMode, "off", StringComparison.OrdinalIgnoreCase))
                {
                    PageBg.Source = null;
                    return;
                }

                // 3) 自己导出的地图渲染图
                if (string.Equals(_cfg.BackgroundMode, "render", StringComparison.OrdinalIgnoreCase))
                {
                    var p = FindRenderBackdrop();
                    if (p != null)
                    {
                        PageBg.Source = LoadBitmap(p);
                        PageDim.Opacity = _cfg.BackgroundDim;
                        return;
                    }
                }

                // 4) 默认：内置插画池，整轮不重复轮换
                var idx = _cfg.BackgroundIndex;
                if (idx < 0 || idx >= Wallpapers.Length) idx = Rng.Next(Wallpapers.Length);
                PageBg.Source = LoadBitmapFromResource("avares://SekiroMapToolkit/Resources/bg/" + Wallpapers[idx]);
                PageDim.Opacity = _cfg.BackgroundDim;
            }
            catch (Exception ex) { Log("[背景] " + ex.Message); }
        }

        void NextBackdrop_Click(object sender, RoutedEventArgs e)
        {
            _cfg.BackgroundImage = "";      // 从自定义图切回内置池
            _cfg.BackgroundMode = "art";
            _cfg.BackgroundIndex = (_cfg.BackgroundIndex + 1) % Wallpapers.Length;
            if (_cfg.BackgroundIndex < 0) _cfg.BackgroundIndex = 0;
            _cfg.Save();
            ApplyBackground(CurrentPage());
            Log("[背景] " + Wallpapers[_cfg.BackgroundIndex] + "（" + (_cfg.BackgroundIndex + 1) + "/" + Wallpapers.Length + "）");
        }

        // 启动时轮换一张没用过的
        void RotateBackdropOnStart()
        {
            if (!_cfg.BackgroundRotate) return;
            if (!string.IsNullOrWhiteSpace(_cfg.BackgroundImage)) return;
            if (!string.Equals(_cfg.BackgroundMode, "art", StringComparison.OrdinalIgnoreCase)) return;
            if (Wallpapers.Length <= 1) return;

            int next;
            if (_cfg.BackgroundIndex < 0) next = Rng.Next(Wallpapers.Length);
            else
            {
                do { next = Rng.Next(Wallpapers.Length); } while (next == _cfg.BackgroundIndex);
            }
            _cfg.BackgroundIndex = next;
            _cfg.Save();
        }

        // 挂机自动换图（用户自定义图 / 渲染图 / 关闭背景时不打扰）
        void AutoRotateBackdrop()
        {
            if (!string.IsNullOrWhiteSpace(_cfg.BackgroundImage)) return;
            if (!string.Equals(_cfg.BackgroundMode, "art", StringComparison.OrdinalIgnoreCase)) return;
            if (Wallpapers.Length <= 1) return;

            int next;
            do { next = Rng.Next(Wallpapers.Length); } while (next == _cfg.BackgroundIndex);
            _cfg.BackgroundIndex = next;
            _cfg.Save();
            ApplyBackground(CurrentPage());
            Log("[壁纸] 自动轮换 → " + Wallpapers[next] + "（" + (next + 1) + "/" + Wallpapers.Length + "）");
        }

        string FindRenderBackdrop()
        {
            try
            {
                var outRoot = OutBox.Text?.Trim();
                if (string.IsNullOrEmpty(outRoot) || !Directory.Exists(outRoot)) return null;
                foreach (var d in Directory.GetDirectories(outRoot).OrderByDescending(x => x))
                {
                    foreach (var name in new[] { "preview.png", "backdrop.png" })
                    {
                        var p = Path.Combine(d, name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        async void PickBackground_Click(object sender, RoutedEventArgs e)
        {
            var f = await PickFile("选择背景图", "图片", "*.png");
            f ??= await PickFile("选择背景图", "JPG 图片", "*.jpg");
            if (string.IsNullOrEmpty(f)) return;
            _cfg.BackgroundImage = f;
            _cfg.Save();
            ApplyBackground(CurrentPage());
            Log("[背景] 已换成 " + f);
        }

        void UseRenderAsBackdrop_Click(object sender, RoutedEventArgs e)
        {
            var map = PreviewCombo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(map)) { Log("[提示] 先选一张地图并渲染缩略图"); return; }
            var p = Path.Combine(OutBox.Text?.Trim() ?? "", map, "preview.png");
            if (!File.Exists(p)) { Log("[提示] 还没有渲染图：" + p); return; }
            _cfg.BackgroundImage = p;
            _cfg.Save();
            ApplyBackground(CurrentPage());
            Log("[背景] 已改用 " + map + " 的渲染图");
        }

        int CurrentPage()
        {
            if (PageExport.IsVisible) return 1;
            if (PageUnpack.IsVisible) return 2;
            if (PageTex.IsVisible) return 3;
            if (PagePreview.IsVisible) return 4;
            if (PageAudio.IsVisible) return 5;
            if (PageAnim.IsVisible) return 6;
            if (PageModel.IsVisible) return 7;
            if (PageTools.IsVisible) return 8;
            if (PageAbout.IsVisible) return 9;
            return 0;
        }

        // ---------------------------------------------------------- logging

        void Log(string s)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => Log(s));
                return;
            }
            _logLines.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
            if (_logLines.Count > 2500) _logLines.RemoveRange(0, _logLines.Count - 2500);
            LogText.Text = string.Join("\n", _logLines);
            LogScroll.ScrollToEnd();
            LogCount.Text = _logLines.Count + " 行";
        }

        void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            LogText.Text = "";
            LogCount.Text = "";
        }

        async void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(string.Join("\n", _logLines));
            Log("已复制 " + _logLines.Count + " 行日志到剪贴板");
        }

        void OpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var outRoot = OutBox.Text?.Trim();
                if (string.IsNullOrEmpty(outRoot) || !Directory.Exists(outRoot)) { Log("[错误] 输出目录不存在"); return; }
                var p = Path.Combine(outRoot, "toolkit.log");
                File.WriteAllText(p, string.Join("\n", _logLines));
                OpenFolder(p);
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        // ---------------------------------------------------------- maps

        void RefreshMapsSilent()
        {
            try
            {
                var root = RootBox.Text?.Trim();
                var maps = string.IsNullOrEmpty(root) || !Directory.Exists(root)
                    ? new List<string>()
                    : SekiroExporter.ListMaps(root);

                _maps.Clear();
                MapList.Children.Clear();
                foreach (var m in maps)
                {
                    var box = new CheckBox { Content = m, FontSize = 13, IsChecked = true, Margin = new Thickness(2, 0, 0, 0) };
                    var row = new Border
                    {
                        Padding = new Thickness(8, 5),
                        CornerRadius = new CornerRadius(7),
                        Background = Brushes.Transparent,
                        Child = box
                    };
                    row.PointerEntered += (_, __) => row.Background = Brush("#101B28");
                    row.PointerExited += (_, __) => row.Background = Brushes.Transparent;
                    MapList.Children.Add(row);
                    _maps.Add(new MapEntry { Name = m, Box = box });
                }

                if (_cfg.LastMaps != null && _cfg.LastMaps.Count > 0)
                    foreach (var e in _maps)
                        e.Box.IsChecked = _cfg.LastMaps.Contains(e.Name);

                MapCountText.Text = maps.Count == 0 ? "未找到 msb" : maps.Count + " 张";
                if (maps.Count == 0 && !string.IsNullOrEmpty(root))
                    Log("[提示] " + Path.Combine(root, "map", "mapstudio") + " 下没有 .msb.dcx，确认解包目录");
            }
            catch (Exception ex) { Log("[错误] 读取地图列表失败：" + ex.Message); }
        }

        void RefreshMaps_Click(object sender, RoutedEventArgs e)
        {
            RefreshMapsSilent();
            UpdateHomeStats();
            Log("地图列表已刷新");
        }

        void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in _maps) m.Box.IsChecked = true;
        }

        void Invert_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in _maps) m.Box.IsChecked = !(m.Box.IsChecked == true);
        }

        void UpdateHomeStats()
        {
            try
            {
                HomeMapCount.Text = _maps.Count.ToString();

                int mbnd = 0, obnd = 0;
                var root = RootBox.Text?.Trim();
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    mbnd = Count(Path.Combine(root, "map"), "*.mapbnd.dcx", true);
                    obnd = Count(Path.Combine(root, "obj"), "*.objbnd.dcx");
                }
                HomeModelCount.Text = mbnd > 0 ? mbnd.ToString() : "—";
                HomeTexCount.Text = obnd > 0 ? obnd.ToString() : "—";
            }
            catch { }
        }

        // ---------------------------------------------------------- export

        async void RunExport_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var maps = _maps.Where(m => m.Box.IsChecked == true).Select(m => m.Name).ToList();
            if (maps.Count == 0) { Log("[错误] 先勾选至少一张地图"); return; }

            var root = RootBox.Text?.Trim();
            var outRoot = OutBox.Text?.Trim();
            var blender = BlenderBox.Text?.Trim();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Log("[错误] 解包目录不存在：" + root); return; }
            if (string.IsNullOrEmpty(outRoot)) outRoot = Path.Combine(root, "map", "临时文件夹");
            try { Directory.CreateDirectory(outRoot); } catch { }

            bool wantBlend = BuildBlendBox.IsChecked == true;
            if (wantBlend && (string.IsNullOrEmpty(blender) || !File.Exists(blender)))
            {
                Log("[错误] Blender 路径无效：" + blender);
                return;
            }

            int limit = (int)(LimitBox.Value ?? 0);
            bool skipFog = SkipFogBox.IsChecked == true;

            _cfg.GameRoot = root;
            _cfg.OutputRoot = outRoot;
            _cfg.BlenderPath = blender;
            _cfg.SkipFog = skipFog;
            _cfg.BuildBlend = wantBlend;
            _cfg.OpenWhenDone = OpenDoneBox.IsChecked == true;
            _cfg.LastMaps = maps;
            _cfg.Save();

            _busy = true;
            _cts = new CancellationTokenSource();
            RunBtn.IsEnabled = false;
            CancelBtn.IsEnabled = true;
            Bar.Maximum = maps.Count * (wantBlend ? 2 : 1);
            Bar.Value = 0;

            var sw = Stopwatch.StartNew();
            int fail = 0;
            Log("==== 开始导出 " + maps.Count + " 张地图（limit=" + limit + "，skipFog=" + skipFog + "）====");

            try
            {
                foreach (var map in maps)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    StatusText.Text = "导出 " + map + " …";
                    Log("-- " + map);

                    var stats = await Task.Run(() => SekiroExporter.ExportMap(root, outRoot, map, limit, Log, _cts.Token));
                    Bar.Value = Math.Min(Bar.Value + 1, Bar.Maximum);
                    if (stats == null) { fail++; continue; }

                    Log(string.Format("[完成] {0}：物件={1} 网格={2} 顶点={3:N0} 贴图={4} 缺贴图={5} 用时 {6}s",
                        map, stats.objectsPlaced, stats.models, stats.vertices, stats.textures,
                        stats.parts, stats.seconds));

                    if (wantBlend)
                    {
                        StatusText.Text = "构建 " + map + ".blend …";
                        var dataDir = Path.Combine(outRoot, map);
                        var blendPath = Path.Combine(dataDir, map + ".blend");
                        bool ok = await Task.Run(() => BlenderRunner.BuildBlend(blender, Config.ScriptsDir, dataDir, blendPath, Log, _cts.Token));
                        Bar.Value = Math.Min(Bar.Value + 1, Bar.Maximum);
                        if (ok) Log("[.blend] " + blendPath);
                        else { fail++; Log("[.blend 失败] " + map); }
                    }
                }
            }
            catch (OperationCanceledException) { Log("[已取消]"); }
            catch (Exception ex) { fail++; Log("[错误] " + ex.GetType().Name + " " + ex.Message); }
            finally
            {
                _busy = false;
                RunBtn.IsEnabled = true;
                CancelBtn.IsEnabled = false;
                StatusText.Text = "完成 · " + sw.Elapsed.TotalSeconds.ToString("F1") + " 秒 · 失败 " + fail;
                UpdateHomeStats();
            }

            if (OpenDoneBox.IsChecked == true) OpenFolder(outRoot);
        }

        void CancelExport_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); StatusText.Text = "正在取消…"; } catch { }
        }

        void ExportAll_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(1);
            foreach (var m in _maps) m.Box.IsChecked = true;
            BuildBlendBox.IsChecked = true;
            RunExport_Click(sender, e);
        }

        // ---------------------------------------------------------- unpack

        void RefreshUnpackButtons()
        {
            try
            {
                var nuxe = ToolCatalog.Find("Nuxe");
                var uxm = ToolCatalog.Find("UXM");
                BtnNuxe.Content = nuxe != null && nuxe.Installed ? "启动 Nuxe 1.3.0" : "下载并启动 Nuxe";
                BtnUxm.Content = uxm != null && uxm.Installed ? "启动 UXM" : "下载并启动 UXM";
                BtnNuxe.IsEnabled = true;
                BtnUxm.IsEnabled = true;
            }
            catch { }
        }

        async void LaunchNuxe_Click(object sender, RoutedEventArgs e) => await LaunchOrInstall("Nuxe");

        async void LaunchUxm_Click(object sender, RoutedEventArgs e) => await LaunchOrInstall("UXM");

        async Task LaunchOrInstall(string key)
        {
            var def = ToolCatalog.Find(key);
            if (def == null) return;
            var exe = def.ResolveExe();
            if (exe == null)
            {
                exe = await InstallTool(def);
                if (exe == null)
                {
                    if (def.AssetUrl == null)
                    {
                        Log("[提示] " + def.Name + " 没有现成包，打开仓库自己编一下：" + def.RepoUrl);
                        OpenUrl(def.RepoUrl);
                    }
                    return;
                }
            }
            LaunchPath(exe, def.Name);
        }

        void LaunchPath(string exe, string label)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Log("[启动] " + label + " → " + exe);
            }
            catch (Exception ex) { Log("[错误] 启动失败：" + ex.Message); }
        }

        void OpenGameRoot_Click(object sender, RoutedEventArgs e)
        {
            var p = Directory.GetParent(RootBox.Text?.Trim() ?? "");
            OpenFolder(p?.FullName ?? RootBox.Text);
        }

        void OpenUnpacked_Click(object sender, RoutedEventArgs e) => OpenFolder(RootBox.Text?.Trim());

        void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(OutBox.Text?.Trim());

        // ---------------------------------------------------------- validate

        void Validate_Click(object sender, RoutedEventArgs e)
        {
            var root = RootBox.Text?.Trim();
            CheckList.Children.Clear();

            int pass = 0, total = 0;
            void Add(string name, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                var dot = new Ellipse
                {
                    Width = 7, Height = 7,
                    Fill = Brush(ok ? "#35E0D0" : "#FF6B6B"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0)
                };
                var stack = new StackPanel { Spacing = 1 };
                stack.Children.Add(new TextBlock { Text = name, FontSize = 12.5 });
                stack.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = Brush("#5C7186") });
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(2, 2)
                };
                grid.Children.Add(dot);
                Grid.SetColumn(stack, 1);
                grid.Children.Add(stack);
                CheckList.Children.Add(grid);
            }

            var rootOk = !string.IsNullOrEmpty(root) && Directory.Exists(root);
            Add("解包目录", rootOk, root ?? "未设置");

            int msb = rootOk ? Count(Path.Combine(root, "map", "mapstudio"), "*.msb.dcx") : 0;
            Add("地图 MSB", msb > 0, msb + " 个 .msb.dcx");

            int mbnd = rootOk ? Count(Path.Combine(root, "map"), "*.mapbnd.dcx", true) : 0;
            Add("地图块 mapbnd", mbnd > 0, mbnd + " 个 .mapbnd.dcx");

            int obnd = rootOk ? Count(Path.Combine(root, "obj"), "*.objbnd.dcx") : 0;
            Add("物件 objbnd", obnd > 0, obnd + " 个 .objbnd.dcx");

            var mtdDir = Path.Combine(root ?? "", "mtd", "_allmaterialbnd_unpacked");
            bool mtdOk = Directory.Exists(mtdDir);
            Add("材质 MTD", mtdOk, mtdOk ? "mtd\\_allmaterialbnd_unpacked" : "缺少 mtd 目录（会退回默认材质）");

            var od = Path.Combine(Config.Dir, "oo2core_6_win64.dll");
            Add("Oodle 解压库", File.Exists(od), od);

            Add("Blender", File.Exists(BlenderBox.Text?.Trim() ?? ""), BlenderBox.Text ?? "未设置");
            Add("构建脚本 build_blend.py", File.Exists(Path.Combine(Config.ScriptsDir, "build_blend.py")),
                Path.Combine(Config.ScriptsDir, "build_blend.py"));
            Add("内置解包器", ToolCatalog.Find("Nuxe")?.Installed == true || ToolCatalog.Find("UXM")?.Installed == true,
                "Nuxe / UXM 任一已装即可（「内置工具」页下载）");

            CheckSummary.Text = pass + " / " + total + " 通过" + (pass == total ? "，可以开导" : "，先补齐红色项");
            Log("[校验] " + CheckSummary.Text);
        }

        static int Count(string dir, string pattern, bool recursive = false)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                return Directory.GetFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Length;
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------- textures

        async void BrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            var p = await PickFolder("选择解包目录（含 map / obj / mtd）");
            if (p != null) { RootBox.Text = p; RefreshMapsSilent(); UpdateHomeStats(); }
        }

        async void BrowseOut_Click(object sender, RoutedEventArgs e)
        {
            var p = await PickFolder("选择输出目录");
            if (p != null) { OutBox.Text = p; UpdateHomeStats(); }
        }

        async void BrowseBlender_Click(object sender, RoutedEventArgs e)
        {
            var f = await PickFile("选择 blender.exe", "可执行文件", "*.exe");
            if (f != null) BlenderBox.Text = f;
        }

        async void BrowseTex_Click(object sender, RoutedEventArgs e)
        {
            var p = await PickFolder("选择包含 .dds 的目录");
            if (p != null) TexDirBox.Text = p;
        }

        async void ConvertTex_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dir = TexDirBox.Text?.Trim();
            var blender = BlenderBox.Text?.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Log("[错误] 贴图目录不存在"); return; }
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender)) { Log("[错误] Blender 路径无效"); return; }

            int n = Directory.GetFiles(dir, "*.dds").Length;
            if (n == 0) { Log("[提示] 目录里没有 .dds"); return; }

            _busy = true;
            TexBar.IsVisible = true;
            Log("开始转换 " + n + " 张 DDS → PNG");
            try
            {
                bool ok = await Task.Run(() => BlenderRunner.ConvertDdsFolder(blender, Config.ScriptsDir, dir, Log, CancellationToken.None));
                Log(ok ? "[完成] 贴图转换结束" : "[失败] 贴图转换出错，看日志");
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
            finally { _busy = false; TexBar.IsVisible = false; }
        }

        // ---------------------------------------------------------- preview

        void RefreshPreviewList()
        {
            _previewMaps.Clear();
            var outRoot = OutBox.Text?.Trim();
            if (!string.IsNullOrEmpty(outRoot) && Directory.Exists(outRoot))
                foreach (var d in Directory.GetDirectories(outRoot).OrderBy(x => x))
                    if (File.Exists(Path.Combine(d, "scene.json")))
                        _previewMaps.Add(Path.GetFileName(d));

            PreviewCombo.ItemsSource = _previewMaps;
            if (PreviewCombo.SelectedIndex < 0 && _previewMaps.Count > 0) PreviewCombo.SelectedIndex = 0;
            PreviewHint.Text = _previewMaps.Count == 0 ? "输出目录里还没有 scene.json，先跑一次导出。" : "缩略图由 Blender 渲染，约 20–40 秒。";
        }

        void PreviewLoad_Click(object sender, RoutedEventArgs e)
        {
            var map = PreviewCombo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(map)) { Log("[提示] 先选一张地图"); return; }
            var dir = Path.Combine(OutBox.Text?.Trim() ?? "", map);
            try
            {
                var sc = SekiroExporter.LoadScene(dir);
                if (sc == null) { Log("[错误] 读不到 scene.json"); return; }
                var s = sc.stats;
                PreviewStats.Text = string.Format(
                    "地图：{0}\n网格：{1}\n材质：{2}\n物件：{3}\n顶点：{4:N0}\n贴图：{5}\n缺贴图：{6}\n缺模型：{7}\n导出耗时：{8} 秒",
                    sc.map, sc.meshCount, sc.materials?.Count ?? 0, s?.objectsPlaced ?? 0, s?.vertices ?? 0,
                    s?.textures ?? 0, sc.missingTextures?.Count ?? 0, sc.missingModels?.Count ?? 0, s?.seconds ?? 0);
                Log("[预览] " + map + " 已载入");
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        async void PreviewRender_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var map = PreviewCombo.SelectedItem?.ToString();
            var blender = BlenderBox.Text?.Trim();
            if (string.IsNullOrEmpty(map)) { Log("[提示] 先选一张地图"); return; }
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender)) { Log("[错误] Blender 路径无效"); return; }

            var dataDir = Path.Combine(OutBox.Text?.Trim() ?? "", map);
            var blend = Path.Combine(dataDir, map + ".blend");
            var png = Path.Combine(dataDir, "preview.png");

            _busy = true;
            PreviewHint.Text = "正在渲染…";
            try
            {
                if (!File.Exists(blend))
                {
                    Log("[构建] 先生成 " + blend);
                    bool built = await Task.Run(() => BlenderRunner.BuildBlend(blender, Config.ScriptsDir, dataDir, blend, Log, CancellationToken.None));
                    if (!built) { PreviewHint.Text = "构建 .blend 失败"; return; }
                }
                bool ok = await Task.Run(() => BlenderRunner.RenderPreview(blender, Config.ScriptsDir, blend, png, Log, CancellationToken.None));
                if (ok)
                {
                    PreviewImage.Source = LoadBitmap(png);
                    PreviewHint.Text = "预览：" + png;
                }
                else PreviewHint.Text = "渲染失败，看日志";
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
            finally { _busy = false; }
        }

        // ---------------------------------------------------------- audio

        /// <summary>清单分组用：环境/场景/混响/特殊按地图分，角色/语音/全局各自一大组。</summary>
        static string GroupKeyOf(AudioBank b)
        {
            switch (b.Category)
            {
                case AudioExporter.CatChar: return "角色音效   （c*）";
                case AudioExporter.CatVoice: return "语音   （vm*，按语言分）";
                case AudioExporter.CatGlobal: return "全局   （main / smain）";
                case AudioExporter.CatSpecial: return "特殊音效   （xm*）";
                default: return b.MapKey + "   ·   环境 / 场景 / 混响";
            }
        }

        static int GroupRank(AudioBank b)
        {
            switch (b.Category)
            {
                case AudioExporter.CatAmbience: return 0;
                case AudioExporter.CatScene: return 1;
                case AudioExporter.CatReverb: return 2;
                case AudioExporter.CatSpecial: return 3;
                case AudioExporter.CatChar: return 4;
                case AudioExporter.CatVoice: return 5;
                case AudioExporter.CatGlobal: return 6;
                default: return 7;
            }
        }

        void RefreshAudioList()
        {
            AudioList.Children.Clear();
            _audioRows.Clear();
            _audioEventRows.Clear();

            var src = AudioSrcBox.Text?.Trim();
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
            {
                AudioCountText.Text = "未找到 sound 目录";
                AudioHint.Text = "把「音库目录」指向解包出来的 文件\\sound（含 *.fsb / *.fev）。";
                return;
            }

            if (_audioEventMode) RefreshEventList(src);
            else RefreshBankList(src, last0: _cfg.LastAudioBanks ?? new List<string>());
        }

        /// <summary>「按事件」模式：从 .fev 里把事件列出来，一个事件 = 一条可单独导出的音频。</summary>
        void RefreshEventList(string src)
        {
            EventIndex idx;
            try
            {
                idx = EventLibrary.Build(src, s => Log(s));
            }
            catch (Exception ex)
            {
                Log("[事件] 扫描失败：" + ex.Message);
                return;
            }
            _eventIndex = idx;

            var q = AudioSearchBox?.Text?.Trim();
            IEnumerable<EventEntry> view = idx.Events;
            if (!string.IsNullOrEmpty(q))
                view = view.Where(e => e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       e.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       e.Bank.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       e.Category.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = view.ToList();
            int shown = 0;
            const int MaxRows = 1500;   // 一次最多渲染这么多行，避免卡死

            foreach (var g in list.GroupBy(e => e.Category + "  ·  " + e.Bank)
                                  .OrderBy(x => CatRank(x.First().Category))
                                  .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (shown >= MaxRows) break;
                AudioList.Children.Add(new TextBlock
                {
                    Text = g.Key + "   ·   " + g.Count() + " 个事件",
                    FontSize = 11,
                    Foreground = Brush("#38D7E8"),
                    Margin = new Thickness(8, 10, 0, 4)
                });

                foreach (var e in g.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (shown >= MaxRows) break;
                    shown++;
                    var box = new CheckBox
                    {
                        Content = e.Display,
                        FontSize = 12.5,
                        IsChecked = false,
                        IsEnabled = e.Resolvable,
                        Margin = new Thickness(2, 0, 0, 0)
                    };
                    var info = new TextBlock
                    {
                        Text = e.Detail,
                        FontSize = 10.5,
                        Foreground = Brush(e.Resolvable ? "#5C7186" : "#8A5A3A"),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var play = new Button
                    {
                        Content = "▶",
                        Classes = { "ghost", "small" },
                        FontSize = 10,
                        Width = 28,
                        Height = 22,
                        Padding = new Thickness(0),
                        IsEnabled = e.Resolvable,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    var ev = e;
                    play.Click += async (_, __) => await PreviewEvent(ev);

                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                    grid.Children.Add(play);
                    Grid.SetColumn(box, 1);
                    grid.Children.Add(box);
                    Grid.SetColumn(info, 2);
                    grid.Children.Add(info);

                    var row = new Border
                    {
                        Padding = new Thickness(8, 4),
                        CornerRadius = new CornerRadius(7),
                        Background = Brushes.Transparent,
                        Child = grid
                    };
                    row.PointerEntered += (_, __) => row.Background = Brush("#101B28");
                    row.PointerExited += (_, __) => row.Background = Brushes.Transparent;
                    AudioList.Children.Add(row);
                    _audioEventRows.Add(new AudioEventRow { Event = e, Box = box });
                }
            }

            if (list.Count > shown)
                AudioList.Children.Add(new TextBlock
                {
                    Text = "…… 还有 " + (list.Count - shown) + " 个事件未显示，用上面的搜索框缩小范围",
                    FontSize = 11,
                    Foreground = Brush("#5C7186"),
                    Margin = new Thickness(10, 12, 0, 6)
                });

            AudioCountText.Text = idx.Events.Count + " 个事件 · " + idx.BankCount + " 个音库";
            AudioHint.Text = string.Format(
                "事件来自 sound\\*.fev（{0} 个音库带事件表，{1} 个只有 fsb）。勾一个事件就只导它那一条音频。" +
                "{2}", idx.BanksWithFev, idx.BanksOnlyFsb,
                idx.Unreadable > 0 ? "  另有 " + idx.Unreadable + " 个音库头部读不出，只能整库匹配。" : "");
        }

        /// <summary>试听一个事件；同一条再点就是停。</summary>
        async Task PreviewEvent(EventEntry e)
        {
            var src = AudioSrcBox.Text?.Trim();
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src)) { Log("[试听] 音库目录不存在"); return; }

            var fsb = Path.Combine(src, e.Bank + ".fsb");
            if (!File.Exists(fsb)) { Log("[试听] 找不到 " + e.Bank + ".fsb"); return; }

            var vgm = EnsureVgmstream();
            if (vgm == null) return;

            AudioStatus.Text = "试听：" + e.Name + " …";
            var ok = await AudioPreview.PlayAsync(vgm, fsb, e, Log);
            if (ok)
            {
                AudioStatus.Text = "正在试听：" + e.Bank + " / " + e.Name + "（再点一次停止）";
                Log("[试听] " + e.Bank + " / " + e.Name);
            }
            else
            {
                AudioStatus.Text = "已停止试听";
            }
        }

        static int CatRank(string cat)
        {
            switch (cat)
            {
                case AudioExporter.CatAmbience: return 0;
                case AudioExporter.CatScene: return 1;
                case AudioExporter.CatReverb: return 2;
                case AudioExporter.CatSpecial: return 3;
                case AudioExporter.CatVoice: return 4;
                case AudioExporter.CatChar: return 5;
                case AudioExporter.CatGlobal: return 6;
                default: return 7;
            }
        }

        /// <summary>「按音库」模式：整库列出来（老行为，适合一次全导）。</summary>
        void RefreshBankList(string src, List<string> last0)
        {
            List<AudioBank> banks;
            try { banks = AudioExporter.Scan(src); }
            catch (Exception ex) { Log("[音频] 扫描失败：" + ex.Message); return; }

            var last = last0 ?? new List<string>();
            long bytes = 0;
            foreach (var g in banks.GroupBy(GroupKeyOf)
                                    .OrderBy(x => GroupRank(x.First()))
                                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                AudioList.Children.Add(new TextBlock
                {
                    Text = g.Key + "   ·   " + g.Count() + " 个音库",
                    FontSize = 11,
                    Foreground = Brush("#38D7E8"),
                    Margin = new Thickness(8, 10, 0, 4)
                });

                foreach (var b in g.OrderBy(x => x.Category).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    bytes += b.Bytes;
                    var box = new CheckBox
                    {
                        Content = b.Name + "    [" + b.Category + (string.IsNullOrEmpty(b.Language) ? "" : " · " + b.Language) + "]",
                        FontSize = 12.5,
                        IsChecked = last.Count == 0 || last.Contains(b.Name),
                        Margin = new Thickness(2, 0, 0, 0)
                    };
                    var size = new TextBlock
                    {
                        Text = (b.Bytes / 1048576.0).ToString("F1") + " MB",
                        FontSize = 10.5,
                        Foreground = Brush("#5C7186"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    grid.Children.Add(box);
                    Grid.SetColumn(size, 1);
                    grid.Children.Add(size);

                    var row = new Border
                    {
                        Padding = new Thickness(8, 4),
                        CornerRadius = new CornerRadius(7),
                        Background = Brushes.Transparent,
                        Child = grid
                    };
                    row.PointerEntered += (_, __) => row.Background = Brush("#101B28");
                    row.PointerExited += (_, __) => row.Background = Brushes.Transparent;
                    AudioList.Children.Add(row);
                    _audioRows.Add(new AudioBankRow { Bank = b, Box = box });
                }
            }

            AudioCountText.Text = banks.Count + " 个音库 · " + (bytes / 1048576.0).ToString("F1") + " MB";
            AudioHint.Text = "按地图归组，括号里是类别；勾选后导出 MP3。类别：环境 / 场景 / 混响 / 特殊 / 语音 / 角色 / 全局。";
        }

        void AudioRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAudioList();
            Log(_audioEventMode
                ? "[音频] 事件列表已刷新：" + _audioEventRows.Count + " 个"
                : "[音频] 音库列表已刷新：" + _audioRows.Count + " 个");
        }

        void AudioModeEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_audioEventMode) return;
            _audioEventMode = true;
            AudioModeEventBtn.Classes.Set("active", true);
            AudioModeBankBtn.Classes.Set("active", false);
            RefreshAudioList();
        }

        void AudioModeBank_Click(object sender, RoutedEventArgs e)
        {
            if (!_audioEventMode) return;
            _audioEventMode = false;
            AudioModeEventBtn.Classes.Set("active", false);
            AudioModeBankBtn.Classes.Set("active", true);
            RefreshAudioList();
        }

        void AudioSearch_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_audioEventMode) return;
            if (_eventIndex == null) return;
            RefreshEventList(AudioSrcBox.Text?.Trim() ?? "");
        }

        void AudioSearchClear_Click(object sender, RoutedEventArgs e)
        {
            AudioSearchBox.Text = "";
        }

        void AudioSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_audioEventMode)
            {
                foreach (var r in _audioEventRows) if (r.Event.Resolvable) r.Box.IsChecked = true;
                return;
            }
            foreach (var r in _audioRows) r.Box.IsChecked = true;
        }

        void AudioInvert_Click(object sender, RoutedEventArgs e)
        {
            if (_audioEventMode)
            {
                foreach (var r in _audioEventRows)
                    if (r.Event.Resolvable) r.Box.IsChecked = !(r.Box.IsChecked == true);
                return;
            }
            foreach (var r in _audioRows) r.Box.IsChecked = !(r.Box.IsChecked == true);
        }

        async void AudioProbe_Click(object sender, RoutedEventArgs e)
        {
            if (_audioBusy) return;
            var vgm = EnsureVgmstream();
            if (vgm == null) return;

            _audioBusy = true;
            AudioStatus.Text = "正在探测子音轨…";
            try
            {
                var rows = _audioRows.ToList();
                int total = 0;
                await Task.Run(() =>
                {
                    foreach (var r in rows)
                    {
                        AudioExporter.Probe(r.Bank, vgm);
                        total += Math.Max(0, r.Bank.Subsongs);
                    }
                });
                foreach (var r in rows)
                {
                    var tag = "   (" + Math.Max(0, r.Bank.Subsongs) + " 轨)";
                    if (r.Box.Content is string s && !s.EndsWith(")")) r.Box.Content = s + tag;
                }
                AudioStatus.Text = "探测完成 · 合计 " + total + " 个子音轨";
                Log("[音频] 探测完成：" + rows.Count + " 个音库，共 " + total + " 个子音轨");
            }
            catch (Exception ex) { Log("[音频] 探测失败：" + ex.Message); }
            finally { _audioBusy = false; }
        }

        string EnsureVgmstream()
        {
            var def = ToolCatalog.Find("vgmstream");
            var exe = def?.ResolveExe();
            if (exe != null) return exe;

            Log("[音频] 缺 vgmstream 解码器，正在从 GitHub 下载…");
            try
            {
                exe = ToolCatalog.InstallAsync(def, _cfg.GitHubToken, Log, CancellationToken.None)
                                  .GetAwaiter().GetResult();
            }
            catch (Exception ex) { Log("[音频] 下载失败：" + ex.Message); }
            RefreshToolStates();
            if (exe == null) Log("[音频] vgmstream 没装成，去「内置工具」页手动下载");
            return exe;
        }

        async void BrowseAudioSrc_Click(object sender, RoutedEventArgs e)
        {
            var p = await PickFolder("选择音库目录（含 *.fsb）");
            if (p != null) { AudioSrcBox.Text = p; RefreshAudioList(); }
        }

        async void BrowseAudioOut_Click(object sender, RoutedEventArgs e)
        {
            var p = await PickFolder("选择音频输出目录");
            if (p != null) AudioOutBox.Text = p;
        }

        async void AudioRun_Click(object sender, RoutedEventArgs e)
        {
            if (_audioBusy) return;

            var src = AudioSrcBox.Text?.Trim();
            var outRoot = AudioOutBox.Text?.Trim();
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src)) { Log("[音频] 音库目录不存在：" + src); return; }
            if (string.IsNullOrEmpty(outRoot)) { Log("[音频] 先设置输出目录"); return; }

            int bitrate = (int)(AudioBitrateBox.Value ?? 192);
            bool keepWav = AudioKeepWavBox.IsChecked == true;

            if (_audioEventMode) { await RunEventExport(src, outRoot, bitrate, keepWav); return; }

            var picked = _audioRows.Where(r => r.Box.IsChecked == true).Select(r => r.Bank).ToList();
            if (picked.Count == 0) { Log("[音频] 先勾选至少一个音库"); return; }

            bool skipSilent = AudioSkipTinyBox.IsChecked == true;

            var vgm = EnsureVgmstream();
            if (vgm == null) return;

            _cfg.AudioSourceRoot = src;
            _cfg.AudioOutputRoot = outRoot;
            _cfg.AudioBitrate = bitrate;
            _cfg.LastAudioBanks = picked.Select(b => b.Name).ToList();
            _cfg.Save();

            _audioBusy = true;
            _audioCts = new CancellationTokenSource();
            AudioRunBtn.IsEnabled = false;
            AudioCancelBtn.IsEnabled = true;
            AudioBar.Maximum = picked.Count;
            AudioBar.Value = 0;

            var sw = Stopwatch.StartNew();
            Log("==== 开始导出音频：" + picked.Count + " 个音库 → " + outRoot + "（" + bitrate + " kbps）====");

            try
            {
                var opt = new AudioExporter.Options
                {
                    SoundRoot = src,
                    OutRoot = outRoot,
                    Vgmstream = vgm,
                    Bitrate = bitrate,
                    KeepWav = keepWav,
                    SkipSilent = skipSilent,
                };

                var res = await Task.Run(() => AudioExporter.ExportBanksAsync(
                    opt, picked, Log, (i, n, label) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            AudioBar.Value = Math.Min(i, AudioBar.Maximum);
                            AudioStatus.Text = label;
                        });
                    }, _audioCts.Token));

                AudioStatus.Text = string.Format("完成 · {0} 个音库 · {1} 个 MP3 · {2:F1} MB · {3:F1} 秒 · 失败 {4}",
                    res.Banks, res.Files, res.Bytes / 1048576.0, sw.Elapsed.TotalSeconds, res.Failed);
                Log("[音频] " + AudioStatus.Text);
            }
            catch (OperationCanceledException) { AudioStatus.Text = "已取消"; Log("[音频] 已取消"); }
            catch (Exception ex) { AudioStatus.Text = "出错"; Log("[音频] " + ex.GetType().Name + " " + ex.Message); }
            finally
            {
                _audioBusy = false;
                AudioRunBtn.IsEnabled = true;
                AudioCancelBtn.IsEnabled = false;
                AudioBar.Value = 0;
            }

            if (OpenDoneBox.IsChecked == true) OpenFolder(outRoot);
        }

        void AudioCancel_Click(object sender, RoutedEventArgs e)
        {
            try { _audioCts?.Cancel(); AudioStatus.Text = "正在取消…"; } catch { }
        }

        /// <summary>按事件导出：只解勾选的那几条音轨。</summary>
        async Task RunEventExport(string src, string outRoot, int bitrate, bool keepWav)        {
            var picked = _audioEventRows.Where(r => r.Box.IsChecked == true && r.Event.Resolvable)
                                        .Select(r => r.Event).ToList();
            if (picked.Count == 0) { Log("[事件] 先勾选至少一个事件"); return; }

            var vgm = EnsureVgmstream();
            if (vgm == null) return;

            _cfg.AudioSourceRoot = src;
            _cfg.AudioOutputRoot = outRoot;
            _cfg.AudioBitrate = bitrate;
            _cfg.Save();

            _audioBusy = true;
            _audioCts = new CancellationTokenSource();
            AudioRunBtn.IsEnabled = false;
            AudioCancelBtn.IsEnabled = true;
            AudioBar.Maximum = Math.Max(1, picked.Select(e => e.Bank).Distinct().Count());
            AudioBar.Value = 0;

            var sw = Stopwatch.StartNew();
            Log("==== 按事件导出：" + picked.Count + " 个事件 → " + outRoot + "（" + bitrate + " kbps）====");

            try
            {
                var opt = new EventExporter.Options
                {
                    SoundRoot = src,
                    OutRoot = outRoot,
                    Vgmstream = vgm,
                    Bitrate = bitrate,
                    KeepWav = keepWav,
                };

                var res = await Task.Run(() => EventExporter.ExportAsync(
                    opt, picked, Log, (i, n, label) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            AudioBar.Value = Math.Min(i, AudioBar.Maximum);
                            AudioStatus.Text = label;
                        });
                    }, _audioCts.Token));

                AudioStatus.Text = string.Format("完成 · {0} 个事件 · {1} 个 MP3 · {2:F1} MB · {3:F1} 秒 · 失败 {4}",
                    res.Events, res.Files, res.Bytes / 1048576.0, sw.Elapsed.TotalSeconds, res.Failed);
                Log("[事件] " + AudioStatus.Text);
            }
            catch (OperationCanceledException) { AudioStatus.Text = "已取消"; Log("[事件] 已取消"); }
            catch (Exception ex) { AudioStatus.Text = "出错"; Log("[事件] " + ex.GetType().Name + " " + ex.Message); }
            finally
            {
                _audioBusy = false;
                AudioRunBtn.IsEnabled = true;
                AudioCancelBtn.IsEnabled = false;
                AudioBar.Value = 0;
            }

            if (OpenDoneBox.IsChecked == true) OpenFolder(outRoot);
        }

        // ---------------------------------------------------------- character animation

        // ========================================================== 模型贴图页

        string _modelChr;
        List<ChrMesh> _modelMeshes;
        bool _modelBusy;
        bool _modelLoading;

        void RefreshModelCharacters()
        {
            ModelChrList.Children.Clear();
            var root = _cfg.GameRoot;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                ModelChrCount.Text = "未设置游戏目录";
                return;
            }

            List<string> chars;
            try { chars = AnimationLibrary.ListCharacters(root); }
            catch (Exception ex) { Log("[模型] 扫描失败：" + ex.Message); return; }

            ModelChrCount.Text = chars.Count + " 个";
            foreach (var c in chars)
            {
                var btn = new Button
                {
                    Content = c,
                    Classes = { "ghost", "small" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 0, 2, 0),
                };
                if (c == _modelChr) btn.Classes.Set("active", true);
                btn.Click += (_, __) => LoadModelCharacter(c);
                ModelChrList.Children.Add(btn);
            }

            if (_modelMeshes == null && chars.Count > 0 && !_modelLoading)
            {
                // 挑一个真有模型的角色：c1010（一心）这类主角色有完整网格，
                // c0000（狼）的 chrbnd 里 flver 是空的，别一进来就是空列表
                var withMesh = chars.FirstOrDefault(x => x == "c1010")
                            ?? chars.FirstOrDefault(x => x != "c0000")
                            ?? chars[0];
                LoadModelCharacter(withMesh);
            }
        }

        void LoadModelCharacter(string chrId)
        {
            var root = _cfg.GameRoot;
            if (string.IsNullOrEmpty(root)) return;

            _modelLoading = true;
            try
            {
                _modelChr = chrId;
                RefreshModelCharacters();

                ModelStatus.Text = "读取 " + chrId + " 模型…";
                ModelList.Children.Clear();

                List<ChrMesh> meshes;
                try { meshes = CharacterAssets.LoadMeshes(root, chrId, s => Log(s)); }
                catch (Exception ex)
                {
                    Log("[模型] " + chrId + " 读取失败：" + ex.Message);
                    ModelStatus.Text = "失败";
                    ModelSummary.Text = "模型读取失败：" + ex.Message;
                    return;
                }

                _modelMeshes = meshes;
                BuildModelList();

                int verts = meshes.Sum(m => m.Positions.Count);
                int tris = meshes.Sum(m => m.Indices.Count) / 3;
                int texCount = meshes.SelectMany(m => m.Textures).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                ModelSummary.Text = string.Format(
                    "{0}\n· 网格 {1} 个\n· 顶点 {2:N0}\n· 三角面 {3:N0}\n· 材质贴图 {4} 张",
                    chrId, meshes.Count, verts, tris, texCount);
                ModelInfo.Text = string.Format("{0} 网格 · {1:N0} 顶点 · {2} 贴图", meshes.Count, verts, texCount);
                ModelStatus.Text = meshes.Count > 0 ? "就绪" : "这个角色没有模型数据";
            }
            finally
            {
                _modelLoading = false;
            }
        }

        void BuildModelList()
        {
            ModelList.Children.Clear();
            if (_modelMeshes == null) return;

            foreach (var m in _modelMeshes.Take(600))
            {
                var row = new StackPanel { Spacing = 1, Margin = new Thickness(4, 3, 4, 3) };
                row.Children.Add(new TextBlock
                {
                    Text = m.Name,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                });

                var texs = m.Textures.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var detail = string.Format("{0:N0} 顶点 · {1:N0} 面 · {2} 贴图",
                    m.Positions.Count, m.Indices.Count / 3, texs.Count);
                row.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Classes = { "hint" } });
                if (texs.Count > 0)
                    row.Children.Add(new TextBlock
                    {
                        Text = string.Join("  ", texs.Take(4)),
                        FontSize = 10,
                        Classes = { "hint" },
                        TextWrapping = TextWrapping.Wrap,
                    });

                ModelList.Children.Add(new Border
                {
                    Classes = { "row" },
                    Child = row,
                    CornerRadius = new CornerRadius(5),
                });
            }

            if (_modelMeshes.Count > 600)
                ModelList.Children.Add(new TextBlock
                {
                    Text = "… 还有 " + (_modelMeshes.Count - 600) + " 个网格未列出",
                    Classes = { "hint" },
                    Margin = new Thickness(8, 6, 8, 6),
                });
        }

        async void ModelRefresh_Click(object sender, RoutedEventArgs e)
        {
            _modelMeshes = null;
            _modelChr = null;
            RefreshModelCharacters();
        }

        async void ModelExportFbx_Click(object sender, RoutedEventArgs e)
        {
            if (_modelBusy) return;
            if (string.IsNullOrEmpty(_modelChr)) { Log("[模型] 先选一个角色"); return; }

            var outRoot = AnimOutBox.Text?.Trim();
            if (string.IsNullOrEmpty(outRoot)) { Log("[模型] 先到动画页设置输出目录"); return; }

            _modelBusy = true;
            ModelBar.Maximum = 1;
            ModelBar.Value = 0;
            ModelStatus.Text = "正在导出模型 FBX…";
            Log("==== 导出模型 " + _modelChr + " → " + outRoot + " ====");

            var sw = Stopwatch.StartNew();
            try
            {
                var chrId = _modelChr;
                var res = await Task.Run(() =>
                    CharacterExport.ExportModel(_cfg.GameRoot, chrId, Path.Combine(outRoot, "动画"), true,
                        s => Log(s)));

                ModelBar.Value = 1;
                ModelStatus.Text = res.Summary();
                Log("[模型] " + res.Summary());
                if (res.Errors.Count > 0) Log("[模型] 错误：" + res.Errors[0]);
                if (OpenDoneBox.IsChecked == true) OpenFolder(res.OutputDir);
            }
            catch (Exception ex)
            {
                ModelStatus.Text = "出错";
                Log("[模型] " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                _modelBusy = false;
                ModelBar.Value = 0;
            }
        }

        async void ModelExportTex_Click(object sender, RoutedEventArgs e)
        {
            if (_modelBusy) return;
            if (string.IsNullOrEmpty(_modelChr)) { Log("[模型] 先选一个角色"); return; }

            var outRoot = AnimOutBox.Text?.Trim();
            if (string.IsNullOrEmpty(outRoot)) { Log("[模型] 先到动画页设置输出目录"); return; }

            _modelBusy = true;
            ModelStatus.Text = "正在导出贴图…";
            try
            {
                var chrId = _modelChr;
                var n = await Task.Run(() =>
                    CharacterAssets.ExportTextures(_cfg.GameRoot, chrId, Path.Combine(outRoot, "动画"),
                        s => Log(s)));

                ModelStatus.Text = "贴图 " + n + " 张";
                Log("[模型] " + chrId + " 贴图 " + n + " 张");
            }
            catch (Exception ex)
            {
                ModelStatus.Text = "出错";
                Log("[模型] " + ex.Message);
            }
            finally
            {
                _modelBusy = false;
            }
        }

        /// <summary>图形编辑器的按钮和滑块（XAML 里不写事件，这里挂）。</summary>
        void WireGraphEditor()
        {
            if (GraphAllBtn != null)
                GraphAllBtn.Click += (_, __) =>
                {
                    _graphSoundOnly = false;
                    GraphSoundBtn.Content = "只看音频";
                    ShowTaeAnim(_taeCur);
                };

            if (GraphSoundBtn != null)
                GraphSoundBtn.Click += (_, __) =>
                {
                    _graphSoundOnly = !_graphSoundOnly;
                    GraphSoundBtn.Content = _graphSoundOnly ? "看全部" : "只看音频";
                    ShowTaeAnim(_taeCur);
                };

            if (GraphTimeSlider != null)
                GraphTimeSlider.PropertyChanged += (_, ev) =>
                {
                    if (ev.Property != Slider.ValueProperty) return;
                    var max = GraphTimeSlider.Maximum;
                    GraphTime.Text = GraphTimeSlider.Value.ToString("F3") + " / " + max.ToString("F3") + " 秒";
                };
        }

        void RefreshAnimCharacters()
        {            AnimChrList.Children.Clear();
            var root = _cfg.GameRoot;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                AnimChrCount.Text = "未设置游戏目录";
                return;
            }

            List<string> chars;
            try { chars = AnimationLibrary.ListCharacters(root); }
            catch (Exception ex) { Log("[动画] 扫描失败：" + ex.Message); return; }

            AnimChrCount.Text = chars.Count + " 个";
            foreach (var c in chars)
            {
                var btn = new Button
                {
                    Content = c,
                    Classes = { "ghost", "small" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 0, 2, 0),
                };
                if (c == _animChrSelected) btn.Classes.Set("active", true);
                btn.Click += (_, __) => LoadAnimCharacter(c);
                AnimChrList.Children.Add(btn);
            }

            // 首次进页面自动展开第一个（c0000 就是狼），省得中间一片空白
            if (_animCurrent == null && chars.Count > 0 && !_animLoading)
                LoadAnimCharacter(chars[0]);
        }

        void LoadAnimCharacter(string chrId)
        {
            var root = _cfg.GameRoot;
            if (string.IsNullOrEmpty(root)) return;

            _animLoading = true;                 // 阻止 RefreshAnimCharacters 再次回来
            try
            {
                _animChrSelected = chrId;
                RefreshAnimCharacters();

                AnimStatus.Text = "读取 " + chrId + " …";
                AnimCharacter a;
                try { a = AnimationLibrary.Load(root, chrId, s => Log(s)); }
                catch (Exception ex) { Log("[动画] " + chrId + " 读取失败：" + ex.Message); AnimStatus.Text = "失败"; return; }

                _animCurrent = a;
                BuildAnimList();

                // 选好角色后自动把 tae 事件读出来，图形编辑器立刻有东西看
                _autoTaeTask = AutoShowFirstTae(chrId);
            }
            finally
            {
                _animLoading = false;
            }
        }

        /// <summary>读完角色后自动把第一条动画的事件画出来（不用用户先点）。</summary>
        async Task AutoShowFirstTae(string chrId)
        {
            try
            {
                _taeLoadedChr = null;
                await LoadTaeFor(chrId);
                if (_taeAnims == null || _taeAnims.Count == 0) return;

                // 优先挑一条有音频事件的，没有就挑事件最多的
                var pick = _taeAnims
                    .Where(a => a.Events.Count > 0)
                    .OrderByDescending(a => a.Events.Count(x => IsSoundEvent(x)))
                    .ThenByDescending(a => a.Events.Count)
                    .FirstOrDefault();

                if (pick != null)
                {
                    ShowTaeAnim(pick);
                    // 顺手把这条动画送进视口。优先用 ID 0（静置），它是角色最标准的姿势，
                    // 不要按名字匹配：TAE 里 a400_* 多处共用 a000_* 的文件名
                    var entry = _animCurrent?.Anims?.FirstOrDefault(x => x.Id == 0)
                             ?? _animCurrent?.Anims?.FirstOrDefault();
                    if (entry != null) await ShowAnimInViewport(entry);
                }
            }
            catch (Exception ex) { Log("[图形编辑器] 自动载入失败：" + ex.Message); }
        }

        void BuildAnimList()
        {
            AnimList.Children.Clear();
            _animRows.Clear();
            if (_animCurrent == null) { AnimCount.Text = ""; return; }

            var q = AnimSearchBox?.Text?.Trim();
            IEnumerable<AnimEntry> view = _animCurrent.Anims;
            if (!string.IsNullOrEmpty(q))
                view = view.Where(e => e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       e.Id.ToString().Contains(q) ||
                                       e.TaeFile.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = view.ToList();
            const int MaxRows = 1200;
            int shown = 0;

            foreach (var g in list.GroupBy(e => e.TaeFile).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (shown >= MaxRows) break;
                AnimList.Children.Add(new TextBlock
                {
                    Text = g.Key + "   ·   " + g.Count() + " 条",
                    FontSize = 11,
                    Foreground = Brush("#38D7E8"),
                    Margin = new Thickness(8, 10, 0, 4)
                });

                foreach (var e in g.OrderBy(x => x.Id))
                {
                    if (shown >= MaxRows) break;
                    shown++;
                    var box = new CheckBox
                    {
                        Content = new TextBlock
                        {
                            Text = e.Display,
                            FontSize = 12,
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        Margin = new Thickness(2, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    ToolTip.SetTip(box, e.Display);
                    var info = new TextBlock
                    {
                        Text = "ID " + e.Id + "  ·  动作 " + e.Actions + "  ·  轨 " + e.Tracks,
                        FontSize = 10.5,
                        Foreground = Brush("#5C7186"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    grid.Children.Add(box);
                    Grid.SetColumn(info, 1);
                    grid.Children.Add(info);

                    var row = new Border
                    {
                        Padding = new Thickness(8, 4),
                        CornerRadius = new CornerRadius(7),
                        Background = Brushes.Transparent,
                        Child = grid,
                        Cursor = new Cursor(StandardCursorType.Hand)
                    };
                    row.PointerEntered += (_, __) => row.Background = Brush("#101B28");
                    row.PointerExited += (_, __) => row.Background = Brushes.Transparent;

                    // 点行 = 在图形编辑器里看这条动画的事件时间轴
                    var capturedEntry = e;
                    row.PointerPressed += async (_, __) =>
                    {
                        try
                        {
                            if (_taeLoadedChr != _animCurrent?.Id)
                                await LoadTaeFor(_animCurrent?.Id);
                            var hit = _taeAnims?.FirstOrDefault(a => a.Id == capturedEntry.Id);
                            ShowTaeAnim(hit);

                            // 视口：载入模型 + 这条动画，画第一帧
                            await ShowAnimInViewport(capturedEntry);
                        }
                        catch (Exception ex) { Log("[图形编辑器] " + ex.Message); }
                    };

                    AnimList.Children.Add(row);
                    _animRows.Add(new AnimRow { Entry = e, Box = box });
                }
            }

            if (list.Count > shown)
                AnimList.Children.Add(new TextBlock
                {
                    Text = "…… 还有 " + (list.Count - shown) + " 条未显示，用搜索框缩小范围",
                    FontSize = 11,
                    Foreground = Brush("#5C7186"),
                    Margin = new Thickness(10, 12, 0, 6)
                });

            AnimCount.Text = _animCurrent.Anims.Count + " 条动画 · " + _animCurrent.TaeCount + " 个 tae";
            AnimStatus.Text = string.IsNullOrEmpty(_animCurrent.Error)
                ? "骨骼 " + (string.IsNullOrEmpty(_animCurrent.Skeleton) ? "?" : _animCurrent.Skeleton)
                : _animCurrent.Error;
        }

        void AnimSearch_Changed(object sender, TextChangedEventArgs e)
        {
            if (_animCurrent == null) return;
            BuildAnimList();
        }

        void AnimSearchClear_Click(object sender, RoutedEventArgs e) { AnimSearchBox.Text = ""; }

        void AnimSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _animRows) r.Box.IsChecked = true;
        }

        void AnimClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _animRows) r.Box.IsChecked = false;
        }

        void AnimRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAnimCharacters();
            if (!string.IsNullOrEmpty(_animChrSelected)) LoadAnimCharacter(_animChrSelected);
        }

        async void BrowseAnimOut_Click(object sender, RoutedEventArgs e)
        {
            var p = await PickFolder("选择动画输出目录");
            if (p != null) AnimOutBox.Text = p;
        }

        async void AnimRun_Click(object sender, RoutedEventArgs e)
        {
            if (_animBusy) return;
            if (_animCurrent == null) { Log("[动画] 先选一个角色"); return; }

            var root = _cfg.GameRoot;
            var outRoot = AnimOutBox.Text?.Trim();
            if (string.IsNullOrEmpty(outRoot)) { Log("[动画] 先设置输出目录"); return; }

            bool whole = AnimWholeBox.IsChecked == true;
            var picked = whole
                ? _animCurrent.Anims.ToList()
                : _animRows.Where(r => r.Box.IsChecked == true).Select(r => r.Entry).ToList();
            if (picked.Count == 0) { Log("[动画] 先勾选至少一条动画"); return; }

            bool wantFbx = AnimFbxBox.IsChecked == true;
            bool wantMesh = AnimMeshBox.IsChecked == true;
            bool wantTex = AnimTexBox.IsChecked == true;
            bool wantAudio = AnimAudioBox.IsChecked == true;
            bool wantRest = AnimRestBox.IsChecked == true;

            _cfg.AnimOutputRoot = outRoot;
            _cfg.Save();

            _animBusy = true;
            AnimRunBtn.IsEnabled = false;
            AnimBar.Maximum = Math.Max(1, picked.Count);
            AnimBar.Value = 0;

            if (wantFbx || wantMesh || wantTex || wantAudio)
            {
                // ---- FBX 一体化路线
                AnimStatus.Text = "正在导出 FBX…";
                Log("==== 导出 " + _animCurrent.Id + " · " + picked.Count + " 条 转 FBX ====");

                var opt = new ChrExportOptions
                {
                    Anim = wantFbx,
                    Mesh = wantMesh || wantFbx,
                    Tex = wantTex,
                    Audio = wantAudio,
                    RestPose = wantRest,
                };

                var sw2 = Stopwatch.StartNew();
                try
                {
                    var chrId = _animCurrent.Id;
                    var files = whole ? null : picked.Select(p => p.Name + ".hkx").ToList();
                    var res = await Task.Run(() =>
                        CharacterExport.Export(root, chrId, files, Path.Combine(outRoot, "动画"), opt,
                            s => Log(s)));

                    AnimBar.Value = AnimBar.Maximum;
                    AnimStatus.Text = res.Summary();
                    if (res.Errors.Count > 0)
                        Log("[动画] " + res.Errors.Count + " 个错误，首个：" + res.Errors[0]);
                    Log("[动画] " + res.Summary());
                    if (OpenDoneBox.IsChecked == true) OpenFolder(res.OutputDir);
                }
                catch (Exception ex)
                {
                    AnimStatus.Text = "出错";
                    Log("[动画] " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    _animBusy = false;
                    AnimRunBtn.IsEnabled = true;
                    AnimBar.Value = 0;
                }
                return;
            }

            // ---- 原始格式路线（.hkx / .tae，给 DSAnimStudio 用）
            AnimStatus.Text = "正在导出 " + picked.Count + " 条动画…";
            Log("==== 导出动画：" + _animCurrent.Id + " · " + picked.Count + " 条 → " + outRoot + " ====");

            var sw = Stopwatch.StartNew();
            try
            {
                int n = await Task.Run(() =>
                {
                    var dir = Path.Combine(outRoot, "动画");
                    if (whole) return AnimationLibrary.ExportRaw(root, _animCurrent.Id, dir, s => Log(s));
                    return AnimationLibrary.ExportSelected(root, picked, dir, s => Log(s));
                });

                AnimBar.Value = AnimBar.Maximum;
                AnimStatus.Text = string.Format("完成 · {0} 个文件 · {1:F1} 秒", n, sw.Elapsed.TotalSeconds);
                Log("[动画] " + AnimStatus.Text);
                if (OpenDoneBox.IsChecked == true) OpenFolder(Path.Combine(outRoot, "动画"));
            }
            catch (Exception ex)
            {
                AnimStatus.Text = "出错";
                Log("[动画] " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                _animBusy = false;
                AnimRunBtn.IsEnabled = true;
                AnimBar.Value = 0;
            }
        }

        // ---------------------------------------------------------- built-in tools

        void BuildToolList()
        {
            ToolList.Children.Clear();
            _toolRows.Clear();

            foreach (var def in ToolCatalog.All)
            {
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                title.Children.Add(new TextBlock { Text = def.Name, FontSize = 12.5, FontWeight = FontWeight.SemiBold });
                title.Children.Add(new Border
                {
                    Background = Brush("#0E2230"),
                    BorderBrush = Brush("#1D3E52"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = def.Tag, FontSize = 9.5, Foreground = Brush("#38D7E8") }
                });
                if (def.NeededNow)
                    title.Children.Add(new Border
                    {
                        Background = Brush("#2A2410"),
                        BorderBrush = Brush("#5C4E14"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(5, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "导出必需", FontSize = 9.5, Foreground = Brush("#F2D65C") }
                    });

                var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(title);
                stack.Children.Add(new TextBlock { Text = def.Desc, FontSize = 10.5, Foreground = Brush("#93A9BC") });
                stack.Children.Add(new TextBlock
                {
                    Text = def.RepoUrl,
                    FontSize = 10,
                    Foreground = Brush("#5C7186"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 520
                });

                var state = new TextBlock
                {
                    FontSize = 10,
                    Foreground = Brush("#5C7186"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };

                var launch = new Button { Content = "启动", Classes = { "ghost", "small" }, VerticalAlignment = VerticalAlignment.Center };
                launch.Click += async (_, __) => await LaunchOrInstall(def.Key);

                var install = new Button { Content = "下载", Classes = { "primary", "small" }, VerticalAlignment = VerticalAlignment.Center };
                install.Click += async (_, __) => await InstallTool(def);

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
                grid.Children.Add(stack);
                Grid.SetColumn(state, 1); grid.Children.Add(state);
                Grid.SetColumn(install, 2); grid.Children.Add(install);
                Grid.SetColumn(launch, 3); grid.Children.Add(launch);

                var row = new Border
                {
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(8),
                    Background = Brushes.Transparent,
                    Child = grid
                };
                row.PointerEntered += (_, __) => row.Background = Brush("#101B28");
                row.PointerExited += (_, __) => row.Background = Brushes.Transparent;
                row.DoubleTapped += async (_, __) => await LaunchOrInstall(def.Key);

                ToolList.Children.Add(row);
                _toolRows.Add(new ToolRow { Def = def, Install = install, Launch = launch, State = state });
            }

            RefreshToolStates();
        }

        void RefreshToolStates()
        {
            foreach (var r in _toolRows)
            {
                var exe = r.Def.Installed ? r.Def.ResolveExe() : null;
                if (exe != null)
                {
                    r.State.Text = "已安装";
                    r.State.Foreground = Brush("#35E0D0");
                    r.Install.Content = "重装";
                    r.Install.Classes.Set("primary", false);
                    r.Install.Classes.Set("ghost", true);
                    r.Launch.IsEnabled = true;
                }
                else if (r.Def.AssetUrl == null)
                {
                    r.State.Text = "仅源码";
                    r.State.Foreground = Brush("#5C7186");
                    r.Install.Content = "仓库";
                    r.Install.Classes.Set("primary", false);
                    r.Install.Classes.Set("ghost", true);
                    r.Launch.IsEnabled = false;
                }
                else
                {
                    r.State.Text = "未安装";
                    r.State.Foreground = Brush("#F2D65C");
                    r.Install.Content = "下载";
                    r.Install.Classes.Set("primary", true);
                    r.Install.Classes.Set("ghost", false);
                    r.Launch.IsEnabled = false;
                }
            }

            ToolCountText.Text = ToolCatalog.InstalledCount + " / " + ToolCatalog.All.Length + " 已装";
            SideStatus.Text = "就绪 · 工具 " + ToolCatalog.InstalledCount + "/" + ToolCatalog.All.Length;
        }

        async Task<string> InstallTool(ToolDef def)
        {
            if (_installing) { Log("[提示] 正在下载中，等这次完成"); return null; }
            if (def.AssetUrl == null)
            {
                Log("[提示] " + def.Name + " 仓库只有源码，打开浏览器：" + def.RepoUrl);
                OpenUrl(def.RepoUrl);
                return null;
            }

            _installing = true;
            try
            {
                var exe = await ToolCatalog.InstallAsync(def, _cfg.GitHubToken, Log, CancellationToken.None);
                RefreshToolStates();
                if (exe != null)
                {
                    var e = _cfg.Tools.FirstOrDefault(t => string.Equals(t.Name, def.Name, StringComparison.OrdinalIgnoreCase));
                    if (e == null) { e = new ToolEntry { Name = def.Name }; _cfg.Tools.Add(e); }
                    e.Path = exe;
                    _cfg.Save();
                }
                ApplyBackground(CurrentPage());
                return exe;
            }
            catch (Exception ex)
            {
                Log("[失败] " + def.Name + "：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
            finally { _installing = false; }
        }

        async void InstallAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var def in ToolCatalog.All)
            {
                if (def.AssetUrl == null) continue;
                if (def.Installed) continue;
                var exe = await InstallTool(def);
                if (exe != null)
                {
                    var row = _cfg.Tools.FirstOrDefault(t => string.Equals(t.Name, def.Name, StringComparison.OrdinalIgnoreCase));
                    if (row == null) { row = new ToolEntry { Name = def.Name }; _cfg.Tools.Add(row); }
                    row.Path = exe;
                }
            }
            _cfg.Save();
            RefreshToolStates();
            Log("[完成] 内置工具已装 " + ToolCatalog.InstalledCount + " / " + ToolCatalog.All.Length);
        }

        void SaveToken_Click(object sender, RoutedEventArgs e)
        {
            _cfg.GitHubToken = TokenBox.Text?.Trim() ?? "";
            _cfg.Save();
            UpdateTokenState();
            Log(string.IsNullOrEmpty(_cfg.GitHubToken) ? "[令牌] 已清空，使用匿名下载" : "[令牌] 已保存（" + Mask(_cfg.GitHubToken) + "）");
        }

        void UpdateTokenState()
        {
            var t = _cfg.GitHubToken ?? "";
            TokenState.Text = string.IsNullOrWhiteSpace(t) ? "未设置 · 匿名下载" : "已设置 · " + Mask(t);
            TokenState.Foreground = Brush(string.IsNullOrWhiteSpace(t) ? "#5C7186" : "#35E0D0");
        }

        static string Mask(string t) =>
            t.Length <= 12 ? "••••" : t.Substring(0, 8) + "…" + t.Substring(t.Length - 4);

        void OpenToolsDir_Click(object sender, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(ToolCatalog.Root); } catch { }
            OpenFolder(ToolCatalog.Root);
        }

        void OpenBlenderSite_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.blender.org/download/");

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }

        // ---------------------------------------------------------- files

        async Task<string> PickFolder(string title)
        {
            try
            {
                var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                });
                return res.FirstOrDefault()?.TryGetLocalPath();
            }
            catch { return null; }
        }

        async Task<string> PickFile(string title, string typeName, string pattern)
        {
            try
            {
                var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(typeName) { Patterns = new[] { pattern } }
                    }
                });
                return res.FirstOrDefault()?.TryGetLocalPath();
            }
            catch { return null; }
        }

        static void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + path + "\"", UseShellExecute = true });
                else if (File.Exists(path))
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
            }
            catch { }
        }

        static Bitmap LoadBitmap(string path)
        {
            using var fs = File.OpenRead(path);
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }

        static Bitmap LoadBitmapFromResource(string uri)
        {
            using var s = AssetLoader.Open(new Uri(uri));
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }

        static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

        // ==================== 图形编辑器（事件时间轴） ====================
        //
        // 对应 DSAnimStudio 的「图形编辑器」：把一条动画的 TAE 事件按时间画成轨道，
        // 点某条事件就在右边看它的参数（SetTurnSpeed 的 224 / Blend 的 16 / PlaySound 的 SoundID…）。
        //
        // 数据来源：TaeEvents.Load 读 chr\*.anibnd.dcx 里的 .tae，
        // 参数靠 DSAnimStudio 那份 TAE.Template.SDT.xml 解（不套模板全是 null）。

        List<TaeAnimInfo> _taeAnims;          // 当前角色的全部 tae 动画
        Border _hlBar;                        // 当前高亮的事件条
        IBrush _hlBarOldBg, _hlBarOldBorder;
        TaeAnimInfo _taeCur;                  // 当前选中的那条
        bool _graphSoundOnly;                 // 只看音频事件
        bool _taeLoading;
        string _taeLoadedChr;                 // 已经载入过 tae 的角色，避免重复解析
        Task _autoTaeTask;                    // 自动载入 tae 的任务（截图模式要等它）

        /// <summary>读当前角色的 TAE 事件表（异步，失败不炸）。</summary>
        async Task LoadTaeFor(string chrId)
        {
            if (_taeLoadedChr == chrId) return;
            if (_animCurrent == null) return;

            // 上一个角色的解析还没完，等它结束再来（不要直接丢掉请求）
            for (int w = 0; w < 120 && _taeLoading; w++) await Task.Delay(100);
            if (_taeLoadedChr == chrId) return;

            var root = _cfg.GameRoot;
            if (string.IsNullOrEmpty(root)) return;

            _taeLoading = true;
            GraphInfo.Text = "读事件…";
            GraphTracks.Children.Clear();
            try
            {
                var anims = await Task.Run(() =>
                {
                    var path = Path.Combine(root, "chr", chrId + ".anibnd.dcx");
                    if (!File.Exists(path)) return new List<TaeAnimInfo>();

                    var bnd = SoulsFormats.BND4.Read(path);
                    var taeFile = bnd.Files.FirstOrDefault(f =>
                        f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase));
                    if (taeFile == null) return new List<TaeAnimInfo>();

                    return TaeEvents.Load(taeFile.Bytes, FindTaeTemplate());
                });

                _taeAnims = anims;
                _taeLoadedChr = chrId;

                int ev = anims.Sum(a => a.Events.Count);
                int withP = anims.Sum(a => a.Events.Count(e => e.Params.Count > 0));
                GraphInfo.Text = anims.Count + " 条 · " + ev + " 事件";
                GraphHint.Text = "事件 " + ev + " 条，其中 " + withP + " 条带参数。点一条看参数。";
                Log("[图形编辑器] " + chrId + "：" + anims.Count + " 条动画，" + ev + " 个事件，"
                    + withP + " 个带参数");
            }
            catch (Exception ex)
            {
                GraphInfo.Text = "读事件失败";
                GraphHint.Text = ex.Message;
                Log("[图形编辑器] " + ex.Message);
            }
            finally
            {
                _taeLoading = false;
            }
        }

        /// <summary>找 TAE 模板：DSAnimStudio 的 Res 目录、或程序自己带的 Res 目录。</summary>
        static string FindTaeTemplate()
        {
            var cands = new List<string>();

            var baseDir = AppContext.BaseDirectory;
            cands.Add(Path.Combine(baseDir, "Res", "TAE.Template.SDT.xml"));
            cands.Add(Path.Combine(baseDir, "TAE.Template.SDT.xml"));

            // 用户的 DSAnimStudio 安装位置（常见几种）
            foreach (var drive in new[] { "E:", "D:", "C:" })
            {
                cands.Add(drive + @"\mapper\只狼\tools\DSAnimStudio\DSAnimStudio-v5-RC6.1-zh-CN-win64\Res\TAE.Template.SDT.xml");
                cands.Add(drive + @"\DSAnimStudio\Res\TAE.Template.SDT.xml");
            }

            foreach (var c in cands)
                if (File.Exists(c)) return c;
            return null;   // 没有模板也能跑，只是参数为空
        }

        /// <summary>选中一条动画，把它的时间轴画出来。</summary>
        void ShowTaeAnim(TaeAnimInfo anim)
        {
            _taeCur = anim;
            _hlBar = null;
            GraphTracks.Children.Clear();
            ParamList.Children.Clear();
            ParamCount.Text = "";

            if (anim == null)
            {
                GraphHint.Text = "左边点一条动画，这里显示它的事件时间轴。";
                return;
            }

            var evs = anim.Events.AsEnumerable();
            if (_graphSoundOnly)
                evs = evs.Where(e => IsSoundEvent(e));
            var list = evs.OrderBy(e => e.Start).ToList();

            // 时间轴范围
            float maxT = 0.001f;
            foreach (var e in list) maxT = Math.Max(maxT, Math.Max(e.Start, e.End));
            GraphTimeSlider.Maximum = maxT;
            GraphTime.Text = "0.000 / " + maxT.ToString("F3") + " 秒";

            if (list.Count == 0)
            {
                GraphHint.Text = _graphSoundOnly
                    ? "这条动画没有音频事件。点「全轨」看全部。"
                    : "这条动画没有事件（TAE 里是空轨道）。";
                return;
            }

            var byType = list.GroupBy(e => e.Type).OrderBy(g => g.Key).ToList();
            GraphHint.Text = list.Count + " 条事件 · " + byType.Count + " 种类型"
                + (_graphSoundOnly ? "（只看音频）" : "");

            // 每种事件类型一行，事件块按时间在行内定位
            const double RowH = 24;
            foreach (var g in byType)
            {
                var sample = g.First();
                var row = new Grid { Height = RowH, ColumnDefinitions = new ColumnDefinitions("128,*") };

                var tag = new TextBlock
                {
                    Text = ShortName(sample.Name, 16),
                    FontSize = 11,
                    Foreground = Brush(IsSoundEvent(sample) ? "#F2B84B" : "#7FE3D0"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                ToolTip.SetTip(tag, sample.Name + "（type " + g.Key + "）· " + g.Count() + " 条");
                row.Children.Add(tag);

                // 轨道底
                var lane = new Grid { Background = Brush("#0C1520"), ClipToBounds = true };
                Grid.SetColumn(lane, 1);
                row.Children.Add(lane);

                foreach (var e in g)
                {
                    double x0 = Math.Max(0, e.Start / maxT);
                    double f = Math.Min(1, Math.Max(e.Start, e.End) / maxT);
                    var bar = new Border
                    {
                        Background = Brush(IsSoundEvent(e) ? "#8A5A12" : "#12555C"),
                        BorderBrush = Brush(IsSoundEvent(e) ? "#F2B84B" : "#38D7E8"),
                        BorderThickness = new Thickness(1, 0, 1, 0),
                        CornerRadius = new CornerRadius(2),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Height = 13,
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    ToolTip.SetTip(bar, e.ToString());
                    var captured = e;
                    var capturedBar = bar;
                    MyLanePlacer(bar, lane, x0, f);
                    bar.PointerPressed += (_, __) => ShowEventParams(captured, capturedBar);
                    lane.Children.Add(bar);
                }

                GraphTracks.Children.Add(row);
            }

            // 默认选中第一条带参数的
            var firstWithParams = list.FirstOrDefault(e => e.Params.Count > 0) ?? list[0];
            ShowEventParams(firstWithParams);
        }

        /// <summary>
        /// 事件条在轨道里的位置：要等 lane 真正测量出宽度才能算像素，
        /// 所以在 SizeChanged 里重放一次。
        /// </summary>
        static void MyLanePlacer(Border bar, Grid lane, double x0, double frac)
        {
            void Apply()
            {
                double w = lane.Bounds.Width;
                if (w <= 1) w = 200;
                double left = x0 * w;
                double width = Math.Max(3, frac * w - left);
                if (left + width > w) width = Math.Max(3, w - left);
                bar.Margin = new Thickness(left, 0, 0, 0);
                bar.Width = width;
            }
            Apply();
            lane.SizeChanged += (_, __) => Apply();
        }

        // ==================== 视口（3D 模型 + 骨架） ====================
        //
        // 蒙皮用 Core/SkinSolver.cs —— 算法逐字照抄 DSAnimStudio：
        //   skin[n] = inverse(Bind[n]) * Pose[n]，n 是 **FLVER 节点索引**（不是 hkx 骨骼索引！）
        // 这点搞错的话，Pelvis 的顶点会被套上 L_Foot_Target1 的矩阵，模型直接炸成放射状。

        SkinSolver _vpSolver;             // 蒙皮求解器（按角色缓存）
        List<ChrMesh> _vpMeshes;          // 当前角色的网格
        FLVER2 _vpFlver;
        DecodedSkeleton _vpSkel;
        string _vpChr;                    // 缓存属于哪个角色
        bool _vpBusy;
        bool _vpFlat;                     // 着色 / 线框
        float _vpYaw = 0.42f, _vpPitch = -0.05f;   // 视角
        bool _vpDrag;
        Point _vpDragFrom;
        float _vpZoom = 1f;
        float _vpFrame;
        DecodedAnimation _vpAnim;
        readonly Dictionary<string, DecodedAnimation> _vpAnimCache = new Dictionary<string, DecodedAnimation>();
        readonly Dictionary<string, Vector3[]> _vpBuf = new Dictionary<string, Vector3[]>();

        ChrTextureResolver _vpTex;                    // 贴图解析
        readonly Dictionary<string, DdsImage> _vpMeshTex = new Dictionary<string, DdsImage>();
        bool _vpPlaying;                              // 实时播放中
        bool _vpLoop = true;                          // 循环
        System.Threading.CancellationTokenSource _vpPlayCts;
        double _vpFps;
        bool _vpShowGrid = true;
        DateTime _vpLastDraw;

        /// <summary>载入角色的模型和骨架（第一次点动画时才有开销）。</summary>
        async Task<bool> EnsureViewport(string chrId)
        {
            if (_vpChr == chrId && _vpSolver != null && _vpMeshes != null) return true;

            var root = _cfg.GameRoot;
            if (string.IsNullOrEmpty(root)) return false;

            ViewportInfo.Text = "载入模型…";
            try
            {
                var data = await Task.Run(() =>
                {
                    var flver = CharacterAssets.LoadFlver(root, chrId);
                    if (flver == null || flver.Meshes.Count == 0) return (null, null, null);
                    var skel = CharacterAssets.LoadSkeleton(root, chrId, null);
                    var meshes = CharacterAssets.LoadMeshes(root, chrId, null);   // 与导出同一套网格
                    return (flver, skel, meshes);
                });

                if (data.Item1 == null)
                {
                    ViewportInfo.Text = "该角色没有模型";
                    return false;
                }

                _vpFlver = data.Item1;
                _vpSkel = data.Item2;
                _vpMeshes = data.Item3 ?? new List<ChrMesh>();
                _vpSolver = new SkinSolver(_vpFlver, _vpSkel);
                _vpChr = chrId;
                _vpBuf.Clear();
                _vpAnimCache.Clear();
                _vpMeshTex.Clear();
                _vpTex = new ChrTextureResolver(root, chrId, Log);

                // 每个网格挂上漫反射贴图（找不到就保持单色）
                int texHit = 0;
                foreach (var m in _vpMeshes)
                {
                    DdsImage img = null;
                    foreach (var t in m.Textures)
                    {
                        img = _vpTex.Get(t);
                        if (img != null) break;
                    }
                    _vpMeshTex[m.Name ?? ""] = img;
                    if (img != null) texHit++;
                }
                Log("[视口] 贴图命中 " + texHit + "/" + _vpMeshes.Count + " 个网格");

                ViewportInfo.Text = _vpMeshes.Count + " 网格 · " + _vpSolver.NodeCount + " 节点";
                Log("[视口] 模型就绪：" + chrId + " 网格 " + _vpMeshes.Count
                    + " 骨架 " + (_vpSkel?.BoneCount ?? 0) + " 绑定 " + _vpSolver.BindHits);
                Log("[视口] " + chrId + "：网格 " + _vpMeshes.Count + "，骨架绑定 "
                    + _vpSolver.BindHits + "/" + _vpSolver.BoneCount);
                return true;
            }
            catch (Exception ex)
            {
                ViewportInfo.Text = "载入失败";
                Log("[视口] 载入失败：" + ex.GetType().Name + " — " + ex.Message);
                if (ex.InnerException != null)
                    Log("[视口] 内层：" + ex.InnerException.GetType().Name + " — " + ex.InnerException.Message);
                Log("[视口] 位置：" + (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
                return false;
            }
        }

        /// <summary>在视口里显示某条动画（载入模型 → 解动画 → 画第 0 帧）。</summary>
        async Task ShowAnimInViewport(AnimEntry entry)
        {
            if (entry == null || _animCurrent == null) return;
            ViewportAnim.Text = entry.Display + " · 拖动旋转 · 滚轮缩放";

            if (!await EnsureViewport(_animCurrent.Id)) return;

            // 解这条动画的关键帧（带缓存）
            DecodedAnimation anim = null;
            var key = _animCurrent.Id + "/" + entry.Name;
            if (_vpAnimCache.TryGetValue(key, out var cached))
            {
                anim = cached;
            }
            else
            {
                try
                {
                    var root = _cfg.GameRoot;
                    var chrId = _animCurrent.Id;
                    var name = entry.Name;
                    anim = await Task.Run(() =>
                    {
                        var hkxs = CharacterAssets.LoadAnimHkx(root, chrId);
                        var comp = CharacterAssets.GetCompendium(root, chrId);

                        // TAE 里的文件名是 xxx.hkt，实际资源是 xxx.hkx；两种都试
                        var want = name ?? "";
                        var wantHkx = System.Text.RegularExpressions.Regex.Replace(
                            want, @"\.hkt$", ".hkx", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var wantBase = System.IO.Path.GetFileNameWithoutExtension(want);

                        (string file, byte[] bytes) pick = default;
                        foreach (var p in hkxs)
                        {
                            if (string.Equals(p.file, want, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(p.file, wantHkx, StringComparison.OrdinalIgnoreCase))
                            { pick = p; break; }
                        }
                        if (pick.bytes == null)
                        {
                            // 再退一步：只比基名（去掉 .hkx / .hkt）
                            foreach (var p in hkxs)
                            {
                                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(p.file),
                                                  wantBase, StringComparison.OrdinalIgnoreCase))
                                { pick = p; break; }
                            }
                        }
                        if (pick.bytes == null) return null;

                        var a = HavokAnimation.Decode(pick.bytes, comp, pick.file, 0);
                        return (a.Ok && a.TrackToBone.Length > 0) ? a : null;
                    });
                    if (anim != null)
                    {
                        _vpAnimCache[key] = anim;
                        Log("[视口] 动画 " + entry.Name + "：" + anim.FrameCount + " 帧，"
                            + anim.TrackToBone.Length + " 轨道");
                    }
                }
                catch (Exception ex) { Log("[视口] 动画解析失败：" + ex.Message); }
            }

            _vpAnim = anim;
            _vpFrame = 0f;

            if (anim == null)
            {
                ViewportAnim.Text = entry.Display + "（没有关键帧，显示静置姿势）· 拖动旋转 · 滚轮缩放";
                if (ViewportFrame != null) ViewportFrame.Maximum = 1;
            }
            else if (ViewportFrame != null)
            {
                ViewportFrame.Maximum = Math.Max(1, anim.FrameCount - 1);
                ViewportFrame.Value = 0;
            }

            RenderViewport();
        }

        /// <summary>把当前帧画到视口里。</summary>
        async void RenderViewport()
        {
            if (_vpBusy || _vpSolver == null || _vpMeshes == null) return;
            _vpBusy = true;
            try
            {
                var frame = _vpFrame;
                var anim = _vpAnim;
                var solver = _vpSolver;
                var meshes = _vpMeshes;
                var flat = _vpFlat;
                var yaw = _vpYaw;
                var pitch = _vpPitch;
                var bufSnapshot = _vpBuf;
                var showGrid = _vpShowGrid;

                var png = await Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // 计算当前帧姿势
                    solver.SetFrame(anim, frame);

                    var list = new List<RenderMesh>();
                    int ci = 0;
                    foreach (var m in meshes)
                    {
                        // 每个网格一个变形缓冲
                        if (!bufSnapshot.TryGetValue(m.Name ?? ("#" + ci), out var buf)
                            || buf.Length != (m.Positions?.Count ?? 0))
                        {
                            buf = new Vector3[m.Positions?.Count ?? 0];
                            bufSnapshot[m.Name ?? ("#" + ci)] = buf;
                        }
                        var color = MeshTint(ci++);
                        var rm = solver.ToRenderMesh(m, buf, color);
                        _vpMeshTex.TryGetValue(m.Name ?? "", out var tex);
                        rm.Texture = _vpFlat ? null : tex;
                        list.Add(rm);
                    }

                    // 取景：跟 DSAnimStudio 一样用固定机位（不随每帧 bbox 抖动）。
                    // 只狼角色身高约 2 米，站原点附近，所以直接钉死。
                    var cam = new OrbitCamera();
                    // 模型是 Z 朝上（Axis.ToBlender 的结果），所以高度写在 Z 上
                    cam.Target = new Vector3(0f, 0f, 1.02f);   // 大约胸腹高度
                    cam.Distance = 3.4f * _vpZoom;
                    cam.FovDeg = 44f;
                    cam.Yaw = yaw;
                    cam.Pitch = pitch;
                    float footY = 0f;   // 地面网格铺在 y=0（脚底）

                    // 复用同一个渲染器实例：Resize 内部只在尺寸变化时重建缓冲，
                    // 每帧 new 一次会反复分配 4.7 MB（像素 + 深度），GC 压力极大。
                    var r = _vpRenderer ??= new SoftRenderer();
                    if (r.Width != 700 || r.Height != 840) r.Resize(700, 840);
                    r.Wireframe = flat;
                    r.FlatShading = false;
                    r.ShowBones = false;
                    r.Background = new Vector3(0.075f, 0.11f, 0.15f);
                    // 先画模型（Render 内部会 Clear），再叠地面网格，这样网格有正确遮挡
                    r.Render(list, cam);
                    if (showGrid) r.DrawGrid(cam, 3.0f, 20, footY);

                    // 直接给像素，不走 PNG 编解码（每帧能省掉一次 1.7 MB 的压缩+解压）
                    return (Pixels: (uint[])r.Pixels.Clone(), W: r.Width, H: r.Height,
                            Ms: sw.Elapsed.TotalMilliseconds);
                });

                if (png.Pixels != null && png.Pixels.Length > 0)
                {
                    BlitToViewport(png.Pixels, png.W, png.H);

                    // 帧率 = 1 / 本帧总耗时（蒙皮 + 光栅化）。
                    double dtMs = Math.Max(0.01, png.Ms);
                    double inst = 1000.0 / dtMs;
                    _vpFps = _vpFps <= 0.01 ? inst : _vpFps * 0.8 + inst * 0.2;
                    _vpLastDraw = DateTime.UtcNow;
                    if (ViewportFps != null) ViewportFps.Text = _vpFps.ToString("F0") + " FPS";
                }

                if (_vpAnim != null)
                {
                    var span = Math.Max(1f, _vpAnim.FrameCount - 1);
                    ViewportTime.Text = frame.ToString("F0") + " / " + span.ToString("F0");
                }
            }
            catch (Exception ex)
            {
                Log("[视口] 渲染失败：" + ex.Message);
            }
            finally
            {
                _vpBusy = false;
            }
        }

        /// <summary>实体页：列出当前角色的网格与贴图状态。</summary>
        void FillEntityTab()
        {
            if (EntityList == null) return;
            EntityList.Children.Clear();

            void Row(string a, string b, string c, bool ok)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,60") };
                g.Children.Add(new TextBlock
                {
                    Text = a, FontSize = 11.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.Parse(ok ? "#C8D6E5" : "#6B7A8C")),
                });
                var tb = new TextBlock { Text = b, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8FA3B8")) };
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                var tc = new TextBlock { Text = c, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(ok ? "#7FE0A0" : "#C05C5C")) };
                Grid.SetColumn(tc, 2);
                g.Children.Add(tc);
                EntityList.Children.Add(g);
            }

            if (_vpMeshes == null || _vpMeshes.Count == 0)
            {
                EntityList.Children.Add(new TextBlock
                {
                    Text = "还没有载入模型。点左边一条动画即可。",
                    FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#6B7A8C")),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            int withTex = 0;
            foreach (var kv in _vpMeshTex) if (kv.Value != null) withTex++;

            EntityList.Children.Add(new TextBlock
            {
                Text = _vpChr + " · " + _vpMeshes.Count + " 网格 · " + withTex + " 个有贴图",
                FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.Parse("#C8D6E5")),
            });

            int i = 0;
            foreach (var m in _vpMeshes)
            {
                i++;
                _vpMeshTex.TryGetValue(m.Name ?? "", out var tex);
                var texName = tex != null ? (tex.Width + "x" + tex.Height) : "无";
                Row(m.Name ?? ("#" + i), m.MaterialName ?? "", texName, tex != null);
            }
        }

        /// <summary>实时播放：按 60fps 推帧，逐帧调 RenderViewport。</summary>
        async void StartPlayback()
        {
            if (_vpPlaying || _vpAnim == null) return;
            _vpPlaying = true;
            if (ViewportPlayBtn != null) ViewportPlayBtn.Content = "暂停";

            var cts = new System.Threading.CancellationTokenSource();
            _vpPlayCts = cts;
            var token = cts.Token;

            var frameCount = Math.Max(1, _vpAnim.FrameCount);
            var start = DateTime.UtcNow;

            try
            {
                while (!token.IsCancellationRequested && _vpPlaying)
                {
                    // 按真实时间推进，动画是多少帧就多少帧
                    var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    float f = (float)(elapsed * 30.0);   // 只狼动画大概 30fps

                    if (f >= frameCount - 1)
                    {
                        if (_vpLoop)
                        {
                            start = DateTime.UtcNow;
                            f = 0f;
                        }
                        else
                        {
                            f = frameCount - 1;
                            _vpFrame = f;
                            RenderViewport();
                            break;
                        }
                    }

                    _vpFrame = f;
                    RenderViewport();

                    if (ViewportFrame != null) ViewportFrame.Value = f;

                    // 等这一帧渲染完（_vpBusy 归 false）
                    int guard = 0;
                    while (_vpBusy && guard++ < 200 && !token.IsCancellationRequested)
                        await Task.Delay(4);

                    await Task.Delay(16, token);   // 约 60fps 刷新
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("[视口] 播放出错：" + ex.Message); }
            finally
            {
                _vpPlaying = false;
                if (ViewportPlayBtn != null) ViewportPlayBtn.Content = "播放";
            }
        }

        void StopPlayback()
        {
            _vpPlaying = false;
            try { _vpPlayCts?.Cancel(); } catch { }
            _vpPlayCts = null;
            if (ViewportPlayBtn != null) ViewportPlayBtn.Content = "播放";
        }

        WriteableBitmap _vpBitmap;
        SoftRenderer _vpRenderer;

        /// <summary>把 BGRA 像素直接写进 WriteableBitmap（不编码 PNG，快得多）。</summary>
        void BlitToViewport(uint[] pixels, int w, int h)
        {
            if (pixels == null || w <= 0 || h <= 0) return;

            if (_vpBitmap == null || _vpBitmap.PixelSize.Width != w || _vpBitmap.PixelSize.Height != h)
            {
                _vpBitmap = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96),
                                                PixelFormat.Bgra8888, AlphaFormat.Premul);
                ViewportImage.Source = _vpBitmap;
            }

            using (var fb = _vpBitmap.Lock())
            {
                unsafe
                {
                    var dst = (uint*)fb.Address;
                    int stride = fb.RowBytes / 4;
                    if (stride == w)
                    {
                        for (int i = 0; i < pixels.Length; i++) dst[i] = pixels[i];
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                        {
                            var src = y * w;
                            var d = y * stride;
                            for (int x = 0; x < w; x++) dst[d + x] = pixels[src + x];
                        }
                    }
                }
            }
            ViewportImage.InvalidateVisual();
        }

        /// <summary>把 SoftRenderer 的 BGRA 像素缓冲编码成 PNG（自带 zlib 存储块，不依赖外部库）。</summary>
        static byte[] EncodePng(int w, int h, uint[] pixels)
        {
            // 原始扫描线：每行 1 字节 filter(0) + w*3 字节 RGB
            var raw = new byte[(w * 3 + 1) * h];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                raw[o++] = 0;
                for (int x = 0; x < w; x++)
                {
                    uint c = pixels[y * w + x];
                    raw[o++] = (byte)((c >> 16) & 0xFF);
                    raw[o++] = (byte)((c >> 8) & 0xFF);
                    raw[o++] = (byte)(c & 0xFF);
                }
            }

            using var ms = new MemoryStream();
            void Be32(uint v)
            {
                ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16));
                ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
            }

            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            // IHDR
            Be32(13);
            var ihdr = new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
            ms.Write(ihdr, 0, 4);
            Be32((uint)w); Be32((uint)h);
            ms.Write(new byte[] { 8, 2, 0, 0, 0 }, 0, 5);
            Be32(Crc32(ihdr, ihdr.Length, 0));
            // 上面 CRC 只算了类型名，补上数据重新算
            ms.Position = 8;
            ms.SetLength(8);
            Be32(13);
            ms.Write(ihdr, 0, 4);
            Be32((uint)w); Be32((uint)h);
            ms.Write(new byte[] { 8, 2, 0, 0, 0 }, 0, 5);
            var ihdrAll = new byte[4 + 13];
            ihdr.CopyTo(ihdrAll, 0);
            ihdrAll[4] = (byte)(w >> 24); ihdrAll[5] = (byte)(w >> 16); ihdrAll[6] = (byte)(w >> 8); ihdrAll[7] = (byte)w;
            ihdrAll[8] = (byte)(h >> 24); ihdrAll[9] = (byte)(h >> 16); ihdrAll[10] = (byte)(h >> 8); ihdrAll[11] = (byte)h;
            ihdrAll[12] = 8; ihdrAll[13] = 2; ihdrAll[14] = 0; ihdrAll[15] = 0; ihdrAll[16] = 0;
            Be32(Crc32(ihdrAll, ihdrAll.Length, 0));

            // IDAT（zlib：无压缩存储块）
            var z = ZlibStore(raw);
            Be32((uint)z.Length);
            var idat = new byte[] { (byte)'I', (byte)'D', (byte)'A', (byte)'T' };
            ms.Write(idat, 0, 4);
            ms.Write(z, 0, z.Length);
            var idatAll = new byte[idat.Length + z.Length];
            idat.CopyTo(idatAll, 0);
            Array.Copy(z, 0, idatAll, 4, z.Length);
            Be32(Crc32(idatAll, idatAll.Length, 0));

            // IEND
            Be32(0);
            var iend = new byte[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' };
            ms.Write(iend, 0, 4);
            Be32(Crc32(iend, iend.Length, 0));

            return ms.ToArray();
        }

        /// <summary>zlib 包装：2 字节头 + deflate 存储块（不压缩）+ adler32。</summary>
        static byte[] ZlibStore(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x01);           // CMF/FLG，最快
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(65535, data.Length - pos);
                bool last = pos + n >= data.Length;
                ms.WriteByte((byte)(last ? 1 : 0));
                ms.WriteByte((byte)(n & 0xFF)); ms.WriteByte((byte)((n >> 8) & 0xFF));
                ms.WriteByte((byte)(~n & 0xFF)); ms.WriteByte((byte)((~n >> 8) & 0xFF));
                ms.Write(data, pos, n);
                pos += n;
            }
            uint a = 1, b = 0;
            foreach (var v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        static uint[] _crcTable;
        static uint Crc32(byte[] data, int len, uint seed)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[i] = c;
                }
            }
            uint crc = seed ^ 0xFFFFFFFFu;
            for (int i = 0; i < len; i++) crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        static Vector3 MeshTint(int i)
        {
            // 每个网格给一点色差，方便肉眼区分部件
            var palette = new[]
            {
                new Vector3(0.78f, 0.74f, 0.68f), new Vector3(0.70f, 0.76f, 0.80f),
                new Vector3(0.82f, 0.72f, 0.62f), new Vector3(0.68f, 0.72f, 0.66f),
                new Vector3(0.80f, 0.78f, 0.72f), new Vector3(0.64f, 0.70f, 0.76f),
            };
            return palette[i % palette.Length];
        }

        void WireViewport()
        {
            if (ViewportFrame != null)
                ViewportFrame.PropertyChanged += (_, ev) =>
                {
                    if (ev.Property != Slider.ValueProperty) return;
                    _vpFrame = (float)ViewportFrame.Value;
                    RenderViewport();
                };

            if (ViewportImage != null)
            {
                ViewportImage.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(ViewportImage).Properties.IsLeftButtonPressed) return;
                    _vpDrag = true;
                    _vpDragFrom = e.GetPosition(ViewportImage);
                };
                ViewportImage.PointerMoved += (_, e) =>
                {
                    if (!_vpDrag) return;
                    var p = e.GetPosition(ViewportImage);
                    var dx = (float)(p.X - _vpDragFrom.X);
                    var dy = (float)(p.Y - _vpDragFrom.Y);
                    _vpDragFrom = p;
                    _vpYaw -= dx * 0.010f;
                    _vpPitch += dy * 0.010f;
                    float lim = MathF.PI / 2f - 0.05f;
                    if (_vpPitch > lim) _vpPitch = lim;
                    if (_vpPitch < -lim) _vpPitch = -lim;
                    RenderViewport();
                };
                ViewportImage.PointerReleased += (_, __) => _vpDrag = false;
                ViewportImage.PointerExited += (_, __) => _vpDrag = false;
                ViewportImage.PointerWheelChanged += (_, e) =>
                {
                    _vpZoom *= e.Delta.Y > 0 ? 0.92f : 1.08f;
                    _vpZoom = Math.Clamp(_vpZoom, 0.3f, 3f);
                    RenderViewport();
                };
            }

            if (ViewportPlayBtn != null)
            {
                ViewportPlayBtn.Click += (_, __) =>
                {
                    if (_vpPlaying) StopPlayback();
                    else if (_vpAnim != null) StartPlayback();
                };
            }

            if (ViewportLoopBtn != null)
            {
                ViewportLoopBtn.Click += (_, __) =>
                {
                    _vpLoop = !_vpLoop;
                    ViewportLoopBtn.Content = _vpLoop ? "循环" : "单次";
                    ViewportLoopBtn.Opacity = _vpLoop ? 1.0 : 0.55;
                };
            }

            // 参数 / 实体 / 日志 切换
            void ShowTab(int which)
            {
                if (ParamScroll != null) ParamScroll.IsVisible = which == 0;
                if (EntityScroll != null) EntityScroll.IsVisible = which == 1;
                if (VpLogScroll != null) VpLogScroll.IsVisible = which == 2;
                if (ParamTitle != null) ParamTitle.IsVisible = which == 0;
                if (ParamCount != null) ParamCount.IsVisible = which == 0;
                if (TabParamBtn != null) TabParamBtn.Opacity = which == 0 ? 1.0 : 0.55;
                if (TabEntityBtn != null) TabEntityBtn.Opacity = which == 1 ? 1.0 : 0.55;
                if (TabLogBtn != null) TabLogBtn.Opacity = which == 2 ? 1.0 : 0.55;
                if (which == 1) FillEntityTab();
                if (which == 2 && VpLogText != null) VpLogText.Text = LogText?.Text ?? "";
            }
            if (TabParamBtn != null) TabParamBtn.Click += (_, __) => ShowTab(0);
            if (TabEntityBtn != null) TabEntityBtn.Click += (_, __) => ShowTab(1);
            if (TabLogBtn != null) TabLogBtn.Click += (_, __) => ShowTab(2);
            ShowTab(0);

            if (ViewportShadeBtn != null)
                ViewportShadeBtn.Click += (_, __) =>
                {
                    _vpFlat = !_vpFlat;
                    ViewportShadeBtn.Content = _vpFlat ? "线框" : "着色";
                    RenderViewport();
                };
        }

        /// <summary>把一条事件的参数列到右边。bar 传了就顺手做高亮。</summary>
        void ShowEventParams(TaeEventInfo e, Border bar = null)
        {
            // 高亮当前选中的事件条
            if (bar != null)
            {
                if (_hlBar != null && _hlBar != bar)
                {
                    _hlBar.Background = _hlBarOldBg;
                    _hlBar.BorderBrush = _hlBarOldBorder;
                }
                if (_hlBar != bar)
                {
                    _hlBarOldBg = bar.Background ?? Brush("#12555C");
                    _hlBarOldBorder = bar.BorderBrush ?? Brush("#38D7E8");
                    bar.Background = Brush(IsSoundEvent(e) ? "#C98A1F" : "#1A8F99");
                }
                _hlBar = bar;
            }

            ParamList.Children.Clear();
            if (e == null) { ParamCount.Text = ""; return; }

            ParamCount.Text = "type " + e.Type;

            ParamList.Children.Add(new TextBlock
            {
                Text = e.Name,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(IsSoundEvent(e) ? "#F2B84B" : "#38D7E8"),
                Margin = new Thickness(8, 4, 6, 2),
                TextWrapping = TextWrapping.Wrap,
            });

            ParamList.Children.Add(new TextBlock
            {
                Text = "开始 " + e.Start.ToString("F3") + " 秒　结束 " + e.End.ToString("F3") + " 秒",
                FontSize = 11,
                Foreground = Brush("#5C7186"),
                Margin = new Thickness(8, 0, 6, 8),
            });

            if (e.Params.Count == 0)
            {
                ParamList.Children.Add(new TextBlock
                {
                    Text = "这条事件没有参数（该事件类型在模板里就是空壳，比如 Blend）。",
                    FontSize = 11.5,
                    Foreground = Brush("#5C7186"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 0, 8, 8),
                });
                return;
            }

            // 音频事件：把 SoundID 翻成音库里的事件名
            //
            // 规律（实测确认）：SoundID 形如 1 0 1 0 0 5 0 0 0，去掉首位 '1'，剩下的
            // "01005000" 就是音库内的编号；具体事件名 = 音库ID + 这段数字。
            // 比如 c1010 的 SoundID 101005000 → 事件 c101005000。
            // 同一个 SoundID 会被 c1010/c1011/c1012 几个同族音库共用，所以不写死音库，
            // 只给搜索词，让音频页列出全部命中。
            if (IsSoundEvent(e))
            {
                var sid = e.GetInt("SoundID", -1);
                if (sid > 0)
                {
                    var sidText = sid.ToString();
                    var tail = sidText.Length > 1 ? sidText.Substring(1) : sidText;

                    ParamList.Children.Add(new TextBlock
                    {
                        Text = "音库内编号　" + tail,
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush("#F2B84B"),
                        Margin = new Thickness(8, 8, 8, 2),
                        TextWrapping = TextWrapping.Wrap,
                    });

                    ParamList.Children.Add(new TextBlock
                    {
                        Text = "事件名 = 音库ID + " + tail + "，例如 c1010 → c1010" + tail,
                        FontSize = 10,
                        Foreground = Brush("#5C7186"),
                        Margin = new Thickness(8, 0, 8, 4),
                        TextWrapping = TextWrapping.Wrap,
                    });

                    var goAudio = new Button
                    {
                        Content = "去「音频导出」找这条",
                        Classes = { "ghost" },
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(8, 2, 8, 8),
                    };
                    var capturedTail = tail;
                    goAudio.Click += async (_, __) =>
                    {
                        ShowPage(5);
                        await Task.Delay(140);
                        if (AudioSearchBox != null) AudioSearchBox.Text = capturedTail;
                    };
                    ParamList.Children.Add(goAudio);
                }
            }

            foreach (var kv in e.Params)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 2) };

                var name = new TextBlock
                {
                    Text = kv.Key,
                    FontSize = 11.5,
                    Foreground = Brush("#93A7BA"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                g.Children.Add(name);

                var val = new TextBlock
                {
                    Text = kv.Value,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas,monospace"),
                    Foreground = Brush("#E8EEF4"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                };
                Grid.SetColumn(val, 1);
                g.Children.Add(val);

                var disp = e.GetParamDisplay(kv.Key);
                var box = new StackPanel { Spacing = 1, Margin = new Thickness(8, 2) };
                box.Children.Add(g);
                if (!string.IsNullOrEmpty(disp) && disp != kv.Value)
                    box.Children.Add(new TextBlock
                    {
                        Text = disp,
                        FontSize = 10,
                        Foreground = Brush("#5C7186"),
                        Margin = new Thickness(0, 0, 0, 2),
                    });

                ParamList.Children.Add(new Border
                {
                    Background = Brush("#0A121C"),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(2, 1),
                    Margin = new Thickness(4, 1),
                    Child = box,
                });
            }
        }

        static bool IsSoundEvent(TaeEventInfo e) =>
            e.Name != null && e.Name.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0;

        static string ShortName(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                _cfg.GameRoot = RootBox.Text?.Trim();
                _cfg.OutputRoot = OutBox.Text?.Trim();
                _cfg.BlenderPath = BlenderBox.Text?.Trim();
                _cfg.SkipFog = SkipFogBox.IsChecked == true;
                _cfg.BuildBlend = BuildBlendBox.IsChecked == true;
                _cfg.OpenWhenDone = OpenDoneBox.IsChecked == true;
                _cfg.GitHubToken = TokenBox.Text?.Trim() ?? "";
                _cfg.LastMaps = _maps.Where(m => m.Box.IsChecked == true).Select(m => m.Name).ToList();
                _cfg.Save();
            }
            catch { }
            base.OnClosing(e);
        }
    }
}
