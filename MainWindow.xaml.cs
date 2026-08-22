using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace copperInspection
{
    public partial class MainWindow : System.Windows.Window
    {
        private string _folderPath = string.Empty;
        private List<string> _imageFiles = new();

        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };

        private PipelineConfig _config = new();
        private ReferenceColor? _referenceColor;

        public MainWindow()
        {
            InitializeComponent();

            _config = ConfigStore.Load();
            LoadReferenceColor();
            ApplyConfig(_config);
        }

        // ══════════════════════════════════════════════════════
        //  CONFIG PERSISTENCE  (config.json next to the .exe)
        // ══════════════════════════════════════════════════════
        private void LoadReferenceColor()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, _config.ReferenceColorPath);
                _referenceColor = ColorDiffDetector.LoadReference(path);
            }
            catch (Exception ex)
            {
                _referenceColor = null;
                StatusLabel.Text = $"Could not load reference colour: {ex.Message}";
            }
        }

        private void ApplyConfig(PipelineConfig c)
        {
            if (c.BackgroundMethod == "ReferenceDiff") RadioReferenceDiff.IsChecked = true;
            else RadioSilhouette.IsChecked = true;

            AutoThresholdCheck.IsChecked = c.AutoThreshold;
            SilhouetteThresholdSlider.Value = c.SilhouetteThreshold;
            SilhouetteThresholdSlider.IsEnabled = !c.AutoThreshold;

            DiffBlurSlider.Value = c.DiffBlur;
            DiffThresholdSlider.Value = c.DiffThreshold;

            if (c.DetectionMethod == "PatchCore-R50") RadioPatchCoreR50.IsChecked = true;
            else if (c.DetectionMethod == "PatchCore-R18") RadioPatchCoreR18.IsChecked = true;
            else RadioColorDiff.IsChecked = true;

            ColorThresholdSlider.Value = _referenceColor?.Threshold ?? c.ColorDiffThreshold;
            ColorMinAreaSlider.Value = _referenceColor?.MinAreaPct ?? c.ColorDiffMinAreaPct;

            UpdateReferenceSwatch();

            if (!string.IsNullOrWhiteSpace(c.LastFolder) && Directory.Exists(c.LastFolder))
            {
                _folderPath = c.LastFolder;
                LoadImagesFromFolder(_folderPath);
            }
        }

        private void UpdateReferenceSwatch()
        {
            if (_referenceColor == null)
            {
                RefColorSwatch.Background = new SolidColorBrush(Colors.DimGray);
                RefColorLabel.Text = "Not loaded";
                RefHexLabel.Text = string.Empty;
                return;
            }
            var bgr = _referenceColor.Bgr;
            byte r = (byte)Math.Clamp(bgr.Val2, 0, 255);
            byte g = (byte)Math.Clamp(bgr.Val1, 0, 255);
            byte b = (byte)Math.Clamp(bgr.Val0, 0, 255);
            RefColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            RefColorLabel.Text = $"R:{r}  G:{g}  B:{b}";
            RefHexLabel.Text = $"#{r:X2}{g:X2}{b:X2}";
        }

        private PipelineConfig CollectConfig() => new()
        {
            BackgroundMethod = RadioReferenceDiff.IsChecked == true ? "ReferenceDiff" : "Silhouette",
            FlattenLighting = _config.FlattenLighting,
            AutoThreshold = AutoThresholdCheck.IsChecked == true,
            SilhouetteThreshold = (int)SilhouetteThresholdSlider.Value,
            RegionsToKeep = _config.RegionsToKeep,
            MirrorOnLeft = _config.MirrorOnLeft,
            SilhouetteMorphKernel = _config.SilhouetteMorphKernel,
            FillHoles = _config.FillHoles,

            DiffBlur = (int)DiffBlurSlider.Value,
            DiffThreshold = (int)DiffThresholdSlider.Value,
            DiffMorphKernel = _config.DiffMorphKernel,
            MatchExposure = _config.MatchExposure,
            AllowResize = _config.AllowResize,

            DetectionMethod = RadioPatchCoreR50.IsChecked == true ? "PatchCore-R50"
                             : RadioPatchCoreR18.IsChecked == true ? "PatchCore-R18"
                             : "ColorDiff",
            ColorDiffThreshold = ColorThresholdSlider.Value,
            ColorDiffMinAreaPct = ColorMinAreaSlider.Value,
            ColorDiffMorphKernel = _config.ColorDiffMorphKernel,
            ReferenceColorPath = _config.ReferenceColorPath,

            LastFolder = string.IsNullOrWhiteSpace(_folderPath) ? null : _folderPath,
        };

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _config = CollectConfig();
            ConfigStore.Save(_config);
            base.OnClosing(e);
        }

        // ══════════════════════════════════════════════════════
        //  FOLDER BROWSE
        // ══════════════════════════════════════════════════════
        private void BrowseFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select any image inside the folder",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp)" +
                         "|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp" +
                         "|All Files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            _folderPath = Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
            LoadImagesFromFolder(_folderPath);
        }

        private void LoadImagesFromFolder(string folder)
        {
            _imageFiles = Directory
                .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();

            if (_imageFiles.Count == 0)
            {
                MessageBox.Show("No supported images found in that folder.",
                    "Empty Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var names = _imageFiles.Select(Path.GetFileName).ToList();
            ReferenceCombo.ItemsSource = null;
            TargetCombo.ItemsSource = null;
            ReferenceCombo.ItemsSource = names;
            TargetCombo.ItemsSource = names;
            TargetCombo.SelectedIndex = 0;
            if (names.Count > 1) ReferenceCombo.SelectedIndex = 1;

            FolderPathLabel.Text = $"{folder}  ({_imageFiles.Count} images)";
            StatusLabel.Text = $"Loaded {_imageFiles.Count} images.";
            ClearResults();
        }

        private void TargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Selection alone doesn't need to do anything until Run is pressed -
            // images are loaded fresh from disk at run time.
        }

        // ══════════════════════════════════════════════════════
        //  METHOD RADIOS — show/hide the right parameter panels
        // ══════════════════════════════════════════════════════
        private void BgMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (SilhouettePanel == null || DiffPanel == null || BackgroundImagePanel == null) return;

            bool diff = RadioReferenceDiff.IsChecked == true;
            SilhouettePanel.Visibility = diff ? Visibility.Collapsed : Visibility.Visible;
            DiffPanel.Visibility = diff ? Visibility.Visible : Visibility.Collapsed;
            BackgroundImagePanel.Visibility = diff ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DetMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (ColorDiffPanel == null) return;
            ColorDiffPanel.Visibility = RadioColorDiff.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AutoThresholdCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (SilhouetteThresholdSlider != null)
                SilhouetteThresholdSlider.IsEnabled = AutoThresholdCheck.IsChecked != true;
        }

        // ══════════════════════════════════════════════════════
        //  SLIDER VALUE LABEL CALLBACKS
        // ══════════════════════════════════════════════════════
        private void SilhouetteThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => SilhouetteThresholdLabel.Text = ((int)e.NewValue).ToString();

        private void DiffBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => DiffBlurLabel.Text = ((int)e.NewValue).ToString();

        private void DiffThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => DiffThresholdLabel.Text = ((int)e.NewValue).ToString();

        private void ColorThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => ColorThresholdLabel.Text = ((int)e.NewValue).ToString();

        private void ColorMinAreaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => ColorMinAreaLabel.Text = e.NewValue.ToString("F1");

        // ══════════════════════════════════════════════════════
        //  RUN DETECTION
        // ══════════════════════════════════════════════════════
        private async void RunBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_folderPath))
            {
                MessageBox.Show("Select an image folder first.",
                    "No Folder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (TargetCombo.SelectedIndex < 0)
            {
                MessageBox.Show("Select an image to inspect.",
                    "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool useDiff = RadioReferenceDiff.IsChecked == true;
            if (useDiff)
            {
                if (ReferenceCombo.SelectedIndex < 0)
                {
                    MessageBox.Show("Select a background image for Reference-Image Diff mode.",
                        "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (ReferenceCombo.SelectedIndex == TargetCombo.SelectedIndex)
                {
                    MessageBox.Show("Background and target images must differ.",
                        "Same Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (RadioPatchCoreR50.IsChecked == true || RadioPatchCoreR18.IsChecked == true)
            {
                MessageBox.Show("PatchCore support arrives in Phase 2 — use Color Difference for now.",
                    "Not Yet Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_referenceColor == null)
            {
                MessageBox.Show($"No reference colour loaded (Assets\\{_config.ReferenceColorPath} missing or invalid).",
                    "Reference Colour Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Snapshot every UI value now — the worker thread must never touch
            // WPF controls directly.
            string targetPath = _imageFiles[TargetCombo.SelectedIndex];
            string? bgPath = useDiff ? _imageFiles[ReferenceCombo.SelectedIndex] : null;

            var silhouetteParams = new SilhouetteParams
            {
                Flatten = _config.FlattenLighting,
                AutoThreshold = AutoThresholdCheck.IsChecked == true,
                Threshold = (int)SilhouetteThresholdSlider.Value,
                Morph = _config.SilhouetteMorphKernel,
                Keep = _config.RegionsToKeep,
                Fill = _config.FillHoles,
            };
            var diffParams = new DiffParams
            {
                Blur = (int)DiffBlurSlider.Value,
                Threshold = (int)DiffThresholdSlider.Value,
                Morph = _config.DiffMorphKernel,
                Keep = _config.RegionsToKeep,
                MatchExposure = _config.MatchExposure,
                AllowResize = _config.AllowResize,
            };
            double colorThreshold = ColorThresholdSlider.Value;
            double colorMinAreaPct = ColorMinAreaSlider.Value;
            int colorMorph = _config.ColorDiffMorphKernel;
            Scalar referenceBgr = _referenceColor.Bgr;

            _config = CollectConfig();
            ConfigStore.Save(_config);

            SetBusy(true);
            StatusLabel.Text = "Running pipeline…";
            ClearResults();

            Mat? original = null, bgSubtracted = null, heatmap = null, overlay = null;
            string bgNote = "";
            string verdict = "N/A";
            double defectPct = 0.0;
            string? err = null;

            await Task.Run(() =>
            {
                try
                {
                    Mat target = ImageLoader.LoadResized(targetPath);
                    original = target;

                    BackgroundResult bgResult;
                    if (useDiff)
                    {
                        using Mat bg = ImageLoader.LoadResized(bgPath!);
                        bgResult = BackgroundSubtraction.SubtractBackground(bg, target, diffParams);
                    }
                    else
                    {
                        bgResult = BackgroundSubtraction.SegmentSilhouette(target, silhouetteParams);
                    }
                    bgNote = bgResult.Note;
                    bgSubtracted = bgResult.Result.Clone();

                    using ColorDiffResult colorResult = ColorDiffDetector.Inspect(
                        bgResult.Result, referenceBgr, colorThreshold, colorMinAreaPct, colorMorph);
                    verdict = colorResult.Verdict;
                    defectPct = colorResult.DefectPct;
                    heatmap = ColorDiffDetector.DistanceHeatmap(
                        bgResult.Result, colorResult.DistanceMap, colorResult.ContentMask);
                    overlay = ColorDiffDetector.MaskOverlay(bgResult.Result, colorResult.DefectMask);

                    bgResult.Dispose();
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            });

            SetBusy(false);

            if (err != null)
            {
                StatusLabel.Text = "Detection failed.";
                MessageBox.Show($"Error:\n\n{err}", "Detection Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                original?.Dispose(); bgSubtracted?.Dispose(); heatmap?.Dispose(); overlay?.Dispose();
                return;
            }
            if (original == null || bgSubtracted == null || heatmap == null || overlay == null) return;

            Cam1TargetImage.Source = MatToBmp(original);
            Cam1ResultImage.Source = MatToBmp(bgSubtracted);
            Cam2TargetImage.Source = MatToBmp(heatmap);
            Cam2ResultImage.Source = MatToBmp(overlay);

            // ── Save report to MongoDB (encode while the Mat is still alive) ──
            try
            {
                Cv2.ImEncode(".png", overlay, out byte[] png);
                var report = new DefectReport
                {
                    Timestamp = DateTime.UtcNow,
                    DefectCount = verdict == "BAD" ? 1 : 0,
                    Method = "ColorDiff",
                    Mode = useDiff ? "ReferenceDiff" : "Silhouette",
                    ReferenceImage = useDiff ? (ReferenceCombo.SelectedItem as string ?? string.Empty) : string.Empty,
                    TargetImage = TargetCombo.SelectedItem as string ?? string.Empty,
                    ImageData = png,
                    ResultImageBase64 = "data:image/png;base64," + Convert.ToBase64String(png)
                };
                _ = SaveReportAsync(report);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Report not saved: {ex.Message}";
            }

            original.Dispose(); bgSubtracted.Dispose(); heatmap.Dispose(); overlay.Dispose();

            if (verdict == "BAD")
            {
                DefectCountLabel.Text = $"⚠  BAD  ({defectPct:F2}% defect area)";
                DefectBadge.Visibility = Visibility.Visible;
                OkBadge.Visibility = Visibility.Collapsed;
            }
            else if (verdict == "GOOD")
            {
                OkBadgeLabel.Text = $"✓  GOOD  ({defectPct:F2}% defect area)";
                DefectBadge.Visibility = Visibility.Collapsed;
                OkBadge.Visibility = Visibility.Visible;
            }
            else
            {
                DefectBadge.Visibility = Visibility.Collapsed;
                OkBadge.Visibility = Visibility.Collapsed;
            }

            string modeNote = useDiff ? "reference-image diff" : "silhouette";
            StatusLabel.Text = $"Done — {verdict}  ·  {modeNote}{(string.IsNullOrEmpty(bgNote) ? "" : "  ·  " + bgNote)}.";
        }

        // ══════════════════════════════════════════════════════
        //  REPORTS
        // ══════════════════════════════════════════════════════
        private ReportsWindow? _reportsWindow;

        private void ReportsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_reportsWindow != null)
            {
                if (_reportsWindow.WindowState == WindowState.Minimized)
                    _reportsWindow.WindowState = WindowState.Normal;
                _reportsWindow.Activate();
                return;
            }

            _reportsWindow = new ReportsWindow { Owner = this };
            _reportsWindow.Closed += (_, _) => _reportsWindow = null;
            _reportsWindow.Show();
        }

        private async Task SaveReportAsync(DefectReport report)
        {
            try
            {
                await ReportStore.InsertAsync(report);
                _reportsWindow?.AddLiveReport(report);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    StatusLabel.Text = $"MongoDB save failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════
        private static BitmapSource MatToBmp(Mat mat)
            => WriteableBitmapConverter.ToWriteableBitmap(mat);

        private void SetBusy(bool busy)
        {
            RunBtn.IsEnabled = !busy;
            BrowseFolderBtn.IsEnabled = !busy;
            TargetCombo.IsEnabled = !busy;
            ReferenceCombo.IsEnabled = !busy;
            ProgressBar.IsIndeterminate = busy;
            ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearResults()
        {
            Cam1TargetImage.Source = null;
            Cam1ResultImage.Source = null;
            Cam2TargetImage.Source = null;
            Cam2ResultImage.Source = null;
            DefectBadge.Visibility = Visibility.Collapsed;
            OkBadge.Visibility = Visibility.Collapsed;
        }
    }
}
