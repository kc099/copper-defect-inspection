using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using copperInspection.Detection;
using MongoDB.Bson;
using OpenCvSharp;

namespace copperInspection.Scan
{
    /// <summary>
    /// One segment's images, handed out live via
    /// <see cref="StripRunRecorder.SegmentImagesReady"/>. Every Mat here is
    /// only valid for the duration of that event - see its doc comment.
    /// </summary>
    /// <summary>Verdict travels with the images so a live view can show the
    /// GOOD/BAD banner per segment - SegmentRecord (the other per-segment
    /// event) only carries Outcome, i.e. whether the capture/inspection
    /// happened at all, not what it decided.</summary>
    public sealed record SegmentImages(
        int Segment, Mat Original, Mat BackgroundSubtracted, Mat Overlay,
        string Verdict, double Score, string Method);

    /// <summary>
    /// Owns the data side of one strip run: inspects each segment as it comes
    /// off the queue, writes its per-frame report, and at the end writes the
    /// run document with the merged defect list and an honest coverage flag.
    ///
    /// This is the piece that joins the three phases together -
    /// <see cref="ScanPipeline"/> supplies the frames, <see cref="InspectionEngine"/>
    /// judges them, and this turns the result into millimetre positions in the
    /// database. Plug <see cref="InspectSegmentAsync"/> in as the pipeline's
    /// consumer.
    /// </summary>
    public sealed class StripRunRecorder
    {
        private readonly ScanConfig _cfg;
        private readonly Func<InspectionSettings> _settingsFactory;
        private readonly Func<string, PatchCoreDetector> _getDetector;

        private readonly object _lock = new();
        private readonly List<DefectBox> _rawDefects = new();
        private int _defectSegments;
        private double _furthestMm;

        private StripRun? _run;

        public StripRunRecorder(
            ScanConfig cfg,
            Func<InspectionSettings> settingsFactory,
            Func<string, PatchCoreDetector> getDetector)
        {
            _cfg = cfg;
            _settingsFactory = settingsFactory;
            _getDetector = getDetector;
        }

        public StripRun? Run => _run;

        /// <summary>
        /// Fires right after each segment's DefectReport is written to
        /// MongoDB - purely for optional live viewing (e.g. pushing it into
        /// an already-open Reports window's live gallery, the same way
        /// MainWindow.SaveReportAsync does for a manual capture), never
        /// required for correctness: the report is in the database either
        /// way. Fires on the pipeline's background consumer thread.
        /// </summary>
        public event Action<DefectReport>? ReportSaved;

        /// <summary>
        /// Fires with every segment's images right after inspection - purely
        /// for optional live viewing (the background-mask window, and/or
        /// mirroring into the main window's result slots), never required for
        /// correctness.
        ///
        /// The Mats are disposed the instant this event returns (they belong
        /// to the "using" in <see cref="InspectSegmentAsync"/>), so a handler
        /// must fully consume them synchronously - e.g. convert to a WPF
        /// BitmapSource and Freeze() it - rather than storing a Mat itself or
        /// touching it from another thread later.
        /// </summary>
        public event Action<SegmentImages>? SegmentImagesReady;

        /// <summary>Open a run. Call once, before arming the pipeline.</summary>
        public async Task<ObjectId> StartAsync(string batchId)
        {
            lock (_lock)
            {
                _rawDefects.Clear();
                _defectSegments = 0;
                _furthestMm = 0;
            }

            // Best-effort: a missing index must not stop a run from happening.
            try { await StripRunStore.EnsureIndexesAsync(); } catch { }

            _run = new StripRun
            {
                BatchId = batchId,
                StartedUtc = DateTime.UtcNow,
                CoverageComplete = false,      // until proven otherwise at the end
                Verdict = "N/A",
                ConfigSnapshot = SnapshotConfig(),
            };

            return await StripRunStore.InsertAsync(_run);
        }

        /// <summary>
        /// The pipeline's consumer callback: inspect one captured frame and
        /// write its report. Throwing here marks just this segment as failed -
        /// <see cref="ScanPipeline"/> keeps the run going.
        /// </summary>
        public async Task InspectSegmentAsync(SegmentJob job)
        {
            if (_run == null) throw new InvalidOperationException("StartAsync must be called first.");

            InspectionSettings settings = _settingsFactory();

            // The engine takes ownership of what loadTarget returns, but the
            // job owns its frame and disposes it - so hand over a clone.
            using InspectionResult result = InspectionEngine.Run(
                _ => job.Frame.Clone(), settings, _getDetector);

            List<DefectBox> defects = MapDefects(job, result);

            // Still inside the "using" - these Mats are valid here and won't
            // be after this method moves on.
            SegmentImagesReady?.Invoke(new SegmentImages(
                job.Segment, result.Original, result.BackgroundSubtracted, result.Overlay,
                result.Verdict, result.Score, result.Method));

            lock (_lock)
            {
                _rawDefects.AddRange(defects);
                if (result.IsDefect) _defectSegments++;
                if (job.EndMm > _furthestMm) _furthestMm = job.EndMm;
            }

            byte[] png = ShouldStoreImage(result) ? Encode(result.Overlay) : Array.Empty<byte>();

            var report = new DefectReport
            {
                Timestamp = job.CapturedUtc,
                DefectCount = defects.Count,
                Method = result.Method,
                Mode = settings.UseDiff ? "ReferenceDiff" : "Silhouette",
                ReferenceImage = settings.BackgroundPath ?? string.Empty,
                TargetImage = $"segment {job.Segment}",
                ImageData = png,
                ResultImageBase64 = png.Length > 0
                    ? "data:image/png;base64," + Convert.ToBase64String(png)
                    : string.Empty,

                StripRunId = _run.Id,
                SegmentIndex = job.Segment,
                SegmentStartMm = job.StartMm,
                SegmentEndMm = job.EndMm,
                EncoderMmAtCapture = job.EncoderMm,
                TriggerErrorMm = job.TriggerErrorMm,
                LineSpeedMmPerSec = job.SpeedMmPerSec,
                Defects = defects,
            };

            await ReportStore.InsertAsync(report);
            ReportSaved?.Invoke(report);
        }

