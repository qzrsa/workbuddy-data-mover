using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace IconGen
{
    /// <summary>
    /// 生成应用图标 app.ico：
    /// 渐变圆角底 + 白色文件夹 + 白色右向箭头（"把数据目录搬走"）。
    /// 每个尺寸独立超采样渲染后缩放，保证 16px 下依然可辨。
    /// 输出经典 DIB（BMP）条目，最大兼容 System.Drawing.Icon 与 Windows 外壳。
    /// </summary>
    internal static class Program
    {
        private const int Supersample = 8;

        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        private static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "app.ico";

            var images = new List<Bitmap>();
            try
            {
                foreach (int size in Sizes) images.Add(Render(size));

                WriteIco(outPath, images);
                Console.WriteLine("已生成：" + Path.GetFullPath(outPath));

                // 顺带导出一张 256px PNG，便于预览
                string pngPath = Path.ChangeExtension(outPath, null) + "-preview.png";
                images[images.Count - 1].Save(pngPath, ImageFormat.Png);
                Console.WriteLine("预览图：" + Path.GetFullPath(pngPath));

                string sheetPath = Path.ChangeExtension(outPath, null) + "-sheet.png";
                SaveContactSheet(sheetPath, images);
                Console.WriteLine("尺寸对照表：" + Path.GetFullPath(sheetPath));

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("生成失败：" + ex);
                return 1;
            }
            finally
            {
                foreach (Bitmap b in images) b.Dispose();
            }
        }

        private static Bitmap Render(int size)
        {
            int s = size * Supersample;
            using var canvas = new Bitmap(s, s, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                float p = s;

                // 圆角渐变底
                var plate = new RectangleF(p * 0.015f, p * 0.015f, p * 0.97f, p * 0.97f);
                using (GraphicsPath path = RoundedRect(plate, p * 0.225f))
                using (var brush = new LinearGradientBrush(
                    new RectangleF(0, 0, p, p),
                    Color.FromArgb(43, 108, 246),
                    Color.FromArgb(14, 176, 164),
                    45f))
                {
                    g.FillPath(brush, path);
                }

                using (var white = new SolidBrush(Color.White))
                {
                    // 文件夹标签
                    using (GraphicsPath tab = RoundedRect(
                        new RectangleF(p * 0.105f, p * 0.325f, p * 0.215f, p * 0.14f), p * 0.045f))
                    {
                        g.FillPath(white, tab);
                    }

                    // 文件夹主体
                    using (GraphicsPath body = RoundedRect(
                        new RectangleF(p * 0.105f, p * 0.405f, p * 0.365f, p * 0.325f), p * 0.055f))
                    {
                        g.FillPath(white, body);
                    }

                    // 右向箭头
                    float ay = p * 0.5675f;
                    float shaftLeft = p * 0.505f;
                    float shaftRight = p * 0.745f;
                    float shaftHalf = p * 0.052f;
                    float headHalf = p * 0.152f;
                    float tip = p * 0.905f;

                    var arrow = new[]
                    {
                        new PointF(shaftLeft, ay - shaftHalf),
                        new PointF(shaftRight, ay - shaftHalf),
                        new PointF(shaftRight, ay - headHalf),
                        new PointF(tip, ay),
                        new PointF(shaftRight, ay + headHalf),
                        new PointF(shaftRight, ay + shaftHalf),
                        new PointF(shaftLeft, ay + shaftHalf),
                    };

                    g.FillPolygon(white, arrow);
                }
            }

            var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g2 = Graphics.FromImage(output))
            {
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g2.CompositingQuality = CompositingQuality.HighQuality;
                g2.SmoothingMode = SmoothingMode.HighQuality;
                g2.DrawImage(canvas, new Rectangle(0, 0, size, size), new Rectangle(0, 0, s, s), GraphicsUnit.Pixel);
            }

            return output;
        }

        /// <summary>尺寸对照表：左半为放大 4 倍的最近邻（看像素级形状），右半为原始尺寸。</summary>
        private static void SaveContactSheet(string path, List<Bitmap> images)
        {
            const int gap = 12;
            int big = 256 * 4;
            int rowsHeight = 0;
            foreach (Bitmap b in images) rowsHeight += Math.Max(b.Height * 4, b.Height) + gap;

            int width = big + gap + 256 + gap * 2;
            int height = rowsHeight + gap;

            using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(240, 242, 245));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                int y = gap;
                foreach (Bitmap b in images)
                {
                    int zoomW = b.Width * 4;
                    int zoomH = b.Height * 4;
                    g.DrawImage(b, new Rectangle(gap, y, zoomW, zoomH));
                    g.DrawImage(b, new Rectangle(gap + big + gap, y, b.Width, b.Height));
                    y += Math.Max(zoomH, b.Height) + gap;
                }
            }

            sheet.Save(path, ImageFormat.Png);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();

            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();

            return path;
        }

        // ---------------- ICO 写出（DIB 条目）----------------

        private static void WriteIco(string path, List<Bitmap> images)
        {
            var payloads = new List<byte[]>(images.Count);
            foreach (Bitmap b in images) payloads.Add(ToDib(b));

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);

            w.Write((ushort)0);              // reserved
            w.Write((ushort)1);              // type = icon
            w.Write((ushort)images.Count);

            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                Bitmap b = images[i];
                w.Write((byte)(b.Width >= 256 ? 0 : b.Width));
                w.Write((byte)(b.Height >= 256 ? 0 : b.Height));
                w.Write((byte)0);            // color count
                w.Write((byte)0);            // reserved
                w.Write((ushort)1);          // planes
                w.Write((ushort)32);         // bit count
                w.Write((uint)payloads[i].Length);
                w.Write((uint)offset);
                offset += payloads[i].Length;
            }

            foreach (byte[] p in payloads) w.Write(p);
        }

        private static byte[] ToDib(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int pixelSize = w * h * 4;
            int maskStride = ((w + 31) / 32) * 4;
            int maskSize = maskStride * h;

            using var ms = new MemoryStream(40 + pixelSize + maskSize);
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                // BITMAPINFOHEADER
                bw.Write(40);
                bw.Write(w);
                bw.Write(h * 2);          // 高度含 AND 掩码
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(0);              // BI_RGB
                bw.Write(pixelSize);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);

                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = h - 1; y >= 0; y--)   // 自下而上
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, w * 4);
                        bw.Write(row);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                bw.Write(new byte[maskSize]);      // AND 掩码全 0，靠 alpha 通道
            }

            return ms.ToArray();
        }
    }
}
