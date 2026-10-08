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

        // Baumer live camera feed - see Camera/BaumerCamera.cs. _camera is
        // the primary camera - the one CameraCombo/Start/Stop Stream control,
        // exactly as before. _cameraB is a second one, auto-detected and
        // auto-connected with no manual controls of its own (v1 - see
        // ConnectCameraBAsync): when a second physical camera is found, its
        // live feed and results take over slots 2 and 4 instead of those
        // slots showing manual Run Detection's background-subtracted/mask
        // views. Each camera talks to its own CameraBridge.exe process
        // ("A"/"B" channels), never sharing one - see Camera/CameraProtocol.cs.
        private readonly BaumerCamera _camera = new("A");
        private readonly BaumerCamera _cameraB = new("B");
        private bool _dualCameraMode = false;
        private List<CameraDeviceInfo> _cameraDevices = new();
        private DispatcherTimer? _liveViewTimer;
        private bool _liveViewActive = false;

        // TEMPORARY: encoder + full scan-pipeline smoke-test - see
        // StartEncoderProbe(). Not the real Strip Scan UI (no Arm/End Strip
        // button, no batch ID field, no strip map) - but a TRIGGER here now
        // really does capture a frame, run it through InspectionEngine, and
        // write a real DefectReport + StripRun to MongoDB. Remove once C9
        // (the real panel) exists.
        private Scan.IEncoderSource? _encoderProbe;
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
            _cameraB.LogLineReceived += line => OnCameraLogLine($"[CamB] {line}");

            // Discovery/connect can take a moment (GenICam device enumeration) -
            // run it off the UI thread so a missing/slow camera never delays
            // the window opening.
            _ = InitCameraAsync();

            // ApplyConfig() above already started the scan probe via
            // TriggerModeCombo's SelectionChanged handler - see its comment.

            StartMongoWatch();
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
        /// <summary>Builds whichever trigger source Scan.Trigger.Mode asks
        /// for ("Encoder" - the existing, unchanged HTTP poll - or
        /// "Interval" - a fixed wall-clock period, see
        /// Scan/IntervalEncoderSource.cs) and arms the scan probe against
        /// it. Also callable again later (TriggerModeCombo_SelectionChanged)
        /// to switch modes live - StopScanTrigger() tears down whatever is
        /// currently running first so the two can never run at once.</summary>
        private void StartEncoderProbe()
        {
            if (_config.Scan.Trigger.Mode == Scan.TriggerMode.Interval)
            {
                _encoderProbe = new Scan.IntervalEncoderSource(_config.Scan);
                _ = ArmScanProbeAsync();
                OnCameraLogLine(
                    $"[Encoder] Interval mode - firing every {_config.Scan.Trigger.IntervalMs:F0} ms, " +
                    "no distance measurement involved.");
                _encoderProbe.Start();
                return;
            }

            if (_config.Scan.Simulate.Enabled)
            {
                OnCameraLogLine("[Encoder] Scan.Simulate.Enabled is true - not polling HTTP.");
                return;
            }

            Scan.HttpEncoderSource httpSource;
            try
            {
                httpSource = new Scan.HttpEncoderSource(_config.Scan);
            }
            catch (InvalidOperationException ex)
            {
                // BaseUrl/Path don't form a valid URL - see EncoderSettings.TryBuildUri.
                OnCameraLogLine($"[Encoder] not started: {ex.Message}");
                return;
            }
            _encoderProbe = httpSource;

            // These fire from the poll loop's own background thread;
            // OnCameraLogLine already marshals to the UI thread via
            // Dispatcher.BeginInvoke, same as it does for the camera bridge.
            httpSource.LogLineReceived += line => OnCameraLogLine($"[Encoder] {line}");
            httpSource.Faulted += why => OnCameraLogLine($"[Encoder] FAULT: {why}");

            _ = ArmScanProbeAsync();

            OnCameraLogLine($"[Encoder] polling {_config.Scan.Encoder.BaseUrl}{_config.Scan.Encoder.Path} " +
                             $"at {_config.Scan.Encoder.PollHz} Hz…");
            httpSource.Start();
        }

        /// <summary>Tears down whatever trigger source/pipeline/open
        /// StripRun is currently running, best-effort, so a fresh
        /// StartEncoderProbe() call never ends up with two trigger sources
        /// feeding one ScanController at once. Used when the Trigger Mode
        /// dropdown changes.</summary>
        private async Task StopScanTriggerAsync()
        {
            // Order matters: kill the trigger source FIRST so no new frames
            // can be queued, then tell the pipeline to wind down, and only
            // then finalise and drop the recorder. Tearing down in the other
            // order leaves the consumer thread holding frames whose recorder
            // has already been nulled.
            _encoderProbe?.Stop();
            _encoderProbe?.Dispose();
            _encoderProbe = null;

            try { _scanPipelineProbe?.End(); }
            catch (Exception ex) { OnCameraLogLine($"[Scan] pipeline end: {ex.Message}"); }

            if (_scanPipelineProbe != null && _stripRecorderProbe != null)
            {
                try
                {
                    await _stripRecorderProbe.FinishAsync(
                        _scanPipelineProbe.Records, Scan.ScanEndReason.Operator);
                }
                catch (Exception ex)
                {
                    OnCameraLogLine($"[Scan] could not finalise StripRun while switching trigger mode: {ex.Message}");
                }
            }

            _scanPipelineProbe = null;
            _stripRecorderProbe = null;
            _scanProbeLastState = Scan.ScanState.Idle;
        }

        /// <summary>"Encoder" / "Fixed Interval" - see the TriggerModeCombo
        /// XAML and StartEncoderProbe. Switching live tears down and
        /// re-arms the whole probe, same sequence FinishAndRearmAsync
        /// already uses between one strip and the next.</summary>
        private async void TriggerModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TriggerModeCombo.SelectedIndex < 0) return;
            bool interval = TriggerModeCombo.SelectedIndex == 1;

            _config.Scan.Trigger.Mode = interval ? Scan.TriggerMode.Interval : Scan.TriggerMode.Encoder;
            if (double.TryParse(TriggerIntervalBox.Text, out double ms) && ms > 0)
                _config.Scan.Trigger.IntervalMs = ms;
            TriggerIntervalBox.IsEnabled = interval;

            // Saved immediately rather than waiting for OnClosing: an abnormal
            // exit (debugger stop, crash) skips OnClosing entirely, which
            // silently threw away the selection and started the next session
            // back on Encoder mode - looking exactly like "the dropdown
            // doesn't work".
            ConfigStore.Save(CollectConfig());

            OnCameraLogLine($"[Scan] switching trigger mode -> {_config.Scan.Trigger.Mode}…");
            await StopScanTriggerAsync();
            StartEncoderProbe();
        }

        /// <summary>Picks up an edited interval value without needing to
        /// toggle the mode dropdown off and back on - only restarts the
        /// probe if Interval mode is actually the one currently selected
        /// (editing the box while on Encoder mode just saves the number for
        /// next time Interval is picked).</summary>
        private async void TriggerIntervalBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TriggerIntervalBox.Text, out double ms) || ms <= 0) return;
            if (ms == _config.Scan.Trigger.IntervalMs) return;

            _config.Scan.Trigger.IntervalMs = ms;
            ConfigStore.Save(CollectConfig());   // see the note in TriggerModeCombo_SelectionChanged
            if (_config.Scan.Trigger.Mode != Scan.TriggerMode.Interval) return;

            OnCameraLogLine($"[Scan] interval changed -> {ms:F0} ms, restarting trigger…");
            await StopScanTriggerAsync();
            StartEncoderProbe();
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
                // Cropped to the inspection ROI before it ever reaches
                // PatchCore - the models were trained on manually cropped
                // stills, not full 2048x1536 frames (see PipelineConfig.Roi*).
                () => CropToRoi(_camera.CaptureFullFrame()),
                // Null-SAFE, not null-forgiving. The pipeline's consumer
                // thread can still be holding a queued frame when the run is
                // torn down (Stop Stream, or a trigger-mode switch), and
                // "_stripRecorderProbe!" threw a NullReferenceException right
                // there the moment that happened. A job arriving after the
                // run has closed has nowhere legitimate to be recorded, so
                // dropping it is the correct outcome - not crashing.
                job => _stripRecorderProbe?.InspectSegmentAsync(job) ?? Task.CompletedTask);

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
                    // Whoever is looking at a manual Run Detection / Capture
                    // result owns these panels until they restart the live
                    // view. Without this the scan repainted all four slots a
                    // second later, so an uploaded image's result flashed up
                    // and vanished - it looked like the manual run had been
                    // ignored. The scan still records every segment to
                    // MongoDB either way; only the on-screen panels are held.
                    if (!_liveViewActive) return;

                    Cam1TargetImage.Source = feed;     // camera 1 feed
                    Cam2TargetImage.Source = result;   // camera 1 result

                    // Slots 2 and 4 belong to camera 2 whenever a real second
                    // camera is connected - mirroring camera 1 into them then
                    // would overwrite camera 2's live feed on EVERY segment
                    // (once a second in Interval mode), so the second feed
                    // could never stay on screen. Only fall back to mirroring
                    // when there genuinely is no second camera.
                    if (!_dualCameraMode)
                    {
                        Cam1ResultImage.Source = feed;     // camera 2 feed (mirrored)
                        Cam2ResultImage.Source = result;   // camera 2 result (mirrored)
                    }

                    // Without this the GOOD/BAD banner only ever reflected a
                    // manual Run Detection / Capture - a running scan updated
                    // all four images every segment while the banner sat
                    // hidden, so there was no verdict on screen at all for
                    // the thing actually being inspected.
                    ShowVerdictBadge(images.Verdict, images.Score, images.Method,
                        $"segment {images.Segment}");
                });
            };

            string batchId = $"probe-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                await recorder.StartAsync(batchId);
            }
            catch (Exception ex)
            {
                // Loud on purpose. This is a hard stop - the pipeline is never
                // armed below, so NOTHING gets captured or inspected and the
                // whole UI just sits there looking idle. Previously this was
                // one line in a small scrolling log box, which reads exactly
                // like "inference is broken" rather than "the database is
                // down" (the usual cause being mongod not running, e.g. when
                // the disk is full).
                OnCameraLogLine($"[Scan] not armed: could not open a StripRun in MongoDB: {ex.Message}");
                StatusLabel.Text =
                    "SCAN NOT ARMED — MongoDB unreachable (mongodb://localhost:27017). " +
                    "No frames will be captured or inspected until it is running.";
                ShowScanBlockedPopup(ex);
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

            // Setting SelectedIndex fires TriggerModeCombo_SelectionChanged,
            // which is also what actually starts the scan probe - same "only
            // one place ever starts this" pattern CameraCombo already uses,
            // so set the interval textbox FIRST or the handler would read its
            // own not-yet-applied default back into _config.Scan instead of
            // config.json's value.
            TriggerIntervalBox.Text = c.Scan.Trigger.IntervalMs.ToString("F0");
            TriggerModeCombo.SelectedIndex = c.Scan.Trigger.Mode == Scan.TriggerMode.Interval ? 1 : 0;

            // Restore the live-camera ROI and keep config.json in step with
            // whatever the operator drags it to.
            LiveRoi.Roi = new NormalizedRoi(c.RoiNx, c.RoiNy, c.RoiNw, c.RoiNh);
            LiveRoi.RoiChanged += roi =>
            {
                _config.RoiNx = roi.X;
                _config.RoiNy = roi.Y;
                _config.RoiNw = roi.W;
                _config.RoiNh = roi.H;
                ConfigStore.Save(CollectConfig());
                StatusLabel.Text =
                    $"Inspection ROI set to {LiveRoi.GetPixelRoi().Width}×{LiveRoi.GetPixelRoi().Height} px " +
                    "of each live frame.";
            };
        }

        /// <summary>
        /// Writes the EXACT pixels that are about to be inspected to a PNG,
        /// so the identical frame can be re-run through the file path
        /// (Browse -> Run Detection) and the two verdicts compared.
        ///
        /// This exists to answer one specific question that is otherwise
        /// unanswerable: trained stills score GOOD while live captures score
        /// BAD, and the live path has things the file path does not (the ROI
        /// crop, the camera itself). Same pixels through both paths splits
        /// that cleanly - differing verdicts mean a code-path bug, matching
        /// verdicts mean the live IMAGE differs from the training data
        /// (exposure/lighting), which is not something software can fix.
        ///
        /// PNG because it must be lossless: a JPEG round trip would change
        /// the very pixels being compared.
        ///
        /// Saved on an explicit Capture click only - never from the scan,
        /// which would write a file every interval tick.
        /// </summary>
        private string? SaveLiveCapture(Mat frame, string tag)
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "LiveCaptures");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{tag}.png");
                Cv2.ImWrite(path, frame);
                return path;
            }
            catch (Exception ex)
            {
                OnCameraLogLine($"[Capture] could not save frame: {ex.Message}");
                return null;
            }
        }

        /// <summary>The live-camera crop, read fresh from config each time so
        /// dragging the box takes effect on the very next frame.
        ///
        /// Live/camera path ONLY - a file opened through Run Detection is
        /// never cropped, because it is already whatever composition the
        /// operator chose. Returns null straight through so callers can keep
        /// treating "no frame" as one case.</summary>
        private Mat? CropToRoi(Mat? frame)
        {
            if (frame == null || frame.Empty()) return frame;

            var roi = new NormalizedRoi(_config.RoiNx, _config.RoiNy, _config.RoiNw, _config.RoiNh);
            using (frame)
            {
                return RoiCrop.Apply(frame, roi, _config.RoiEnabled);
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

            // Carried forward from the live object, same reason as Scan below:
            // CollectConfig() builds a BRAND NEW PipelineConfig, so any field
            // not copied here silently reverts to its default on every save
            // (which happens on app close AND on every Run Detection click).
            // Without these five lines a dragged ROI would survive until the
            // next click and then quietly snap back to the 0.15/0.70 default.
            RoiEnabled = _config.RoiEnabled,
            RoiNx = _config.RoiNx,
            RoiNy = _config.RoiNy,
            RoiNw = _config.RoiNw,
            RoiNh = _config.RoiNh,

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
            _cameraB.Dispose();
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

            // A second physical camera, if present, auto-connects on its own
            // channel with no picker of its own (v1 - see the field comments
            // on _cameraB). devices[0] is always primary/_camera above.
            if (devices.Count >= 2)
                _ = ConnectCameraBAsync(devices[1]);
        }

        /// <summary>Auto-connects the second camera and switches the image
        /// grid into dual-camera mode: slots 1+2 become both cameras' live
        /// feeds, slots 3+4 become both cameras' latest inspection results
        /// (via CaptureBtn_Click's dual-capture path) instead of manual Run
        /// Detection's Original/BackgroundSubtracted/Heatmap/Overlay mapping.
        /// No manual Start/Stop/picker for this camera yet - it just runs
        /// once found, same as the very first single-camera auto-connect did.</summary>
        private async Task ConnectCameraBAsync(CameraDeviceInfo device)
        {
            string? error = null;
            await Task.Run(() =>
            {
                if (!_cameraB.EnsureStarted()) { error = "camera bridge failed to start"; return; }
                try { _cameraB.Connect(device); }
                catch (Exception ex) { error = ex.Message; }
            });

            if (error != null)
            {
                OnCameraLogLine($"[CamB] connect failed: {error}");
                return;
            }

            _dualCameraMode = true;
            SetDualCameraLabels(true);
            OnCameraLogLine($"[CamB] connected - dual camera view active ({device}).");
        }

        /// <summary>Swaps the four image-grid header labels between manual
        /// Run Detection's meaning (Original/Background Subtracted/Distance
        /// from Reference/Defect Mask) and dual-camera meaning (each
        /// camera's live feed / each camera's latest result), so the labels
        /// never lie about what's actually on screen.</summary>
        private void SetDualCameraLabels(bool dual)
        {
            Slot1Header.Text = dual ? "Camera 1 — Live" : "Original Image";
            Slot2Header.Text = dual ? "Camera 2 — Live" : "Background Subtracted";
            Slot3Header.Text = dual ? "Camera 1 — Result" : "Distance from Reference";
            Slot4Header.Text = dual ? "Camera 2 — Result" : "Defect Mask";
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

            // Re-arm the scan if Stop Stream tore it down, so the pair of
            // buttons is symmetric rather than one-way.
            if (_encoderProbe == null) StartEncoderProbe();

            StatusLabel.Text = "Live stream started.";
        }

        private async void StopStreamBtn_Click(object sender, RoutedEventArgs e)
        {
            // Testing with an uploaded file shouldn't fight the live feed for
            // slot 1 - this is the manual escape hatch for that, on top of the
            // automatic pause when a file is picked (TargetCombo_SelectionChanged).
            SetLiveViewActive(false);
            _camera.StopStreaming();

            // ...and stop the SCAN too. Stopping the preview alone used to
            // leave the interval timer running, so the pipeline kept asking
            // the bridge for frames and inspecting them once a second - the
            // camera looked stopped while captures and verdicts carried on
            // underneath, overwriting whatever the operator was trying to
            // look at. "Stop Stream" means stop using the camera, full stop.
            await StopScanTriggerAsync();

            UpdateCameraButtonStates();
            StatusLabel.Text = "Live stream and strip scan stopped.";
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
                // Slot 1 is about to show a file or a frozen result instead -
                // an ROI box over that would be meaningless, since only live
                // frames are ever cropped.
                LiveRoi.Visibility = Visibility.Collapsed;
            }
        }

        private void LiveViewTimer_Tick(object? sender, EventArgs e)
        {
            using Mat? frame = _camera.GetLatestPreviewFrame();
            if (frame != null && !frame.Empty())
            {
                Cam1TargetImage.Source = MatToBmp(frame);

                // The ROI box is positioned against the DISPLAYED picture, so
                // it needs the frame's real size to account for letterboxing.
                LiveRoi.SetSourceSize(frame.Width, frame.Height);
                LiveRoi.Visibility = Visibility.Visible;
            }

            // Slot 2 normally belongs to manual Run Detection's own
            // background-subtracted view - only steal it for camera 2's
            // live feed while dual-camera mode is actually active.
            if (_dualCameraMode)
            {
                using Mat? frameB = _cameraB.GetLatestPreviewFrame();
                if (frameB != null && !frameB.Empty())
                    Cam1ResultImage.Source = MatToBmp(frameB);
            }
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
            if (_dualCameraMode)
            {
                await RunDualCaptureAsync();
                return;
            }

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
            Mat? frame = await Task.Run(() => CropToRoi(_camera.CaptureFullFrame()));
            SetBusy(false);

            if (frame == null || frame.Empty())
            {
                frame?.Dispose();
                MessageBox.Show("No frame received yet - wait a moment and try again.",
                    "Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Exactly what inference is about to see, ROI crop included.
            string? savedPath = SaveLiveCapture(frame, "capture");
            if (savedPath != null)
                OnCameraLogLine($"[Capture] frame saved for comparison: {savedPath} " +
                                $"({frame.Width}x{frame.Height})");

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

            // Appended after the pipeline, which sets its own status text.
            if (savedPath != null)
                StatusLabel.Text += $"   ·   frame saved: {savedPath}";
        }

        // ══════════════════════════════════════════════════════
        //  DUAL-CAMERA CAPTURE (two physical cameras connected -
        //  see ConnectCameraBAsync/SetDualCameraLabels)
        // ══════════════════════════════════════════════════════

        /// <summary>Dual-camera version of Capture &amp; Inspect: captures and
        /// runs inspection on BOTH cameras and paints camera 1's live capture
        /// + result into slots 1+3, camera 2's into slots 2+4 (see
        /// SetDualCameraLabels for what those slots are called while this is
        /// active). Each camera's report is saved to MongoDB separately, so
        /// nothing is lost from the report history versus single-camera use.
        ///
        /// Runs the two cameras SEQUENTIALLY, not in parallel: both share the
        /// same cached PatchCoreDetector instance per backbone
        /// (GetOrLoadDetector), and calling Inspect() on it from two threads
        /// at once has never been audited for thread-safety - not worth the
        /// risk of a hard-to-reproduce race for what's still a manual,
        /// occasional button click.</summary>
        private async Task RunDualCaptureAsync()
        {
            bool useDiff = RadioReferenceDiff.IsChecked == true;
            string? bgPath = null;
            if (useDiff)
            {
                if (ReferenceCombo.SelectedIndex < 0)
                {
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
                MessageBox.Show($"No reference colour loaded (Assets\\{_config.ReferenceColorPath} missing or invalid).",
                    "Reference Colour Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var (silhouetteParams, diffParams, colorThreshold, colorMinAreaPct, colorMorph, referenceBgr) = SnapshotParams();
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

            SetLiveViewActive(false);
            SetBusy(true);
            StatusLabel.Text = "Capturing both cameras…";

            (InspectionResult? a, string? errA) = await CaptureAndInspectAsync(_camera, settings);
            (InspectionResult? b, string? errB) = await CaptureAndInspectAsync(_cameraB, settings);

            SetBusy(false);

            if (a == null && b == null)
            {
                StatusLabel.Text = "Both cameras failed.";
                MessageBox.Show($"Camera 1: {errA}\nCamera 2: {errB}", "Detection Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using (a) using (b)
            {
                string va = "Cam1: failed";
                string vb = "Cam2: failed";
                bool anyBad = false;

                if (a != null)
                {
                    Cam1TargetImage.Source = MatToBmp(a.Original);
                    Cam2TargetImage.Source = MatToBmp(a.Overlay);
                    va = $"Cam1: {a.Verdict} ({DescribeScore(a)})";
                    anyBad |= a.IsDefect;
                    _ = SaveReportAsync(BuildCaptureReport(a, useDiff, "Live Capture Cam1"));
                }
                else
                {
                    OnCameraLogLine($"[CamA] capture/inspect failed: {errA}");
                }

                if (b != null)
                {
                    Cam1ResultImage.Source = MatToBmp(b.Original);
                    Cam2ResultImage.Source = MatToBmp(b.Overlay);
                    vb = $"Cam2: {b.Verdict} ({DescribeScore(b)})";
                    anyBad |= b.IsDefect;
                    _ = SaveReportAsync(BuildCaptureReport(b, useDiff, "Live Capture Cam2"));
                }
                else
                {
                    OnCameraLogLine($"[CamB] capture/inspect failed: {errB}");
                }

                if (anyBad)
                {
                    DefectCountLabel.Text = $"⚠ BAD — {va} · {vb}";
                    DefectBadge.Visibility = Visibility.Visible;
                    OkBadge.Visibility = Visibility.Collapsed;
                }
                else
                {
                    OkBadgeLabel.Text = $"✓ GOOD — {va} · {vb}";
                    DefectBadge.Visibility = Visibility.Collapsed;
                    OkBadge.Visibility = Visibility.Visible;
                }

                StatusLabel.Text = $"Done — {va} · {vb}";
            }
        }

        private static string DescribeScore(InspectionResult r) =>
            r.Method.StartsWith("PatchCore") ? $"Score: {r.Score:F3}" : $"{r.Score:F2}% defect area";

        private DefectReport BuildCaptureReport(InspectionResult result, bool useDiff, string label)
        {
            Cv2.ImEncode(".png", result.Overlay, out byte[] png);
            return new DefectReport
            {
                Timestamp = DateTime.UtcNow,
                DefectCount = result.IsDefect ? 1 : 0,
                Method = result.Method,
                Mode = useDiff ? "ReferenceDiff" : "Silhouette",
                ReferenceImage = useDiff ? (ReferenceCombo.SelectedItem as string ?? string.Empty) : string.Empty,
                TargetImage = $"{label} {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                ImageData = png,
                ResultImageBase64 = "data:image/png;base64," + Convert.ToBase64String(png),
            };
        }

        /// <summary>Captures a fresh, lossless frame from one camera and runs
        /// it through InspectionEngine - the same engine manual single-camera
        /// Capture and Run Detection use, so a dual-camera result can never
        /// disagree with what a single-camera run of the same settings would
        /// produce. Returns (null, reason) instead of throwing so one
        /// camera's failure doesn't stop the other's capture in
        /// RunDualCaptureAsync.</summary>
        private async Task<(InspectionResult? result, string? error)> CaptureAndInspectAsync(
            BaumerCamera cam, InspectionSettings settings)
        {
            if (!cam.IsConnected || !cam.IsStreaming)
                return (null, "not connected/streaming");

            InspectionResult? result = null;
            string? error = null;
            await Task.Run(() =>
            {
                try
                {
                    result = InspectionEngine.Run(
                        isPatchCore =>
                        {
                            Mat? raw = CropToRoi(cam.CaptureFullFrame());
                            if (raw == null || raw.Empty())
                            {
                                raw?.Dispose();
                                throw new InvalidOperationException("No frame received from camera.");
                            }

                            // Same comparison aid as the single-camera path -
                            // see SaveLiveCapture.
                            SaveLiveCapture(raw, cam == _camera ? "cam1" : "cam2");
                            if (isPatchCore) return raw;
                            using Mat resized = raw;
                            return ImageLoader.ResizeToDisplay(resized);
                        },
                        settings, GetOrLoadDetector);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            });
            return (result, error);
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

                ShowVerdictBadge(result.Verdict, result.Score, result.Method);
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

        private DispatcherTimer? _mongoWatchTimer;

        /// <summary>
        /// Polls MongoDB and shows the result as a coloured dot in the status
        /// bar, because a stopped mongod is indistinguishable from a broken
        /// camera or model from the UI alone: the scan silently never arms,
        /// so nothing is captured or inspected and every panel stays empty.
        ///
        /// A plain TCP connect rather than a driver ping - it answers the
        /// only question being asked here ("is the server up?"), costs
        /// nothing, and can't be slowed down by the driver's own server
        /// selection timeout while the UI waits on it.
        /// </summary>
        private void StartMongoWatch()
        {
            _mongoWatchTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _mongoWatchTimer.Tick += async (_, _) => await RefreshMongoStatusAsync();
            _mongoWatchTimer.Start();
            _ = RefreshMongoStatusAsync();   // don't make the operator wait 5s for the first answer
        }

        private async Task RefreshMongoStatusAsync()
        {
            bool up = await Task.Run(() =>
            {
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    return tcp.ConnectAsync("localhost", 27017).Wait(TimeSpan.FromMilliseconds(800))
                           && tcp.Connected;
                }
                catch { return false; }
            });

            MongoDot.Fill = new SolidColorBrush(up
                ? Color.FromRgb(0x22, 0xC5, 0x5E)    // green
                : Color.FromRgb(0xEF, 0x44, 0x44));  // red
            MongoStatusText.Text = up ? "MongoDB: running" : "MongoDB: NOT RUNNING";
            MongoStatusText.Foreground = new SolidColorBrush(up
                ? Color.FromRgb(0x90, 0x90, 0xB8)
                : Color.FromRgb(0xEF, 0x44, 0x44));
            MongoStatusText.ToolTip = up
                ? "mongodb://localhost:27017 is reachable."
                : "mongodb://localhost:27017 is NOT reachable. Strip scanning cannot arm, " +
                  "so no frames will be captured or inspected. Start it from an admin " +
                  "PowerShell with:  net start MongoDB";
        }

        /// <summary>Set once a session so re-arming (which happens after
        /// every finished strip) can't turn one dead database into a popup
        /// every few seconds.</summary>
        private bool _scanBlockedPopupShown;

        /// <summary>
        /// A blocking popup for the ONE failure that silently stops
        /// everything: no StripRun means the pipeline never arms, so no frame
        /// is captured, nothing is inspected, no banner appears and all four
        /// image slots stay empty - which looks identical to "the camera or
        /// the model is broken".
        ///
        /// This cost two hours of debugging once, because the only signal was
        /// a single line in a small scrolling log box. It states the actual
        /// cause and the actual fix, and checks the two things that are
        /// nearly always behind it (service stopped, disk full) rather than
        /// just echoing a MongoDB timeout nobody can act on.
        /// </summary>
        private void ShowScanBlockedPopup(Exception ex)
        {
            if (_scanBlockedPopupShown) return;
            _scanBlockedPopupShown = true;

            // Deliberately a TCP probe rather than ServiceController: that
            // type lives in a separate NuGet package, and "is the port
            // answering" is the question that actually matters here anyway -
            // a running service with a wedged server would still be useless.
            bool portOpen;
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                portOpen = tcp.ConnectAsync("localhost", 27017).Wait(TimeSpan.FromMilliseconds(800))
                           && tcp.Connected;
            }
            catch { portOpen = false; }
            string serviceState = portOpen
                ? "port 27017 is open (but the run still could not be opened)"
                : "NOT reachable on port 27017 - the server is not running";

            string diskNote = "";
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\");
                double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                diskNote = $"\n  • Free space on {drive.Name}  :  {freeGb:F1} GB" +
                           (freeGb < 2 ? "   ← too low, MongoDB will refuse to run" : "");
            }
            catch { /* diagnostics must never be the thing that throws */ }

            MessageBox.Show(
                "Strip scanning is NOT running.\n\n" +
                "The app could not open a run record in MongoDB, so the scan was never armed. " +
                "Until this is fixed:\n" +
                "  • no frames are captured\n" +
                "  • nothing is inspected\n" +
                "  • the GOOD/BAD banner and the four image panels stay empty\n\n" +
                "This is NOT a camera or model problem.\n\n" +
                "Checked just now:\n" +
                $"  • MongoDB               :  {serviceState}" +
                diskNote +
                "\n  • Connection            :  mongodb://localhost:27017\n\n" +
                "To fix: free up disk space if it is low, then start MongoDB from an " +
                "ADMIN PowerShell:\n\n" +
                "    net start MongoDB\n\n" +
                "Then restart this app.\n\n" +
                $"Underlying error: {ex.Message}",
                "Scan not armed — MongoDB unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        /// <summary>The one place the GOOD/BAD banner is rendered, so a
        /// manual run, a Capture and a scanned segment can't end up showing
        /// it in three slightly different ways (or, as happened with the
        /// scan, not showing it at all). An unknown verdict hides both
        /// badges rather than guessing.</summary>
        private void ShowVerdictBadge(string verdict, double score, string method, string? context = null)
        {
            string subLabel = method.StartsWith("PatchCore")
                ? $"Score: {score:F3}"
                : $"{score:F2}% defect area";
            if (context != null) subLabel = $"{context} · {subLabel}";

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