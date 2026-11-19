using System;
using System.Linq;
using System.Text;
using Avalonia;

namespace SekiroMapToolkit
{
    static class Program
    {
        // set via --shot <png> for a headless UI self-test
        public static string ShotPath;
        public static int ShotPage = -1;
        // set via --chr c1010: 截图时直接选这个角色
        public static string ShotChr;
        // --play：截图前先播一小段，验证实时播放
        public static bool ShotPlay;
        // --tab：截图时切到哪个标签页（0 参数 / 1 实体 / 2 日志）
        public static int ShotTab = -1;

        [STAThread]
        public static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Core.Oodle.Ensure();

            if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = Cli.Run(args);
                return;
            }

            int i = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < args.Length) ShotPath = args[i + 1];
            int pi = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
            if (pi >= 0 && pi + 1 < args.Length) int.TryParse(args[pi + 1], out ShotPage);

            int ci = Array.FindIndex(args, a => a.Equals("--chr", StringComparison.OrdinalIgnoreCase));
            if (ci >= 0 && ci + 1 < args.Length) ShotChr = args[ci + 1];

            ShotPlay = Array.Exists(args, a => a.Equals("--play", StringComparison.OrdinalIgnoreCase));

            int ti = Array.FindIndex(args, a => a.Equals("--tab", StringComparison.OrdinalIgnoreCase));
            if (ti >= 0 && ti + 1 < args.Length) int.TryParse(args[ti + 1], out ShotTab);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}