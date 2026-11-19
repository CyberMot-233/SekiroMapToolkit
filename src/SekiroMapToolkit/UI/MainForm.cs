using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SekiroMapToolkit.Core;

namespace SekiroMapToolkit.UI
{
    class MainForm : Form
    {
        readonly Config cfg;

        TextBox txtRoot, txtOut, txtBlender;
        CheckedListBox lstMaps;
        NumericUpDown numLimit;
        CheckBox chkFog, chkBlend, chkOpen;
        Button btnRun, btnCancel;
        RichTextBox log;
        ProgressBar bar;
        Label lblStatus;
        ComboBox cmbPreview;
        MapPreviewCanvas canvas;
        ListView listTools, listChecks;
        TextBox txtTexDir;
        CheckBox chkKeepDds;
        Button btnTex;

        CancellationTokenSource cts;
        bool running;

        public MainForm()
        {
            cfg = Config.Load();
            Text = "只狼地图导出工具集  ·  Sekiro Map Toolkit";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 880);
            MinimumSize = new Size(1000, 720);
            Font = new Font("Microsoft YaHei UI", 9f);
            TryLoadIcon();

            BuildUi();
            LoadSettingsIntoUi();
            RefreshMaps();
            RefreshOutputMaps();
            RefreshTools();
            FormClosing += OnFormClosing;
        }

        // ------------------------------------------------------------------ ui

