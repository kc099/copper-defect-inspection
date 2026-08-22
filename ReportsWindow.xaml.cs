using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace copperInspection
{
    public partial class ReportsWindow : Window, INotifyPropertyChanged
    {
        private const int RecentCount = 6;

        // Backing collection for the gallery. Using ObservableCollection lets us
        // prepend live reports without re-querying (the UI updates automatically).
        private readonly System.Collections.ObjectModel.ObservableCollection<DefectReport> _items = new();

        // ── Grid shape the gallery is laid out with (bound from XAML) ──
        private int _gridRows = 2;
        private int _gridColumns = 3;
        public int GridRows    { get => _gridRows;    set { _gridRows = value;    OnPropertyChanged(); } }
        public int GridColumns { get => _gridColumns; set { _gridColumns = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Pick rows/columns so all <paramref name="count"/> cards fit without scrolling.</summary>
        private void SetGridShape(int count)
        {
            if (count <= 0) { GridColumns = 1; GridRows = 1; return; }
            int cols = count <= 6 ? Math.Min(3, count) : (int)Math.Ceiling(Math.Sqrt(count));
            GridColumns = cols;
            GridRows    = (int)Math.Ceiling(count / (double)cols);
        }

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
            await RunQueryAsync(
                $"Last {RecentCount} Runs",
                () => ReportStore.QueryRecentAsync(RecentCount));
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
                SetGridShape(reports.Count);
                foreach (DefectReport r in reports) _items.Add(r);

                CountLabel.Text          = $"{reports.Count} report(s) shown.";
                EmptyLabel.Visibility    = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        /// Prepend a freshly-detected report to the gallery without re-querying,
        /// preserving whatever filter/date-range the user is currently viewing.
        /// </summary>
        public void AddLiveReport(DefectReport report)
        {
            _items.Insert(0, report);
            SetGridShape(_items.Count);

            CountLabel.Text          = $"{_items.Count} report(s) shown.";
            EmptyLabel.Visibility    = Visibility.Collapsed;
            DownloadAllBtn.IsEnabled = true;
        }

        // ══════════════════════════════════════════════════════
        //  PER-CARD ACTIONS
        // ══════════════════════════════════════════════════════
        private void CardView_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DefectReport r) return;
            if (r.ImageData.Length == 0) return;

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
            if (r.ImageData.Length == 0) return;

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
                if (r.ImageData.Length == 0) continue;
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
    }
}
