using System;
using System.Collections.Generic;

namespace copperInspection.Scan
{
    public enum ScanState
    {
        /// <summary>Not scanning. Encoder readings are ignored.</summary>
        Idle,

        /// <summary>Armed and waiting for the operator's button press.</summary>
        Armed,

        /// <summary>Running: firing a capture every Pitch millimetres.</summary>
        Scanning,

        /// <summary>Strip over; waiting for the queue to drain.</summary>
        Finalising,
    }

    /// <summary>Why a run ended.</summary>
    public enum ScanEndReason { None, Operator, LengthReached, IdleTimeout, Fault, RestartedByOperator }

    /// <summary>
    /// What the controller decided about one encoder reading.
    /// </summary>
    public readonly record struct TriggerDecision(
        bool Fire,
        int Segment,
        double NominalMm,
        double ActualMm,
        IReadOnlyList<int> SkippedSegments)
    {
        public static readonly TriggerDecision None =
            new(false, 0, 0, 0, Array.Empty<int>());

        /// <summary>
        /// How far past the ideal trigger point the capture actually happened.
        /// Worth recording per segment: if it starts drifting, something is
        /// wrong with the encoder, the line, or the delay estimate.
        /// </summary>
        public double TriggerErrorMm => ActualMm - NominalMm;
    }

    /// <summary>
    /// The scan state machine and trigger logic. Pure: no hardware, no camera,
    /// no clock of its own - it only reacts to encoder samples handed to it, so
    /// the awkward cases (line stops, line reverses, app hitches past several
    /// thresholds) can all be exercised in tests.
    /// </summary>
    public sealed class ScanController
    {
        private readonly ScanConfig _cfg;

        // Rolling window of recent readings, for the speed estimate.
        private readonly (double Mm, double Sec)[] _window = new (double, double)[8];
        private int _windowCount;
        private int _windowNext;
        private double _clockSec;   // advances with the samples, not with wall time

        private int _armedAtZeroCount;
        private bool _haveArmBaseline;

        public ScanController(ScanConfig cfg) => _cfg = cfg;

        public ScanState State { get; private set; } = ScanState.Idle;

        /// <summary>The next segment index that may fire. 1-based.</summary>
        public int NextSegment { get; private set; } = 1;

        /// <summary>Distance since the button press, mm.</summary>
        public double PositionMm { get; private set; }

        public double SpeedMmPerSec { get; private set; }

        /// <summary>Segments whose trigger point went by without a capture.
        /// Non-empty means coverage is incomplete.</summary>
        public List<int> SkippedSegments { get; } = new();

        public ScanEndReason EndReason { get; private set; } = ScanEndReason.None;

        /// <summary>Highest segment index actually captured.</summary>
        public int LastFiredSegment { get; private set; }

        /// <summary>
        /// Set only when EndReason is RestartedByOperator: the zeroCount of
        /// the press that ended this run. A caller re-arming after such an
        /// end can pass this straight back into <see cref="Arm"/> so the SAME
        /// press that ended one strip also starts the next one, rather than
        /// requiring a separate extra press - see Arm's doc comment.
        /// </summary>
        public int? RestartZeroCount { get; private set; }

        public double PitchMm => ScanGeometry.PitchMm(_cfg);

        /// <summary>
        /// Arm the run.
        ///
        /// With no argument: the next button press starts it. The press that
        /// armed it does not count - the current press count becomes the
        /// baseline and this waits for it to change.
        ///
        /// With <paramref name="knownZeroCount"/>: skips that wait:
        /// <paramref name="knownZeroCount"/> is treated as ALREADY pressed, so
        /// the very next sample (which will carry that same count, since
        /// nothing new has happened yet) is recognized as the start
        /// immediately. This is what makes "every press starts a new strip"
        /// true even though closing out the PREVIOUS strip's database record
        /// happens asynchronously afterward: the press that ended run 1 is
        /// reused as the press that begins run 2, instead of demanding a
        /// second, separate press nobody asked the operator to make. Pass
        /// <see cref="RestartZeroCount"/> here after a RestartedByOperator
        /// end; omit it after any other end reason, where a genuinely new
        /// press is correct.
        /// </summary>
        public void Arm(int? knownZeroCount = null)
        {
            State = ScanState.Armed;
            NextSegment = 1;
            PositionMm = 0;
            SpeedMmPerSec = 0;
            LastFiredSegment = 0;
            EndReason = ScanEndReason.None;
            RestartZeroCount = null;
            SkippedSegments.Clear();
            _windowCount = 0;
            _windowNext = 0;
            _clockSec = 0;

            if (knownZeroCount.HasValue)
            {
                _armedAtZeroCount = knownZeroCount.Value - 1;
                _haveArmBaseline = true;
            }
            else
            {
                _haveArmBaseline = false;
            }
        }

        /// <summary>Operator pressed End Strip, or the length was reached.</summary>
        public void End(ScanEndReason reason = ScanEndReason.Operator)
        {
            if (State == ScanState.Scanning || State == ScanState.Armed)
            {
                State = ScanState.Finalising;
                EndReason = reason;
            }
        }

