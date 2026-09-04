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
using System.Windows.Threading;
using copperInspection.Camera;
using copperInspection.Detection;
using System.Numerics.Tensors;

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
        private PatchCoreDetector _detector;
        private string? _loadedBackbone = null;
        private bool _busy = false;

        // Baumer live camera feed - see Camera/BaumerCamera.cs.
        private readonly BaumerCamera _camera = new();
        private List<CameraDeviceInfo> _cameraDevices = new();
        private DispatcherTimer? _liveViewTimer;
        private bool _liveViewActive = false;

        // TEMPORARY: encoder + full scan-pipeline smoke-test - see
        // StartEncoderProbe(). Not the real Strip Scan UI (no Arm/End Strip
        // button, no batch ID field, no strip map) - but a TRIGGER here now
        // really does capture a frame, run it through InspectionEngine, and
        // write a real DefectReport + StripRun to MongoDB. Remove once C9
        // (the real panel) exists.
        private Scan.HttpEncoderSource? _encoderProbe;
        private Scan.ScanPipeline? _scanPipelineProbe;
        private Scan.StripRunRecorder? _stripRecorderProbe;
        private Scan.ScanState _scanProbeLastState = Scan.ScanState.Idle;
        private LiveMaskWindow? _liveMaskWindow;
        private string _pitchNote = "";


        public MainWindow()
        {
            InitializeComponent();

            _config = ConfigStore.Load();
            LoadReferenceColor();
            ApplyConfig(_config);

            // Show CameraBridge.exe's own diagnostics live instead of making
            // anyone go find CameraBridge.log by hand.
            _camera.LogLineReceived += OnCameraLogLine;

            // Discovery/connect can take a moment (GenICam device enumeration) -
            // run it off the UI thread so a missing/slow camera never delays
            // the window opening.
            _ = InitCameraAsync();

            StartEncoderProbe();
        }

        private void OnCameraLogLine(string line)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (CameraLogBox.Text == "(camera bridge not started yet)")
                    CameraLogBox.Text = "";
                CameraLogBox.AppendText(line + Environment.NewLine);
                if (CameraLogBox.Text.Length > 20000)
                    CameraLogBox.Text = CameraLogBox.Text[^15000..];
                CameraLogBox.ScrollToEnd();
            });
        }

        /// <summary>
        /// TEMPORARY smoke-test, not the real Strip Scan UI: starts polling
        /// Scan.Encoder.BaseUrl and routes every line into the same log box
        /// the camera bridge uses (prefixed "[Encoder]"), plus
        /// HttpEncoderSource already writes every line to Debug.WriteLine on
        /// its own regardless of whether anything is listening here.
        ///
        /// Also arms a ScanController against the SAME live samples and logs
        /// every trigger decision (prefixed "[Scan]"), so pressing the button
        /// and changing speed in the mock's own web UI can be watched turning
        /// into "TRIGGER segment N" lines in real time - no camera, no
        /// inspection, just the trigger math against real encoder data.
        ///
        /// Polling itself is deliberately NOT gated on ScanConfig.Validate() -
        /// that check also requires the pixel scale to be set (irrelevant to
        /// "can this app reach the encoder over HTTP"). Arming the trigger
        /// probe IS gated, but on a direct Pitch check rather than Validate(),
        /// because Validate()'s other rules (pixel scale, encoder settings)
        /// don't matter for exercising trigger math either - only geometry
        /// does. The real pre-flight gate belongs on the future Arm button.
        /// </summary>
        private void StartEncoderProbe()
        {
            if (_config.Scan.Simulate.Enabled)
            {
                OnCameraLogLine("[Encoder] Scan.Simulate.Enabled is true - not polling HTTP.");
                return;
            }

            try
            {
                _encoderProbe = new Scan.HttpEncoderSource(_config.Scan);
            }
            catch (InvalidOperationException ex)
            {
                // BaseUrl/Path don't form a valid URL - see EncoderSettings.TryBuildUri.
                OnCameraLogLine($"[Encoder] not started: {ex.Message}");
                return;
            }

            // These fire from the poll loop's own background thread;
            // OnCameraLogLine already marshals to the UI thread via
            // Dispatcher.BeginInvoke, same as it does for the camera bridge.
            _encoderProbe.LogLineReceived += line => OnCameraLogLine($"[Encoder] {line}");
            _encoderProbe.Faulted += why => OnCameraLogLine($"[Encoder] FAULT: {why}");

            _ = ArmScanProbeAsync();

            OnCameraLogLine($"[Encoder] polling {_config.Scan.Encoder.BaseUrl}{_config.Scan.Encoder.Path} " +
                             $"at {_config.Scan.Encoder.PollHz} Hz…");
            _encoderProbe.Start();
        }

       
        private async Task ArmScanProbeAsync()
        {
            try
            {
                await ArmScanProbeCoreAsync();
            }
            catch (Exception ex)
            {
                OnCameraLogLine($"[Scan] not armed: unexpected error: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[Scan] ArmScanProbeAsync failed:\n{ex}");
            }
        }

        private async Task ArmScanProbeCoreAsync()
        {
            double fov = _config.Scan.Geometry.FovAlongTravelMm;
            double overlap = _config.Scan.Geometry.OverlapMm;
            double pitch = Scan.ScanGeometry.PitchMm(_config.Scan);

            if (fov <= 0)
            {
                OnCameraLogLine(
                    "[Scan] not armed: Scan.Geometry.FovAlongTravelMm is 0 in config.json - " +
                    "set it to something greater than OverlapMm before triggering can be tested.");
                return;
            }

            if (pitch <= 0)
            {
                OnCameraLogLine(
                    $"[Scan] not armed: FovAlongTravelMm ({fov:F1}) and OverlapMm ({overlap:F1}) give " +
                    $"Pitch = {pitch:F1} mm. Pitch must be POSITIVE - it's how far the strip travels " +
                    "between captures. In config.json, either raise Scan.Geometry.FovAlongTravelMm " +
                    "above OverlapMm, or lower OverlapMm below FovAlongTravelMm. " +
                    "e.g. FovAlongTravelMm: 100, OverlapMm: 25 -> Pitch = 75 mm.");
                return;
            }

            bool useDiff = _config.BackgroundMethod == "ReferenceDiff";
            if (useDiff)
            {
                // A diff-mode background image needs picking (the manual UI's
                // ReferenceCombo); this probe has no UI for that, so refuse
                // rather than crash on the first capture.
                OnCameraLogLine(
                    "[Scan] not armed: BackgroundMethod is \"ReferenceDiff\" in config.json, which needs " +
                    "a background image path this probe doesn't collect. Switch BackgroundMethod to " +
                    "\"Silhouette\" to test the probe, or wait for the real Strip Scan UI.");
                return;
            }

            // The pipeline itself is built ONCE and lives for the whole
            // session; only the recorder (and the MongoDB run it owns)
            // is per-strip. "inspect" reads _stripRecorderProbe fresh on
            // every call rather than capturing one instance, so it always
            // routes to whichever strip is CURRENTLY open even after
            // re-arming below swaps that field out for a new run.
            _scanPipelineProbe = new Scan.ScanPipeline(
                _config.Scan,
                _encoderProbe!,
                () => _camera.CaptureFullFrame(),
                job => _stripRecorderProbe!.InspectSegmentAsync(job));

            _scanPipelineProbe.SegmentFinished += rec => OnCameraLogLine(
                $"[Scan] segment {rec.Segment}   [{rec.StartMm:F1}-{rec.EndMm:F1}]mm   " +
                $"encoderMm={rec.EncoderMm:F1}   error={rec.TriggerErrorMm:F1}mm   " +
                $"speed={rec.SpeedMmPerSec:F0}mm/s   -> {rec.Outcome}" +
                (rec.Error != null ? $"   ({rec.Error})" : ""));

            _scanPipelineProbe.Fault += why => OnCameraLogLine($"[Scan] FAULT: {why}");

            _scanPipelineProbe.RunFinished += reason =>
            {
                OnCameraLogLine($"[Scan] run finished: {reason}");
                _ = FinishAndRearmAsync(reason);
            };

            // Purely observational - logs Armed -> Scanning so the button
            // press itself is visible, separate from each segment's own line.
            _encoderProbe!.SampleReceived += _ =>
            {
                if (_scanPipelineProbe == null) return;
                if (_scanPipelineProbe.State != _scanProbeLastState)
                {
                    _scanProbeLastState = _scanPipelineProbe.State;
                    OnCameraLogLine($"[Scan] state -> {_scanProbeLastState}");
                }
            };

            if (_config.Scan.Run.ShowBackgroundMaskWindow)
            {
                _liveMaskWindow = new LiveMaskWindow();

                // This runs from the constructor, before THIS window has ever
                // been shown - Left/Top default to NaN until then (Width is
                // fine, it's set explicitly in XAML). Assigning NaN into a
                // WindowStartupLocation="Manual" window's Left/Top can fail
                // when WPF creates the native window, so only position it
                // relative to the main window when that's actually known yet.
                if (!double.IsNaN(Left) && !double.IsNaN(Top) && !double.IsNaN(Width))
                {
                    _liveMaskWindow.Left = Left + Width + 12;
                    _liveMaskWindow.Top = Top;
                }
                _liveMaskWindow.Show();
            }

            _pitchNote = $"Pitch = {pitch:F1} mm (Fov {fov:F1} - Overlap {overlap:F1})";
            await StartNewStripRunAsync(useDiff);
        }

        /// <summary>
        /// Opens a fresh MongoDB StripRun and arms the (already-built,
        /// long-lived) pipeline for it. Called once for the first strip, and
        /// again every time RunFinished fires - see FinishAndRearmAsync -
        /// so consecutive button presses in the mock keep working without
        /// restarting the app. Every call gets its own StripRunRecorder
        /// (each strip is its own Mongo document), which is why the
        /// SegmentImagesReady subscriptions are (re)made here rather than
        /// once at startup.
        /// </summary>
        private async Task StartNewStripRunAsync(bool useDiff)
        {
            var silhouette = new SilhouetteParams
            {
                Flatten = _config.FlattenLighting,
                AutoThreshold = _config.AutoThreshold,
                Threshold = _config.SilhouetteThreshold,
                Morph = _config.SilhouetteMorphKernel,
                Keep = _config.RegionsToKeep,
                Fill = _config.FillHoles,
            };
            var diff = new DiffParams
            {
                Blur = _config.DiffBlur,
                Threshold = _config.DiffThreshold,
                Morph = _config.DiffMorphKernel,
                Keep = _config.RegionsToKeep,
                MatchExposure = _config.MatchExposure,
                AllowResize = _config.AllowResize,
            };

            var recorder = new Scan.StripRunRecorder(
                _config.Scan,
                () => InspectionSettings.ForScan(
                    _config.Scan, useDiff, backgroundPath: null, silhouette, diff,
                    _config.ColorDiffThreshold, _config.ColorDiffMinAreaPct, _config.ColorDiffMorphKernel,
                    _referenceColor?.Bgr ?? default),
                GetOrLoadDetector);

            // Push every scan-produced report into the live gallery in an
            // already-open Reports window, the same way a manual capture
            // does via SaveReportAsync - without this, scan reports land in
            // MongoDB correctly but the open window just never hears about
            // them, so it sits frozen at whatever it showed when opened.
            recorder.ReportSaved += report =>
                Dispatcher.BeginInvoke(() => _reportsWindow?.AddLiveReport(report));

            if (_liveMaskWindow != null)
            {
                recorder.SegmentImagesReady += images =>
                {
                    // Fires on the pipeline's background consumer thread - the
                    // Mats are only valid for the duration of this call (see
                    // SegmentImagesReady's doc comment), so the conversion to
                    // a BitmapSource, and the Freeze() that makes it safe to
                    // hand to another thread, both happen synchronously here.
                    BitmapSource bmp = MatToBmp(images.BackgroundSubtracted);
                    bmp.Freeze();
                    Dispatcher.BeginInvoke(() =>
                        _liveMaskWindow?.UpdateImage(bmp, $"Segment {images.Segment} - Background Subtracted"));
                };
            }

            // Mirror every capture into the main window's four result slots -
            // only ONE physical camera exists right now (_camera), so "camera
            // 1" and "camera 2" show the same images until a real second
            // camera is wired up:
            //   slot 1 (top-left)     = camera 1 feed      = the raw capture
            //   slot 2 (top-right)    = camera 2 feed      = mirrored capture
            //   slot 3 (bottom-left)  = camera 1 result     = heatmap overlay
            //   slot 4 (bottom-right) = camera 2 result     = mirrored overlay
            // NOTE: the static labels under these four boxes still read
            // "Original / Background Subtracted / Distance from Reference /
            // Defect Mask" - that's manual Run Detection's own, different
            // mapping into these same four controls, left untouched.
            recorder.SegmentImagesReady += images =>
            {
                BitmapSource feed = MatToBmp(images.Original);
                BitmapSource result = MatToBmp(images.Overlay);
                feed.Freeze();
                result.Freeze();
                Dispatcher.BeginInvoke(() =>
                {
                    Cam1TargetImage.Source = feed;     // camera 1 feed
                    Cam1ResultImage.Source = feed;     // camera 2 feed (mirrored)
                    Cam2TargetImage.Source = result;   // camera 1 result
                    Cam2ResultImage.Source = result;   // camera 2 result (mirrored)
                });
            };

            string batchId = $"probe-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                await recorder.StartAsync(batchId);
            }
            catch (Exception ex)
            {
                OnCameraLogLine($"[Scan] not armed: could not open a StripRun in MongoDB: {ex.Message}");
                return;
            }

            _stripRecorderProbe = recorder;
            _scanPipelineProbe!.Arm();
            _scanProbeLastState = _scanPipelineProbe.State;

            OnCameraLogLine(
                $"[Scan] StripRun opened, batchId={batchId}   armed - {_pitchNote}. " +
                "Waiting for the button press in the mock's /ui… " +
                "(camera must already be Connected + Streaming for a trigger to capture anything)");
        }

        /// <summary>Closes out the strip that just ended, then immediately
        /// re-arms for the next one - see StartNewStripRunAsync.</summary>
        private async Task FinishAndRearmAsync(Scan.ScanEndReason reason)
        {
            if (_stripRecorderProbe == null || _scanPipelineProbe == null) return;

            bool useDiff = _config.BackgroundMethod == "ReferenceDiff";
            try
            {
                Scan.StripRun run = await _stripRecorderProbe.FinishAsync(_scanPipelineProbe.Records, reason);
                OnCameraLogLine(
                    $"[Scan] StripRun closed: {run.Verdict}   segments={run.SegmentCount}   " +
                    $"defects={run.DefectCount}   coverageComplete={run.CoverageComplete}" +
                    (run.MissingSegments.Count > 0
                        ? $"   missing=[{string.Join(",", run.MissingSegments)}]"
                        : ""));
            }
            catch (Exception ex)
            {
                OnCameraLogLine($"[Scan] could not finalise StripRun: {ex.Message}");
            }

            await StartNewStripRunAsync(useDiff);
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

            FlattenLightingCheck.IsChecked = c.FlattenLighting;
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
            FlattenLighting = FlattenLightingCheck.IsChecked == true,
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

            // No UI edits this yet (no Strip Scan panel) - carry it forward
            // unchanged. Without this line, CollectConfig() silently produces
            // a brand-new default ScanConfig() instead (PipelineConfig.Scan's
            // own field initializer), and every save - on app close AND on
            // every "Run Detection" click - overwrites config.json's whole
            // Scan block back to hard defaults. This was the actual cause of
            // Scan.Encoder.BaseUrl/TimeoutMs/Geometry.FovAlongTravelMm
            // repeatedly reverting during testing.
            Scan = _config.Scan,

            LastFolder = string.IsNullOrWhiteSpace(_folderPath) ? null : _folderPath,
        };

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _config = CollectConfig();
            ConfigStore.Save(_config);
            SetLiveViewActive(false);
            _camera.Dispose();
            // Best-effort: End() drains the queue asynchronously and the
            // window is closing regardless, so this may not finish before
            // the process exits - acceptable for a smoke-test probe, not for
            // the real Arm/End Strip UI later.
            _scanPipelineProbe?.End();
            _encoderProbe?.Dispose();
            _liveMaskWindow?.Close();
            base.OnClosing(e);
        }

        // ══════════════════════════════════════════════════════
        //  FOLDER BROWSE
        // ══════════════════════════════════════════════════════
        private void BrowseFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select an image to inspect",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp)" +
                         "|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp" +
                         "|All Files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            // The dialog is used to pick a folder (via one file in it), but the
            // actual file the user clicked is what should end up selected +
            // previewed - not just whatever sorts first in that folder.
            _folderPath = Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
            LoadImagesFromFolder(_folderPath, dlg.FileName);
        }

        private void LoadImagesFromFolder(string folder, string? selectedFile = null)
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

            // Default to whichever file the user actually clicked in the Browse
            // dialog, not just the alphabetically-first file in the folder.
            int targetIndex = 0;
            if (selectedFile != null)
            {
                string selectedName = Path.GetFileName(selectedFile);
                int idx = names.FindIndex(n => string.Equals(n, selectedName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) targetIndex = idx;
            }
            TargetCombo.SelectedIndex = targetIndex;   // fires TargetCombo_SelectionChanged -> previews it
            if (names.Count > 1)
                ReferenceCombo.SelectedIndex = targetIndex == 0 ? 1 : 0;

            FolderPathLabel.Text = $"{folder}  ({_imageFiles.Count} images)";
            StatusLabel.Text = $"Loaded {_imageFiles.Count} images.";
        }

        private void TargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // A file was picked for testing - stop the live feed from
            // immediately overwriting it in slot 1. Start Stream resumes it.
            SetLiveViewActive(false);

            // Show the picked image immediately - don't make the operator click
            // Run Detection just to confirm what they selected.
            ClearResults();
            PreviewSelectedTarget();
        }

        private void PreviewSelectedTarget()
        {
            if (TargetCombo.SelectedIndex < 0 || TargetCombo.SelectedIndex >= _imageFiles.Count)
                return;

            try
            {
                using Mat img = ImageLoader.LoadResized(_imageFiles[TargetCombo.SelectedIndex]);
                Cam1TargetImage.Source = MatToBmp(img);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Failed to preview image: {ex.Message}";
            }
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
        //  CAMERA (Baumer / NeoAPI live feed)
        // ══════════════════════════════════════════════════════
        private async Task InitCameraAsync()
        {
            var devices = await Task.Run(() =>
            {
                // Launches CameraBridge.exe (separate process - see
                // Camera/CameraProtocol.cs for why) and lists cameras
                // through it. Returns empty if the bridge exe isn't present
                // or fails to start - same as "no camera detected".
                if (!_camera.EnsureStarted()) return new List<CameraDeviceInfo>();
                return _camera.ListCameras();
            });
            _cameraDevices = devices;
            CameraCombo.ItemsSource = null;
            CameraCombo.ItemsSource = devices;

            if (devices.Count == 0)
            {
                CameraStatusLabel.Text = "No camera detected";
                return;
            }

            // Selecting index 0 fires CameraCombo_SelectionChanged, which does
            // the actual Connect() + StartStreaming() - this is also the same
            // code path used when an operator later picks a different camera
            // from the dropdown, so there is only one place that ever connects.
            CameraCombo.SelectedIndex = 0;
        }

        private async void CameraCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CameraCombo.SelectedIndex < 0 || CameraCombo.SelectedIndex >= _cameraDevices.Count) return;

            SetLiveViewActive(false);
            var device = _cameraDevices[CameraCombo.SelectedIndex];
            CameraStatusLabel.Text = $"Connecting to {device}…";
            CameraCombo.IsEnabled = false;

            string? error = null;
            await Task.Run(() =>
            {
                try { _camera.Connect(device); }
                catch (Exception ex) { error = ex.Message; }
            });

            CameraCombo.IsEnabled = !_busy;
            if (error != null)
            {
                CameraStatusLabel.Text = $"Connect failed: {error}";
                UpdateCameraButtonStates();
                return;
            }

            CameraStatusLabel.Text = $"Connected — streaming ({device})";
            UpdateCameraButtonStates();
            SetLiveViewActive(true);
        }

        private void StartStreamBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_camera.IsConnected)
            {
                StatusLabel.Text = "No camera connected.";
                return;
            }
            _camera.StartStreaming();
            UpdateCameraButtonStates();
            SetLiveViewActive(true);
            StatusLabel.Text = "Live stream started.";
        }

        private void StopStreamBtn_Click(object sender, RoutedEventArgs e)
        {
            // Testing with an uploaded file shouldn't fight the live feed for
            // slot 1 - this is the manual escape hatch for that, on top of the
            // automatic pause when a file is picked (TargetCombo_SelectionChanged).
            SetLiveViewActive(false);
            _camera.StopStreaming();
            UpdateCameraButtonStates();
            StatusLabel.Text = "Live stream stopped.";
        }

        /// <summary>Starts/stops the DispatcherTimer that paints slot 1 from the
        /// camera's latest frame. Decoupled from the camera's own frame rate -
        /// polls at a fixed ~15 fps so a fast camera can't flood the UI thread's
        /// dispatcher queue, and does nothing to the acquisition thread itself
        /// (that keeps running independently either way).</summary>
        private void SetLiveViewActive(bool active)
        {
            _liveViewActive = active && _camera.IsConnected && _camera.IsStreaming;
            if (_liveViewActive)
            {
                if (_liveViewTimer == null)
                {
                    _liveViewTimer = new DispatcherTimer(DispatcherPriority.Render)
                    {
                        Interval = TimeSpan.FromMilliseconds(66),
                    };
                    _liveViewTimer.Tick += LiveViewTimer_Tick;
                }
                _liveViewTimer.Start();
            }
            else
            {
                _liveViewTimer?.Stop();
            }
        }

        private void LiveViewTimer_Tick(object? sender, EventArgs e)
        {
            using Mat? frame = _camera.GetLatestPreviewFrame();
            if (frame == null || frame.Empty()) return;
            Cam1TargetImage.Source = MatToBmp(frame);
        }

        /// <summary>Single source of truth for the camera-related buttons'
        /// enabled state - called both from SetBusy and right after any camera
        /// state change (connect/disconnect/start/stop) so they never go stale.</summary>
        private void UpdateCameraButtonStates()
        {
            CameraCombo.IsEnabled = !_busy;
            StartStreamBtn.IsEnabled = !_busy && _camera.IsConnected && !_camera.IsStreaming;
            StopStreamBtn.IsEnabled = !_busy && _camera.IsConnected && _camera.IsStreaming;
            CaptureBtn.IsEnabled = !_busy && _camera.IsConnected && _camera.IsStreaming;
        }

        // ══════════════════════════════════════════════════════
        //  Patchcore loading class
        // ══════════════════════════════════════════════════════



        private PatchCoreDetector GetOrLoadDetector(string backboneName)
        {
            // If the detector is already initialized for this backbone, reuse it
            if (_detector != null && _loadedBackbone == backboneName)
            {
                return _detector;
            }

            // Dispose existing session before loading new model
            _detector?.Dispose();

            string onnxFile = backboneName == "PatchCore-R18"
                ? "patchcore_resnet18.onnx"
                : "patchcore_resnet50.onnx";

            string binFile = backboneName == "PatchCore-R18"
                ? "patchcore_resnet18_bank.bin"
                : "patchcore_resnet50_bank.bin";

            string metaFile = backboneName == "PatchCore-R18"
                ? "patchcore_resnet18_meta.json"
                : "patchcore_resnet50_meta.json";

            string onnxPath = Path.Combine(AppContext.BaseDirectory, "Assets", onnxFile);
            string binPath = Path.Combine(AppContext.BaseDirectory, "Assets", binFile);
            string metaPath = Path.Combine(AppContext.BaseDirectory, "Assets", metaFile);

            if (!File.Exists(onnxPath) || !File.Exists(binPath) || !File.Exists(metaPath))
            {
                throw new FileNotFoundException($"PatchCore asset files missing in Assets directory for {backboneName}. " +
                                                $"Ensure {onnxFile}, {binFile}, and {metaFile} exist.");
            }

            // Threshold, image_min/image_max, and is_grayscale are all read
            // from metaFile inside the constructor (PatchCoreDetector.LoadMetadata)
            // - single source of truth, so there's no separate parsing here to
            // drift out of sync with it.
            _detector = new PatchCoreDetector(onnxPath, binPath, metaPath);
            _loadedBackbone = backboneName;
            return _detector;
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

            // Determine Detection Mode
            string detectionMethod = RadioPatchCoreR50.IsChecked == true ? "PatchCore-R50"
                                   : RadioPatchCoreR18.IsChecked == true ? "PatchCore-R18"
                                   : "ColorDiff";

            if (detectionMethod == "ColorDiff" && _referenceColor == null)
            {
                MessageBox.Show($"No reference colour loaded (Assets\\{_config.ReferenceColorPath} missing or invalid).",
                    "Reference Colour Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Snapshot parameters
            string targetPath = _imageFiles[TargetCombo.SelectedIndex];
            string? bgPath = useDiff ? _imageFiles[ReferenceCombo.SelectedIndex] : null;
            string targetLabel = TargetCombo.SelectedItem as string ?? Path.GetFileName(targetPath);

            var (silhouetteParams, diffParams, colorThreshold, colorMinAreaPct, colorMorph, referenceBgr) = SnapshotParams();

            _config = CollectConfig();
            ConfigStore.Save(_config);

            // PatchCore loads at native resolution, not the 800px display
            // size: BackgroundSubtraction's morphology kernels are a fixed
            // pixel size (e.g. 5x5), so they erode/smooth proportionally more
            // on a downscaled image - segmenting it differently than the
            // Python reference tooling, which never resizes before
            // segmenting, and than however the model's own training/
            // calibration images were prepared. Confirmed empirically: same
            // image+settings scored GOOD at native resolution, BAD at
            // 800px-wide. See docs/WORK_LOG.md. Color Difference keeps the
            // 800px path - this fix is scoped to what was actually shown to
            // need it.
            await RunDetectionPipelineAsync(
                isPatchCore => isPatchCore ? ImageLoader.LoadFull(targetPath) : ImageLoader.LoadResized(targetPath),
                targetLabel, useDiff, bgPath, silhouetteParams, diffParams, detectionMethod,
                colorThreshold, colorMinAreaPct, colorMorph, referenceBgr);
        }

        // ══════════════════════════════════════════════════════
        //  CAPTURE (grab the live frame and run it through the
        //  currently selected pipeline, same as Run Detection)
        // ══════════════════════════════════════════════════════
        private async void CaptureBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_camera.IsConnected || !_camera.IsStreaming)
            {
                MessageBox.Show("No live camera stream - connect a camera and start the stream first.",
                    "Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Round trip to CameraBridge.exe for a fresh, lossless frame -
            // deliberately not GetLatestPreviewFrame(), which is JPEG-
            // compressed for the live view and shouldn't feed detection.
            SetBusy(true);
            StatusLabel.Text = "Capturing frame…";
            Mat? frame = await Task.Run(() => _camera.CaptureFullFrame());
            SetBusy(false);

            if (frame == null || frame.Empty())
            {
                frame?.Dispose();
                MessageBox.Show("No frame received yet - wait a moment and try again.",
                    "Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool useDiff = RadioReferenceDiff.IsChecked == true;
            string? bgPath = null;
            if (useDiff)
            {
                if (ReferenceCombo.SelectedIndex < 0)
                {
                    frame.Dispose();
                    MessageBox.Show("Select a background image for Reference-Image Diff mode.",
                        "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                bgPath = _imageFiles[ReferenceCombo.SelectedIndex];
            }

            string detectionMethod = RadioPatchCoreR50.IsChecked == true ? "PatchCore-R50"
                                   : RadioPatchCoreR18.IsChecked == true ? "PatchCore-R18"
                                   : "ColorDiff";
            if (detectionMethod == "ColorDiff" && _referenceColor == null)
            {
                frame.Dispose();
                MessageBox.Show($"No reference colour loaded (Assets\\{_config.ReferenceColorPath} missing or invalid).",
                    "Reference Colour Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var (silhouetteParams, diffParams, colorThreshold, colorMinAreaPct, colorMorph, referenceBgr) = SnapshotParams();
            string label = $"Live Capture {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            // The captured frame is already native camera resolution -
            // PatchCore uses it as-is (see the LoadFull comment in
            // RunBtn_Click); Color Difference still downscales to the 800px
            // path it was calibrated against, same as the file-based flow.
            await RunDetectionPipelineAsync(
                isPatchCore =>
                {
                    if (isPatchCore) return frame;
                    using Mat raw = frame;
                    return ImageLoader.ResizeToDisplay(raw);
                },
                label, useDiff, bgPath, silhouetteParams, diffParams, detectionMethod,
                colorThreshold, colorMinAreaPct, colorMorph, referenceBgr);
        }

        /// <summary>Reads the current parameter controls into the value types
        /// the pipeline needs - shared by both Run Detection and Capture so
        /// they can never read the UI differently from one another.</summary>
        private (SilhouetteParams silhouette, DiffParams diff, double colorThreshold,
            double colorMinAreaPct, int colorMorph, Scalar referenceBgr) SnapshotParams()
        {
            var silhouetteParams = new SilhouetteParams
            {
                Flatten = FlattenLightingCheck.IsChecked == true,
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
            Scalar referenceBgr = _referenceColor?.Bgr ?? new Scalar(0, 0, 0);
            return (silhouetteParams, diffParams, colorThreshold, colorMinAreaPct, colorMorph, referenceBgr);
        }

        /// <summary>The actual background-subtraction + detector dispatch +
        /// verdict/report pipeline, shared by Run Detection (file-based
        /// target) and Capture (live-frame target). loadTarget is called
        /// exactly once, inside the background Task, with whether the
        /// selected detection method is PatchCore - it returns the Mat the
        /// rest of the pipeline should treat as "the original image", and the
        /// pipeline takes ownership of (and disposes) whatever it returns.</summary>
        private async Task RunDetectionPipelineAsync(
            Func<bool, Mat> loadTarget,
            string targetLabel,
            bool useDiff,
            string? bgPath,
            SilhouetteParams silhouetteParams,
            DiffParams diffParams,
            string detectionMethod,
            double colorThreshold,
            double colorMinAreaPct,
            int colorMorph,
            Scalar referenceBgr)
        {
            // Don't let the live feed repaint slot 1 out from under whatever
            // this run is about to show there.
            SetLiveViewActive(false);

            SetBusy(true);
            StatusLabel.Text = $"Running pipeline ({detectionMethod})…";
            ClearResults();

            // The pipeline itself lives in InspectionEngine, shared with the
            // strip-scan consumer, so a manual run and a scanned segment can
            // never drift apart in behaviour.
            InspectionResult? result = null;
            string? err = null;
            string bgNote = "";
            string verdict = "N/A";
            string? appliedSegNote = null;

            var settings = new InspectionSettings
            {
                UseDiff = useDiff,
                BackgroundPath = bgPath,
                Silhouette = silhouetteParams,
                Diff = diffParams,
                Method = detectionMethod,
                ColorThreshold = colorThreshold,
                ColorMinAreaPct = colorMinAreaPct,
                ColorMorph = colorMorph,
                ReferenceBgr = referenceBgr,
            };

            await Task.Run(() =>
            {
                try
                {
                    result = InspectionEngine.Run(loadTarget, settings, GetOrLoadDetector);
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
                result?.Dispose();
                return;
            }
            if (result == null) return;

            using (result)
            {
                // Reflect whatever segmentation settings actually ran back onto
                // the controls, so the UI never shows something different from
                // what was fed to the model - the engine may have overridden
                // them with the PatchCore model's recorded training settings.
                if (result.SegmentationNote != null &&
                    result.AppliedSilhouette != null && result.AppliedDiff != null)
                {
                    FlattenLightingCheck.IsChecked = result.AppliedSilhouette.Flatten;
                    AutoThresholdCheck.IsChecked = result.AppliedSilhouette.AutoThreshold;
                    SilhouetteThresholdSlider.Value = result.AppliedSilhouette.Threshold;
                    DiffBlurSlider.Value = result.AppliedDiff.Blur;
                    DiffThresholdSlider.Value = result.AppliedDiff.Threshold;
                }

                Cam1TargetImage.Source = MatToBmp(result.Original);
                Cam1ResultImage.Source = MatToBmp(result.BackgroundSubtracted);
                Cam2TargetImage.Source = MatToBmp(result.Heatmap);
                Cam2ResultImage.Source = MatToBmp(result.Overlay);

                // ── Save report to MongoDB ──
                try
                {
                    Cv2.ImEncode(".png", result.Overlay, out byte[] png);
                    var report = new DefectReport
                    {
                        Timestamp = DateTime.UtcNow,
                        DefectCount = result.IsDefect ? 1 : 0,
                        Method = result.Method,
                        Mode = useDiff ? "ReferenceDiff" : "Silhouette",
                        ReferenceImage = useDiff ? (ReferenceCombo.SelectedItem as string ?? string.Empty) : string.Empty,
                        TargetImage = targetLabel,
                        ImageData = png,
                        ResultImageBase64 = "data:image/png;base64," + Convert.ToBase64String(png)
                    };
                    _ = SaveReportAsync(report);
                }
                catch (Exception ex)
                {
                    StatusLabel.Text = $"Report not saved: {ex.Message}";
                }

                bgNote = result.BackgroundNote;
                appliedSegNote = result.SegmentationNote;
                verdict = result.Verdict;

                string subLabel = result.Method.StartsWith("PatchCore")
                    ? $"Score: {result.Score:F3}"
                    : $"{result.Score:F2}% defect area";

                if (verdict == "BAD")
                {
                    DefectCountLabel.Text = $"⚠ BAD ({subLabel})";
                    DefectBadge.Visibility = Visibility.Visible;
                    OkBadge.Visibility = Visibility.Collapsed;
                }
                else if (verdict == "GOOD")
                {
                    OkBadgeLabel.Text = $"✓ GOOD ({subLabel})";
                    DefectBadge.Visibility = Visibility.Collapsed;
                    OkBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    DefectBadge.Visibility = Visibility.Collapsed;
                    OkBadge.Visibility = Visibility.Collapsed;
                }
            }

            string modeNote = useDiff ? "reference-image diff" : "silhouette";
            string segAppliedNote = appliedSegNote != null ? $" · {appliedSegNote}" : "";
            StatusLabel.Text = $"Done — {verdict} · {modeNote}{(string.IsNullOrEmpty(bgNote) ? "" : " · " + bgNote)}{segAppliedNote}.";
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

        private StripRunsWindow? _stripRunsWindow;

        private void StripRunsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_stripRunsWindow != null)
            {
                if (_stripRunsWindow.WindowState == WindowState.Minimized)
                    _stripRunsWindow.WindowState = WindowState.Normal;
                _stripRunsWindow.Activate();
                return;
            }

            _stripRunsWindow = new StripRunsWindow { Owner = this };
            _stripRunsWindow.Closed += (_, _) => _stripRunsWindow = null;
            _stripRunsWindow.Show();
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
            _busy = busy;
            RunBtn.IsEnabled = !busy;
            BrowseFolderBtn.IsEnabled = !busy;
            TargetCombo.IsEnabled = !busy;
            ReferenceCombo.IsEnabled = !busy;
            ProgressBar.IsIndeterminate = busy;
            ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UpdateCameraButtonStates();
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