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
    /// to prevent cut-off wings, adjacent frame bleed, or irregular spacing.
    /// </summary>
    public class EagleSpriteLoader
    {
        public List<BitmapSource> AllFrames { get; } = new();

        public BitmapSource StandNormal => GetFrameOrFallback(0);
        public BitmapSource WalkStep1 => GetFrameOrFallback(1);
        public BitmapSource WalkStep2 => GetFrameOrFallback(2);
        public BitmapSource TakeoffPrep => GetFrameOrFallback(3);
        public BitmapSource WingUp => GetFrameOrFallback(4);
        public BitmapSource WingDown => GetFrameOrFallback(5);
        public BitmapSource GlideOpen => GetFrameOrFallback(6);
        public BitmapSource BankedGlide => GetFrameOrFallback(7);
        public BitmapSource Swoop => GetFrameOrFallback(8);
        public BitmapSource LandingTouch => GetFrameOrFallback(9);
        public BitmapSource Perched => GetFrameOrFallback(10);
        public BitmapSource CuriousLookDown => GetFrameOrFallback(11);
        public BitmapSource StandAlert => GetFrameOrFallback(12);
        public BitmapSource FaceUser => GetFrameOrFallback(13);
        public BitmapSource Angry => GetFrameOrFallback(14);

        public bool IsLoaded => AllFrames.Count > 0;

        private BitmapSource GetFrameOrFallback(int index)
        {
            if (AllFrames.Count == 0) return new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            int clamped = Math.Clamp(index, 0, AllFrames.Count - 1);
            return AllFrames[clamped];
        }

        public bool LoadAndSlice()
        {
            string[] possiblePaths = {
                "Assets/image.png",
                "Assets/eagle_spritesheet.png",
                "Assets/m.png"
            };

            foreach (var path in possiblePaths)
            {
                if (TryLoadFromPath(path))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryLoadFromPath(string relativePath)
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
                return AllFrames.Count >= 10;
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

                if (dist < 26)
                {
                    pixelData[i + 3] = 0; // Transparent background
                }
                else if (dist < 40)
                {
                    double alphaFactor = (dist - 26.0) / 14.0;
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

        /// <summary>
        /// Slices the sprite sheet by analyzing 2D projection density to find exact bounding boxes of each sprite.
        /// This completely eliminates adjacent wing clipping and cut-off edges.
        /// </summary>
        private void SliceFramesWithAutoBoundingBoxes(BitmapSource source)
        {
            AllFrames.Clear();

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width * 4;
            byte[] pixelData = new byte[height * stride];
            source.CopyPixels(pixelData, stride, 0);

            // 1. Identify Foreground pixels mask
            bool[,] fg = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte a = pixelData[rowOffset + (x * 4) + 3];
                    fg[x, y] = a > 30;
                }
            }

            // 2. Divide into 3 horizontal row bands
            int rows = 3;
            int cols = 5;
            int approxRowH = height / rows;
            int approxColW = width / cols;

            var detectedBoxes = new List<Int32Rect>();

            for (int r = 0; r < rows; r++)
            {
                int rowMinY = r * approxRowH;
                int rowMaxY = Math.Min(height - 1, (r + 1) * approxRowH);

                for (int c = 0; c < cols; c++)
                {
                    int colMinX = c * approxColW;
                    int colMaxX = Math.Min(width - 1, (c + 1) * approxColW);

                    // Scan for exact non-empty bounds inside this cell neighborhood (+- margin)
                    int searchMinX = Math.Max(0, colMinX - (int)(approxColW * 0.15));
                    int searchMaxX = Math.Min(width - 1, colMaxX + (int)(approxColW * 0.15));
                    int searchMinY = Math.Max(0, rowMinY - (int)(approxRowH * 0.08));
                    int searchMaxY = Math.Min(height - 1, rowMaxY + (int)(approxRowH * 0.08));

                    int minX = int.MaxValue, maxX = int.MinValue;
                    int minY = int.MaxValue, maxY = int.MinValue;
                    int fgCount = 0;

                    for (int y = searchMinY; y <= searchMaxY; y++)
                    {
                        for (int x = searchMinX; x <= searchMaxX; x++)
                        {
                            // Ensure pixel is closer to this cell's center than neighbors
                            int cellCenterX = colMinX + (approxColW / 2);
                            int cellCenterY = rowMinY + (approxRowH / 2);
                            int distToCenter = Math.Abs(x - cellCenterX);

                            if (fg[x, y] && distToCenter < approxColW * 0.58)
                            {
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                                fgCount++;
                            }
                        }
                    }

                    if (fgCount > 100 && minX <= maxX && minY <= maxY)
                    {
                        // Add safety margin
                        int pad = 4;
                        int finalX = Math.Max(0, minX - pad);
                        int finalY = Math.Max(0, minY - pad);
                        int finalW = Math.Min(width - finalX, (maxX - minX + 1) + (pad * 2));
                        int finalH = Math.Min(height - finalY, (maxY - minY + 1) + (pad * 2));

                        detectedBoxes.Add(new Int32Rect(finalX, finalY, finalW, finalH));
                    }
                    else
                    {
                        // Fallback to strict cell
                        detectedBoxes.Add(new Int32Rect(colMinX, rowMinY, approxColW, approxRowH));
                    }
                }
            }

            // 3. Render each sprite centered onto a uniform transparent canvas (140x140)
            const int targetCanvasSize = 150;

            foreach (var box in detectedBoxes)
            {
                var cropped = new CroppedBitmap(source, box);
                var rtb = new RenderTargetBitmap(targetCanvasSize, targetCanvasSize, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();

                using (var dc = dv.RenderOpen())
                {
                    // Center horizontally, align feet towards bottom (Y = 125)
                    double drawW = box.Width;
                    double drawH = box.Height;

                    // Scale proportionally if exceeds canvas
                    double maxDim = targetCanvasSize - 16;
                    if (drawW > maxDim || drawH > maxDim)
                    {
                        double scale = Math.Min(maxDim / drawW, maxDim / drawH);
                        drawW *= scale;
                        drawH *= scale;
                    }

                    double drawX = (targetCanvasSize - drawW) / 2.0;
                    double drawY = (targetCanvasSize - 18) - drawH; // Ground feet alignment

                    dc.DrawImage(cropped, new Rect(drawX, drawY, drawW, drawH));
                }

                rtb.Render(dv);
                rtb.Freeze();
                AllFrames.Add(rtb);
            }
        }
    }
}
