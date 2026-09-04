using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using copperInspection.Scan;
using MongoDB.Bson;

namespace copperInspection
{
    /// <summary>
    /// Results view for continuous strip scans: pick a run, see which parts of
    /// the strip were inspected and what was found, and click through to the
    /// image behind any segment.
    ///
    /// The strip map is the point of it. A list of two hundred frames answers
    /// "was frame 137 bad"; the map answers "where on this strip is the
    /// problem", which is the question anyone actually has.
    /// </summary>
    public partial class StripRunsWindow : Window
    {
        private const int RecentCount = 25;

        private readonly ObservableCollection<StripRun> _runs = new();
        private readonly ObservableCollection<SegmentTile> _tiles = new();
        private readonly ObservableCollection<DefectRow> _defects = new();

        private StripRun? _selected;

        public StripRunsWindow()
        {
            InitializeComponent();
            RunsList.ItemsSource = _runs;
            StripMap.ItemsSource = _tiles;
            DefectsGrid.ItemsSource = _defects;

            FromDate.SelectedDate = DateTime.Today.AddDays(-7);
            ToDate.SelectedDate = DateTime.Today;

            Loaded += async (_, _) => await LoadRecentAsync();
        }

        // ══════════════════════════════════════════════════════
        //  LOADING RUNS
        // ══════════════════════════════════════════════════════
        private async void LoadRecent_Click(object sender, RoutedEventArgs e) => await LoadRecentAsync();

        private Task LoadRecentAsync()
            => LoadRunsAsync(() => StripRunStore.QueryRecentAsync(RecentCount));

        private async void LoadRange_Click(object sender, RoutedEventArgs e)
        {
            DateTime from = (FromDate.SelectedDate ?? DateTime.Today).Date;
            DateTime to = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1);

            if (to <= from)
            {
                MessageBox.Show("The 'To' date must be on or after the 'From' date.",
                    "Invalid Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadRunsAsync(() => StripRunStore.QueryByDateAsync(
                from.ToUniversalTime(), to.ToUniversalTime()));
        }

