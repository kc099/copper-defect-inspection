using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace copperInspection
{
    public partial class ReportsWindow : Window
    {
        /// <summary>Number of frames in the live gallery. Fixed — never grows.</summary>
        private const int LiveSlots = 6;

        /// <summary>
        /// Live = the six fixed frames, newest first, oldest pushed out.
        /// Range = every report in the chosen dates, wrapped and scrollable.
        /// </summary>
        private enum GalleryMode { Live, Range }

        private GalleryMode _mode = GalleryMode.Live;

        /// <summary>How many live results arrived while a date range was on screen.</summary>
        private int _pendingLive;

        private readonly ObservableCollection<DefectReport> _items = new();

        public ReportsWindow()
        {
            InitializeComponent();
            ReportsItems.ItemsSource = _items;

            // Default range for the date filter.
            FromDate.SelectedDate = DateTime.Today.AddDays(-7);
            ToDate.SelectedDate   = DateTime.Today;

            // On open, show the last six runs.
            Loaded += async (_, _) => await LoadRecentAsync();
        }

        // ══════════════════════════════════════════════════════
        //  LOADING
        // ══════════════════════════════════════════════════════
        private async void LoadRecent_Click(object sender, RoutedEventArgs e)
            => await LoadRecentAsync();

        private async System.Threading.Tasks.Task LoadRecentAsync()
        {
            ApplyMode(GalleryMode.Live);
            await RunQueryAsync(
                $"Live — Last {LiveSlots} Runs",
                () => ReportStore.QueryRecentAsync(LiveSlots));
        }

        private async void LoadRange_Click(object sender, RoutedEventArgs e)
        {
            DateTime fromLocal = (FromDate.SelectedDate ?? DateTime.Today).Date;
            DateTime toLocal   = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1);

            if (toLocal <= fromLocal)
            {
                MessageBox.Show("The 'To' date must be on or after the 'From' date.",
                    "Invalid Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ApplyMode(GalleryMode.Range);
            string header = $"{fromLocal:dd MMM yyyy}  →  {toLocal.AddDays(-1):dd MMM yyyy}";
            await RunQueryAsync(header,
                () => ReportStore.QueryByDateAsync(fromLocal.ToUniversalTime(), toLocal.ToUniversalTime()));
        }

        private async System.Threading.Tasks.Task RunQueryAsync(
            string header, Func<System.Threading.Tasks.Task<List<DefectReport>>> query)
        {
            HeaderLabel.Text = header;
            CountLabel.Text  = "Loading…";
            _items.Clear();
            EmptyLabel.Visibility = Visibility.Collapsed;
            DownloadAllBtn.IsEnabled = false;

            try
            {
                List<DefectReport> reports = await query();
                foreach (DefectReport r in reports) _items.Add(r);

                // Live mode always shows exactly LiveSlots frames: pad the unfilled ones.
                if (_mode == GalleryMode.Live)
                    while (_items.Count < LiveSlots) _items.Add(DefectReport.Placeholder());

                UpdateCountLabel(reports.Count);
                EmptyLabel.Visibility    = reports.Count == 0 && _mode == GalleryMode.Range
                                           ? Visibility.Visible : Visibility.Collapsed;
                DownloadAllBtn.IsEnabled = reports.Count > 0;
            }
            catch (Exception ex)
            {
                CountLabel.Text = "Failed to load.";
                MessageBox.Show($"Could not query MongoDB:\n\n{ex.Message}",
                    "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════
        //  LIVE UPDATE  (called by MainWindow after a detection is saved)
        // ══════════════════════════════════════════════════════
        /// <summary>
        /// Push a freshly-detected report into the six live slots: it takes the first
        /// frame and the oldest one drops off the end, so the slot count — and the
        /// grid shape — never change. Ignored while a date range is on screen.
        /// </summary>
        public void AddLiveReport(DefectReport report)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddLiveReport(report)); return; }

            if (_mode == GalleryMode.Range)
            {
                _pendingLive++;              // don't disturb the range the user is reading
                UpdateLiveHint();
                return;
            }

            _items.Insert(0, report);
            while (_items.Count > LiveSlots) _items.RemoveAt(_items.Count - 1);  // drop oldest / placeholder

            UpdateCountLabel(_items.Count(r => !r.IsPlaceholder));
            EmptyLabel.Visibility    = Visibility.Collapsed;
            DownloadAllBtn.IsEnabled = true;
        }

        // ══════════════════════════════════════════════════════
        //  PER-CARD ACTIONS
        // ══════════════════════════════════════════════════════
        private void CardView_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DefectReport r) return;
            if (r.IsPlaceholder || r.ImageData.Length == 0) return;

            var img = new Image
            {
                Source  = BytesToImage(r.ImageData),
                Stretch = System.Windows.Media.Stretch.None,
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                img, System.Windows.Media.BitmapScalingMode.HighQuality);

            var viewer = new Window
            {
                Title  = $"{r.DefectCount} defect(s) — {r.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                Owner  = this,
                Width  = 900,
                Height = 700,
                Background = System.Windows.Media.Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    Content = img
                }
            };
            viewer.Show();
        }

        private void CardDownload_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DefectReport r) return;
            if (r.IsPlaceholder || r.ImageData.Length == 0) return;

            var dlg = new SaveFileDialog
            {
                Title    = "Save Report Image",
                Filter   = "PNG Image (*.png)|*.png",
                FileName = SuggestFileName(r)
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                File.WriteAllBytes(dlg.FileName, r.ImageData);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save file:\n\n{ex.Message}",
                    "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════
        //  DOWNLOAD ALL SHOWN
        // ══════════════════════════════════════════════════════
        private void DownloadAll_Click(object sender, RoutedEventArgs e)
        {
            if (ReportsItems.ItemsSource is not IEnumerable<DefectReport> reports) return;

            var dlg = new OpenFolderDialog { Title = "Choose a folder to save all report images" };
            if (dlg.ShowDialog() != true) return;

            int saved = 0, failed = 0;
            foreach (DefectReport r in reports)
            {
                if (r.IsPlaceholder || r.ImageData.Length == 0) continue;
                try
                {
                    File.WriteAllBytes(Path.Combine(dlg.FolderName, SuggestFileName(r)), r.ImageData);
                    saved++;
                }
                catch { failed++; }
            }

            MessageBox.Show(
                $"Saved {saved} image(s)." + (failed > 0 ? $"\n{failed} failed." : ""),
                "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ══════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════
        private static string SuggestFileName(DefectReport r)
            => $"report_{r.Timestamp.ToLocalTime():yyyyMMdd_HHmmss}_{r.DefectCount}defects.png";

        private static BitmapImage BytesToImage(byte[] data)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        // ══════════════════════════════════════════════════════
        //  MODE
        // ══════════════════════════════════════════════════════
        /// <summary>
        /// Swap the gallery between the fixed 2×3 live grid (no scrolling) and the
        /// wrapping, scrollable date-range grid.
        /// </summary>
        private void ApplyMode(GalleryMode mode)
        {
            _mode = mode;
            bool live = mode == GalleryMode.Live;

            ReportsItems.ItemsPanel = (ItemsPanelTemplate)FindResource(live ? "LivePanel" : "RangePanel");
            GalleryScroll.VerticalScrollBarVisibility =
                live ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

            if (live) { _pendingLive = 0; UpdateLiveHint(); }
        }

        /// <param name="real">Reports actually shown, not counting live placeholders.</param>
        private void UpdateCountLabel(int real)
            => CountLabel.Text = _mode == GalleryMode.Live
                ? $"Live · {real} of {LiveSlots} slots filled."
                : $"{real} report(s) shown.";

        private void UpdateLiveHint()
        {
            if (_mode == GalleryMode.Range && _pendingLive > 0)
            {
                LiveHintLabel.Text = $"🔴  {_pendingLive} new result(s) captured — "
                                   + "press \"Live — Last 6\" to see them.";
                LiveHintLabel.Visibility = Visibility.Visible;
            }
            else
            {
                LiveHintLabel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
