using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// DDS 解码器。只狼角色的贴图全是 DDS（BC1/BC3/BC7/未压缩 RGBA），
    /// 要显示贴图就得先在 CPU 上解成像素。
    ///
    /// 支持：
    ///   DXT1 (BC1)  — 4:1 压缩，带 1-bit alpha
    ///   DXT3 (BC2)  — 4:1 + 4-bit alpha
    ///   DXT5 (BC3)  — 4:1 + 插值 alpha
    ///   BC7         — 只狼用得最多的格式（DX10 扩展头）
    ///   RGBA8 / BGRA8 / R8 未压缩
    /// 其余格式返回 null（上层跳过，不影响其它网格）。
    /// </summary>
    class DdsImage
    {
        public int Width;
        public int Height;

        /// <summary>RGBA8，逐行从上到下，长度 = Width*Height*4。</summary>
        public byte[] Rgba;

        /// <summary>解好的贴图缓存（key = 文件全路径），避免反复解码。</summary>
        static readonly Dictionary<string, DdsImage> Cache = new Dictionary<string, DdsImage>(StringComparer.OrdinalIgnoreCase);

        static readonly object Gate = new object();

        /// <summary>按路径取贴图；找不到或格式不支持返回 null。</summary>
        public static DdsImage Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (Gate)
            {
                if (Cache.TryGetValue(path, out var hit)) return hit;
            }
            DdsImage img = null;
            try
            {
                if (File.Exists(path)) img = Decode(File.ReadAllBytes(path));
            }
            catch { img = null; }

            lock (Gate) { Cache[path] = img; }
            return img;
        }

        public static void ClearCache()
        {
            lock (Gate) { Cache.Clear(); }
        }

        /// <summary>双线性采样，返回 0..1 的 RGB（贴图用 sRGB、不做 gamma 变换，和 DSAnimStudio 一致）。</summary>
        public Vector3 Sample(float u, float v)
        {
            if (Rgba == null || Width <= 0 || Height <= 0) return Vector3.One;

            // DDS 的 v 从上到下，UV 的 v 从下到上
            float fx = u * Width - 0.5f;
            float fy = (1f - v) * Height - 0.5f;

            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;

            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = Wrap(x0, Width); x1 = Wrap(x1, Width);
            y0 = Wrap(y0, Height); y1 = Wrap(y1, Height);

            var c00 = At(x0, y0);
            var c10 = At(x1, y0);
            var c01 = At(x0, y1);
            var c11 = At(x1, y1);

            var a = Vector3.Lerp(c00, c10, tx);
            var b = Vector3.Lerp(c01, c11, tx);
            return Vector3.Lerp(a, b, ty);
        }

        static int Wrap(int v, int n)
        {
            if (v < 0) return 0;
            if (v >= n) return n - 1;
            return v;
        }

        Vector3 At(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return new Vector3(Rgba[i] / 255f, Rgba[i + 1] / 255f, Rgba[i + 2] / 255f);
        }

        // ------------------------------------------------------------ 解码

        const uint Magic = 0x20534444;   // "DDS "

        public static DdsImage Decode(byte[] data)
        {
            if (data == null || data.Length < 128) return null;
            if (BitConverter.ToUInt32(data, 0) != Magic) return null;

            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            int mipCount = Math.Max(1, BitConverter.ToInt32(data, 28));
            uint pfFlags = BitConverter.ToUInt32(data, 80);
            uint fourCC = BitConverter.ToUInt32(data, 84);
            int rgbBits = BitConverter.ToInt32(data, 88);
            uint rMask = BitConverter.ToUInt32(data, 92);
            uint gMask = BitConverter.ToUInt32(data, 96);
            uint bMask = BitConverter.ToUInt32(data, 100);
            uint aMask = BitConverter.ToUInt32(data, 104);

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

            int offset = 128;
            uint dxgiFormat = 0;

            // DX10 扩展头
            if (fourCC == 0x30315844)   // "DX10"
            {
                if (data.Length < 148) return null;
                dxgiFormat = BitConverter.ToUInt32(data, 128);
                offset = 148;
            }

            var img = new DdsImage { Width = width, Height = height, Rgba = new byte[width * height * 4] };

            bool ok;
            if (dxgiFormat != 0)
            {
                ok = DecodeDxgi(img, data, offset, dxgiFormat);
            }
            else if (fourCC == 0x31545844)      // DXT1
            {
                ok = DecodeBc(img, data, offset, 8, false, false);
            }
            else if (fourCC == 0x33545844)      // DXT3
            {
                ok = DecodeBc(img, data, offset, 16, true, false);
            }
            else if (fourCC == 0x35545844)      // DXT5
            {
                ok = DecodeBc(img, data, offset, 16, true, true);
            }
            else if (fourCC == 0x31495441 || fourCC == 0x32495441)   // ATI1 / ATI2 (BC4 / BC5)
            {
                ok = DecodeAtiN(img, data, offset, fourCC == 0x32495441);
            }
            else if ((pfFlags & 0x40) != 0 && rgbBits == 32)
            {
                ok = DecodeUncompressed(img, data, offset, rMask, gMask, bMask, aMask);
            }
            else
            {
                return null;
            }

            return ok ? img : null;
        }

        static bool DecodeDxgi(DdsImage img, byte[] data, int offset, uint fmt)
        {
            switch (fmt)
            {
                case 70: case 71: case 72:      // BC1 UNORM / SRGB
                    return DecodeBc(img, data, offset, 8, false, false);
                case 73: case 74: case 75:      // BC2
                    return DecodeBc(img, data, offset, 16, true, false);
                case 76: case 77: case 78:      // BC3
                    return DecodeBc(img, data, offset, 16, true, true);
                case 97: case 98: case 99:      // BC7
                    return DecodeBc7(img, data, offset);
                case 28:                        // R8G8B8A8
                case 29:
                    return DecodeUncompressed(img, data, offset, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
                case 87:                        // B8G8R8A8
                case 88:
                    return DecodeUncompressed(img, data, offset, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
                case 61:                        // R8
                {
                    int n = img.Width * img.Height;
                    if (data.Length < offset + n) return false;
                    for (int i = 0; i < n; i++)
                    {
                        byte v = data[offset + i];
                        img.Rgba[i * 4] = v; img.Rgba[i * 4 + 1] = v;
                        img.Rgba[i * 4 + 2] = v; img.Rgba[i * 4 + 3] = 255;
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        static bool DecodeUncompressed(DdsImage img, byte[] data, int offset, uint rM, uint gM, uint bM, uint aM)
        {
            int n = img.Width * img.Height;
            if (data.Length < offset + n * 4) return false;
            for (int i = 0; i < n; i++)
            {
                uint p = BitConverter.ToUInt32(data, offset + i * 4);
                img.Rgba[i * 4] = Channel(p, rM);
                img.Rgba[i * 4 + 1] = Channel(p, gM);
                img.Rgba[i * 4 + 2] = Channel(p, bM);
                img.Rgba[i * 4 + 3] = aM == 0 ? (byte)255 : Channel(p, aM);
            }
            return true;
        }

        static byte Channel(uint pixel, uint mask)
        {
            if (mask == 0) return 255;
            int shift = 0;
            while (((mask >> shift) & 1) == 0 && shift < 32) shift++;
            uint m = mask >> shift;
            uint v = (pixel & mask) >> shift;
            if (m == 0) return 0;
            return (byte)(v * 255 / m);
        }

        // ------------------------------------------------------------ BC4 / BC5（ATI1 / ATI2）

        /// <summary>BC4（单通道）/ BC5（双通道）法线贴图。</summary>
        static bool DecodeAtiN(DdsImage img, byte[] data, int offset, bool twoChannel)
        {
            int bw = (img.Width + 3) / 4;
            int bh = (img.Height + 3) / 4;
            int blockSize = twoChannel ? 16 : 8;
            if (data.Length < offset + bw * bh * blockSize) return false;

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int src = offset + (by * bw + bx) * blockSize;
                    var red = new byte[16];
                    var green = new byte[16];
                    DecodeBc4Block(data, src, red);
                    if (twoChannel) DecodeBc4Block(data, src + 8, green);
                    else for (int i = 0; i < 16; i++) green[i] = red[i];

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px, y = by * 4 + py;
                            if (x >= img.Width || y >= img.Height) continue;
                            int bi = py * 4 + px;
                            int di = (y * img.Width + x) * 4;
                            if (twoChannel)
                            {
                                img.Rgba[di] = red[bi];
                                img.Rgba[di + 1] = green[bi];
                                // BC5 存的是 XY，Z 靠重建
                                float rx = red[bi] / 255f * 2f - 1f;
                                float ry = green[bi] / 255f * 2f - 1f;
                                float rz = MathF.Sqrt(MathF.Max(0f, 1f - rx * rx - ry * ry));
                                img.Rgba[di + 2] = (byte)((rz * 0.5f + 0.5f) * 255f);
                            }
                            else
                            {
                                img.Rgba[di] = red[bi];
                                img.Rgba[di + 1] = red[bi];
                                img.Rgba[di + 2] = red[bi];
                            }
                            img.Rgba[di + 3] = 255;
                        }
                    }
                }
            }
            return true;
        }

        static void DecodeBc4Block(byte[] data, int src, byte[] dst)
        {
            int a0 = data[src], a1 = data[src + 1];
            var pal = new byte[8];
            pal[0] = (byte)a0; pal[1] = (byte)a1;
            if (a0 > a1)
            {
                for (int i = 2; i < 8; i++)
                    pal[i] = (byte)(((7 - i) * a0 + (i - 1) * a1) / 7);
            }
            else
            {
                for (int i = 2; i < 6; i++)
                    pal[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5);
                pal[6] = 0; pal[7] = 255;
            }
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)data[src + 2 + i] << (i * 8);
            for (int i = 0; i < 16; i++)
                dst[i] = pal[(bits >> (i * 3)) & 7];
        }

        // ------------------------------------------------------------ BC1 / BC2 / BC3

        static bool DecodeBc(DdsImage img, byte[] data, int offset, int blockSize, bool hasAlpha, bool interpolatedAlpha)
        {
            int bw = (img.Width + 3) / 4;
            int bh = (img.Height + 3) / 4;
            int need = offset + bw * bh * blockSize;
            if (data.Length < need) return false;

            var c0 = new byte[3];
            var c1 = new byte[3];
            var pal = new byte[4 * 3];

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int src = offset + (by * bw + bx) * blockSize;

                    // alpha 表（DXT3 是 4bit 显式，DXT5 是插值）
                    byte[] alpha = null;
                    if (hasAlpha)
                    {
                        alpha = new byte[16];
                        if (interpolatedAlpha)
                        {
                            // DXT5: 2 个端点 + 16 个 3bit 索引
                            int a0 = data[src], a1 = data[src + 1];
                            var ap = new byte[8];
                            ap[0] = (byte)a0; ap[1] = (byte)a1;
                            if (a0 > a1)
                            {
                                for (int i = 2; i < 8; i++)
                                    ap[i] = (byte)(((7 - i) * a0 + (i - 1) * a1) / 7);
                            }
                            else
                            {
                                for (int i = 2; i < 6; i++)
                                    ap[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5);
                                ap[6] = 0; ap[7] = 255;
                            }
                            // 48 bit 索引
                            ulong bits = 0;
                            for (int i = 0; i < 6; i++) bits |= (ulong)data[src + 2 + i] << (i * 8);
                            for (int i = 0; i < 16; i++)
                                alpha[i] = ap[(bits >> (i * 3)) & 7];
                            src += 8;
                        }
                        else
                        {
                            // DXT3: 每像素 4bit
                            for (int i = 0; i < 8; i++)
                            {
                                byte b = data[src + i];
                                alpha[i * 2] = (byte)((b & 0x0F) * 17);
                                alpha[i * 2 + 1] = (byte)((b >> 4) * 17);
                            }
                            src += 8;
                        }
                    }

                    // 颜色：2 个 565 端点 + 16 个 2bit 索引
                    ushort p0 = BitConverter.ToUInt16(data, src);
                    ushort p1 = BitConverter.ToUInt16(data, src + 2);
                    Rgb565(p0, c0, 0);
                    Rgb565(p1, c1, 0);

                    for (int k = 0; k < 3; k++) { pal[k] = c0[k]; pal[3 + k] = c1[k]; }

                    bool transparentMode = !hasAlpha && p0 <= p1;
                    if (p0 > p1 || hasAlpha)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            pal[6 + k] = (byte)((2 * c0[k] + c1[k]) / 3);
                            pal[9 + k] = (byte)((c0[k] + 2 * c1[k]) / 3);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            pal[6 + k] = (byte)((c0[k] + c1[k]) / 2);
                            pal[9 + k] = 0;
                        }
                    }

                    uint idx = BitConverter.ToUInt32(data, src + 4);

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px, y = by * 4 + py;
                            if (x >= img.Width || y >= img.Height) continue;

                            int ci = (int)((idx >> ((py * 4 + px) * 2)) & 3);
                            int di = (y * img.Width + x) * 4;

                            img.Rgba[di] = pal[ci * 3];
                            img.Rgba[di + 1] = pal[ci * 3 + 1];
                            img.Rgba[di + 2] = pal[ci * 3 + 2];

                            byte a = 255;
                            if (alpha != null) a = alpha[py * 4 + px];
                            else if (transparentMode && ci == 3) a = 0;
                            img.Rgba[di + 3] = a;
                        }
                    }
                }
            }
            return true;
        }

        static void Rgb565(ushort v, byte[] dst, int o)
        {
            int r = (v >> 11) & 0x1F;
            int g = (v >> 5) & 0x3F;
            int b = v & 0x1F;
            dst[o] = (byte)((r << 3) | (r >> 2));
            dst[o + 1] = (byte)((g << 2) | (g >> 4));
            dst[o + 2] = (byte)((b << 3) | (b >> 2));
        }

        // ------------------------------------------------------------ BC7

        /// <summary>
        /// BC7 解码。BC7 有 8 种模式、每种的分区/端点/索引位数都不同，
        /// 这里按 D3D 规范逐个模式实现（只狼主要用 mode 6 和 mode 1）。
        /// </summary>
        static bool DecodeBc7(DdsImage img, byte[] data, int offset)
        {
            int bw = (img.Width + 3) / 4;
            int bh = (img.Height + 3) / 4;
            if (data.Length < offset + bw * bh * 16) return false;

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int src = offset + (by * bw + bx) * 16;
                    var block = new byte[16];
                    Array.Copy(data, src, block, 0, 16);

                    var outPx = new byte[16 * 4];
                    if (!Bc7Block(block, outPx)) return false;

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px, y = by * 4 + py;
                            if (x >= img.Width || y >= img.Height) continue;
                            int si = (py * 4 + px) * 4;
                            int di = (y * img.Width + x) * 4;
                            img.Rgba[di] = outPx[si];
                            img.Rgba[di + 1] = outPx[si + 1];
                            img.Rgba[di + 2] = outPx[si + 2];
                            img.Rgba[di + 3] = outPx[si + 3];
                        }
                    }
                }
            }
            return true;
        }

        // BC7 分区表
        static readonly byte[][] Bc7Partitions =
        {
            new byte[]{0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1},
            new byte[]{0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1},
            new byte[]{0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,1,0,0,1,1,0,0,1,1,0,0,1,1},
            new byte[]{0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1},
            new byte[]{0,0,1,1,0,1,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,1,0,0,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,1,1,1,0,0,1,1,0,0,1,1,0,0,1,1},
            new byte[]{0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,1},
            new byte[]{0,0,0,1,0,0,1,1,0,0,0,1,0,0,0,1},
            new byte[]{0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1},
            new byte[]{0,0,0,0,0,0,1,1,0,0,1,1,0,1,1,1},
            new byte[]{0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1},
            new byte[]{0,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0},
            new byte[]{0,0,0,1,0,1,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,1},
            new byte[]{0,0,0,0,0,1,1,0,0,1,1,0,0,1,1,0},
            new byte[]{0,1,1,1,1,1,1,0,1,1,1,0,1,1,1,0},
            new byte[]{0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0},
            new byte[]{0,1,1,1,1,1,0,0,1,1,0,0,1,0,0,0},
            new byte[]{0,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0},
            new byte[]{0,0,0,1,0,0,0,1,0,1,1,0,0,1,1,0},
            new byte[]{0,0,0,0,0,0,0,0,0,1,1,0,0,1,1,0},
            new byte[]{0,0,0,0,0,0,0,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,1,0,1,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,1,0,0,0,1,0,0,0,1,0,1,1,1},
            new byte[]{0,0,1,1,0,1,1,0,0,1,1,0,0,1,1,0},
            new byte[]{0,1,1,1,1,1,0,0,1,1,0,0,1,0,0,0},
            new byte[]{0,0,1,1,0,0,1,1,0,1,1,0,1,1,0,0},
            new byte[]{0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,1,1,0,0,1,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,1,1,0,0,1,1,0,0,1,1},
            new byte[]{0,0,1,1,0,1,1,1,0,1,1,1,0,0,1,1},
            new byte[]{0,0,1,1,0,1,1,1,0,1,1,1,0,1,1,0},
            new byte[]{0,1,1,1,1,1,1,1,1,0,0,0,0,0,0,1},
            new byte[]{0,0,0,1,0,0,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,1,1,0,1,1,1,0,1,1,1,0,0,0,0,0},
            new byte[]{0,0,1,1,0,0,1,1,0,1,1,0,1,1,0,0},
            new byte[]{0,1,1,0,0,1,1,0,1,1,0,0,1,1,0,0},
            new byte[]{0,0,0,1,0,1,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,0,1,0,0,0,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,1,1,0,0,1,1,0,1,1,1},
            new byte[]{0,0,1,1,0,0,1,1,0,0,1,1,0,1,1,1},
            new byte[]{0,1,1,1,1,1,1,0,1,1,1,0,1,1,1,0},
            new byte[]{0,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0},
            new byte[]{0,0,0,1,0,1,1,1,0,1,1,1,0,1,1,1},
            new byte[]{0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1},
            new byte[]{0,0,1,1,0,0,1,1,0,0,1,1,0,1,1,0},
            new byte[]{0,0,0,1,0,0,0,1,0,0,0,1,0,1,1,0},
            new byte[]{0,0,0,0,0,0,0,1,0,0,0,1,0,1,1,1},
            new byte[]{0,0,0,1,0,1,1,1,0,1,1,1,0,0,0,1},
            new byte[]{0,1,1,1,1,1,1,0,1,1,1,0,1,0,0,0},
            new byte[]{0,0,1,1,0,0,1,1,0,1,1,0,1,1,0,0},
            new byte[]{0,0,1,1,0,0,1,1,0,1,1,1,0,0,1,1},
            new byte[]{0,1,1,1,1,1,1,1,1,0,0,0,0,0,0,0},
            new byte[]{0,1,1,1,1,1,1,0,1,1,1,0,0,0,0,0},
            new byte[]{0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1},
            new byte[]{0,0,0,0,0,0,1,1,0,0,1,1,0,0,1,1},
            new byte[]{0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1},
            new byte[]{0,0,0,1,0,0,1,0,0,0,1,0,1,1,0,0},
            new byte[]{0,1,1,0,1,1,0,0,0,1,1,0,1,1,0,0},
            new byte[]{0,0,1,1,0,1,1,0,1,1,0,0,1,1,0,0},
        };

        static bool Bc7Block(byte[] b, byte[] outPx)
        {
            // 找模式（最高位的 1 的位置）
            int mode = 0;
            int bit = 0;
            while (bit < 8 && (b[0] & (1 << bit)) == 0) bit++;
            if (bit >= 8) return false;
            mode = bit;

            // 取出 128 bit 流
            var bits = new ulong[2];
            bits[0] = BitConverter.ToUInt64(b, 0);
            bits[1] = BitConverter.ToUInt64(b, 8);

            var reader = new BitReader(b);

            // 每个模式的参数（端点精度、索引位数、分区数、是否有 p-bit）
            int numSubsets = 1, colorBits = 8, alphaBits = 8, idxBits = 3, pBits = 0;
            bool hasRotation = false, hasIdxSelector = false;

            switch (mode)
            {
                case 0: numSubsets = 3; colorBits = 4; alphaBits = 0; idxBits = 3; pBits = 1; break;
                case 1: numSubsets = 2; colorBits = 6; alphaBits = 0; idxBits = 3; pBits = 0; hasIdxSelector = true; break;
                case 2: numSubsets = 3; colorBits = 5; alphaBits = 0; idxBits = 2; pBits = 0; break;
                case 3: numSubsets = 2; colorBits = 7; alphaBits = 0; idxBits = 2; pBits = 1; break;
                case 4: numSubsets = 1; colorBits = 5; alphaBits = 6; idxBits = 2; hasRotation = true; hasIdxSelector = true; break;
                case 5: numSubsets = 1; colorBits = 7; alphaBits = 8; idxBits = 2; hasRotation = true; break;
                case 6: numSubsets = 1; colorBits = 7; alphaBits = 7; idxBits = 4; pBits = 1; break;
                case 7: numSubsets = 2; colorBits = 5; alphaBits = 5; idxBits = 2; pBits = 1; break;
                default: return false;
            }

            reader.Skip(mode + 1);

            int[] partition = null;
            if (numSubsets > 1)
            {
                int part = (int)reader.Read(4);
                if (part >= 64) return false;
                int tableIdx = (numSubsets - 1) * 64 + part;
                if (tableIdx >= Bc7Partitions.Length) tableIdx = part;
                partition = new int[16];
                var src = Bc7Partitions[tableIdx];
                for (int k = 0; k < 16; k++) partition[k] = src[k];
            }

            int rotation = 0;
            if (hasRotation) rotation = (int)reader.Read(2);

            int idxSel = 0;
            if (hasIdxSelector) idxSel = (int)reader.Read(1);

            // 端点：每个子集 2 个端点，每端点 RGBA
            int epCount = numSubsets * 2;
            var ep = new int[epCount][];
            for (int i = 0; i < epCount; i++) ep[i] = new int[4];

            for (int i = 0; i < epCount; i++)
            {
                ep[i][0] = (int)reader.Read(colorBits);
                ep[i][1] = (int)reader.Read(colorBits);
                ep[i][2] = (int)reader.Read(colorBits);
                ep[i][3] = alphaBits > 0 ? (int)reader.Read(alphaBits) : 255;
            }

            // p-bits
            if (pBits > 0)
            {
                for (int i = 0; i < epCount; i++)
                {
                    int p = (int)reader.Read(1);
                    for (int c = 0; c < 3; c++)
                        ep[i][c] = (ep[i][c] << 1) | p;
                    if (alphaBits > 0) ep[i][3] = (ep[i][3] << 1) | p;
                }
                colorBits++; alphaBits = alphaBits > 0 ? alphaBits + 1 : 0;
            }

            // 索引
            var colorIdx = new int[16];
            var alphaIdx = new int[16];
            for (int i = 0; i < 16; i++)
            {
                colorIdx[i] = (int)reader.Read(idxBits);
                alphaIdx[i] = hasIdxSelector ? (int)reader.Read(idxBits) : colorIdx[i];
            }

            // 端点反量化
            int cb = colorBits, ab = alphaBits > 0 ? alphaBits : 8;
            for (int i = 0; i < epCount; i++)
            {
                for (int c = 0; c < 3; c++)
                    ep[i][c] = Expand(ep[i][c], cb);
                ep[i][3] = alphaBits > 0 ? Expand(ep[i][3], ab) : 255;
            }

            // 组装像素
            for (int i = 0; i < 16; i++)
            {
                int sub = partition == null ? 0 : partition[i];
                int e0 = sub * 2, e1 = sub * 2 + 1;

                int ci = colorIdx[i];
                int ai = hasIdxSelector ? alphaIdx[i] : ci;

                var c = Interp(ep[e0], ep[e1], ci, idxBits);
                var a = hasIdxSelector ? Interp(ep[e0], ep[e1], ai, idxBits)[3] : Interp(ep[e0], ep[e1], ci, idxBits)[3];

                // 只有 alpha 通道的模式（4/5/7）要看 idxSel
                if (hasIdxSelector && idxSel == 1)
                {
                    // 索引对调：颜色用 alphaIdx，alpha 用 colorIdx
                    c = Interp(ep[e0], ep[e1], alphaIdx[i], idxBits);
                    a = Interp(ep[e0], ep[e1], colorIdx[i], idxBits)[3];
                }

                int r = c[0], g = c[1], bl = c[2], al = a;

                // 通道旋转
                if (rotation == 1) { var t = al; al = r; r = t; }
                else if (rotation == 2) { var t = al; al = g; g = t; }
                else if (rotation == 3) { var t = al; al = bl; bl = t; }

                outPx[i * 4] = (byte)Math.Clamp(r, 0, 255);
                outPx[i * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                outPx[i * 4 + 2] = (byte)Math.Clamp(bl, 0, 255);
                outPx[i * 4 + 3] = (byte)Math.Clamp(al, 0, 255);
            }

            return true;
        }

        static int[] Interp(int[] e0, int[] e1, int idx, int idxBits)
        {
            int max = (1 << idxBits) - 1;
            var r = new int[4];
            if (idx == 0) { r[0] = e0[0]; r[1] = e0[1]; r[2] = e0[2]; r[3] = e0[3]; return r; }
            if (idx == max) { r[0] = e1[0]; r[1] = e1[1]; r[2] = e1[2]; r[3] = e1[3]; return r; }
            for (int c = 0; c < 4; c++)
                r[c] = ((max - idx) * e0[c] + idx * e1[c] + max / 2) / max;
            return r;
        }

        static int Expand(int v, int bits)
        {
            if (bits >= 8) return v;
            int maxIn = (1 << bits) - 1;
            return v * 255 / maxIn;
        }

        /// <summary>按位读，LSB 在前（BC7/DDS 的约定）。</summary>
        class BitReader
        {
            readonly byte[] _d;
            int _pos;

            public BitReader(byte[] d) { _d = d; }

            public void Skip(int n) { _pos += n; }

            public uint Read(int n)
            {
                uint v = 0;
                for (int i = 0; i < n; i++)
                {
                    int byteIdx = _pos >> 3;
                    int bitIdx = _pos & 7;
                    if (byteIdx >= _d.Length) { _pos++; continue; }
                    if ((_d[byteIdx] & (1 << bitIdx)) != 0) v |= 1u << i;
                    _pos++;
                }
                return v;
            }
        }
    }
}
