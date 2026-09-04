using System;
using System.Collections.Generic;
using copperInspection.Detection;
using OpenCvSharp;

namespace copperInspection
{
    /// <summary>Everything the engine needs to inspect one frame, snapshotted
    /// so nothing can change underneath a run in progress.</summary>
    public sealed class InspectionSettings
    {
        public required bool UseDiff { get; init; }
        public string? BackgroundPath { get; init; }
        public required SilhouetteParams Silhouette { get; init; }
        public required DiffParams Diff { get; init; }

        /// <summary>"ColorDiff", "PatchCore-R18" or "PatchCore-R50".</summary>
        public required string Method { get; init; }

        public double ColorThreshold { get; init; } = 80.0;
        public double ColorMinAreaPct { get; init; } = 0.5;
        public int ColorMorph { get; init; } = 5;
        public Scalar ReferenceBgr { get; init; }

        /// <summary>
        /// Optional cheap screen run before <see cref="Method"/>. When set, the
        /// screen runs on every frame and the model only runs on frames the
        /// screen finds suspicious. The screen must be tuned to over-trigger:
        /// anything it clears never reaches the model.
        /// </summary>
        public string? ScreenMethod { get; init; }

        /// <summary>
        /// Build the settings for a strip-scan segment from the scan config.
        /// The two-stage screen is turned on here and only here, so flipping
        /// Scan.Inspection.TwoStage.Enabled in config.json is all it takes -
        /// and a manual Run Detection is unaffected, because it never goes
        /// through this path.
        /// </summary>
        public static InspectionSettings ForScan(
            Scan.ScanConfig scan,
            bool useDiff,
            string? backgroundPath,
            SilhouetteParams silhouette,
            DiffParams diff,
            double colorThreshold,
            double colorMinAreaPct,
            int colorMorph,
            Scalar referenceBgr) => new()
        {
            UseDiff = useDiff,
            BackgroundPath = backgroundPath,
            Silhouette = silhouette,
            Diff = diff,
            Method = scan.Inspection.Method,
            ColorThreshold = colorThreshold,
            ColorMinAreaPct = colorMinAreaPct,
            ColorMorph = colorMorph,
            ReferenceBgr = referenceBgr,
            ScreenMethod = scan.Inspection.TwoStage.Enabled
                ? scan.Inspection.TwoStage.ScreenMethod
                : null,
        };

        public InspectionSettings With(string method) => new()
        {
            UseDiff = UseDiff,
            BackgroundPath = BackgroundPath,
            Silhouette = Silhouette,
            Diff = Diff,
            Method = method,
            ColorThreshold = ColorThreshold,
            ColorMinAreaPct = ColorMinAreaPct,
            ColorMorph = ColorMorph,
            ReferenceBgr = ReferenceBgr,
            ScreenMethod = null,
        };
    }

    /// <summary>
    /// The four images and the verdict from one inspection. The caller owns
    /// every Mat here and must dispose them.
    /// </summary>
    public sealed class InspectionResult : IDisposable
    {
        public required Mat Original { get; init; }
        public required Mat BackgroundSubtracted { get; init; }
        public required Mat Heatmap { get; init; }
        public required Mat Overlay { get; init; }

        /// <summary>"GOOD", "BAD" or "N/A".</summary>
        public required string Verdict { get; init; }

        /// <summary>Anomaly score for PatchCore, defect area percentage for
        /// ColorDiff. Interpret with <see cref="Method"/>.</summary>
        public required double Score { get; init; }

        public required string Method { get; init; }

        /// <summary>Defect bounding boxes in pixels. Empty for PatchCore,
        /// which scores rather than localises.</summary>
        public IReadOnlyList<Rect> Boxes { get; init; } = Array.Empty<Rect>();

        public string BackgroundNote { get; init; } = "";

        /// <summary>Set when the model's recorded training settings overrode
        /// the requested segmentation settings.</summary>
        public string? SegmentationNote { get; init; }

        /// <summary>Set when a two-stage screen cleared the frame without
        /// running the model.</summary>
        public string? ScreenNote { get; init; }

        /// <summary>The segmentation settings that ACTUALLY ran, which may
        /// differ from what was asked for - the caller reflects these back onto
        /// the UI so what is displayed always matches what the model saw.</summary>
        public SilhouetteParams? AppliedSilhouette { get; init; }
        public DiffParams? AppliedDiff { get; init; }

        public bool IsDefect => Verdict == "BAD";

        public void Dispose()
        {
            Original.Dispose();
            BackgroundSubtracted.Dispose();
            Heatmap.Dispose();
            Overlay.Dispose();
        }
    }

