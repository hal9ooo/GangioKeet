# Crea un ICO multi-risoluzione da una PNG generata (con clipping del bordo bianco).
# Uso: powershell -ExecutionPolicy Bypass -File scripts/make_client_icon_from_png.ps1 <source.png> <out.ico>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Output
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class GangioIconBuilder
{
    public static void Build(string sourcePath, string outputPath)
    {
        using (Bitmap source = new Bitmap(sourcePath))
        {
            int w = source.Width;
            int h = source.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData data = source.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte[] pixels = new byte[stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            bool[] background = new bool[w * h];
            Queue<int> queue = new Queue<int>();

            for (int x = 0; x < w; x++)
            {
                TryEnqueue(pixels, stride, w, background, queue, x, 0);
                TryEnqueue(pixels, stride, w, background, queue, x, h - 1);
            }

            for (int y = 0; y < h; y++)
            {
                TryEnqueue(pixels, stride, w, background, queue, 0, y);
                TryEnqueue(pixels, stride, w, background, queue, w - 1, y);
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w;
                int y = idx / w;
                if (x > 0) TryEnqueue(pixels, stride, w, background, queue, x - 1, y);
                if (x < w - 1) TryEnqueue(pixels, stride, w, background, queue, x + 1, y);
                if (y > 0) TryEnqueue(pixels, stride, w, background, queue, x, y - 1);
                if (y < h - 1) TryEnqueue(pixels, stride, w, background, queue, x, y + 1);
            }

            int minX = w;
            int minY = h;
            int maxX = -1;
            int maxY = -1;
            int birdMinX = w;
            int birdMinY = h;
            int birdMaxX = -1;
            int birdMaxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (background[idx]) continue;
                    int i = y * stride + x * 4;
                    if (pixels[i + 3] == 0) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;

                    // Il pappagallino e' chiaro (bianco/menta/becco), lo sfondo e' verde scuro.
                    int brightest = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                    if (brightest >= 200)
                    {
                        if (x < birdMinX) birdMinX = x;
                        if (y < birdMinY) birdMinY = y;
                        if (x > birdMaxX) birdMaxX = x;
                        if (y > birdMaxY) birdMaxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                throw new InvalidOperationException("Nessun contenuto trovato nell'immagine.");
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (background[y * w + x])
                    {
                        pixels[y * stride + x * 4 + 3] = 0;
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            source.UnlockBits(data);

            int bw = maxX - minX + 1;
            int bh = maxY - minY + 1;
            using (Bitmap cropped = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(cropped))
                {
                    g.DrawImage(source, new Rectangle(0, 0, bw, bh), new Rectangle(minX, minY, bw, bh), GraphicsUnit.Pixel);
                }

                int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
                List<byte[]> payloads = new List<byte[]>();

                foreach (int size in sizes)
                {
                    using (Bitmap scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.Clear(Color.Transparent);

                            // Uccellino grande su tutte le taglie: crop centrale al 74% della tile,
                            // così il soggetto riempie l'icona e il verde resta solo di cornice.
                            int side = (int)Math.Round(Math.Min(bw, bh) * 0.74);
                            int left = minX + ((bw - side) / 2);
                            int top = minY + ((bh - side) / 2);

                            using (GraphicsPath rounded = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f))
                            {
                                g.SetClip(rounded);
                                g.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle(left, top, side, side), GraphicsUnit.Pixel);
                            }
                        }

                        using (MemoryStream ms = new MemoryStream())
                        {
                            scaled.Save(ms, ImageFormat.Png);
                            payloads.Add(ms.ToArray());
                        }
                    }
                }

                using (MemoryStream output = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(output))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)sizes.Length);

                    int offset = 6 + 16 * sizes.Length;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                        writer.Write(dim);
                        writer.Write(dim);
                        writer.Write((byte)0);
                        writer.Write((byte)0);
                        writer.Write((ushort)1);
                        writer.Write((ushort)32);
                        writer.Write((uint)payloads[i].Length);
                        writer.Write((uint)offset);
                        offset += payloads[i].Length;
                    }

                    foreach (byte[] payload in payloads)
                    {
                        writer.Write(payload);
                    }

                    writer.Flush();
                    File.WriteAllBytes(outputPath, output.ToArray());
                }
            }
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool IsWhite(byte[] pixels, int stride, int x, int y)
    {
        int i = y * stride + x * 4;
        return pixels[i + 3] > 0 && pixels[i] >= 235 && pixels[i + 1] >= 235 && pixels[i + 2] >= 235;
    }

    private static void TryEnqueue(byte[] pixels, int stride, int w, bool[] background, Queue<int> queue, int x, int y)
    {
        int idx = y * w + x;
        if (background[idx]) return;
        if (!IsWhite(pixels, stride, x, y)) return;
        background[idx] = true;
        queue.Enqueue(idx);
    }
}
"@

[GangioIconBuilder]::Build((Resolve-Path $Source).Path, $Output)
Write-Output "Icona scritta: $Output ($([System.IO.File]::ReadAllBytes((Resolve-Path $Output)).Length) byte)"