        /// <summary>Back to Idle once the queue has drained.</summary>
        public void Reset() => State = ScanState.Idle;

        /// <summary>
        /// Feed one encoder reading in. Returns whether to capture, and which
        /// segment it is.
        /// </summary>
        public TriggerDecision OnSample(in EncoderSample sample)
        {
            if (State == ScanState.Idle || State == ScanState.Finalising)
                return TriggerDecision.None;

            UpdateSpeed(sample);

            // ── Waiting for the button ──
            if (State == ScanState.Armed)
            {
                if (!_haveArmBaseline)
                {
                    _armedAtZeroCount = sample.ZeroCount;
                    _haveArmBaseline = true;
                    return TriggerDecision.None;
                }

                if (sample.ZeroCount == _armedAtZeroCount)
                    return TriggerDecision.None;   // not pressed yet

                // Pressed: the strip has just filled the field of view.
                State = ScanState.Scanning;
                _armedAtZeroCount = sample.ZeroCount;
                // Fall through - segment 1's threshold is 0 mm, so it fires now.
            }
            else if (sample.ZeroCount != _armedAtZeroCount)
            {
                // Pressed again mid-run. Treat it as "this strip is over"
                // rather than silently re-basing, which would splice two
                // strips into one run and make every position after it wrong.
                // The press itself is not lost, though - a caller that closes
                // out this run and re-arms with RestartZeroCount gets THIS
                // same press recognized as the start of the next strip, so
                // every press starts exactly one strip, never two presses for
                // one.
                RestartZeroCount = sample.ZeroCount;
                End(ScanEndReason.RestartedByOperator);
                return TriggerDecision.None;
            }

            PositionMm = sample.PositionMm;

            // Where the strip will be when the shutter actually opens: the
            // reading's own age in flight, plus the capture delay if
            // compensation is switched on.
            double predicted = PositionMm
                             + SpeedMmPerSec * sample.AgeSeconds
                             + ScanGeometry.LeadMm(_cfg, SpeedMmPerSec);

            if (predicted < ScanGeometry.ThresholdMm(_cfg, NextSegment))
                return TriggerDecision.None;

            // ── Fire ──
            // Which segment does the strip's CURRENT position correspond to?
            // Normally NextSegment. If the app hitched, it can be further on.
            int here = Math.Max(NextSegment, ScanGeometry.SegmentIndexAt(_cfg, predicted));

            IReadOnlyList<int> skipped = Array.Empty<int>();
            if (here > NextSegment)
            {
                // The no-burst rule. Those thresholds are positions in SPACE,
                // and that material is already past the camera. Capturing late
                // for segment 5 while the strip is at segment 8 would store
                // segment 8's material labelled as segment 5 - worse than an
                // honest logged gap.
                var gaps = new List<int>();
                for (int s = NextSegment; s < here; s++) gaps.Add(s);
                SkippedSegments.AddRange(gaps);
                skipped = gaps;
            }

            var decision = new TriggerDecision(
                Fire: true,
                Segment: here,
                NominalMm: ScanGeometry.ThresholdMm(_cfg, here),
                ActualMm: PositionMm,
                SkippedSegments: skipped);

            LastFiredSegment = here;

            // The latch: this index can never fire again, so a jittering or
            // reversing encoder cannot re-trigger a segment.
            NextSegment = here + 1;

            // Known-length strips finish on their own.
            if (_cfg.Run.StripLengthMm > 0 &&
                here >= ScanGeometry.SegmentCountFor(_cfg, _cfg.Run.StripLengthMm))
            {
                End(ScanEndReason.LengthReached);
            }

            return decision;
        }

        /// <summary>
        /// Speed from a short window of recent readings. Used both to
        /// interpolate a reading's age and to lead the trigger, so it needs to
        /// be steady rather than exact - a couple of hundred milliseconds of
        /// history is plenty at line speeds.
        /// </summary>
        private void UpdateSpeed(in EncoderSample sample)
        {
            // Samples don't carry a wall clock, so time is counted in poll
            // intervals. The poll rate is fixed, so an arrival index is a good
            // enough time base - and it keeps the controller deterministic,
            // which is what makes it testable.
            double pollSec = _cfg.Encoder.PollHz > 0 ? 1.0 / _cfg.Encoder.PollHz : 0.02;
            _clockSec += pollSec;

            _window[_windowNext] = (sample.PositionMm, _clockSec);
            _windowNext = (_windowNext + 1) % _window.Length;
            if (_windowCount < _window.Length) _windowCount++;

            if (_windowCount < 2) { SpeedMmPerSec = 0; return; }

            int newest = (_windowNext - 1 + _window.Length) % _window.Length;
            int oldest = _windowCount < _window.Length
                ? 0
                : _windowNext;   // the slot about to be overwritten is the oldest

            double dt = _window[newest].Sec - _window[oldest].Sec;
            if (dt <= 0) { SpeedMmPerSec = 0; return; }

            double v = (_window[newest].Mm - _window[oldest].Mm) / dt;

            // A reversing line should not produce a negative lead that fires
            // triggers early; clamp at rest.
            SpeedMmPerSec = v > 0 ? v : 0;
        }
    }
}
