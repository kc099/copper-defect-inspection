using System;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace copperInspection
{
    /// <summary>
    /// Port of bg_subtraction_app.py's core pipeline. Two ways to isolate the
    /// component from the blue backlit scene: Silhouette (one image - the
    /// component is dark against the background, so dark == foreground) or
    /// ReferenceDiff (two images - absdiff against a background plate).
    /// See PIPELINE.md in the Python project for the full rationale.
    /// </summary>
    public sealed class SilhouetteParams
    {
        public bool Flatten { get; set; } = true;
        public bool AutoThreshold { get; set; } = true;
        public int Threshold { get; set; } = 60;
        public int Morph { get; set; } = 5;
        public int Keep { get; set; } = 2;
        public bool Fill { get; set; } = true;
    }

    public sealed class DiffParams
    {
        public int Blur { get; set; } = 5;
        public int Threshold { get; set; } = 20;
        public int Morph { get; set; } = 5;
        public int Keep { get; set; } = 2;
        public bool MatchExposure { get; set; } = true;
        public bool AllowResize { get; set; } = false;
    }

    public sealed class BackgroundResult : IDisposable
    {
        public required Mat Result { get; init; }   // colour pixels kept, black elsewhere
        public required Mat Mask { get; init; }      // 0/255 mask, same size as the source
        public string Note { get; init; } = "";

        public void Dispose()
        {
            Result.Dispose();
            Mask.Dispose();
        }
    }

    public static class BackgroundSubtraction
    {
        /// <summary>Divides out the diffuser gradient so one threshold suits the
        /// whole frame - built at thumbnail scale, far cheaper than a large-sigma
        /// blur on a full-resolution image.</summary>
        public static Mat FlattenIllumination(Mat gray)
        {
            int h = gray.Rows, w = gray.Cols;
            double scale = 64.0 / Math.Max(h, w);
            int smallW = Math.Max(8, (int)(w * scale));
            int smallH = Math.Max(8, (int)(h * scale));

            using Mat small = new();
            Cv2.Resize(gray, small, new Size(smallW, smallH), interpolation: InterpolationFlags.Area);
            using Mat smallBlurred = new();
            Cv2.GaussianBlur(small, smallBlurred, new Size(0, 0), 3);
            using Mat illum = new();
            Cv2.Resize(smallBlurred, illum, new Size(w, h), interpolation: InterpolationFlags.Linear);

            using Mat grayF = new();
            gray.ConvertTo(grayF, MatType.CV_32FC1);
            using Mat illumF = new();
            illum.ConvertTo(illumF, MatType.CV_32FC1, 1.0, 1.0); // +1 so we never divide by 0

            using Mat flatF = new();
            Cv2.Divide(grayF, illumF, flatF, 128.0); // grayF / illumF * 128

            Mat flat = new();
            flatF.ConvertTo(flat, MatType.CV_8UC1); // ConvertTo saturates to [0,255]
            return flat;
        }

        /// <summary>Flood-fills in from the border and keeps whatever the flood
        /// can't reach - closes interior holes (specular highlights) without
        /// touching the mask's outer boundary.</summary>
        public static Mat FillHoles(Mat mask)
        {
            using Mat padded = new();
            Cv2.CopyMakeBorder(mask, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.Black);
            using Mat flooded = padded.Clone();
            using Mat ffMask = new(padded.Rows + 2, padded.Cols + 2, MatType.CV_8UC1, Scalar.Black);
            Cv2.FloodFill(flooded, ffMask, new Point(0, 0), Scalar.White);

            using Mat floodedInv = new();
            Cv2.BitwiseNot(flooded, floodedInv);
            using Mat holes = new(floodedInv, new Rect(1, 1, mask.Cols, mask.Rows));

            Mat result = new();
            Cv2.BitwiseOr(mask, holes, result);
            return result;
        }

        /// <summary>Labels every connected blob and keeps only the n biggest -
        /// the step that removes fixture edges and stray noise once threshold
        /// has produced far more blobs than the real component + mirror.</summary>
        public static (Mat mask, int found, int kept) KeepLargestRegions(Mat mask, int n)
        {
            using Mat labels = new();
            using Mat stats = new();
            using Mat centroids = new();
            int count = Cv2.ConnectedComponentsWithStats(
                mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
            int found = count - 1;
            if (found <= 0)
                return (mask.Clone(), 0, 0);

            var byArea = Enumerable.Range(1, found)
                .Select(i => (label: i, area: stats.At<int>(i, (int)ConnectedComponentsTypes.Area)))
                .OrderByDescending(t => t.area)
                .Take(n)
                .Select(t => t.label)
                .ToArray();

            Mat result = Mat.Zeros(mask.Size(), MatType.CV_8UC1);
            foreach (int label in byArea)
            {
                using Mat labelMask = new();
                Cv2.Compare(labels, label, labelMask, CmpTypes.EQ);
                Cv2.BitwiseOr(result, labelMask, result);
            }
            return (result, found, byArea.Length);
        }

        public static Mat Cleanup(Mat mask, int morph)
        {
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(morph, morph));
            using Mat opened = new();
            Cv2.MorphologyEx(mask, opened, MorphTypes.Open, kernel);
            Mat closed = new();
            Cv2.MorphologyEx(opened, closed, MorphTypes.Close, kernel);
            return closed;
        }

        /// <summary>Median of a single-channel byte Mat - OpenCvSharp has no
        /// built-in equivalent to numpy's median, so this sorts a flat copy.
        /// Fine for a one-time per-run call on a resized (&lt;=800px) image.</summary>
        private static double Median(Mat gray)
        {
            using Mat cont = gray.IsContinuous() ? gray : gray.Clone();
            int total = cont.Rows * cont.Cols;
            var data = new byte[total];
            Marshal.Copy(cont.Data, data, 0, total);
            Array.Sort(data);
            int mid = total / 2;
            return total % 2 == 0 ? (data[mid - 1] + data[mid]) / 2.0 : data[mid];
        }

        /// <summary>The component is dark against the backlight, so dark ==
        /// foreground. Single image, no background plate needed.</summary>
        public static BackgroundResult SegmentSilhouette(Mat fg, SilhouetteParams p)
        {
            using Mat gray = new();
            Cv2.CvtColor(fg, gray, ColorConversionCodes.BGR2GRAY);

            Mat work = p.Flatten ? FlattenIllumination(gray) : gray.Clone();
            using Mat workBlurred = new();
            Cv2.GaussianBlur(work, workBlurred, new Size(5, 5), 0);
            work.Dispose();

            Mat mask = new();
            double level = p.AutoThreshold
                ? Cv2.Threshold(workBlurred, mask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu)
                : Cv2.Threshold(workBlurred, mask, p.Threshold, 255, ThresholdTypes.BinaryInv);

            ReplaceWith(ref mask, Cleanup(mask, p.Morph));

            int found = 0, kept = 0;
            if (p.Keep > 0)
            {
                var (kMask, f, k) = KeepLargestRegions(mask, p.Keep);
                ReplaceWith(ref mask, kMask);
                found = f; kept = k;
            }
            if (p.Fill)
                ReplaceWith(ref mask, FillHoles(mask));

            Mat result = new();
            Cv2.BitwiseAnd(fg, fg, result, mask);

            string note = $"Threshold {level:F0}{(p.AutoThreshold ? " (auto)" : "")}";
            if (p.Keep > 0)
                note += $" | {found} regions found, kept {kept}";

            return new BackgroundResult { Result = result, Mask = mask, Note = note };
        }

        /// <summary>absdiff against a background plate, exposure-matched. Both
        /// frames must be pixel-aligned and the same size - see PIPELINE.md for
        /// why a mismatched pair produces noise instead of a real signal.</summary>
        public static BackgroundResult SubtractBackground(Mat bg, Mat fg, DiffParams p)
        {
            string note = "";

            Mat bgUse = bg;
            bool ownsBgUse = false;
            if (bg.Rows != fg.Rows || bg.Cols != fg.Cols)
            {
                if (!p.AllowResize)
                    throw new InvalidOperationException(
                        $"Background is {bg.Cols}x{bg.Rows} but the image is {fg.Cols}x{fg.Rows}.\n\n" +
                        "absdiff needs both frames the same size, shot from the same camera " +
                        "position - rescaling breaks pixel alignment and the result is mostly " +
                        "noise. Use matching frames, tick Allow Resize to override, or use " +
                        "Silhouette mode instead.");

                note = $"Background resized {bg.Cols}x{bg.Rows} -> {fg.Cols}x{fg.Rows}";
                bgUse = new Mat();
                Cv2.Resize(bg, bgUse, new Size(fg.Cols, fg.Rows), interpolation: InterpolationFlags.Area);
                ownsBgUse = true;
            }

            using Mat bgGray = new();
            using Mat fgGray = new();
            Cv2.CvtColor(bgUse, bgGray, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(fg, fgGray, ColorConversionCodes.BGR2GRAY);
            if (ownsBgUse) bgUse.Dispose();

            Mat bgGrayFinal = bgGray;
            bool ownsBgGrayFinal = false;
            if (p.MatchExposure)
            {
                double fgMedian = Median(fgGray);
                double bgMedian = Math.Max(Median(bgGray), 1.0);
                double gain = fgMedian / bgMedian;

                using Mat bgF = new();
                bgGray.ConvertTo(bgF, MatType.CV_32FC1);
                using Mat bgScaled = new();
                Cv2.Multiply(bgF, new Scalar(gain), bgScaled);
                Mat bgU8 = new();
                bgScaled.ConvertTo(bgU8, MatType.CV_8UC1);
                bgGrayFinal = bgU8;
                ownsBgGrayFinal = true;

                note += (note.Length > 0 ? " | " : "") + $"Exposure gain {gain:F2}";
            }

            int k = p.Blur | 1;
            using Mat bgBlur = new();
            using Mat fgBlur = new();
            Cv2.GaussianBlur(bgGrayFinal, bgBlur, new Size(k, k), 0);
            Cv2.GaussianBlur(fgGray, fgBlur, new Size(k, k), 0);
            if (ownsBgGrayFinal) bgGrayFinal.Dispose();

            using Mat diff = new();
            Cv2.Absdiff(fgBlur, bgBlur, diff);
            Mat mask = new();
            Cv2.Threshold(diff, mask, p.Threshold, 255, ThresholdTypes.Binary);
            ReplaceWith(ref mask, Cleanup(mask, p.Morph));

            if (p.Keep > 0)
            {
                var (kMask, found, kept) = KeepLargestRegions(mask, p.Keep);
                ReplaceWith(ref mask, kMask);
                note += (note.Length > 0 ? " | " : "") + $"{found} regions found, kept {kept}";
            }

            Mat result = new();
            Cv2.BitwiseAnd(fg, fg, result, mask);

            return new BackgroundResult { Result = result, Mask = mask, Note = note };
        }

        private static void ReplaceWith(ref Mat mat, Mat replacement)
        {
            mat.Dispose();
            mat = replacement;
        }
    }
}