        /// <summary>
        /// Close the run: merge the defects, work out what was actually covered
        /// and write it all back.
        /// </summary>
        public async Task<StripRun> FinishAsync(
            IReadOnlyList<SegmentRecord> records, ScanEndReason reason)
        {
            if (_run == null) throw new InvalidOperationException("StartAsync must be called first.");

            List<DefectBox> merged;
            lock (_lock) merged = DefectMerger.Merge(_cfg, _rawDefects);

            // Anything that did not end up inspected is a piece of strip nobody
            // looked at, whatever the reason.
            List<int> missing = records
                .Where(r => r.Outcome != SegmentOutcome.Inspected)
                .Select(r => r.Segment)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            bool complete = missing.Count == 0 && reason != ScanEndReason.Fault;

            _run.EndedUtc = DateTime.UtcNow;
            _run.SegmentCount = records.Count(r => r.Outcome == SegmentOutcome.Inspected);
            _run.DefectSegmentCount = _defectSegments;
            _run.TotalLengthMm = _furthestMm;
            _run.MissingSegments = missing;
            _run.CoverageComplete = complete;
            _run.EndReason = reason.ToString();
            _run.Defects = merged;

            // "No defects seen" is not the same claim as "no defects present"
            // when frames went missing, so an incomplete run never reports GOOD.
            _run.Verdict = merged.Count > 0 ? "BAD"
                         : complete ? "GOOD"
                         : "INCOMPLETE";

            await StripRunStore.ReplaceAsync(_run);
            return _run;
        }

        // ── helpers ───────────────────────────────────────────────────────

        private List<DefectBox> MapDefects(SegmentJob job, InspectionResult result)
        {
            var defects = new List<DefectBox>(result.Boxes.Count);
            foreach (Rect box in result.Boxes)
            {
                defects.Add(DefectMapper.Map(
                    _cfg, job.Segment,
                    box.X, box.Y, box.Width, box.Height,
                    job.Frame.Width, job.Frame.Height,
                    result.Score));
            }

            // PatchCore scores rather than localises, so it produces no boxes.
            // A defective segment must still get a position, or the strip map
            // would show a bad segment with nothing to point at - fall back to
            // the whole frame's footprint.
            if (defects.Count == 0 && result.IsDefect)
            {
                defects.Add(new DefectBox
                {
                    StartMm = job.StartMm,
                    EndMm = job.EndMm,
                    AcrossStartMm = 0,
                    AcrossEndMm = AcrossExtentMm(job),
                    AreaMm2 = (job.EndMm - job.StartMm) * AcrossExtentMm(job),
                    Score = result.Score,
                    PxX = 0,
                    PxY = 0,
                    PxW = job.Frame.Width,
                    PxH = job.Frame.Height,
                    SourceSegments = new List<int> { job.Segment },
                });
            }

            return defects;
        }

        private double AcrossExtentMm(SegmentJob job)
        {
            double scale = _cfg.Geometry.PxPerMmAcross;
            if (scale <= 0) return 0;
            int px = ScanGeometry.TravelIsX(_cfg) ? job.Frame.Height : job.Frame.Width;
            return px / scale;
        }

        /// <summary>
        /// Whether to keep this segment's image. At a small field of view a run
        /// is hundreds of segments, and keeping every full-resolution PNG is
        /// tens of GB a shift - so the default keeps only the ones with
        /// something to look at.
        /// </summary>
        private bool ShouldStoreImage(InspectionResult result)
            => !string.Equals(_cfg.Run.StoreImagesFor, "defects-only", StringComparison.OrdinalIgnoreCase)
               || result.IsDefect;

        private static byte[] Encode(Mat overlay)
        {
            Cv2.ImEncode(".png", overlay, out byte[] png);
            return png;
        }

        private string SnapshotConfig()
        {
            try
            {
                return JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = false });
            }
            catch
            {
                return string.Empty;   // a snapshot is nice to have, not worth failing a run over
            }
        }
    }
}
