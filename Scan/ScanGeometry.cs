using System;

namespace copperInspection.Scan
{
    /// <summary>
    /// The scan arithmetic, as pure functions with no state and no hardware:
    /// where each frame fires, what strip it covers, and where a defect pixel
    /// sits on the strip in millimetres.
    ///
    /// Kept deliberately free of WPF, OpenCV and MongoDB so it can be unit
    /// tested on its own - the stop / reverse / jump-past-threshold cases are
    /// easy to get subtly wrong and painful to debug on a live line.
    /// </summary>
    public static class ScanGeometry
    {
        /// <summary>
        /// How far the strip travels between one capture and the next.
        /// Always derived, never stored - a stored pitch could disagree with
        /// the FOV and overlap it came from, and that disagreement means
        /// silent gaps in coverage.
        /// </summary>
        public static double PitchMm(ScanConfig cfg)
            => cfg.Geometry.FovAlongTravelMm - cfg.Geometry.OverlapMm;

        /// <summary>
        /// Encoder distance (mm since the button press) at which segment
        /// <paramref name="segment"/> fires. Segments are 1-based, so segment 1
        /// fires at the offset - 0 mm under the convention in use here, i.e.
        /// on the button press itself.
        /// </summary>
        public static double ThresholdMm(ScanConfig cfg, int segment)
        {
            if (segment < 1) throw new ArgumentOutOfRangeException(nameof(segment));
            return cfg.Geometry.FirstTriggerOffsetMm + (segment - 1) * PitchMm(cfg);
        }

        /// <summary>The stretch of strip that segment n's frame covers, in mm
        /// from the leading edge.</summary>
        public static (double StartMm, double EndMm) SegmentRangeMm(ScanConfig cfg, int segment)
        {
            double start = (segment - 1) * PitchMm(cfg);
            return (start, start + cfg.Geometry.FovAlongTravelMm);
        }

        /// <summary>
        /// The highest segment index whose trigger distance has been reached at
        /// <paramref name="positionMm"/>, or 0 if none has.
        ///
        /// This is what makes the no-burst rule possible: if the app hitches
        /// and the position jumps past several thresholds, we can tell where we
        /// actually are instead of firing a burst of captures for material that
        /// has already gone past.
        /// </summary>
        public static int SegmentIndexAt(ScanConfig cfg, double positionMm)
        {
            double pitch = PitchMm(cfg);
            if (pitch <= 0) return 0;

            double past = positionMm - cfg.Geometry.FirstTriggerOffsetMm;
            if (past < 0) return 0;

            return (int)Math.Floor(past / pitch) + 1;
        }

        /// <summary>How many segments a strip of a known length needs.</summary>
        public static int SegmentCountFor(ScanConfig cfg, double stripLengthMm)
        {
            double pitch = PitchMm(cfg);
            if (pitch <= 0 || stripLengthMm <= 0) return 0;

            // The last frame only has to REACH the end of the strip, not start
            // before it - so this is the count whose covered range spans the
            // whole length.
            double coverable = stripLengthMm - cfg.Geometry.FovAlongTravelMm;
            if (coverable <= 0) return 1;
            return (int)Math.Ceiling(coverable / pitch) + 1;
        }

        /// <summary>
        /// The smallest overlap that can absorb the worst-case position error
        /// plus a whole defect. Below this, frames can leave un-imaged gaps -
        /// strip the report counts as inspected but that nothing ever saw.
        ///
        ///     Overlap_min = v_max x (poll_interval + latency_residual)
        ///                 + largest_defect + safety_margin
        ///
        /// Capture-delay compensation is what makes the latency term small:
        /// with it on, only the ERROR in the delay estimate is left (taken as
        /// 20%); with it off, the whole delay counts.
        /// </summary>
        public static double MinimumSafeOverlapMm(ScanConfig cfg)
        {
            double v = cfg.Run.MaxLineSpeedMmPerSec;
            double pollSec = cfg.Encoder.PollHz > 0 ? 1.0 / cfg.Encoder.PollHz : 0.0;

            double delaySec = Math.Abs(cfg.CaptureDelay.Ms) / 1000.0;
            double residualSec = cfg.CaptureDelay.Enabled ? delaySec * DelayResidualFraction : delaySec;

            return v * (pollSec + residualSec)
                 + cfg.Geometry.LargestDefectMm
                 + cfg.Geometry.SafetyMarginMm;
        }

        /// <summary>How much of the configured capture delay is left over after
        /// compensation - i.e. how wrong the delay estimate is assumed to be.</summary>
        public const double DelayResidualFraction = 0.2;

        /// <summary>
        /// How far ahead of the threshold to fire, so the shutter opens at the
        /// right place rather than the right moment. Zero when compensation is
        /// switched off.
        /// </summary>
        public static double LeadMm(ScanConfig cfg, double speedMmPerSec)
            => cfg.CaptureDelay.Enabled ? speedMmPerSec * (cfg.CaptureDelay.Ms / 1000.0) : 0.0;

        // ══════════════════════════════════════════════════════════════════
        //  PIXEL -> STRIP MILLIMETRES
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Turns a defect's pixel box in segment <paramref name="segment"/>'s
        /// frame into an absolute position on the strip, in millimetres from
        /// the leading edge - the "this defect runs from 90 mm to 100 mm"
        /// that goes in the database.
        ///
        /// Absolute, never "position within frame 7": the segment's own start
        /// is added in, so the number means the same thing whichever frame the
        /// defect happened to land in.
        /// </summary>
        /// <param name="pxAlong">Box origin on the travel axis, in pixels.</param>
        /// <param name="pxLength">Box extent on the travel axis, in pixels.</param>
        /// <param name="imageLengthPx">Full image extent on the travel axis.</param>
        public static (double StartMm, double EndMm) PixelToStripMm(
            ScanConfig cfg, int segment, int pxAlong, int pxLength, int imageLengthPx)
        {
            double scale = cfg.Geometry.PxPerMmAlong;
            if (scale <= 0) return (0, 0);

            double segStart = SegmentRangeMm(cfg, segment).StartMm;

            // When the camera is mounted so that travel runs against the pixel
            // axis, the box has to be flipped before it means anything.
            int origin = cfg.Geometry.TravelReversed
                ? imageLengthPx - pxAlong - pxLength
                : pxAlong;

            double start = segStart + origin / scale;
            return (start, start + pxLength / scale);
        }

        /// <summary>
        /// Position across the strip's width, in mm from the image edge. Tells
        /// the operator which side of the strip a defect is on, which is
        /// usually the next question after "where along".
        /// </summary>
        public static (double StartMm, double EndMm) PixelToAcrossMm(
            ScanConfig cfg, int pxAcross, int pxWidth)
        {
            double scale = cfg.Geometry.PxPerMmAcross;
            if (scale <= 0) return (0, 0);
            return (pxAcross / scale, (pxAcross + pxWidth) / scale);
        }

        /// <summary>True when the strip travels along the image's X axis.</summary>
        public static bool TravelIsX(ScanConfig cfg)
            => !string.Equals(cfg.Geometry.TravelAxis, "Y", StringComparison.OrdinalIgnoreCase);
    }
}
