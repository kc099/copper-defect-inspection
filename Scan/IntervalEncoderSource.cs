using System;
using System.Threading;

namespace copperInspection.Scan
{
    /// <summary>
    /// A trigger source with no distance measurement at all: fires a capture
    /// on a fixed wall-clock period (<see cref="ScanConfig.Trigger"/>'s
    /// IntervalMs when Mode is <see cref="TriggerMode.Interval"/>) instead of
    /// reacting to real or simulated strip position.
    ///
    /// Implements <see cref="IEncoderSource"/> rather than bypassing
    /// <see cref="ScanController"/>/<see cref="ScanPipeline"/>: each tick is
    /// handed to the controller as a synthetic <see cref="EncoderSample"/>
    /// whose position has advanced by exactly one <see cref="ScanGeometry.PitchMm"/>.
    /// Since the controller only ever reacts to the samples it's given and
    /// cannot tell a real reading from a synthetic one (by design - see
    /// IEncoderSource's own doc comment), every tick becomes exactly one
    /// segment, with no changes needed anywhere downstream
    /// (ScanPipeline/StripRunRecorder/defect-position mapping all keep
    /// working unmodified). The "position" recorded per segment is therefore
    /// synthetic (segment index x pitch), not a measurement of anything
    /// physical - meaningful only in that mode, not comparable to an
    /// encoder-driven run's positions.
    ///
    /// The first tick only establishes Armed's baseline zero-count (the same
    /// "wait for a press" rule a real encoder needs - see
    /// ScanController.OnSample) - so there is one interval's worth of startup
    /// delay before the first real capture fires. Every tick after that
    /// fires one.
    /// </summary>
    public sealed class IntervalEncoderSource : IEncoderSource
    {
        private readonly double _pitchMm;
        private readonly TimeSpan _period;
        private Timer? _timer;
        private long _seq;
        private double _mm;
        private int _zeroCount;

        public event Action<EncoderSample>? SampleReceived;
        public event Action<string>? Faulted; // never raised - there is nothing to go stale.

        public bool IsConnected { get; private set; }
        public EncoderSample? Latest { get; private set; }

        public IntervalEncoderSource(ScanConfig cfg)
        {
            _pitchMm = ScanGeometry.PitchMm(cfg);
            double ms = Math.Max(50, cfg.Trigger.IntervalMs);
            _period = TimeSpan.FromMilliseconds(ms);
        }

        public void Start()
        {
            if (_timer != null) return;
            IsConnected = true;
            _timer = new Timer(_ => Tick(), null, _period, _period);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            IsConnected = false;
        }

        public void Dispose() => Stop();

        private void Tick()
        {
            _seq++;

            // Tick 1 carries ZeroCount 0, which ScanController (while Armed)
            // only uses to establish its baseline - no position change needed
            // yet. Tick 2 changes ZeroCount to 1, which IS a change from that
            // baseline, so THIS is the one moment ScanController treats as
            // "the button was pressed" - exactly like a real press, position
            // is still 0 at that instant (mirrors a real encoder zeroing
            // ZeroMm to the current Mm right as the button goes down), which
            // is what makes segment 1's 0mm threshold fire on this same
            // tick instead of being silently counted as skipped (confirmed
            // via a standalone test: advancing position on this same tick
            // made the first real capture jump straight to segment 2).
            // Tick 3 onward just keeps advancing one pitch per tick with
            // ZeroCount staying constant at 1 (a real change here would read
            // as "pressed again mid-run", ending the strip).
            if (_seq >= 2) _zeroCount = 1;
            if (_seq >= 3) _mm += _pitchMm;

            var sample = new EncoderSample(
                Seq: _seq,
                Mm: _mm,
                ZeroMm: 0,
                ZeroCount: _zeroCount,
                Button: false,
                AgeSeconds: 0);

            Latest = sample;
            SampleReceived?.Invoke(sample);
        }
    }
}
