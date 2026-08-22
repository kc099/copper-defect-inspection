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
    }
}
