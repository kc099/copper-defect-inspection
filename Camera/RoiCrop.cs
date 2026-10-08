using System;
using OpenCvSharp;

namespace copperInspection.Camera
{
    /// <summary>
    /// A region of interest in normalized (0-1) coordinates, plus the one
    /// function that turns a live frame into the crop the model expects.
    ///
    /// Deliberately standalone - no PatchCore, no camera, no WPF - so the ROI
    /// stays its own feature rather than an inline crop buried in whatever
    /// the inference call happens to look like this month. The UI edits it
    /// (RoiCropper), config.json persists it (PipelineConfig.Roi*), and the
    /// live capture paths apply it; none of those three know about each other.
    ///
    /// Normalized rather than pixels on purpose: the box has to mean the same
    /// thing whether it was drawn on a 2048x1536 frame, a scaled-down preview,
    /// or a different camera entirely.
    /// </summary>
    public readonly record struct NormalizedRoi(double X, double Y, double W, double H)
    {
        /// <summary>Matches RoiCropper's own default - a sane centre box to
        /// start from before anyone drags it.</summary>
        public static readonly NormalizedRoi Default = new(0.15, 0.15, 0.70, 0.70);

        /// <summary>Clamped to the frame and guaranteed at least 1px, so a
        /// badly-dragged or badly-edited box can never produce a crop OpenCV
        /// will throw on.</summary>
        public Rect ToPixelRect(int srcWidth, int srcHeight)
        {
            int x = (int)Math.Round(X * srcWidth);
            int y = (int)Math.Round(Y * srcHeight);
            int w = (int)Math.Round(W * srcWidth);
            int h = (int)Math.Round(H * srcHeight);

            x = Math.Clamp(x, 0, Math.Max(0, srcWidth - 1));
            y = Math.Clamp(y, 0, Math.Max(0, srcHeight - 1));
            w = Math.Clamp(w, 1, srcWidth - x);
            h = Math.Clamp(h, 1, srcHeight - y);

            return new Rect(x, y, w, h);
        }

        public bool IsWholeFrame => X <= 0 && Y <= 0 && W >= 1 && H >= 1;
    }

    public static class RoiCrop
    {
        /// <summary>
        /// Crops a live frame to the ROI. Always returns a NEW Mat and never
        /// disposes <paramref name="frame"/> - the caller still owns what it
        /// passed in, which is the opposite of the alias-and-dispose pattern
        /// that has bitten this codebase before (see docs/WORK_LOG.md).
        ///
        /// Returns a plain clone when the ROI is disabled or covers the whole
        /// frame, so callers never need a separate "is it on?" branch.
        /// </summary>
        public static Mat Apply(Mat frame, NormalizedRoi roi, bool enabled)
        {
            if (!enabled || roi.IsWholeFrame) return frame.Clone();

            Rect rect = roi.ToPixelRect(frame.Width, frame.Height);

            // .Clone() matters: new Mat(frame, rect) is a VIEW into the
            // parent's buffer, so it would silently become invalid the moment
            // the caller disposes the frame it still rightly owns.
            using Mat view = new(frame, rect);
            return view.Clone();
        }
    }
}
