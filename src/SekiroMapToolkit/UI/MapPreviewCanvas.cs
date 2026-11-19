using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SekiroMapToolkit.Core;

namespace SekiroMapToolkit.UI
{
    // top-down 2D scatter of every placed object, drawn from scene.json.
    // useful to eyeball "did the map rotate / mirror / run off to infinity" in one second.
    class MapPreviewCanvas : Control
    {
        SceneDef scene;
        public string Caption { get; private set; } = "";

        public MapPreviewCanvas()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 26, 30);
            ResizeRedraw = true;
        }

        public void SetScene(SceneDef s, string caption)
        {
            scene = s;
            Caption = caption ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            if (scene?.objects == null || scene.objects.Count == 0)
            {
                TextRenderer.DrawText(g, "没有场景数据", Font, ClientRectangle, Color.Gray,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // collect XY of placements
            int n = scene.objects.Count;
            var xs = new float[n];
            var ys = new float[n];
            float minx = float.MaxValue, maxx = float.MinValue, miny = float.MaxValue, maxy = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var o = scene.objects[i];
                float x = o.loc?[0] ?? 0, y = o.loc?[1] ?? 0;
                xs[i] = x; ys[i] = y;
                if (x < minx) minx = x;
                if (x > maxx) maxx = x;
                if (y < miny) miny = y;
                if (y > maxy) maxy = y;
            }
            // clip extreme outliers (skyboxes) to the 1..99 percentile box
            var sx = Percentiles(xs, 1, 99);
            var sy = Percentiles(ys, 1, 99);
            float bx0 = sx.Item1, bx1 = sx.Item2, by0 = sy.Item1, by1 = sy.Item2;

            var pad = 28f;
            var rect = new RectangleF(pad, pad, Math.Max(ClientSize.Width - pad * 2, 10), Math.Max(ClientSize.Height - pad * 2 - 22, 10));
            float w = Math.Max(bx1 - bx0, 1f), h = Math.Max(by1 - by0, 1f);
            float k = Math.Min(rect.Width / w, rect.Height / h);

            Func<float, float, PointF> map = (x, y) => new PointF(
                rect.X + (x - bx0) * k + (rect.Width - w * k) / 2f,
                rect.Bottom - (y - by0) * k - (rect.Height - h * k) / 2f);

            // grid
            using (var pen = new Pen(Color.FromArgb(40, 60, 70, 80)))
            {
                for (int i = 0; i <= 10; i++)
                {
                    float gx = rect.X + rect.Width * i / 10f;
                    float gy = rect.Y + rect.Height * i / 10f;
                    g.DrawLine(pen, gx, rect.Y, gx, rect.Bottom);
                    g.DrawLine(pen, rect.X, gy, rect.Right, gy);
                }
            }

            // objects, colour by material class (map piece vs object) guessed from name
            int drawn = 0;
            int stride = n > 40000 ? n / 40000 : 1;
            for (int i = 0; i < n; i += stride)
            {
                var o = scene.objects[i];
                var p = map(xs[i], ys[i]);
                bool mapPiece = (o.name ?? "").StartsWith("m", StringComparison.OrdinalIgnoreCase)
                                && (o.name ?? "").Length > 1 && char.IsDigit((o.name ?? "")[1]);
                using (var b = new SolidBrush(mapPiece ? Color.FromArgb(190, 90, 170, 230) : Color.FromArgb(200, 240, 180, 90)))
                    g.FillRectangle(b, p.X - 1.5f, p.Y - 1.5f, 3f, 3f);
                drawn++;
            }

            // border + legend
            using (var pen = new Pen(Color.FromArgb(80, 200, 200, 200)))
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

            var legend = string.Format("{0}   物件={1}   地图块={2}   绘制={3}   包围盒 {4:F0}..{5:F0} / {6:F0}..{7:F0}",
                Caption, n, scene.meshCount, drawn, bx0, bx1, by0, by1);
            TextRenderer.DrawText(g, legend, Font, new Point(8, ClientSize.Height - 22), Color.FromArgb(210, 210, 210));
        }

        static (float, float) Percentiles(float[] v, double p0, double p1)
        {
            var c = (float[])v.Clone();
            Array.Sort(c);
            int i0 = (int)Math.Round((p0 / 100.0) * (c.Length - 1));
            int i1 = (int)Math.Round((p1 / 100.0) * (c.Length - 1));
            return (c[Math.Max(0, Math.Min(c.Length - 1, i0))], c[Math.Max(0, Math.Min(c.Length - 1, i1))]);
        }
    }
}