    /// <summary>
    /// The inspection pipeline with no UI attached: background subtraction,
    /// then detection, then the four result images.
    ///
    /// Extracted out of MainWindow so a manually captured frame and a scanned
    /// strip segment go through EXACTLY the same code. That matters more than
    /// it looks: a second, parallel copy of this logic drifting away from the
    /// first is precisely the class of bug documented in
    /// docs/PATCHCORE_R50_FLOW_COMPARISON.md, where segmentation settings that
    /// disagreed with the model's training produced wrong verdicts.
    ///
    /// Lives in the main process alongside ONNX Runtime. The camera stays
    /// behind CameraBridge.exe and neoAPI never enters this project - see
    /// docs/CAMERA_ARCHITECTURE.md.
    /// </summary>
    public static class InspectionEngine
    {
        /// <summary>
        /// Inspect one frame.
        /// </summary>
        /// <param name="loadTarget">Given whether the run is PatchCore (which
        /// needs native resolution), returns the image to inspect. The result
        /// takes ownership of what this returns.</param>
        /// <param name="settings">Snapshot of how to run. Never read live UI
        /// state in here.</param>
        /// <param name="getDetector">Supplies a loaded PatchCore detector for a
        /// backbone name. Kept as a callback so the engine does not own the
        /// model's lifetime - loading it is expensive and the caller caches it.</param>
        public static InspectionResult Run(
            Func<bool, Mat> loadTarget,
            InspectionSettings settings,
            Func<string, PatchCoreDetector> getDetector)
        {
            // Two-stage: run the cheap screen first and only escalate frames it
            // finds suspicious. Most strip is good, so this skips the model on
            // the large majority of frames.
            if (!string.IsNullOrEmpty(settings.ScreenMethod) &&
                !string.Equals(settings.ScreenMethod, settings.Method, StringComparison.OrdinalIgnoreCase))
            {
                InspectionResult screen = RunSingle(loadTarget, settings.With(settings.ScreenMethod!), getDetector);
                if (!screen.IsDefect)
                {
                    return new InspectionResult
                    {
                        Original = screen.Original,
                        BackgroundSubtracted = screen.BackgroundSubtracted,
                        Heatmap = screen.Heatmap,
                        Overlay = screen.Overlay,
                        Verdict = screen.Verdict,
                        Score = screen.Score,
                        Method = settings.ScreenMethod!,
                        Boxes = screen.Boxes,
                        BackgroundNote = screen.BackgroundNote,
                        SegmentationNote = screen.SegmentationNote,
                        ScreenNote = $"cleared by {settings.ScreenMethod} - model not run",
                        AppliedSilhouette = screen.AppliedSilhouette,
                        AppliedDiff = screen.AppliedDiff,
                    };
                }

                // Suspicious: throw the screen's images away and let the model
                // produce the authoritative verdict.
                screen.Dispose();
            }

            return RunSingle(loadTarget, settings, getDetector);
        }