        private async Task LoadRunsAsync(Func<Task<List<StripRun>>> query)
        {
            RunCountLabel.Text = "Loading…";
            _runs.Clear();
            ClearDetail();

            try
            {
                List<StripRun> runs = await query();
                foreach (StripRun r in runs) _runs.Add(r);
                RunCountLabel.Text = $"{runs.Count} run(s).";
                if (runs.Count > 0) RunsList.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                RunCountLabel.Text = "Failed to load.";
                MessageBox.Show($"Could not query MongoDB:\n\n{ex.Message}",
                    "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════
        //  SHOWING ONE RUN
        // ══════════════════════════════════════════════════════
        private async void RunsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RunsList.SelectedItem is not StripRun run) { ClearDetail(); return; }
            _selected = run;
            await ShowRunAsync(run);
        }

        private async Task ShowRunAsync(StripRun run)
        {
            RunTitle.Text = string.IsNullOrWhiteSpace(run.BatchId)
                ? $"Run of {run.StartedUtc.ToLocalTime():dd MMM yyyy HH:mm:ss}"
                : run.BatchId;

            string duration = run.EndedUtc is DateTime ended
                ? $"{(ended - run.StartedUtc).TotalSeconds:F0} s"
                : "still open";

            RunSubtitle.Text =
                $"{run.SegmentCount} segment(s)  ·  {run.TotalLengthMm / 1000.0:F2} m  ·  " +
                $"{run.DefectCount} defect(s)  ·  {duration}  ·  ended: {run.EndReason}";

            ShowCoverageBanner(run);
            ShowDefects(run);
            await ShowStripMapAsync(run);
        }

        /// <summary>
        /// The coverage banner never gets to be quietly reassuring: a run that
        /// lost segments cannot claim the strip is good, because the missing
        /// parts were never looked at.
        /// </summary>
        private void ShowCoverageBanner(StripRun run)
        {
            CoverageBanner.Visibility = Visibility.Visible;

            if (!run.CoverageComplete)
            {
                CoverageBanner.Background = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
                string which = run.MissingSegments.Count == 0
                    ? ""
                    : $" Segments not inspected: {string.Join(", ", run.MissingSegments.Take(30))}" +
                      (run.MissingSegments.Count > 30 ? $" … (+{run.MissingSegments.Count - 30} more)" : "") + ".";

                CoverageText.Text =
                    "⚠  COVERAGE INCOMPLETE — part of this strip was never inspected, " +
                    "so a clean result here does not mean the strip is clean." + which;
            }
            else if (run.Verdict == "BAD")
            {
                CoverageBanner.Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                CoverageText.Text = $"✖  BAD — {run.DefectCount} defect(s) found. Full coverage.";
            }
            else
            {
                CoverageBanner.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
                CoverageText.Text = "✓  GOOD — every segment inspected, no defects found.";
            }
        }

        private void ShowDefects(StripRun run)
        {
            _defects.Clear();
            int i = 1;
            foreach (DefectBox d in run.Defects.OrderBy(d => d.StartMm))
            {
                _defects.Add(new DefectRow
                {
                    Index = i++,
                    Range = d.RangeText,
                    Length = $"{d.LengthMm:F1} mm",
                    Across = d.AcrossText,
                    Score = $"{d.Score:F3}",
                    Segments = d.SegmentsText,
                    Merged = d.MergedCount > 1 ? $"{d.MergedCount} (seen across a seam)" : "1",
                    StartMm = d.StartMm,
                    FirstSegment = d.SourceSegments.Count > 0 ? d.SourceSegments[0] : 0,
                });
            }

            DefectsHeader.Text = $"Defects  ({run.Defects.Count})";
            DefectsEmpty.Visibility = run.Defects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DefectsGrid.Visibility = run.Defects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task ShowStripMapAsync(StripRun run)
        {
            _tiles.Clear();
            MapHint.Text = "Loading segments…";

            List<DefectReport> segments;
            try
            {
                // Image bytes are excluded by the query - a run can be hundreds
                // of segments and the map only needs the metadata. The image is
                // fetched on click, when it is actually going to be shown.
                segments = await StripRunStore.QuerySegmentsAsync(run.Id);
            }
            catch (Exception ex)
            {
                MapHint.Text = $"Could not load segments: {ex.Message}";
                return;
            }

            var missing = new HashSet<int>(run.MissingSegments);
            var byIndex = segments.ToDictionary(s => s.SegmentIndex);

            int highest = Math.Max(
                segments.Count > 0 ? segments.Max(s => s.SegmentIndex) : 0,
                missing.Count > 0 ? missing.Max() : 0);

            for (int n = 1; n <= highest; n++)
            {
                if (byIndex.TryGetValue(n, out DefectReport? report))
                {
                    bool bad = report.DefectCount > 0;
                    _tiles.Add(new SegmentTile
                    {
                        Segment = n,
                        Label = n.ToString(),
                        Fill = bad ? Brushes.IndianRed : GoodBrush,
                        HasImage = true,
                        Tip = $"Segment {n}\n{report.SegmentStartMm:F1} – {report.SegmentEndMm:F1} mm\n" +
                              $"{report.DefectCount} defect(s)\n" +
                              $"trigger error {report.TriggerErrorMm:F1} mm  ·  " +
                              $"{report.LineSpeedMmPerSec:F0} mm/s\n" +
                              "Click to view the image.",
                    });
                }
                else
                {
                    _tiles.Add(new SegmentTile
                    {
                        Segment = n,
                        Label = n.ToString(),
                        Fill = MissingBrush,
                        HasImage = false,
                        Tip = $"Segment {n}\nNOT INSPECTED — this part of the strip was never looked at.",
                    });
                }
            }

            int notInspected = _tiles.Count(t => !t.HasImage);
            MapHint.Text = highest == 0
                ? "No segments recorded for this run."
                : $"{highest} segment(s)" +
                  (notInspected > 0 ? $"  ·  {notInspected} never inspected" : "") +
                  "  ·  click a segment to open its image";
        }

        private static readonly Brush GoodBrush =
            new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

        private static readonly Brush MissingBrush =
            new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

        private void ClearDetail()
        {
            _selected = null;
            _tiles.Clear();
            _defects.Clear();
            RunTitle.Text = "Select a run";
            RunSubtitle.Text = "";
            MapHint.Text = "";
            DefectsHeader.Text = "Defects";
            DefectsEmpty.Visibility = Visibility.Collapsed;
            CoverageBanner.Visibility = Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════
        //  DRILL-DOWN
        // ══════════════════════════════════════════════════════
        private async void SegmentTick_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SegmentTile tile) return;
            if (_selected == null) return;

            if (!tile.HasImage)
            {
                MessageBox.Show(
                    $"Segment {tile.Segment} was never inspected, so there is no image for it.",
                    "Not Inspected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await OpenSegmentAsync(_selected.Id, tile.Segment);
        }

        private async void DefectsGrid_DoubleClick(object sender, RoutedEventArgs e)
        {
            if (DefectsGrid.SelectedItem is not DefectRow row) return;
            if (_selected == null || row.FirstSegment <= 0) return;
            await OpenSegmentAsync(_selected.Id, row.FirstSegment);
        }

        private async Task OpenSegmentAsync(ObjectId runId, int segment)
        {
            DefectReport? report;
            try
            {
                report = await StripRunStore.QuerySegmentAsync(runId, segment);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load the segment:\n\n{ex.Message}",
                    "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (report == null)
            {
                MessageBox.Show($"No report stored for segment {segment}.",
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (report.ImageData.Length == 0)
            {
                // Expected when Scan.Run.StoreImagesFor is "defects-only": good
                // segments deliberately keep no image, because at a small field
                // of view that is tens of GB a shift.
                MessageBox.Show(
                    $"Segment {segment} ({report.SegmentStartMm:F1} – {report.SegmentEndMm:F1} mm) " +
                    $"was inspected and found {report.DefectCount} defect(s), but its image was not " +
                    "kept.\n\nSet Scan.Run.StoreImagesFor to \"all\" in config.json to keep every " +
                    "segment's image.",
                    "No Image Stored", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var img = new Image
            {
                Source = BytesToImage(report.ImageData),
                Stretch = Stretch.None,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            new Window
            {
                Title = $"Segment {segment}  ·  {report.SegmentStartMm:F1} – {report.SegmentEndMm:F1} mm  ·  " +
                        $"{report.DefectCount} defect(s)",
                Owner = this,
                Width = 1000,
                Height = 760,
                Background = Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = img,
                }
            }.Show();
        }

        private static BitmapImage BytesToImage(byte[] data)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // ── view models ───────────────────────────────────────────────────
        public sealed class SegmentTile
        {
            public int Segment { get; init; }
            public string Label { get; init; } = "";
            public Brush Fill { get; init; } = Brushes.Gray;
            public string Tip { get; init; } = "";
            public bool HasImage { get; init; }
        }

        public sealed class DefectRow
        {
            public int Index { get; init; }
            public string Range { get; init; } = "";
            public string Length { get; init; } = "";
            public string Across { get; init; } = "";
            public string Score { get; init; } = "";
            public string Segments { get; init; } = "";
            public string Merged { get; init; } = "";
            public double StartMm { get; init; }
            public int FirstSegment { get; init; }
        }
    }
}
