using System;
using System.Collections.Generic;

namespace copperInspection.Scan
{
    /// <summary>
    /// Everything about a continuous strip scan that can change on the line:
    /// where the encoder PCB lives, how big the camera's field of view is, how
    /// much the frames overlap, and how the inspection runs.
    ///
    /// Hangs off <see cref="PipelineConfig"/> so it lands in the same
    /// hand-editable config.json as the rest of the pipeline settings, and is
    /// re-read at the start of every run (never mid-run - see
    /// docs/STRIP_SCAN_BUILD_PLAN.md section 6 for why that matters).
    /// </summary>
    public sealed class ScanConfig
    {
        public EncoderSettings Encoder { get; set; } = new();
        public CaptureDelaySettings CaptureDelay { get; set; } = new();
        public GeometrySettings Geometry { get; set; } = new();
        public ScanInspectionSettings Inspection { get; set; } = new();
        public RunSettings Run { get; set; } = new();
        public SimulateSettings Simulate { get; set; } = new();

        /// <summary>
        /// Problems that must be fixed before a run can start, as sentences an
        /// operator can act on. Empty list == good to go.
        ///
        /// Called by the pre-flight check when ARM is pressed. Failing loudly
        /// here is much better than starting a run whose coverage is quietly
        /// untrustworthy - a gap in coverage looks exactly like clean strip in
        /// the report.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();

            // ── Geometry ──
            if (Geometry.FovAlongTravelMm <= 0)
                problems.Add(
                    "Field of view is not set. Measure the camera's view along the direction " +
                    "of travel with a tape and put it in Scan.Geometry.FovAlongTravelMm.");

            if (Geometry.OverlapMm < 0)
                problems.Add("Scan.Geometry.OverlapMm cannot be negative.");

            if (Geometry.FovAlongTravelMm > 0 && Geometry.OverlapMm >= Geometry.FovAlongTravelMm)
                problems.Add(
                    $"Overlap ({Geometry.OverlapMm:F1} mm) must be smaller than the field of view " +
                    $"({Geometry.FovAlongTravelMm:F1} mm), otherwise the strip never advances.");

            if (Geometry.PxPerMmAlong <= 0 || Geometry.PxPerMmAcross <= 0)
                problems.Add(
                    "Pixel scale is not set (Scan.Geometry.PxPerMmAlong / PxPerMmAcross). " +
                    "Without it, defect positions cannot be recorded in millimetres.");

            // ── Overlap big enough to absorb the position error? ──
            if (Geometry.FovAlongTravelMm > 0 && Geometry.OverlapMm < Geometry.FovAlongTravelMm)
            {
                double needed = ScanGeometry.MinimumSafeOverlapMm(this);
                if (Geometry.OverlapMm < needed)
                {
                    problems.Add(
                        $"Overlap of {Geometry.OverlapMm:F1} mm is too small: at " +
                        $"{Run.MaxLineSpeedMmPerSec:F0} mm/s with {Encoder.PollHz} Hz polling and " +
                        $"capture-delay compensation {(CaptureDelay.Enabled ? "on" : "OFF")}, " +
                        $"at least {needed:F1} mm is needed or frames can leave un-imaged gaps." +
                        (CaptureDelay.Enabled
                            ? ""
                            : " Turning Scan.CaptureDelay.Enabled back on would reduce this."));
                }
            }

            // ── Encoder ──
            if (!Simulate.Enabled)
            {
                if (!Encoder.TryBuildUri(out _, out string? urlError))
                    problems.Add(urlError!);
            }

            if (Encoder.PollHz < 1 || Encoder.PollHz > 500)
                problems.Add("Scan.Encoder.PollHz must be between 1 and 500.");

            if (Encoder.TimeoutMs < 10)
                problems.Add("Scan.Encoder.TimeoutMs is too small; use at least 10 ms.");

            if (Encoder.MaxStaleMs < Encoder.TimeoutMs)
                problems.Add(
                    "Scan.Encoder.MaxStaleMs must be at least as large as TimeoutMs, " +
                    "or a single slow response would be treated as a dead encoder.");