        private static InspectionResult RunSingle(
            Func<bool, Mat> loadTarget,
            InspectionSettings settings,
            Func<string, PatchCoreDetector> getDetector)
        {
            bool isPatchCore = settings.Method.StartsWith("PatchCore", StringComparison.Ordinal);

            // Work on copies: the caller's settings objects are a snapshot and
            // the model's recorded settings may override them below, which must
            // not leak back out into the caller's state.
            var silhouette = Clone(settings.Silhouette);
            var diff = Clone(settings.Diff);

            string? segmentationNote = null;
            bool skipSegmentation = false;

            // For PatchCore, load the detector FIRST so its recorded
            // segmentation settings - the exact settings its training images
            // were built with - can override whatever was asked for. This is
            // what stops segmentation from silently drifting away from what the
            // model was actually trained on, which has produced wrong verdicts
            // more than once (docs/PATCHCORE_R50_FLOW_COMPARISON.md). Applied
            // every run, not just once, so a stray tweak between runs cannot
            // reintroduce the drift.
            if (isPatchCore)
            {
                PatchCoreDetector.RecordedSegmentationSettings? rec =
                    getDetector(settings.Method).SegmentationSettings;

                if (rec != null)
                {
                    if (rec.Mode == "Silhouette")
                    {
                        if (settings.UseDiff)
                            throw new InvalidOperationException(
                                "This model was trained with Silhouette segmentation, but " +
                                "Reference-Image Diff is selected. Switch to Silhouette mode and retry.");

                        silhouette.Flatten = rec.Flatten ?? silhouette.Flatten;
                        silhouette.AutoThreshold = rec.AutoThreshold ?? silhouette.AutoThreshold;
                        silhouette.Threshold = rec.Threshold ?? silhouette.Threshold;
                        silhouette.Morph = rec.Morph ?? silhouette.Morph;
                        silhouette.Keep = rec.Keep ?? silhouette.Keep;
                        silhouette.Fill = rec.Fill ?? silhouette.Fill;
                        segmentationNote = "using the model's recorded training settings";
                    }
                    else if (rec.Mode == "ReferenceDiff")
                    {
                        if (!settings.UseDiff || settings.BackgroundPath == null)
                            throw new InvalidOperationException(
                                "This model was trained with Reference-Image Diff segmentation. " +
                                "Switch to that mode, pick a background image, and retry.");

                        diff.Blur = rec.DiffBlur ?? diff.Blur;
                        diff.Threshold = rec.DiffThreshold ?? diff.Threshold;
                        diff.Morph = rec.DiffMorph ?? diff.Morph;
                        diff.Keep = rec.DiffKeep ?? diff.Keep;
                        diff.MatchExposure = rec.DiffMatchExposure ?? diff.MatchExposure;
                        segmentationNote = "using the model's recorded training settings";
                    }
                    else if (rec.Mode == "None")
                    {
                        skipSegmentation = true;
                        segmentationNote = "segmentation skipped - model was trained on raw, unsegmented images";
                    }
                }
            }

            Mat target = loadTarget(isPatchCore);
            Mat? bgSubtracted = null, heatmap = null, overlay = null;
            BackgroundResult? bgResult = null;

            try
            {
                // ── 1. Background subtraction ──
                if (skipSegmentation)
                {
                    bgResult = new BackgroundResult
                    {
                        Result = target.Clone(),
                        Mask = new Mat(target.Size(), MatType.CV_8UC1, Scalar.All(255)),
                        Note = "No segmentation (model trained on raw images)",
                    };
                }
                else if (settings.UseDiff)
                {
                    using Mat bg = isPatchCore
                        ? ImageLoader.LoadFull(settings.BackgroundPath!)
                        : ImageLoader.LoadResized(settings.BackgroundPath!);
                    bgResult = BackgroundSubtraction.SubtractBackground(bg, target, diff);
                }
                else
                {
                    bgResult = BackgroundSubtraction.SegmentSilhouette(target, silhouette);
                }

                bgSubtracted = bgResult.Result.Clone();

                // ── 2. Detection ──
                string verdict;
                double score;
                IReadOnlyList<Rect> boxes = Array.Empty<Rect>();

                if (isPatchCore)
                {
                    PatchCoreDetector detector = getDetector(settings.Method);

                    // The overlay base is the raw photo, not the
                    // background-subtracted view: the heatmap is blended onto
                    // what the camera actually saw.
                    (bool isDefect, float anomalyScore, Mat heatmapRaw, Mat heatmapOnOriginal) =
                        detector.Inspect(bgResult.Result, target);

                    verdict = isDefect ? "BAD" : "GOOD";
                    score = anomalyScore;
                    heatmap = heatmapRaw;
                    overlay = heatmapOnOriginal;
                }
                else
                {
                    using ColorDiffResult color = ColorDiffDetector.Inspect(
                        bgResult.Result, settings.ReferenceBgr,
                        settings.ColorThreshold, settings.ColorMinAreaPct, settings.ColorMorph);

                    verdict = color.Verdict;
                    score = color.DefectPct;
                    boxes = BoxesFromMask(color.DefectMask);
                    heatmap = ColorDiffDetector.DistanceHeatmap(
                        bgResult.Result, color.DistanceMap, color.ContentMask);
                    overlay = ColorDiffDetector.MaskOverlay(bgResult.Result, color.DefectMask);
                }

                var result = new InspectionResult
                {
                    Original = target,
                    BackgroundSubtracted = bgSubtracted,
                    Heatmap = heatmap,
                    Overlay = overlay,
                    Verdict = verdict,
                    Score = score,
                    Method = settings.Method,
                    Boxes = boxes,
                    BackgroundNote = bgResult.Note,
                    SegmentationNote = segmentationNote,
                    AppliedSilhouette = silhouette,
                    AppliedDiff = diff,
                };

                // Ownership has transferred to the result; don't let the
                // failure path below dispose them.
                target = null!;
                bgSubtracted = null;
                heatmap = null;
                overlay = null;
                return result;
            }
            catch
            {
                target?.Dispose();
                bgSubtracted?.Dispose();
                heatmap?.Dispose();
                overlay?.Dispose();
                throw;
            }
            finally
            {
                bgResult?.Dispose();
            }
        }

        /// <summary>
        /// Defect bounding boxes from a 0/255 mask. These are what become
        /// millimetre positions on the strip, so a defect can be reported as
        /// "90 mm to 100 mm" rather than just "somewhere in frame 7".
        /// </summary>
        private static IReadOnlyList<Rect> BoxesFromMask(Mat defectMask)
        {
            Cv2.FindContours(defectMask, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var boxes = new List<Rect>(contours.Length);
            foreach (Point[] contour in contours)
            {
                Rect box = Cv2.BoundingRect(contour);
                if (box.Width > 0 && box.Height > 0) boxes.Add(box);
            }
            return boxes;
        }

        private static SilhouetteParams Clone(SilhouetteParams p) => new()
        {
            Flatten = p.Flatten,
            AutoThreshold = p.AutoThreshold,
            Threshold = p.Threshold,
            Morph = p.Morph,
            Keep = p.Keep,
            Fill = p.Fill,
        };

        private static DiffParams Clone(DiffParams p) => new()
        {
            Blur = p.Blur,
            Threshold = p.Threshold,
            Morph = p.Morph,
            Keep = p.Keep,
            MatchExposure = p.MatchExposure,
            AllowResize = p.AllowResize,
        };
    }
}
