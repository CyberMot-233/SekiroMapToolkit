using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SekiroMapToolkit.Core
{
    /// <summary>一个可渲染的三角形集合（已展开成世界坐标的三角形缓存）。</summary>
    class RenderMesh
    {
        public string Name = "";
        public Vector3[] Positions = Array.Empty<Vector3>();
        public Vector3[] Normals = Array.Empty<Vector3>();
        public Vector2[] Uvs = Array.Empty<Vector2>();
        public int[] Indices = Array.Empty<int>();
        public int[] BoneIndices = Array.Empty<int>();     // 每顶点主骨
        public Vector3 Color = new Vector3(0.78f, 0.74f, 0.68f);
        public bool Visible = true;

        /// <summary>漫反射贴图；为 null 时用 Color 平涂。</summary>
        public DdsImage Texture;
    }

    /// <summary>轨道相机。</summary>
    class OrbitCamera
    {
        public Vector3 Target = Vector3.Zero;
        public float Yaw = 0.6f;       // 弧度
        public float Pitch = 0.25f;
        public float Distance = 3.0f;
        public float FovDeg = 45f;
        public float Near = 0.02f;
        public float Far = 400f;

        /// <summary>
        /// 上方向。默认 Z-up（只狼的 ChrMesh.Positions 经 Axis.ToBlender 后是 Z 朝上）。
        /// 如果渲染的是 Y-up 数据（FLVER 原始空间），把它设成 UnitY。
        /// </summary>
        public Vector3 Up = Vector3.UnitZ;

        public Vector3 Eye
        {
            get
            {
                // 绕 Up 轴环绕：Z-up 时用 (sin,cos) 铺在 XY 平面，Y 是高度
                if (MathF.Abs(Up.Z) > 0.5f)
                {
                    var cp = MathF.Cos(Pitch);
                    return Target + new Vector3(
                        MathF.Sin(Yaw) * cp * Distance,
                        -MathF.Cos(Yaw) * cp * Distance,
                        MathF.Sin(Pitch) * Distance);
                }
                var cpY = MathF.Cos(Pitch);
                return Target + new Vector3(
                    MathF.Sin(Yaw) * cpY * Distance,
                    MathF.Sin(Pitch) * Distance,
                    MathF.Cos(Yaw) * cpY * Distance);
            }
        }

        public Matrix4x4 View =>
            Matrix4x4.CreateLookAt(Eye, Target, Up);

        public Matrix4x4 Projection(float aspect)
        {
            var f = 1f / MathF.Tan(FovDeg * MathF.PI / 360f);
            var m = new Matrix4x4
            {
                M11 = f / aspect,
                M22 = f,
                M33 = (Far + Near) / (Near - Far),
                M34 = -1f,
                M43 = 2f * Far * Near / (Near - Far),
                M44 = 0f,
            };
            return m;
        }

        /// <summary>绕目标点转。</summary>
        public void Rotate(float dx, float dy)
        {
            Yaw -= dx * 0.008f;
            Pitch += dy * 0.008f;
            float lim = MathF.PI / 2f - 0.02f;
            if (Pitch > lim) Pitch = lim;
            if (Pitch < -lim) Pitch = -lim;
        }

        public void Zoom(float delta)
        {
            Distance *= MathF.Pow(0.9f, delta);
            if (Distance < 0.05f) Distance = 0.05f;
            if (Distance > 500f) Distance = 500f;
        }

        public void Pan(float dx, float dy, float aspect)
        {
            var fwd = Vector3.Normalize(Target - Eye);
            var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
            var up = Vector3.Cross(right, fwd);
            float k = Distance * 0.0016f;
            Target += right * (-dx * k) + up * (dy * k);
        }

        /// <summary>把镜头框到这批网格上。</summary>
        public void FrameTo(IEnumerable<RenderMesh> meshes)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            bool any = false;
            foreach (var m in meshes)
            {
                if (m == null || !m.Visible) continue;
                foreach (var p in m.Positions)
                {
                    any = true;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            if (!any) { Target = Vector3.Zero; Distance = 3f; return; }

            Target = (min + max) * 0.5f;
            float r = (max - min).Length() * 0.5f;
            if (r < 1e-4f) r = 0.5f;
            Distance = r / MathF.Tan(FovDeg * MathF.PI / 360f) * 1.35f;
            Near = r * 0.002f; if (Near < 0.001f) Near = 0.001f;
            Far = r * 20f + 50f;
        }

        /// <summary>屏幕坐标射线（用于拾取，目前给骨骼高亮留接口）。</summary>
        internal (Vector3 origin, Vector3 dir) ScreenRay(float sx, float sy, float w, float h)
        {
            float nx = (2f * sx / w - 1f);
            float ny = (1f - 2f * sy / h);
            float th = MathF.Tan(FovDeg * MathF.PI / 360f);
            var fwd = Vector3.Normalize(Target - Eye);
            var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
            var up = Vector3.Cross(right, fwd);
            var dir = Vector3.Normalize(fwd + right * (nx * th * (w / h)) + up * (ny * th));
            return (Eye, dir);
        }
    }

    /// <summary>
    /// 纯软件 3D 渲染器：z-buffer 光栅化，不依赖 OpenGL / DirectX。
    ///
    /// 为什么自己写：这台机器上要保证单文件 EXE 拆包即用，
    /// 拉 OpenGL/Vulkan 绑定的部署成本比几行光栅化高得多。
    /// 目标不是 3A 画质，是「能转着看模型和动画」。
    /// </summary>
    class SoftRenderer
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public uint[] Pixels { get; private set; } = Array.Empty<uint>();
        public int[] DepthBits { get; private set; } = Array.Empty<int>();

        // 显示开关
        public bool Wireframe;
        public bool ShowBones = true;
        public bool FlatShading;
        /// <summary>0=不剔除 1=只画正面(负面积) 2=只画背面(正面积)</summary>
        public int CullMode = 1;
        public Vector3 Background = new Vector3(0.055f, 0.075f, 0.095f);
        public Vector3 LightDir = Vector3.Normalize(new Vector3(-0.4f, -0.7f, 0.55f));
        public float Ambient = 0.30f;
        public float Gain = 1.15f;

        public void Resize(int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            if (w == Width && h == Height && Pixels.Length == w * h) return;
            Width = w; Height = h;
            Pixels = new uint[w * h];
            DepthBits = new int[w * h];
        }

        public void Clear()
        {
            uint bg = Pack(Background);
            var px = Pixels;
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            var dp = DepthBits;
            for (int i = 0; i < dp.Length; i++) dp[i] = int.MinValue;
        }

        /// <summary>渲染一批网格。</summary>
        public void Render(IReadOnlyList<RenderMesh> meshes, OrbitCamera cam, float yawOffset = 0f)
        {
            if (Width == 0 || Height == 0) return;
            Clear();

            float aspect = (float)Width / Height;
            var vp = cam.View * cam.Projection(aspect);

            var active = new List<RenderMesh>(meshes.Count);
            foreach (var m in meshes)
            {
                if (m == null || !m.Visible) continue;
                if (m.Positions.Length == 0 || m.Indices.Length < 3) continue;
                active.Add(m);
            }
            if (active.Count == 0) return;

            // ---- 第一步：顶点变换 + 三角形装配。按网格并行，每个网格写自己的槽位 ----
            var perMesh = new List<Tri>[active.Count];
            Parallel.For(0, active.Count, i => { perMesh[i] = BuildTris(active[i], vp); });

            // 摊平成一个大数组
            int total = 0;
            for (int i = 0; i < perMesh.Length; i++) total += perMesh[i].Count;
            if (total == 0) return;

            var tris = new Tri[total];
            int o = 0;
            for (int i = 0; i < perMesh.Length; i++)
            {
                var src = perMesh[i];
                src.CopyTo(tris, o);
                o += src.Count;
            }

            // ---- 第二步：按屏幕 y 范围分桶，每桶一个线程独立填充 ----
            // 关键：先把三角形分到它覆盖的行带里，各线程只碰自己桶里的，
            // 否则每个线程都要遍历全部 59.7 万个三角形做剔除（24 核 = 1400 万次空转）。
            int bands = Math.Clamp(Environment.ProcessorCount, 1, 32);
            int rows = (Height + bands - 1) / bands;

            if (bands == 1)
            {
                for (int i = 0; i < tris.Length; i++) FillTri(tris[i], 0, Height - 1, null);
                return;
            }

            var buckets = new List<int>[bands];
            for (int i = 0; i < bands; i++) buckets[i] = new List<int>(tris.Length / bands + 64);

            for (int i = 0; i < tris.Length; i++)
            {
                int b0 = (int)(tris[i].MinY / rows);
                int b1 = (int)(tris[i].MaxY / rows);
                if (b0 < 0) b0 = 0;
                if (b1 >= bands) b1 = bands - 1;
                if (b1 < b0) continue;
                for (int b = b0; b <= b1; b++) buckets[b].Add(i);
            }

            Parallel.For(0, bands, bi =>
            {
                int y0 = bi * rows;
                int y1 = Math.Min(Height - 1, y0 + rows - 1);
                if (y0 > y1) return;
                var bucket = buckets[bi];
                for (int k = 0; k < bucket.Count; k++)
                    FillTri(tris[bucket[k]], y0, y1, null);
            });
        }

        /// <summary>一个已经算到屏幕空间的三角形。</summary>
        struct Tri
        {
            public SP A, B, C;
            public Vector3 W;          // 三顶点的 w，用于深度
            public Vector2 Ua, Ub, Uc; // UV
            public uint Color;         // 无贴图时的固定色
            public DdsImage Tex;
            public float Shade;
            public Vector3 Tint;
            public bool Textured;
            public float MinY, MaxY;   // 屏幕空间 y 范围，用于行带早退
        }

        /// <summary>顶点变换 + 背面剔除，产出一个网格的所有屏幕空间三角形。</summary>
        List<Tri> BuildTris(RenderMesh m, Matrix4x4 vp)
        {
            int n = m.Positions.Length;
            var cache = new Vector4[n];
            for (int i = 0; i < n; i++)
                cache[i] = Vector4.Transform(new Vector4(m.Positions[i], 1f), vp);

            var outList = new List<Tri>(m.Indices.Length / 3 + 8);
            var idx = m.Indices;
            bool textured = m.Texture != null;

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
                if ((uint)ia >= (uint)n || (uint)ib >= (uint)n || (uint)ic >= (uint)n) continue;

                var ca = cache[ia]; var cb = cache[ib]; var cc = cache[ic];
                if (ca.W <= 0.0001f && cb.W <= 0.0001f && cc.W <= 0.0001f) continue;

                var sa = ToScreen(ca); var sb = ToScreen(cb); var sc = ToScreen(cc);
                if (!sa.Ok || !sb.Ok || !sc.Ok) continue;

                float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sc.X - sa.X) * (sb.Y - sa.Y);
                if (CullMode == 1 && area > -0.001f) continue;
                if (CullMode == 2 && area < 0.001f) continue;
                if (MathF.Abs(area) < 0.001f) continue;

                float minY = MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y));
                float maxY = MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y));
                if (maxY < 0f || minY > Height) continue;

                float ndl = 1f;
                if (!FlatShading)
                {
                    var na = ia < m.Normals.Length ? m.Normals[ia] : Vector3.UnitY;
                    var nb = ib < m.Normals.Length ? m.Normals[ib] : Vector3.UnitY;
                    var nc = ic < m.Normals.Length ? m.Normals[ic] : Vector3.UnitY;
                    var nrm = na + nb + nc;
                    float len = nrm.Length();
                    if (len > 1e-6f) nrm /= len;
                    ndl = MathF.Abs(Vector3.Dot(nrm, -LightDir));
                }
                float sh = Ambient + (1f - Ambient) * ndl;
                sh *= Gain;
                if (textured && sh < 0.78f) sh = 0.78f;
                if (sh > 1.45f) sh = 1.45f;

                var tri = new Tri
                {
                    A = sa, B = sb, C = sc,
                    W = new Vector3(ca.W, cb.W, cc.W),
                    Tex = m.Texture,
                    Shade = sh,
                    Tint = m.Color,
                    Textured = textured,
                    MinY = minY,
                    MaxY = maxY,
                };

                if (textured)
                {
                    tri.Ua = ia < m.Uvs.Length ? m.Uvs[ia] : Vector2.Zero;
                    tri.Ub = ib < m.Uvs.Length ? m.Uvs[ib] : Vector2.Zero;
                    tri.Uc = ic < m.Uvs.Length ? m.Uvs[ic] : Vector2.Zero;
                }
                else
                {
                    tri.Color = Pack(new Vector3(
                        MathF.Min(1f, m.Color.X * sh),
                        MathF.Min(1f, m.Color.Y * sh),
                        MathF.Min(1f, m.Color.Z * sh)));
                }

                outList.Add(tri);
            }
            return outList;
        }

        void FillTri(Tri t, int bandY0, int bandY1, object _)
        {
            if (t.MaxY < bandY0 || t.MinY > bandY1) return;
            if (t.Textured)
                RasterizeTextured(t.A, t.B, t.C, t.W, t.Ua, t.Ub, t.Uc, t.Tex, t.Shade, t.Tint, bandY0, bandY1);
            else
                Rasterize(t.A, t.B, t.C, t.W, t.Color, bandY0, bandY1);
        }

        struct Vtx
        {
            public Vector4 Clip;
            public Vector3 World;
            public Vector3 Normal;
            public Vector2 Uv;
        }

        void DrawMesh(RenderMesh m, Matrix4x4 vp, OrbitCamera cam, int bandY0, int bandY1)
        {
            int n = m.Positions.Length;
            var cache = new Vtx[n];
            for (int i = 0; i < n; i++)
            {
                var wp = m.Positions[i];
                var clip = Vector4.Transform(new Vector4(wp, 1f), vp);
                cache[i] = new Vtx
                {
                    Clip = clip,
                    World = wp,
                    Normal = i < m.Normals.Length ? m.Normals[i] : Vector3.UnitY,
                    Uv = i < m.Uvs.Length ? m.Uvs[i] : Vector2.Zero,
                };
            }

            var idx = m.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;
                DrawTri(cache[a], cache[b], cache[c], m.Color, m.Texture, bandY0, bandY1);
            }
        }

        void DrawTri(Vtx a, Vtx b, Vtx c, Vector3 color, DdsImage tex, int bandY0, int bandY1)
        {
            // 简单近平面裁剪：三个点都在后面就丢掉
            if (a.Clip.W <= 0.0001f && b.Clip.W <= 0.0001f && c.Clip.W <= 0.0001f) return;

            var sa = ToScreen(a.Clip);
            var sb = ToScreen(b.Clip);
            var sc = ToScreen(c.Clip);
            if (!sa.Ok || !sb.Ok || !sc.Ok) return;

            // 整个三角形都不在本线程的行带里，直接跳过
            float tminY = MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y));
            float tmaxY = MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y));
            if (tmaxY < bandY0 || tminY > bandY1) return;

            // 背面剔除（屏幕空间有向面积）
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sc.X - sa.X) * (sb.Y - sa.Y);
            if (CullMode == 1 && area > -0.001f) return;   // 只画负面积
            if (CullMode == 2 && area < 0.001f) return;    // 只画正面积
            if (MathF.Abs(area) < 0.001f) return;

            float ndl = 1f;
            if (!FlatShading)
            {
                var nrm = Vector3.Normalize(a.Normal + b.Normal + c.Normal);
                ndl = MathF.Abs(Vector3.Dot(nrm, -LightDir));
            }
            float sh = Ambient + (1f - Ambient) * ndl;
            sh *= Gain;
            // 有贴图时抬高暗面，避免背光面糊成一团黑
            if (tex != null && sh < 0.78f) sh = 0.78f;
            if (sh > 1.45f) sh = 1.45f;

            // 没有贴图：整面一个颜色
            if (tex == null)
            {
                uint flat = Pack(new Vector3(
                    MathF.Min(1f, color.X * sh),
                    MathF.Min(1f, color.Y * sh),
                    MathF.Min(1f, color.Z * sh)));
                Rasterize(sa, sb, sc, new Vector3(a.Clip.W, b.Clip.W, c.Clip.W), flat, bandY0, bandY1);
                return;
            }

            // 有贴图：带上 UV 走纹理采样（1/w 透视校正）
            var wa = new Vector3(a.Clip.W, b.Clip.W, c.Clip.W);
            RasterizeTextured(sa, sb, sc, wa, a.Uv, b.Uv, c.Uv, tex, sh, color, bandY0, bandY1);
        }

        /// <summary>带贴图的光栅化：透视校正 UV + 双线性采样。</summary>
        void RasterizeTextured(SP a, SP b, SP c, Vector3 ws, Vector2 uva, Vector2 uvb, Vector2 uvc,
                               DdsImage tex, float shade, Vector3 tint, int bandY0, int bandY1)
        {
            int minX = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)));
            int maxX = (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)));
            int minY = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
            int maxY = (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));

            if (minX < 0) minX = 0;
            if (minY < 0) minY = 0;
            if (maxX > Width - 1) maxX = Width - 1;
            if (maxY > Height - 1) maxY = Height - 1;
            // 只画属于本线程的行带
            if (minY < bandY0) minY = bandY0;
            if (maxY > bandY1) maxY = bandY1;
            if (minX > maxX || minY > maxY) return;

            float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (MathF.Abs(area) < 1e-7f) return;
            float invArea = 1f / area;

            // 透视校正：UV 和 w 一起插值
            var iw = new Vector3(1f / ws.X, 1f / ws.Y, 1f / ws.Z);
            var uv0 = uva * iw.X;
            var uv1 = uvb * iw.Y;
            var uv2 = uvc * iw.Z;

            var px = Pixels;
            var dp = DepthBits;

            for (int y = minY; y <= maxY; y++)
            {
                float fy = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float fx = x + 0.5f;

                    float l0 = ((b.X - fx) * (c.Y - fy) - (c.X - fx) * (b.Y - fy)) * invArea;
                    if (l0 < -0.0001f) continue;
                    float l1 = ((c.X - fx) * (a.Y - fy) - (a.X - fx) * (c.Y - fy)) * invArea;
                    if (l1 < -0.0001f) continue;
                    float l2 = 1f - l0 - l1;
                    if (l2 < -0.0001f) continue;

                    float depth = l0 * iw.X + l1 * iw.Y + l2 * iw.Z;
                    int i = y * Width + x;
                    if (depth <= dp[i]) continue;
                    dp[i] = (int)(depth * 1e6f);

                    float iwSum = l0 * iw.X + l1 * iw.Y + l2 * iw.Z;
                    if (iwSum <= 1e-8f) continue;
                    float inv = 1f / iwSum;

                    float u = (l0 * uv0.X + l1 * uv1.X + l2 * uv2.X) * inv;
                    float v = (l0 * uv0.Y + l1 * uv1.Y + l2 * uv2.Y) * inv;

                    var tc = tex.Sample(u, v);
                    px[i] = Pack(new Vector3(
                        MathF.Min(1f, tc.X * tint.X * shade),
                        MathF.Min(1f, tc.Y * tint.Y * shade),
                        MathF.Min(1f, tc.Z * tint.Z * shade)));
                }
            }
        }

        struct SP { public float X, Y, W; public bool Ok; }

        SP ToScreen(Vector4 clip)
        {
            var r = new SP();
            if (clip.W <= 0.0001f) { r.Ok = false; return r; }
            float invW = 1f / clip.W;
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            // 太远太偏的三角形直接丢，防止数值炸
            if (float.IsNaN(ndcX) || float.IsNaN(ndcY)) { r.Ok = false; return r; }
            r.X = (ndcX * 0.5f + 0.5f) * Width;
            r.Y = (0.5f - ndcY * 0.5f) * Height;
            r.W = clip.W;
            r.Ok = true;
            return r;
        }

        void Rasterize(SP a, SP b, SP c, Vector3 ws, uint color, int bandY0, int bandY1)
        {
            int minX = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)));
            int maxX = (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)));
            int minY = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
            int maxY = (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));

            if (minX < 0) minX = 0;
            if (minY < 0) minY = 0;
            if (maxX > Width - 1) maxX = Width - 1;
            if (maxY > Height - 1) maxY = Height - 1;
            // 只画属于本线程的行带
            if (minY < bandY0) minY = bandY0;
            if (maxY > bandY1) maxY = bandY1;
            if (minX > maxX || minY > maxY) return;

            // 面积（已保证为负 = 正面）
            float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (MathF.Abs(area) < 1e-7f) return;
            float invArea = 1f / area;

            float w0 = ws.X, w1 = ws.Y, w2 = ws.Z;

            var px = Pixels;
            var dp = DepthBits;

            for (int y = minY; y <= maxY; y++)
            {
                float fy = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float fx = x + 0.5f;

                    float l0 = ((b.X - fx) * (c.Y - fy) - (c.X - fx) * (b.Y - fy)) * invArea;
                    if (l0 < -0.0001f) continue;
                    float l1 = ((c.X - fx) * (a.Y - fy) - (a.X - fx) * (c.Y - fy)) * invArea;
                    if (l1 < -0.0001f) continue;
                    float l2 = 1f - l0 - l1;
                    if (l2 < -0.0001f) continue;

                    // 深度：用屏幕空间重心插值 1/w，越大越近
                    float depth = l0 * (1f / w0) + l1 * (1f / w1) + l2 * (1f / w2);
                    int i = y * Width + x;
                    if (depth <= dp[i]) continue;
                    dp[i] = (int)(depth * 1e6f);
                    px[i] = color;
                }
            }
        }

        /// <summary>画骨骼线框（骨架 + 当前帧姿势）。</summary>
        public void DrawSkeleton(DecodedSkeleton skel, Vector3[] worldPositions, OrbitCamera cam,
                                 Vector3 color, float thickness = 1.6f)
        {
            if (skel == null || worldPositions == null) return;
            var vp = cam.View * cam.Projection((float)Width / Height);
            uint col = Pack(color);

            for (int i = 0; i < worldPositions.Length && i < skel.Parents.Length; i++)
            {
                int p = skel.Parents[i];
                if (p < 0 || p >= worldPositions.Length) continue;
                DrawLine3D(worldPositions[i], worldPositions[p], vp, col, thickness);
            }
            // 关节小球
            foreach (var wp in worldPositions)
                DrawJoint(wp, vp, Pack(new Vector3(0.95f, 0.75f, 0.25f)));
        }

        void DrawJoint(Vector3 wp, Matrix4x4 vp, uint col)
        {
            var clip = Vector4.Transform(new Vector4(wp, 1f), vp);
            var s = ToScreen(clip);
            if (!s.Ok) return;
            int cx = (int)s.X, cy = (int)s.Y;
            int r = 2;
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    int px2 = cx + x, py2 = cy + y;
                    if (px2 < 0 || py2 < 0 || px2 >= Width || py2 >= Height) continue;
                    int i = py2 * Width + px2;
                    Pixels[i] = col;
                }
        }

        void DrawLine3D(Vector3 p0, Vector3 p1, Matrix4x4 vp, uint col, float thickness)
        {
            var c0 = Vector4.Transform(new Vector4(p0, 1f), vp);
            var c1 = Vector4.Transform(new Vector4(p1, 1f), vp);
            if (c0.W <= 0.0001f && c1.W <= 0.0001f) return;

            var s0 = ToScreen(c0);
            var s1 = ToScreen(c1);
            if (!s0.Ok || !s1.Ok) return;

            float x0 = s0.X, y0 = s0.Y, x1 = s1.X, y1 = s1.Y;
            float dx = x1 - x0, dy = y1 - y0;
            int steps = (int)MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
            if (steps <= 0) steps = 1;
            if (steps > 4000) steps = 4000;

            float sx = dx / steps, sy = dy / steps;
            float w0 = 1f / c0.W, w1 = 1f / c1.W;

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float depth = w0 + (w1 - w0) * t;
                int cx = (int)(x0 + sx * i);
                int cy = (int)(y0 + sy * i);

                int rr = (int)MathF.Ceiling(thickness);
                for (int oy = -rr; oy <= rr; oy++)
                    for (int ox = -rr; ox <= rr; ox++)
                    {
                        int px2 = cx + ox, py2 = cy + oy;
                        if (px2 < 0 || py2 < 0 || px2 >= Width || py2 >= Height) continue;
                        int di = py2 * Width + px2;
                        if (depth * 1e6f <= DepthBits[di]) continue;
                        DepthBits[di] = (int)(depth * 1e6f);
                        Pixels[di] = col;
                    }
            }
        }

        /// <summary>
        /// 画地面网格（DSAnimStudio 那种绿黄坐标轴 + 灰色格子）。
        /// 只在 Z=0 平面上画，网格间距自适应。
        /// </summary>
        public void DrawGrid(OrbitCamera cam, float extent = 4f, int divisions = 16,
                             float planeY = 0f,
                             Vector3? gridColor = null, Vector3? axisX = null, Vector3? axisY = null)
        {
            var vp = cam.View * cam.Projection((float)Width / Height);
            var g = gridColor ?? new Vector3(0.22f, 0.26f, 0.24f);
            var cx = axisX ?? new Vector3(0.72f, 0.28f, 0.24f);   // X 轴偏红
            var cy = axisY ?? new Vector3(0.35f, 0.68f, 0.32f);   // Y 轴偏绿

            float step = extent * 2f / Math.Max(1, divisions);
            var col = Pack(g);
            var colX = Pack(cx);
            var colY = Pack(cy);

            // 平行 X 轴的线
            for (int i = -divisions; i <= divisions; i++)
            {
                float y = i * step;
                bool isAxis = MathF.Abs(y) < 1e-4f;
                DrawWorldLine(vp, new Vector3(-extent, y, planeY), new Vector3(extent, y, planeY),
                              isAxis ? colX : col);
            }
            // 平行 Y 轴的线
            for (int i = -divisions; i <= divisions; i++)
            {
                float x = i * step;
                bool isAxis = MathF.Abs(x) < 1e-4f;
                DrawWorldLine(vp, new Vector3(x, -extent, planeY), new Vector3(x, extent, planeY),
                              isAxis ? colY : col);
            }
        }

        void DrawWorldLine(Matrix4x4 vp, Vector3 a, Vector3 b, uint col)
        {
            var c0 = Vector4.Transform(new Vector4(a, 1f), vp);
            var c1 = Vector4.Transform(new Vector4(b, 1f), vp);
            if (c0.W <= 0.0001f || c1.W <= 0.0001f) return;

            var s0 = ToScreen(c0);
            var s1 = ToScreen(c1);
            if (!s0.Ok || !s1.Ok) return;

            float dx = s1.X - s0.X, dy = s1.Y - s0.Y;
            int steps = (int)MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
            if (steps <= 0) return;
            if (steps > 6000) steps = 6000;

            float sx = dx / steps, sy = dy / steps;
            float w0 = 1f / c0.W, w1 = 1f / c1.W;

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float depth = w0 + (w1 - w0) * t;
                int cx = (int)(s0.X + sx * i);
                int cy = (int)(s0.Y + sy * i);
                if (cx < 0 || cy < 0 || cx >= Width || cy >= Height) continue;
                int di = cy * Width + cx;
                if (depth * 1e6f <= DepthBits[di]) continue;
                DepthBits[di] = (int)(depth * 1e6f);
                Pixels[di] = col;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Pack(Vector3 c)
        {
            uint r = (uint)(Clamp01(c.X) * 255f);
            uint g = (uint)(Clamp01(c.Y) * 255f);
            uint b = (uint)(Clamp01(c.Z) * 255f);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
