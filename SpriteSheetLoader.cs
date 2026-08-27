using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkCat
{
    /// <summary>
    /// Loads, chroma-keys background transparency, and auto-detects sprite bounding boxes
    /// to eliminate cut-off tails, legs, or adjacent frame bleed.
    /// </summary>
    public class SpriteSheetLoader
    {
        public List<BitmapSource> WalkFrames { get; } = new();
        public List<BitmapSource> SprintFrames { get; } = new();
        public List<BitmapSource> StrikeFrames { get; } = new();

        public bool IsLoaded => WalkFrames.Count > 0;

        public bool LoadAndSlice(string relativePath = "Assets/cat_spritesheet.jpg")
        {
            try
            {
                string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
                if (!File.Exists(fullPath))
                {
                    fullPath = Path.GetFullPath(relativePath);
                    if (!File.Exists(fullPath)) return false;
                }

                var uri = new Uri(fullPath, UriKind.Absolute);
                var rawBitmap = new BitmapImage();
                rawBitmap.BeginInit();
                rawBitmap.UriSource = uri;
                rawBitmap.CacheOption = BitmapCacheOption.OnLoad;
                rawBitmap.EndInit();
                rawBitmap.Freeze();

                var transparentBitmap = MakeBackgroundTransparent(rawBitmap);
                transparentBitmap.Freeze();

                SliceFramesWithAutoBoundingBoxes(transparentBitmap);
                return WalkFrames.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static WriteableBitmap MakeBackgroundTransparent(BitmapSource source)
        {
            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            int stride = width * 4;
            byte[] pixelData = new byte[height * stride];
            formatted.CopyPixels(pixelData, stride, 0);

            // Sample background color from top-left (0,0)
            int bgB = pixelData[0];
            int bgG = pixelData[1];
            int bgR = pixelData[2];

            for (int i = 0; i < pixelData.Length; i += 4)
            {
                byte b = pixelData[i + 0];
                byte g = pixelData[i + 1];
                byte r = pixelData[i + 2];

                int dr = r - bgR;
                int dg = g - bgG;
                int db = b - bgB;
                double dist = Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
                int luminance = (r * 299 + g * 587 + b * 114) / 1000;

                if (dist < 42 || luminance < 65)
                {
                    pixelData[i + 3] = 0; // Transparent
                }
                else if (dist < 60)
                {
                    double alphaFactor = (dist - 42.0) / 18.0;
                    pixelData[i + 3] = (byte)(255 * Math.Clamp(alphaFactor, 0.0, 1.0));
                }
                else
                {
                    pixelData[i + 3] = 255;
                }
            }

            var writeable = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            writeable.WritePixels(new Int32Rect(0, 0, width, height), pixelData, stride, 0);
            return writeable;
        }

        private void SliceFramesWithAutoBoundingBoxes(BitmapSource source)
        {
            WalkFrames.Clear();
            SprintFrames.Clear();
            StrikeFrames.Clear();

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width * 4;
            byte[] pixelData = new byte[height * stride];
            source.CopyPixels(pixelData, stride, 0);

            bool[,] fg = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte a = pixelData[rowOffset + (x * 4) + 3];
                    fg[x, y] = a > 40;
                }
            }

            int rows = 3;
            int cols = 4;
            int cellW = width / cols;
            int cellH = height / rows;

            // Crop and clean Walk frames (Cols 0-1)
            WalkFrames.Add(ExtractCenteredFrame(source, fg, 0 * cellW, 0 * cellH, cellW, cellH));
            WalkFrames.Add(ExtractCenteredFrame(source, fg, 1 * cellW, 0 * cellH, cellW, cellH));
            WalkFrames.Add(ExtractCenteredFrame(source, fg, 0 * cellW, 1 * cellH, cellW, cellH));
            WalkFrames.Add(ExtractCenteredFrame(source, fg, 1 * cellW, 1 * cellH, cellW, cellH));

            // Sprint frames (Cols 2-3)
            SprintFrames.Add(ExtractCenteredFrame(source, fg, 2 * cellW, 0 * cellH, cellW, cellH));
            SprintFrames.Add(ExtractCenteredFrame(source, fg, 3 * cellW, 0 * cellH, cellW, cellH));
            SprintFrames.Add(ExtractCenteredFrame(source, fg, 2 * cellW, 1 * cellH, cellW, cellH));
            SprintFrames.Add(ExtractCenteredFrame(source, fg, 3 * cellW, 1 * cellH, cellW, cellH));

            // Strike frames (Row 2, right 3 frames)
            int strikeColW = (width / 2) / 3;
            int strikeStartX = width / 2;
            StrikeFrames.Add(ExtractCenteredFrame(source, fg, strikeStartX + (0 * strikeColW), 2 * cellH, strikeColW, cellH));
            StrikeFrames.Add(ExtractCenteredFrame(source, fg, strikeStartX + (1 * strikeColW), 2 * cellH, strikeColW, cellH));
            StrikeFrames.Add(ExtractCenteredFrame(source, fg, strikeStartX + (2 * strikeColW), 2 * cellH, strikeColW, cellH));
        }

        private static BitmapSource ExtractCenteredFrame(BitmapSource source, bool[,] fg, int startX, int startY, int w, int h)
        {
            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            int fgCount = 0;

            int endX = Math.Min(source.PixelWidth - 1, startX + w);
            int endY = Math.Min(source.PixelHeight - 1, startY + h);

            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    if (fg[x, y])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        fgCount++;
                    }
                }
            }

            const int canvasSize = 130;
            var rtb = new RenderTargetBitmap(canvasSize, canvasSize, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();

            if (fgCount > 50 && minX <= maxX && minY <= maxY)
            {
                int pad = 3;
                int cropX = Math.Max(0, minX - pad);
                int cropY = Math.Max(0, minY - pad);
                int cropW = Math.Min(source.PixelWidth - cropX, (maxX - minX + 1) + (pad * 2));
                int cropH = Math.Min(source.PixelHeight - cropY, (maxY - minY + 1) + (pad * 2));

                var cropped = new CroppedBitmap(source, new Int32Rect(cropX, cropY, cropW, cropH));
                using (var dc = dv.RenderOpen())
                {
                    double drawW = cropW;
                    double drawH = cropH;

                    double maxDim = canvasSize - 16;
                    if (drawW > maxDim || drawH > maxDim)
                    {
                        double scale = Math.Min(maxDim / drawW, maxDim / drawH);
                        drawW *= scale;
                        drawH *= scale;
                    }

                    double drawX = (canvasSize - drawW) / 2.0;
                    double drawY = (canvasSize - 16) - drawH; // Feet alignment
                    dc.DrawImage(cropped, new Rect(drawX, drawY, drawW, drawH));
                }
            }

            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
