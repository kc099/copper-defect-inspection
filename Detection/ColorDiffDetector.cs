using System;
using System.IO;
using System.Text.Json;
using OpenCvSharp;

namespace copperInspection
{
    /// <summary>
    /// Port of color_diff_inspector.py. See COLOR_DIFF_INFERENCE.md in the
    /// Python project for the plan this implements, the measured numbers the
    /// default threshold is based on, and its known limits (in particular:
    /// not yet validated against confirmed defect samples).
    /// </summary>
    public sealed class ColorDiffResult : IDisposable
    {
        public required string Verdict { get; init; }       // "GOOD", "BAD", or "N/A"
        public double DefectPct { get; init; }
        public required Mat DistanceMap { get; init; }        // float32, same size as source
        public required Mat DefectMask { get; init; }         // uint8 0/255, cleaned + clipped to content
        public required Mat ContentMask { get; init; }        // uint8 0/255

        public void Dispose()
        {
            DistanceMap.Dispose();
            DefectMask.Dispose();
            ContentMask.Dispose();
        }
    }

    public sealed class ReferenceColor
    {
        public required Scalar Bgr { get; init; }
        public double Threshold { get; init; } = 80.0;
        public double MinAreaPct { get; init; } = 0.5;
        public int Morph { get; init; } = 5;
    }

    public static class ColorDiffDetector
    {
        /// <summary>Loads a reference_color.json written by
        /// color_diff_inspector.py's "Save..." button.</summary>
        public static ReferenceColor LoadReference(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var bgrArr = root.GetProperty("reference_bgr");
            double b = bgrArr[0].GetDouble();
            double g = bgrArr[1].GetDouble();
            double r = bgrArr[2].GetDouble();

            return new ReferenceColor
            {
                Bgr = new Scalar(b, g, r),
                Threshold = root.TryGetProperty("threshold", out var t) ? t.GetDouble() : 80.0,
                MinAreaPct = root.TryGetProperty("min_area_pct", out var m) ? m.GetDouble() : 0.5,
                Morph = root.TryGetProperty("morph", out var k) ? k.GetInt32() : 5,
            };
        }

        /// <summary>Every content pixel's distance from the reference colour,
        /// thresholded into a candidate defect mask, cleaned, and turned into a
        /// GOOD/BAD verdict from how much of the strip it covers.</summary>
        public static ColorDiffResult Inspect(Mat img, Scalar referenceBgr,
                                              double threshold, double minAreaPct, int morph)
        {
            Mat[] channels = Cv2.Split(img);
            using Mat maxCh = new();
            Cv2.Max(channels[0], channels[1], maxCh);
            Cv2.Max(maxCh, channels[2], maxCh);
            foreach (var c in channels) c.Dispose();

            Mat contentMask = new();
            Cv2.Threshold(maxCh, contentMask, 0, 255, ThresholdTypes.Binary);
            int contentCount = Cv2.CountNonZero(contentMask);

            if (contentCount == 0)
            {
                return new ColorDiffResult
                {
                    Verdict = "N/A",
                    DefectPct = 0.0,
                    DistanceMap = new Mat(img.Size(), MatType.CV_32FC1, Scalar.All(0)),
                    DefectMask = Mat.Zeros(img.Size(), MatType.CV_8UC1),
                    ContentMask = contentMask,
                };
            }

            using Mat imgF = new();
            img.ConvertTo(imgF, MatType.CV_32FC3);
            using Mat diffF = new();
            Cv2.Subtract(imgF, referenceBgr, diffF);

            Mat[] diffCh = Cv2.Split(diffF);
            using Mat sq0 = new(); using Mat sq1 = new(); using Mat sq2 = new();
            Cv2.Multiply(diffCh[0], diffCh[0], sq0);
            Cv2.Multiply(diffCh[1], diffCh[1], sq1);
            Cv2.Multiply(diffCh[2], diffCh[2], sq2);
            foreach (var c in diffCh) c.Dispose();

            using Mat sum01 = new();
            Cv2.Add(sq0, sq1, sum01);
            using Mat sumSq = new();
            Cv2.Add(sum01, sq2, sumSq);

            Mat distanceMap = new();
            Cv2.Sqrt(sumSq, distanceMap);

            using Mat overThresh = new();
            Cv2.Threshold(distanceMap, overThresh, threshold, 255, ThresholdTypes.Binary);
            using Mat overThresh8 = new();
            overThresh.ConvertTo(overThresh8, MatType.CV_8UC1);

            using Mat rawMask = new();
            Cv2.BitwiseAnd(overThresh8, contentMask, rawMask);

            using Mat cleaned = BackgroundSubtraction.Cleanup(rawMask, morph);
            Mat defectMask = new();
            Cv2.BitwiseAnd(cleaned, contentMask, defectMask);

            int defectCount = Cv2.CountNonZero(defectMask);
            double defectPct = (double)defectCount / contentCount * 100.0;
            string verdict = defectPct >= minAreaPct ? "BAD" : "GOOD";

            return new ColorDiffResult
            {
                Verdict = verdict,
                DefectPct = defectPct,
                DistanceMap = distanceMap,
                DefectMask = defectMask,
                ContentMask = contentMask,
            };
        }

        /// <summary>Distance-from-reference, colour-mapped and blended over the
        /// source image so a person can see where the deviation actually is.</summary>
        public static Mat DistanceHeatmap(Mat bgrImage, Mat distanceMap, Mat contentMask, double alpha = 0.55)
        {
            Cv2.MinMaxLoc(distanceMap, out _, out double maxVal, out _, out _, contentMask);
            if (maxVal < 1e-6) maxVal = 1e-6;

            using Mat norm = new();
            distanceMap.ConvertTo(norm, MatType.CV_8UC1, 255.0 / maxVal);
            Mat colored = new();
            Cv2.ApplyColorMap(norm, colored, ColormapTypes.Inferno);

            Mat blended = new();
            Cv2.AddWeighted(bgrImage, 1 - alpha, colored, alpha, 0, blended);
            colored.Dispose();

            using (Mat inv = new())
            {
                Cv2.BitwiseNot(contentMask, inv);
                blended.SetTo(Scalar.Black, inv);
            }
            return blended;
        }

        /// <summary>The cleaned defect mask outlined in red over the source image.</summary>
        public static Mat MaskOverlay(Mat bgrImage, Mat defectMask)
        {
            Mat marked = bgrImage.Clone();
            marked.SetTo(new Scalar(0, 0, 255), defectMask);
            Mat blended = new();
            Cv2.AddWeighted(bgrImage, 0.5, marked, 0.5, 0, blended);
            marked.Dispose();
            return blended;
        }

    }
}
