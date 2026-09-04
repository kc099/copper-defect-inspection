using System.Windows;
using System.Windows.Media.Imaging;

namespace copperInspection
{
    /// <summary>
    /// A small always-on-top window that shows one image at a time, updated
    /// live as strip-scan segments come in. Gated behind
    /// Scan.Run.ShowBackgroundMaskWindow - see MainWindow's ArmScanProbeAsync
    /// for where it's created and fed.
    ///
    /// Deliberately dumb: this window has no idea what a "segment" or a
    /// "background mask" is. It just paints whatever BitmapSource it's given
    /// under whatever caption it's given - keeping it decoupled from the scan
    /// pipeline means it could just as easily show a different stage of the
    /// pipeline later without changing this file at all.
    /// </summary>
    public partial class LiveMaskWindow : Window
    {
        private int _count;

        public LiveMaskWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Show a new image. MUST be called on the UI thread - callers off the
        /// UI thread should Dispatcher.BeginInvoke into this, and the
        /// BitmapSource must already be Frozen so it's safe to have been built
        /// on a different thread.
        /// </summary>
        public void UpdateImage(BitmapSource image, string caption)
        {
            _count++;
            MaskImage.Source = image;
            SegmentLabel.Text = caption;
            CountLabel.Text = $"{_count} segment{(_count == 1 ? "" : "s")} shown";
            EmptyLabel.Visibility = Visibility.Collapsed;
        }
    }
}