        void TryLoadIcon()
        {
            try
            {
                using var s = GetType().Assembly.GetManifestResourceStream("SekiroMapToolkit.app.ico");
                if (s != null) Icon = new Icon(s);
                else Icon = SystemIcons.Application;
            }
            catch { }
        }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildHeader(), 0, 0);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildExportTab());
            tabs.TabPages.Add(BuildUnpackTab());
            tabs.TabPages.Add(BuildTextureTab());
            tabs.TabPages.Add(BuildPreviewTab());
            tabs.TabPages.Add(BuildToolsTab());
            tabs.TabPages.Add(BuildHelpTab());
            root.Controls.Add(tabs, 0, 1);

            root.Controls.Add(BuildLogPanel(), 0, 2);

            var status = new Panel { Dock = DockStyle.Fill, Height = 26, Padding = new Padding(8, 2, 8, 2) };
            bar = new ProgressBar { Dock = DockStyle.Left, Width = 260 };
            lblStatus = new Label { Dock = DockStyle.Fill, Text = "就绪", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
            status.Controls.Add(lblStatus);
            status.Controls.Add(bar);
            root.Controls.Add(status, 0, 3);

            Controls.Add(root);
        }

        Control BuildHeader()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(10, 8, 10, 6) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.RowCount = 4;
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            txtRoot = new TextBox { Dock = DockStyle.Fill, ReadOnly = false };
            txtOut = new TextBox { Dock = DockStyle.Fill };
            txtBlender = new TextBox { Dock = DockStyle.Fill };

            t.Controls.Add(MakeLabel("解包目录"), 0, 0);
            t.Controls.Add(txtRoot, 1, 0);
            var b1 = MakeButton("浏览…", (s, e) => PickFolder(txtRoot));
            t.Controls.Add(b1, 2, 0);
            var b1b = MakeButton("自动检测", (s, e) => { AutoDetectRoot(); RefreshMaps(); });
            t.Controls.Add(b1b, 3, 0);

            t.Controls.Add(MakeLabel("输出目录"), 0, 1);
            t.Controls.Add(txtOut, 1, 1);
            var b2 = MakeButton("浏览…", (s, e) => PickFolder(txtOut));
            t.Controls.Add(b2, 2, 1);
            var b2b = MakeButton("打开目录", (s, e) => OpenPath(txtOut.Text));
            t.Controls.Add(b2b, 3, 1);

            t.Controls.Add(MakeLabel("Blender"), 0, 2);
            t.Controls.Add(txtBlender, 1, 2);
            var b3 = MakeButton("浏览…", (s, e) => PickFile(txtBlender, "blender.exe|blender.exe"));
            t.Controls.Add(b3, 2, 2);
            var b3b = MakeButton("保存设置", (s, e) => { SaveUiIntoSettings(); cfg.Save(); Log("设置已保存到 " + Path.Combine(Config.Dir, "config.json")); });
            t.Controls.Add(b3b, 3, 2);

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "流程：UXM / Nuxe 解包 → 勾选地图 → 导出数据 → 自动调用 Blender 生成 .blend（贴图已打包进文件）",
                ForeColor = Color.FromArgb(90, 90, 90),
                TextAlign = ContentAlignment.MiddleLeft
            };
            t.Controls.Add(hint, 0, 3);
            t.SetColumnSpan(hint, 4);
            return t;
        }

        TabPage BuildExportTab()
        {
            var page = new TabPage("① 地图导出") { Padding = new Padding(8) };
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // ---- left: map list
            var gbMaps = new GroupBox { Text = "检测到的地图（map/mapstudio/*.msb.dcx）", Dock = DockStyle.Fill, Padding = new Padding(6) };
            lstMaps = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            var mapBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight };
            mapBtns.Controls.Add(MakeButton("刷新", (s, e) => RefreshMaps()));
            mapBtns.Controls.Add(MakeButton("全选", (s, e) => CheckAll(true)));
            mapBtns.Controls.Add(MakeButton("全不选", (s, e) => CheckAll(false)));
            gbMaps.Controls.Add(lstMaps);
            gbMaps.Controls.Add(mapBtns);
            split.Controls.Add(gbMaps, 0, 0);

            // ---- right: options
            var gbOpt = new GroupBox { Text = "导出选项", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var opt = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            opt.RowCount = 6;

            opt.Controls.Add(MakeLabel("限制模型数（0 = 全部）"), 0, 0);
            numLimit = new NumericUpDown { Dock = DockStyle.Left, Width = 120, Maximum = 1000000, Minimum = 0 };
            opt.Controls.Add(numLimit, 1, 0);

            chkFog = new CheckBox { Text = "跳过云雾 / 天空壳体（不透明的雾壳会挡住整个场景）", Dock = DockStyle.Fill, Checked = true, Height = 26 };
            opt.Controls.Add(chkFog, 0, 1); opt.SetColumnSpan(chkFog, 2);

            chkBlend = new CheckBox { Text = "导出后立即用 Blender 生成 .blend（每张地图一个）", Dock = DockStyle.Fill, Checked = true, Height = 26 };
            opt.Controls.Add(chkBlend, 0, 2); opt.SetColumnSpan(chkBlend, 2);

            chkOpen = new CheckBox { Text = "全部完成后打开输出目录", Dock = DockStyle.Fill, Height = 26 };
            opt.Controls.Add(chkOpen, 0, 3); opt.SetColumnSpan(chkOpen, 2);

            var runRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 46, Padding = new Padding(0, 8, 0, 0) };
            btnRun = new Button { Text = "开始导出", Width = 150, Height = 34, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnRun.Click += (s, e) => _ = RunAsync();
            btnCancel = new Button { Text = "取消", Width = 90, Height = 34, Enabled = false };
            btnCancel.Click += (s, e) => { try { cts?.Cancel(); } catch { } };
            runRow.Controls.Add(btnRun);
            runRow.Controls.Add(btnCancel);
            opt.Controls.Add(runRow, 0, 4); opt.SetColumnSpan(runRow, 2);

            var note = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 120,
                ForeColor = Color.FromArgb(90, 90, 90),
                Text = "输出结构：\r\n<输出目录>\\<地图名>\\meshes.bin   —— 顶点/法线/UV/索引（已转换到 Blender Z-up）\r\n" +
                       "<输出目录>\\<地图名>\\scene.json   —— 物件摆放（loc / quat / scale）与材质表\r\n" +
                       "<输出目录>\\<地图名>\\tex\\*.dds     —— 材质引用的贴图\r\n" +
                       "<输出目录>\\<地图名>\\<地图名>.blend —— Blender 场景（贴图已打包）"
            };
            opt.Controls.Add(note, 0, 5); opt.SetColumnSpan(note, 2);

            gbOpt.Controls.Add(opt);
            split.Controls.Add(gbOpt, 1, 0);
            page.Controls.Add(split);
            return page;
        }

        TabPage BuildUnpackTab()
        {
            var page = new TabPage("② 解包与校验") { Padding = new Padding(8) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 210f));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

            var info = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Text =
                    "解包（只要地图，用哪种都行）：\r\n" +
                    "1. UXM（推荐，Nordgaren/UXM-Selective-Unpack）\r\n" +
                    "   打开 UXM.exe → Browse 选 sekiro.exe → Unpack 全解包；\r\n" +
                    "   勾 Use Selected Files 可只解 map/、obj/、mtd/、parts/，省几十 GB。\r\n" +
                    "2. Nuxe（本机 tools\\Nuxe-main，解包/打包含 DCX、BHD 处理）\r\n" +
                    "   用法与 UXM 类似，选游戏 exe 后 Unpack。\r\n\r\n" +
                    "只狼地图导出实际只需要这些：\r\n" +
                    "  map\\mapstudio\\m*.msb.dcx     —— 摆放数据（敌人/物件/地图块的位置与旋转）\r\n" +
                    "  map\\m*\\m*_*.mapbnd.dcx       —— 地图块模型（地形、建筑）\r\n" +
                    "  obj\\*.objbnd.dcx             —— 场景物件模型（草木、宝箱、灯笼…）\r\n" +
                    "  mtd\\                         —— 材质定义（决定贴图槽位与默认贴图）\r\n\r\n" +
                    "注意：解包后目录通常是 <游戏根>\\文件\\，本工具默认就指向这里。"
            };
            top.Controls.Add(info, 0, 0);

            var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10, 6, 6, 6) };
            btns.Controls.Add(MakeButton("启动 UXM 解包器", (s, e) => LaunchTool("UXM 解包器")));
            btns.Controls.Add(MakeButton("启动 Nuxe 解包器", (s, e) => LaunchTool("Nuxe 解包器")));
            btns.Controls.Add(MakeButton("打开解包目录", (s, e) => OpenPath(txtRoot.Text)));
            btns.Controls.Add(MakeButton("校验解包完整性", (s, e) => ValidateUnpack()));
            top.Controls.Add(btns, 1, 0);

            listChecks = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            listChecks.Columns.Add("检查项", 260);
            listChecks.Columns.Add("结果", 90);
            listChecks.Columns.Add("说明", 620);
            t.Controls.Add(top, 0, 0);
            t.Controls.Add(listChecks, 0, 1);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildTextureTab()
        {
            var page = new TabPage("③ 贴图工具") { Padding = new Padding(8) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            t.Controls.Add(MakeLabel("贴图目录"), 0, 0);
            txtTexDir = new TextBox { Dock = DockStyle.Fill };
            t.Controls.Add(txtTexDir, 1, 0);
            t.Controls.Add(MakeButton("浏览…", (s, e) => PickFolder(txtTexDir)), 2, 0);

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill };
            btnTex = new Button { Text = "批量 DDS → PNG", Width = 160, Height = 30 };
            btnTex.Click += (s, e) => _ = ConvertTexturesAsync();
            chkKeepDds = new CheckBox { Text = "保留原 DDS", Height = 28, Width = 140 };
            row.Controls.Add(btnTex);
            row.Controls.Add(chkKeepDds);
            t.Controls.Add(row, 0, 1); t.SetColumnSpan(row, 2);

            var tip = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(90, 90, 90),
                Text = "把 DDS 转成不透明 RGB PNG（丢弃 alpha，透明像素用邻近颜色填充，避免纸片 / 树叶变成黑片）。\r\n" +
                       "法线贴图（*_n.dds）按 Non-Color 处理，不做 sRGB 编码。\r\n" +
                       "导出 .blend 时这一步会自动做，这里只是单独处理某一批贴图（比如给其他软件用）。"
            };
            t.Controls.Add(tip, 0, 2); t.SetColumnSpan(tip, 3);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildPreviewTab()
        {
            var page = new TabPage("④ 场景预览") { Padding = new Padding(8) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
            cmbPreview = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            row.Controls.Add(new Label { Text = "已导出的地图：", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
            row.Controls.Add(cmbPreview);
            row.Controls.Add(MakeButton("刷新", (s, e) => RefreshOutputMaps()));
            row.Controls.Add(MakeButton("载入 scene.json", (s, e) => LoadPreview()));
            row.Controls.Add(MakeButton("渲染缩略图 PNG", (s, e) => _ = RenderThumbAsync()));
            t.Controls.Add(row, 0, 0);

            canvas = new MapPreviewCanvas { Dock = DockStyle.Fill };
            t.Controls.Add(canvas, 0, 1);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildToolsTab()
        {
            var page = new TabPage("⑤ 外部工具") { Padding = new Padding(8) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            listTools = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            listTools.Columns.Add("名称", 200);
            listTools.Columns.Add("路径", 860);
            listTools.DoubleClick += (s, e) => LaunchSelectedTool();
            t.Controls.Add(listTools, 0, 0);

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            row.Controls.Add(MakeButton("启动", (s, e) => LaunchSelectedTool()));
            row.Controls.Add(MakeButton("浏览并替换路径", (s, e) => ReplaceToolPath()));
            row.Controls.Add(MakeButton("重新检测", (s, e) => { RefreshTools(); }));
            row.Controls.Add(MakeButton("打开输出目录", (s, e) => OpenPath(txtOut.Text)));
            t.Controls.Add(row, 0, 1);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildHelpTab()
        {
            var page = new TabPage("⑥ 说明") { Padding = new Padding(8) };
            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                Text =
                    "只狼地图导出工具集\r\n\r\n" +
                    "【它做什么】\r\n" +
                    "把解包出来的只狼地图资源（MSBS 摆放 + FLVER2 模型 + MTD 材质 + TPF 贴图）转成 Blender 能直接打开的 .blend：\r\n" +
                    "每个物件的位置、旋转、缩放都和 DSMapStudio 里看到的完全一致，贴图打包进 .blend，丢掉光源和碰撞体。\r\n\r\n" +
                    "【为什么不是直接导 FBX / OBJ】\r\n" +
                    "一张图有 600+ 模型、7000+ 物件、1800 万三角面。任何“先合并再导出”的做法都会爆内存或把模型揉碎，\r\n" +
                    "所以这里走的是：每个模型只保留一份网格数据（meshes.bin），物件靠 scene.json 里的变换重复引用。\r\n\r\n" +
                    "【坐标约定】\r\n" +
                    "MSB 是 X 右 / Y 前 / Z 上（右手系），Blender 是 X 右 / Y 进屏幕 / Z 上。\r\n" +
                    "导出时统一做 (x, z, -y) 转换，四元数用 (w, x, y, z) 顺序写进 scene.json，\r\n" +
                    "所以 build_blend.py 里 Quaternion((q0,q1,q2,q3)) 直接就是对的，不需要再折腾轴向。\r\n\r\n" +
                    "【常见问题】\r\n" +
                    "· 导出报“找不到 msb”：检查解包目录是不是 <Steam>\\steamapps\\common\\Sekiro\\文件，且 map\\mapstudio 里有 m*.msb.dcx。\r\n" +
                    "· 贴图缺一堆：obj 包里的 TPF 名字和材质路径对不上时会漏；先跑一遍“校验解包完整性”，确认 obj / mtd / map 三个目录都在。\r\n" +
                    "· Blender 打不开 / 卡死：用 4.2 以上版本，命令行模式（-b）跑，别在 GUI 里点脚本。\r\n" +
                    "· .blend 里物件变成一坨：说明 scene.json 的 quat 顺序被改过；不要动导出器，那是验证过的顺序。\r\n" +
                    "· 想要低模 / 上 CS2：本目录 历史脚本_CS2 里是会话里做过的整套减面与 vmap 写入脚本，按需取用。\r\n\r\n" +
                    "【命令行】\r\n" +
                    "SekiroMapToolkit.exe --cli --root \"...\\Sekiro\\文件\" --out \"...\\临时文件夹\" --maps m10_00_00_00,m15_00_00_00 --blend\r\n" +
                    "参数：--maps 逗号分隔，省略则导出全部；--limit N 只导前 N 个模型（调试用）；--blend 顺带生成 .blend。"
            };
            page.Controls.Add(rtb);
            return page;
        }

        Control BuildLogPanel()
        {
            var gb = new GroupBox { Text = "运行日志", Dock = DockStyle.Fill, Padding = new Padding(6) };
            log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(20, 22, 26),
                ForeColor = Color.FromArgb(200, 220, 200),
                Font = new Font("Consolas", 9f),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false
            };
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 28, FlowDirection = FlowDirection.RightToLeft };
            row.Controls.Add(MakeButton("清空", (s, e) => log.Clear()));
            row.Controls.Add(MakeButton("打开输出目录", (s, e) => OpenPath(txtOut.Text)));
            gb.Controls.Add(log);
            gb.Controls.Add(row);
            return gb;
        }

        // ------------------------------------------------------------------ helpers

        static Label MakeLabel(string text) => new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false };

        static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = false, Width = 112, Height = 26, Margin = new Padding(3) };
            b.Click += onClick;
            return b;
        }

        void PickFolder(TextBox target)
        {
            using var d = new FolderBrowserDialog { SelectedPath = Directory.Exists(target.Text) ? target.Text : "" };
            if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
        }

        void PickFile(TextBox target, string filter)
        {
            using var d = new OpenFileDialog { Filter = filter };
            if (File.Exists(target.Text)) d.InitialDirectory = Path.GetDirectoryName(target.Text);
            if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.FileName;
        }

        static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        void AutoDetectRoot()
        {
            foreach (var drive in new[] { "E:", "D:", "C:", "F:" })
            {
                var p = Path.Combine(drive + "\\", "SteamLibrary", "steamapps", "common", "Sekiro", "文件");
                if (Directory.Exists(p))
                {
                    txtRoot.Text = p;
                    txtOut.Text = Path.Combine(p, "map", "临时文件夹");
                    Directory.CreateDirectory(txtOut.Text);
                    Log("已定位解包目录：" + p);
                    return;
                }
            }
            Log("[提示] 没找到默认 Steam 路径，请手动选择解包后的“文件”目录。");
        }

        // ------------------------------------------------------------------ log

        void Log(string s)
        {
            if (s == null) return;
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), s); } catch { }
                return;
            }
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = s.StartsWith("[错误]") || s.StartsWith("[FAIL]") ? Color.FromArgb(255, 120, 120)
                               : s.StartsWith("[完成]") || s.StartsWith("[OK]") ? Color.FromArgb(140, 230, 140)
                               : s.StartsWith("[跳过]") || s.StartsWith("[提示]") ? Color.FromArgb(230, 210, 120)
                               : Color.FromArgb(200, 220, 200);
            log.AppendText(line + Environment.NewLine);
            log.ScrollToCaret();
        }

        void SetStatus(string s) { if (!IsDisposed) lblStatus.Text = s; }

        // ------------------------------------------------------------------ settings

        void LoadSettingsIntoUi()
        {
            txtRoot.Text = cfg.GameRoot ?? "";
            txtOut.Text = cfg.OutputRoot ?? "";
            txtBlender.Text = cfg.BlenderPath ?? "";
            chkFog.Checked = cfg.SkipFog;
            chkBlend.Checked = cfg.BuildBlend;
            chkOpen.Checked = cfg.OpenWhenDone;
            numLimit.Value = Math.Max(0, Math.Min(cfg.LimitModels, 1000000));
            if (Directory.Exists(txtOut.Text)) txtTexDir.Text = txtOut.Text;
        }

        void SaveUiIntoSettings()
        {
            cfg.GameRoot = txtRoot.Text.Trim();
            cfg.OutputRoot = txtOut.Text.Trim();
            cfg.BlenderPath = txtBlender.Text.Trim();
            cfg.SkipFog = chkFog.Checked;
            cfg.BuildBlend = chkBlend.Checked;
            cfg.OpenWhenDone = chkOpen.Checked;
            cfg.LimitModels = (int)numLimit.Value;
            cfg.LastMaps = CheckedMaps().ToList();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                if (MessageBox.Show(this, "还在运行，确定退出？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                { e.Cancel = true; return; }
                try { cts?.Cancel(); } catch { }
            }
            SaveUiIntoSettings();
            cfg.Save();
        }

        // ------------------------------------------------------------------ maps

        void RefreshMaps()
        {
            var root = txtRoot.Text.Trim();
            var maps = SekiroExporter.ListMaps(root);
            var keep = new HashSet<string>(cfg.LastMaps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            lstMaps.Items.Clear();
            foreach (var m in maps)
                lstMaps.Items.Add(m, keep.Count == 0 || keep.Contains(m));
            Log("检测到 " + maps.Count + " 张地图" + (maps.Count == 0 ? "（目录不对？）" : "：" + string.Join(", ", maps)));
        }

        void CheckAll(bool on)
        {
            for (int i = 0; i < lstMaps.Items.Count; i++) lstMaps.SetItemChecked(i, on);
        }

        IEnumerable<string> CheckedMaps()
        {
            foreach (var o in lstMaps.CheckedItems) yield return o.ToString();
        }

        // ------------------------------------------------------------------ run

        async Task RunAsync()
        {
            if (running) return;
            var root = txtRoot.Text.Trim();
            var outRoot = txtOut.Text.Trim();
            var blender = txtBlender.Text.Trim();
            var maps = CheckedMaps().ToList();
            var doBlend = chkBlend.Checked;

            if (!Directory.Exists(root)) { Log("[错误] 解包目录不存在：" + root); return; }
            if (maps.Count == 0) { Log("[错误] 先勾选至少一张地图。"); return; }
            if (doBlend && !File.Exists(blender)) { Log("[错误] Blender 路径无效：" + blender); return; }

            Directory.CreateDirectory(outRoot);
            running = true;
            btnRun.Enabled = false;
            btnCancel.Enabled = true;
            cts = new CancellationTokenSource();

            int total = maps.Count * (doBlend ? 2 : 1);
            bar.Style = ProgressBarStyle.Continuous;
            bar.Maximum = total;
            bar.Value = 0;
            var sw = Stopwatch.StartNew();
            int ok = 0, failed = 0;

            try
            {
                foreach (var map in maps)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    SetStatus("导出 " + map + " …");
                    Log("== 导出 " + map + " ==");
                    var stats = await Task.Run(() => SekiroExporter.ExportMap(root, outRoot, map, (int)numLimit.Value, Log, cts.Token));
                    if (stats == null) { failed++; continue; }
                    bar.Value = Math.Min(bar.Maximum, bar.Value + 1);

                    if (doBlend)
                    {
                        var dataDir = Path.Combine(outRoot, map);
                        var blendPath = Path.Combine(dataDir, map + ".blend");
                        SetStatus("Blender 构建 " + map + " …");
                        Log("== Blender 构建 " + map + " ==");
                        var okBlend = await Task.Run(() => BlenderRunner.BuildBlend(blender, Config.ScriptsDir, dataDir, blendPath, Log, cts.Token));
                        if (okBlend) { ok++; Log("[OK] " + blendPath); }
                        else { failed++; Log("[FAIL] " + map + " 的 .blend 没有生成"); }
                        bar.Value = Math.Min(bar.Maximum, bar.Value + 1);
                    }
                    else ok++;
                }
                Log("[完成] 成功=" + ok + " 失败=" + failed + " 用时 " + sw.Elapsed.TotalMinutes.ToString("F1") + " 分钟");
                SetStatus("完成：成功 " + ok + " / 失败 " + failed);
                if (chkOpen.Checked) OpenPath(outRoot);
            }
            catch (OperationCanceledException)
            {
                Log("[提示] 已取消。");
                SetStatus("已取消");
            }
            catch (Exception e)
            {
                Log("[错误] " + e.GetType().Name + "：" + e.Message);
                Log(e.StackTrace ?? "");
                SetStatus("出错：" + e.Message);
            }
            finally
            {
                running = false;
                btnRun.Enabled = true;
                btnCancel.Enabled = false;
                try { cts.Dispose(); } catch { }
                cts = null;
                SaveUiIntoSettings();
                cfg.Save();
                RefreshOutputMaps();
            }
        }

        // ------------------------------------------------------------------ texture

        async Task ConvertTexturesAsync()
        {
            if (running) return;
            var dir = txtTexDir.Text.Trim();
            var blender = txtBlender.Text.Trim();
            if (!Directory.Exists(dir)) { Log("[错误] 贴图目录不存在：" + dir); return; }
            if (!File.Exists(blender)) { Log("[错误] Blender 路径无效：" + blender); return; }

            running = true; btnTex.Enabled = false;
            cts = new CancellationTokenSource();
            bar.Style = ProgressBarStyle.Marquee;
            SetStatus("转换贴图 …");
            try
            {
                var script = Path.Combine(Config.ScriptsDir, "convert_dds.py");
                var args = new List<string> { "-b", "--factory-startup", "--python", script, "--", dir };
                if (chkKeepDds.Checked) args.Add("--keep-dds");
                var code = await Task.Run(() => BlenderRunner.Run(blender, args, Log, cts.Token));
                Log(code == 0 ? "[完成] 贴图转换结束" : "[错误] 贴图转换失败，退出码 " + code);
                SetStatus("贴图转换结束");
            }
            catch (OperationCanceledException) { Log("[提示] 已取消"); }
            catch (Exception e) { Log("[错误] " + e.Message); }
            finally { running = false; btnTex.Enabled = true; bar.Style = ProgressBarStyle.Continuous; bar.Value = 0; }
        }

        // ------------------------------------------------------------------ validate

        void ValidateUnpack()
        {
            var root = txtRoot.Text.Trim();
            listChecks.Items.Clear();
            bool allOk = true;

            void Add(string item, bool ok, string note)
            {
                if (!ok) allOk = false;
                var lvi = new ListViewItem(item);
                lvi.SubItems.Add(ok ? "通过" : "缺少");
                lvi.SubItems.Add(note);
                lvi.ForeColor = ok ? Color.FromArgb(30, 120, 30) : Color.FromArgb(190, 40, 40);
                listChecks.Items.Add(lvi);
            }

            Add("解包根目录", Directory.Exists(root), root);
            var msbs = Directory.Exists(Path.Combine(root, "map", "mapstudio"))
                ? Directory.GetFiles(Path.Combine(root, "map", "mapstudio"), "*.msb.dcx").Length : 0;
            Add("map\\mapstudio\\*.msb.dcx", msbs > 0, "找到 " + msbs + " 个 msb（摆放数据）");

            var objBnds = Directory.Exists(Path.Combine(root, "obj"))
                ? Directory.GetFiles(Path.Combine(root, "obj"), "*.objbnd.dcx").Length : 0;
            Add("obj\\*.objbnd.dcx", objBnds > 0, "找到 " + objBnds + " 个物件包（草木/宝箱/灯笼…）");

            var mtds = Directory.Exists(Path.Combine(root, "mtd"))
                ? Directory.GetFiles(Path.Combine(root, "mtd"), "*.mtd", SearchOption.AllDirectories).Length : 0;
            Add("mtd\\*.mtd", mtds > 0, "找到 " + mtds + " 个材质定义");

            int mapBnds = 0;
            var mapDir = Path.Combine(root, "map");
            if (Directory.Exists(mapDir))
                foreach (var d in Directory.GetDirectories(mapDir))
                    mapBnds += Directory.GetFiles(d, "*.mapbnd.dcx").Length;
            Add("map\\m*\\*.mapbnd.dcx", mapBnds > 0, "找到 " + mapBnds + " 个地图块包（地形/建筑）");

            var oo2 = Path.Combine(Directory.GetParent(root)?.FullName ?? root, "oo2core_6_win64.dll");
            Add("oo2core_6_win64.dll", File.Exists(oo2), "DCX 的 Oodle 解压依赖，缺了读不了 msb / bnd");

            Add("Blender", File.Exists(txtBlender.Text.Trim()), txtBlender.Text.Trim());
            var bs = Path.Combine(Config.ScriptsDir, "build_blend.py");
            Add("build_blend.py", File.Exists(bs), bs);

            Log(allOk ? "[完成] 解包校验通过，可以直接导出。" : "[提示] 校验有缺项，按“说明”列补齐后再导出。");
            SetStatus(allOk ? "校验通过" : "校验发现缺项");
        }

        // ------------------------------------------------------------------ preview

        void RefreshOutputMaps()
        {
            cmbPreview.Items.Clear();
            var outRoot = txtOut.Text.Trim();
            if (!Directory.Exists(outRoot)) return;
            foreach (var d in Directory.GetDirectories(outRoot).OrderBy(x => x))
            {
                if (File.Exists(Path.Combine(d, "scene.json")))
                    cmbPreview.Items.Add(Path.GetFileName(d));
            }
            if (cmbPreview.Items.Count > 0 && cmbPreview.SelectedIndex < 0) cmbPreview.SelectedIndex = 0;
        }

        void LoadPreview()
        {
            if (cmbPreview.SelectedItem == null) { Log("[提示] 还没有导出过地图。"); return; }
            var map = cmbPreview.SelectedItem.ToString();
            var dir = Path.Combine(txtOut.Text.Trim(), map);
            var scene = SekiroExporter.LoadScene(dir);
            if (scene == null) { Log("[错误] 读不了 " + Path.Combine(dir, "scene.json")); return; }
            canvas.SetScene(scene, map + "  " + scene.stats?.objectsPlaced + " 物件 / " + scene.stats?.models + " 模型");
            Log("已载入 " + map + "：物件=" + scene.objects?.Count + " 网格=" + scene.meshCount +
                " 顶点=" + scene.stats?.vertices + " 贴图=" + scene.stats?.textures);
        }

        async Task RenderThumbAsync()
        {
            if (cmbPreview.SelectedItem == null) { Log("[提示] 先选一张地图。"); return; }
            var map = cmbPreview.SelectedItem.ToString();
            var dir = Path.Combine(txtOut.Text.Trim(), map);
            var blend = Path.Combine(dir, map + ".blend");
            if (!File.Exists(blend)) { Log("[提示] 先生成 .blend：" + blend); return; }
            var png = Path.Combine(dir, map + "_preview.png");
            SetStatus("渲染缩略图 …");
            var ok = await Task.Run(() => BlenderRunner.RenderPreview(txtBlender.Text.Trim(), Config.ScriptsDir, blend, png, Log, CancellationToken.None));
            if (ok)
            {
                Log("[完成] " + png);
                OpenPath(png);
            }
            else Log("[错误] 渲染失败");
            SetStatus("就绪");
        }

        // ------------------------------------------------------------------ tools

        void RefreshTools()
        {
            listTools.Items.Clear();
            foreach (var t in cfg.Tools.OrderBy(t => t.Name))
            {
                var lvi = new ListViewItem(t.Name);
                lvi.SubItems.Add(t.Path ?? "");
                lvi.Tag = t;
                if (string.IsNullOrEmpty(t.Path) || !File.Exists(t.Path)) lvi.ForeColor = Color.FromArgb(190, 60, 60);
                listTools.Items.Add(lvi);
            }
        }

        void LaunchTool(string name)
        {
            var t = cfg.Tools.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (t == null || string.IsNullOrEmpty(t.Path) || !File.Exists(t.Path))
            { Log("[错误] 没有找到 " + name + "，去“⑤ 外部工具”里指定路径。"); return; }
            try { Process.Start(new ProcessStartInfo(t.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(t.Path) }); Log("启动 " + name); }
            catch (Exception e) { Log("[错误] 启动失败：" + e.Message); }
        }

        void LaunchSelectedTool()
        {
            if (listTools.SelectedItems.Count == 0) return;
            var t = listTools.SelectedItems[0].Tag as ToolEntry;
            if (t == null || string.IsNullOrEmpty(t.Path) || !File.Exists(t.Path))
            { Log("[错误] 路径无效，先“浏览并替换路径”。"); return; }
            try { Process.Start(new ProcessStartInfo(t.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(t.Path) }); Log("启动 " + t.Name); }
            catch (Exception e) { Log("[错误] 启动失败：" + e.Message); }
        }

        void ReplaceToolPath()
        {
            if (listTools.SelectedItems.Count == 0) return;
            var t = listTools.SelectedItems[0].Tag as ToolEntry;
            using var d = new OpenFileDialog { Filter = "可执行文件|*.exe|所有文件|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                t.Path = d.FileName;
                cfg.Save();
                RefreshTools();
                Log(t.Name + " → " + t.Path);
            }
        }
    }
}