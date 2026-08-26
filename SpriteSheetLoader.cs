using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkCat
{
    /// <summary>
    /// Loads, chroma-keys background transparency, and slices the cat sprite sheet into animation frames.
    /// </summary>
    public class SpriteSheetLoader
    {
        public List<CroppedBitmap> WalkFrames { get; } = new();
        public List<CroppedBitmap> SprintFrames { get; } = new();
        public List<CroppedBitmap> StrikeFrames { get; } = new();

        public bool IsLoaded => WalkFrames.Count > 0;

        public bool LoadAndSlice(string relativePath = "Assets/cat_spritesheet.jpg")
        {
            try
            {
                string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
                if (!File.Exists(fullPath))
                {
                    // Check direct directory
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

                // Process chroma-key transparency (Remove dark background)
                var transparentBitmap = MakeBackgroundTransparent(rawBitmap);
                transparentBitmap.Freeze();

                SliceFrames(transparentBitmap);
                return true;
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

                // Compute color distance to background
                int dr = r - bgR;
                int dg = g - bgG;
                int db = b - bgB;
                double dist = Math.Sqrt((dr * dr) + (dg * dg) + (db * db));

                // Brightness / cream detection: Cat body is cream (#F6F3E7)
                int luminance = (r * 299 + g * 587 + b * 114) / 1000;

                if (dist < 42 || luminance < 65)
                {
                    pixelData[i + 3] = 0; // Transparent
                }
                else if (dist < 60)
                {
                    // Smooth edge antialiasing
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

        private void SliceFrames(BitmapSource source)
        {
            WalkFrames.Clear();
            SprintFrames.Clear();
            StrikeFrames.Clear();

            int w = source.PixelWidth;
            int h = source.PixelHeight;

            // Grid bounds based on generated sprite sheet layout:
            // 3 rows, 4 main horizontal sectors
            int cellW = w / 4;
            int cellH = h / 3;

            // Slicing Walk Cycle (Col 0 and Col 1 across rows)
            // Frame 1: Row 0, Col 0
            WalkFrames.Add(CreateFrame(source, 0 * cellW, 0 * cellH, cellW, cellH));
            // Frame 2: Row 0, Col 1
            WalkFrames.Add(CreateFrame(source, 1 * cellW, 0 * cellH, cellW, cellH));
            // Frame 3: Row 1, Col 0
            WalkFrames.Add(CreateFrame(source, 0 * cellW, 1 * cellH, cellW, cellH));
            // Frame 4: Row 1, Col 1
            WalkFrames.Add(CreateFrame(source, 1 * cellW, 1 * cellH, cellW, cellH));

            // Slicing Sprint Cycle (Col 2 and Col 3 across row 0 and 1)
            SprintFrames.Add(CreateFrame(source, 2 * cellW, 0 * cellH, cellW, cellH));
            SprintFrames.Add(CreateFrame(source, 3 * cellW, 0 * cellH, cellW, cellH));
            SprintFrames.Add(CreateFrame(source, 2 * cellW, 1 * cellH, cellW, cellH));
            SprintFrames.Add(CreateFrame(source, 3 * cellW, 1 * cellH, cellW, cellH));

            // Slicing Strike Swipe Cycle (Row 2, Right side 3 frames)
            int strikeColW = (w / 2) / 3;
            int strikeStartX = w / 2;
            StrikeFrames.Add(CreateFrame(source, strikeStartX + (0 * strikeColW), 2 * cellH, strikeColW, cellH));
            StrikeFrames.Add(CreateFrame(source, strikeStartX + (1 * strikeColW), 2 * cellH, strikeColW, cellH));
            StrikeFrames.Add(CreateFrame(source, strikeStartX + (2 * strikeColW), 2 * cellH, strikeColW, cellH));
        }

        private static CroppedBitmap CreateFrame(BitmapSource source, int x, int y, int width, int height)
        {
            // Pad inner margin slightly to avoid neighboring sprite bleed
            int padX = (int)(width * 0.04);
            int padY = (int)(height * 0.04);

            int finalX = Math.Max(0, x + padX);
            int finalY = Math.Max(0, y + padY);
            int finalW = Math.Min(source.PixelWidth - finalX, width - (padX * 2));
            int finalH = Math.Min(source.PixelHeight - finalY, height - (padY * 2));

            var cropped = new CroppedBitmap(source, new Int32Rect(finalX, finalY, finalW, finalH));
            cropped.Freeze();
            return cropped;
        }
    }
}
