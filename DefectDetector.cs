using System;
using OpenCvSharp;

namespace copperInspection
{
    /// <summary>Shared image loading, used by both background-subtraction and
    /// detection so every image enters the pipeline at the same resolution.</summary>
    public static class ImageLoader
    {
        public const int DisplayWidth = 800;

        public static Mat LoadResized(string path) => LoadAndResize(path, DisplayWidth);

        /// <summary>Loads at native resolution, no resize at all. Needed for
        /// PatchCore: BackgroundSubtraction's morphology kernels are a fixed
        /// pixel size (e.g. 5x5), so they erode/smooth proportionally more on
        /// a downscaled image than on the original - which segments the
        /// image differently than however the Python reference tooling
        /// (which never resizes before segmenting) produced the images the
        /// model was actually trained/calibrated on. Confirmed empirically:
        /// the same image+settings scored GOOD at native resolution and BAD
        /// at 800px-wide. See docs/WORK_LOG.md.</summary>
        public static Mat LoadFull(string path)
        {
            Mat img = Cv2.ImRead(path, ImreadModes.Color);
            if (img.Empty())
                throw new InvalidOperationException($"Cannot load image: {path}");
            return img;
        }

        private static Mat LoadAndResize(string path, int maxWidth)
        {
            Mat img = Cv2.ImRead(path, ImreadModes.Color);
            if (img.Empty())
                throw new InvalidOperationException($"Cannot load image: {path}");

            if (img.Width <= maxWidth)
                return img;

            double scale = (double)maxWidth / img.Width;
            int newH = (int)(img.Height * scale);
            Mat resized = new();
            Cv2.Resize(img, resized, new Size(maxWidth, newH), interpolation: InterpolationFlags.Area);
            img.Dispose();
            return resized;
        }

        /// <summary>Same downscale as LoadResized, but for a Mat that's already
        /// in memory (e.g. a live camera capture) instead of a file on disk.
        /// Always returns a new Mat and never disposes src - caller keeps
        /// owning what it passed in.</summary>
        public static Mat ResizeToDisplay(Mat src)
        {
            if (src.Width <= DisplayWidth) return src.Clone();

            double scale = (double)DisplayWidth / src.Width;
            int newH = (int)(src.Height * scale);
            Mat resized = new();
            Cv2.Resize(src, resized, new Size(DisplayWidth, newH), interpolation: InterpolationFlags.Area);
            return resized;
        }
    }
}