            // ── Run ──
            if (Run.QueueCapacity < 1)
                problems.Add("Scan.Run.QueueCapacity must be at least 1.");

            if (Run.MaxLineSpeedMmPerSec <= 0)
                problems.Add("Scan.Run.MaxLineSpeedMmPerSec must be greater than zero.");

            if (Run.StripLengthMm < 0)
                problems.Add("Scan.Run.StripLengthMm cannot be negative (use 0 for 'unknown').");

            // ── Inspection ──
            if (Inspection.TwoStage.Enabled &&
                string.Equals(Inspection.TwoStage.ScreenMethod, Inspection.Method, StringComparison.OrdinalIgnoreCase))
                problems.Add(
                    "Two-stage inspection is on but the cheap screen and the confirming model are " +
                    "the same method, so every frame would be inspected twice for no benefit.");

            return problems;
        }
    }

    /// <summary>
    /// The encoder PCB's HTTP endpoint. The PCB reports true millimetres
    /// already scaled on its side, so there is no counts-to-mm conversion
    /// anywhere in this app.
    /// </summary>
    public sealed class EncoderSettings
    {
        public string BaseUrl { get; set; } = "http://192.168.1.50";
        public string Path { get; set; } = "/encoder";

        /// <summary>How often to ask the PCB where the strip is.</summary>
        public int PollHz { get; set; } = 50;

        /// <summary>A slow response must never stall the poll loop.</summary>
        public int TimeoutMs { get; set; } = 150;

        /// <summary>
        /// If no fresh reading (advancing "seq") arrives within this long, the
        /// encoder is treated as faulted and the run is aborted. Never fall
        /// back to a timer: that would claim coverage nobody measured.
        /// </summary>
        public int MaxStaleMs { get; set; } = 300;

        /// <summary>
        /// Combine BaseUrl and Path into the full endpoint URL, or fail with a
        /// human-readable reason. Shared by <see cref="ScanConfig.Validate"/>
        /// and <c>HttpEncoderSource</c> so pre-flight validation and the poller
        /// can never disagree about what counts as a valid address - without
        /// this, a bad Path (BaseUrl fine on its own, but the two together
        /// don't form a valid URL) would pass pre-flight and only show up as a
        /// raw exception when the poller tries to construct its request.
        /// </summary>
        public bool TryBuildUri(out Uri? uri, out string? error)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                uri = null;
                error = "Scan.Encoder.BaseUrl is empty - set the encoder PCB's address.";
                return false;
            }

            string baseUrl = BaseUrl.TrimEnd('/');
            string path = string.IsNullOrEmpty(Path) ? "/encoder"
                : Path.StartsWith('/') ? Path : "/" + Path;

            if (!Uri.TryCreate(baseUrl + path, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = $"Scan.Encoder.BaseUrl/Path do not form a valid http:// address: \"{baseUrl}{path}\".";
                uri = null;
                return false;
            }

            error = null;
            return true;
        }
    }

    /// <summary>
    /// Compensation for the time between "the encoder says we're there" and
    /// "the shutter actually opens". Positive Ms fires the trigger early so
    /// the shutter opens at the right PLACE rather than the right MOMENT;
    /// negative deliberately waits.
    /// </summary>
    public sealed class CaptureDelaySettings
    {
        public bool Enabled { get; set; } = true;
        public double Ms { get; set; } = 80.0;
    }

    public sealed class GeometrySettings
    {
        /// <summary>
        /// How much strip the camera sees in one frame, along the direction of
        /// travel. Measured with a tape. 0 means "not measured yet" and the
        /// pre-flight check refuses to start rather than guessing.
        /// </summary>
        public double FovAlongTravelMm { get; set; } = 0.0;

        /// <summary>
        /// Deliberate re-imaging at each seam. An ABSOLUTE number of mm, not a
        /// percentage of the FOV - what it absorbs (position error and defect
        /// size) doesn't shrink when the FOV does.
        /// </summary>
        public double OverlapMm { get; set; } = 25.0;

        /// <summary>
        /// Distance between the button press and the first capture. 0 for the
        /// convention in use here: the operator presses once the strip has
        /// FULLY entered the field of view, so frame 1 fires on the press.
        /// </summary>
        public double FirstTriggerOffsetMm { get; set; } = 0.0;

        /// <summary>"X" or "Y" - which image axis the strip travels along.</summary>
        public string TravelAxis { get; set; } = "X";

        /// <summary>True if increasing distance means decreasing pixel coordinate.</summary>
        public bool TravelReversed { get; set; } = false;

        public double PxPerMmAlong { get; set; } = 0.0;
        public double PxPerMmAcross { get; set; } = 0.0;

        /// <summary>Longest defect we expect to see, in mm. Feeds the minimum
        /// safe overlap so a defect can never be cut by a frame boundary.</summary>
        public double LargestDefectMm { get; set; } = 10.0;

        /// <summary>Extra mm added on top of the computed minimum overlap.</summary>
        public double SafetyMarginMm { get; set; } = 5.0;

        /// <summary>
        /// How much two sightings must overlap, as a fraction of the smaller
        /// one, before they are treated as the same defect seen twice across a
        /// frame seam. Higher merges less (risks double-counting), lower merges
        /// more (risks collapsing two nearby defects into one).
        /// </summary>
        public double DefectMergeOverlapFraction { get; set; } = 0.5;
    }

    /// <summary>How the scan inspects each segment. Named apart from the
    /// engine's own InspectionSettings, which is a per-frame run snapshot -
    /// these two are easy to confuse and mean different things.</summary>
    public sealed class ScanInspectionSettings
    {
        /// <summary>The model that decides the verdict. PatchCore-R18 in production.</summary>
        public string Method { get; set; } = "PatchCore-R18";

        public TwoStageSettings TwoStage { get; set; } = new();
    }

    /// <summary>
    /// Optional cheap-screen-first mode: run a fast method on every frame and
    /// only escalate suspicious frames to the model. Worth turning on only if
    /// the model can't hold the per-frame budget. The screen must be tuned to
    /// over-trigger - anything it clears never reaches the model.
    /// </summary>
    public sealed class TwoStageSettings
    {
        public bool Enabled { get; set; } = false;
        public string ScreenMethod { get; set; } = "ColorDiff";
    }

    public sealed class RunSettings
    {
        /// <summary>
        /// How many captured frames may wait for inspection. Small on purpose:
        /// a full-resolution frame is tens of MB.
        /// </summary>
        public int QueueCapacity { get; set; } = 6;

        /// <summary>No encoder movement for this long ends the run.</summary>
        public double IdleTimeoutSec { get; set; } = 20.0;

        /// <summary>Known strip length in mm, or 0 if strips vary.</summary>
        public double StripLengthMm { get; set; } = 0;

        /// <summary>Used to size the minimum safe overlap.</summary>
        public double MaxLineSpeedMmPerSec { get; set; } = 170.0;

        /// <summary>"all" or "defects-only" - which segments keep their image.</summary>
        public string StoreImagesFor { get; set; } = "defects-only";

        /// <summary>
        /// When true, a small always-on-top window opens showing every
        /// segment's background-subtraction mask live as it's captured -
        /// for watching segmentation quality on real strip data without
        /// digging through MongoDB after the fact. Off by default; costs
        /// nothing when off.
        /// </summary>
        public bool ShowBackgroundMaskWindow { get; set; } = false;
    }

    /// <summary>
    /// Drives the scan from a software ramp instead of the PCB, so the whole
    /// pipeline can be built and tested with no encoder and no line.
    /// </summary>
    public sealed class SimulateSettings
    {
        public bool Enabled { get; set; } = false;
        public double SpeedMmPerSec { get; set; } = 166.7;
    }
}
