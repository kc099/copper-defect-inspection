using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using copperInspection.Camera;

namespace copperInspection
{
    /// <summary>
    /// A draggable / resizable ROI rectangle drawn over a live image preview.
    ///
    /// Stores the box in NORMALIZED (0-1) coordinates, never pixels, so the
    /// same box means the same part of the strip whether the window is
    /// resized, the preview is letterboxed, or the camera resolution changes.
    /// <see cref="GetPixelRoi"/> converts it to source-pixel coordinates for
    /// the actual crop.
    ///
    /// It overlays the preview rather than owning the Image, so the existing
    /// code that paints frames into that Image is untouched.
    ///
    /// Letterboxing is handled explicitly: the preview Image uses
    /// Stretch="Uniform", so the picture rarely fills the control - there are
    /// bars on two sides. The box is positioned against the DISPLAYED PICTURE
    /// rect, not the control bounds, otherwise a box that looks like it's on
    /// the strip would crop somewhere else entirely.
    /// </summary>
    public partial class RoiCropper : UserControl
    {
        private const double MinNormalizedSize = 0.02;

        private NormalizedRoi _roi = NormalizedRoi.Default;
        private Point _dragStart;
        private NormalizedRoi _dragOrigin;
        private bool _draggingBox;
        private string? _draggingHandle;

        /// <summary>Source frame size, used to work out the letterboxed
        /// picture rect. Set this whenever the preview's source changes;
        /// until it is, the control assumes the picture fills the bounds.</summary>
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }

        /// <summary>Fires whenever the operator finishes moving or resizing
        /// the box - i.e. when it's worth persisting, not on every mouse
        /// move.</summary>
        public event Action<NormalizedRoi>? RoiChanged;

        public RoiCropper()
        {
            InitializeComponent();
            SizeChanged += (_, _) => Redraw();
            Loaded += (_, _) => Redraw();
        }

        public NormalizedRoi Roi
        {
            get => _roi;
            set { _roi = Clamp(value); Redraw(); }
        }

        public void SetSourceSize(int width, int height)
        {
            if (width == SourceWidth && height == SourceHeight) return;
            SourceWidth = width;
            SourceHeight = height;
            Redraw();
        }

        /// <summary>The ROI in SOURCE-PIXEL coordinates, ready to crop with.
        /// Falls back to the control's own known source size when no explicit
        /// size is given.</summary>
        public OpenCvSharp.Rect GetPixelRoi() => _roi.ToPixelRect(
            SourceWidth > 0 ? SourceWidth : 1,
            SourceHeight > 0 ? SourceHeight : 1);

        public OpenCvSharp.Rect GetPixelRoi(int srcWidth, int srcHeight)
            => _roi.ToPixelRect(srcWidth, srcHeight);

        // ── the letterboxed picture rect inside this control ──────────────
        private Rect PictureRect()
        {
            double cw = ActualWidth, ch = ActualHeight;
            if (cw <= 0 || ch <= 0) return new Rect(0, 0, 0, 0);
            if (SourceWidth <= 0 || SourceHeight <= 0) return new Rect(0, 0, cw, ch);

            double scale = Math.Min(cw / SourceWidth, ch / SourceHeight);
            double w = SourceWidth * scale, h = SourceHeight * scale;
            return new Rect((cw - w) / 2, (ch - h) / 2, w, h);
        }

        private void Redraw()
        {
            Rect pic = PictureRect();
            if (pic.Width <= 0 || pic.Height <= 0) return;

            double x = pic.X + _roi.X * pic.Width;
            double y = pic.Y + _roi.Y * pic.Height;
            double w = _roi.W * pic.Width;
            double h = _roi.H * pic.Height;

            RoiBox.Width = w;
            RoiBox.Height = h;
            Canvas.SetLeft(RoiBox, x);
            Canvas.SetTop(RoiBox, y);

            Place(HandleTL, x, y);
            Place(HandleTR, x + w, y);
            Place(HandleBL, x, y + h);
            Place(HandleBR, x + w, y + h);

            Canvas.SetLeft(RoiLabel, x);
            Canvas.SetTop(RoiLabel, Math.Max(0, y - 20));
            RoiLabelText.Text = SourceWidth > 0
                ? $"ROI {GetPixelRoi().Width}×{GetPixelRoi().Height}px"
                : "ROI";
        }

        private static void Place(Rectangle handle, double cx, double cy)
        {
            Canvas.SetLeft(handle, cx - handle.Width / 2);
            Canvas.SetTop(handle, cy - handle.Height / 2);
        }

        private static NormalizedRoi Clamp(NormalizedRoi r)
        {
            double w = Math.Clamp(r.W, MinNormalizedSize, 1.0);
            double h = Math.Clamp(r.H, MinNormalizedSize, 1.0);
            double x = Math.Clamp(r.X, 0.0, 1.0 - w);
            double y = Math.Clamp(r.Y, 0.0, 1.0 - h);
            return new NormalizedRoi(x, y, w, h);
        }

        // ── moving the whole box ──────────────────────────────────────────
        private void Box_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _draggingBox = true;
            _dragStart = e.GetPosition(HostCanvas);
            _dragOrigin = _roi;
            RoiBox.CaptureMouse();
            e.Handled = true;
        }

        private void Box_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingBox) return;
            Rect pic = PictureRect();
            if (pic.Width <= 0 || pic.Height <= 0) return;

            Point now = e.GetPosition(HostCanvas);
            double dx = (now.X - _dragStart.X) / pic.Width;
            double dy = (now.Y - _dragStart.Y) / pic.Height;

            _roi = Clamp(_dragOrigin with { X = _dragOrigin.X + dx, Y = _dragOrigin.Y + dy });
            Redraw();
            e.Handled = true;
        }

        private void Box_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingBox) return;
            _draggingBox = false;
            RoiBox.ReleaseMouseCapture();
            RoiChanged?.Invoke(_roi);
            e.Handled = true;
        }

        // ── resizing from a corner ────────────────────────────────────────
        private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var handle = (Rectangle)sender;
            _draggingHandle = handle.Tag as string;
            _dragStart = e.GetPosition(HostCanvas);
            _dragOrigin = _roi;
            handle.CaptureMouse();
            e.Handled = true;
        }

        private void Handle_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingHandle == null) return;
            Rect pic = PictureRect();
            if (pic.Width <= 0 || pic.Height <= 0) return;

            Point now = e.GetPosition(HostCanvas);
            double dx = (now.X - _dragStart.X) / pic.Width;
            double dy = (now.Y - _dragStart.Y) / pic.Height;

            // Work in edges rather than x/y/w/h: dragging a corner moves two
            // edges and leaves the opposite two where they are, which is
            // fiddly to express as an origin+size delta but trivial as edges.
            double left = _dragOrigin.X, top = _dragOrigin.Y;
            double right = _dragOrigin.X + _dragOrigin.W, bottom = _dragOrigin.Y + _dragOrigin.H;

            if (_draggingHandle.Contains('L')) left += dx; else right += dx;
            if (_draggingHandle.StartsWith('T')) top += dy; else bottom += dy;

            if (right < left) (left, right) = (right, left);
            if (bottom < top) (top, bottom) = (bottom, top);

            _roi = Clamp(new NormalizedRoi(left, top, right - left, bottom - top));
            Redraw();
            e.Handled = true;
        }

        private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingHandle == null) return;
            _draggingHandle = null;
            ((Rectangle)sender).ReleaseMouseCapture();
            RoiChanged?.Invoke(_roi);
            e.Handled = true;
        }
    }
}
