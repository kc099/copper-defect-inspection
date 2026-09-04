using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenCvSharp;

namespace copperInspection.Scan
{
    /// <summary>How one segment of the strip ended up. Written whatever the
    /// outcome, so a run can always account for every trigger it fired.</summary>
    public sealed record SegmentRecord(
        int Segment,
        double StartMm,
        double EndMm,
        double EncoderMm,
        double TriggerErrorMm,
        double SpeedMmPerSec,
        SegmentOutcome Outcome,
        string? Error,
        DateTime CapturedUtc);

    /// <summary>
    /// The producer/consumer machinery for a strip run.
    ///
    /// Three threads, deliberately:
    ///   encoder  -> decides WHEN to capture (fast maths only)
    ///   capture  -> grabs the frame and enqueues it, never waits on inspection
    ///   consumer -> inspects one frame at a time
    ///
    /// The queue between capture and inspection is what makes a slow
    /// inspection show up as a LOGGED dropped segment instead of a silently
    /// missed trigger. Without it, one slow model run would swallow a trigger
    /// and a whole pitch of strip would go uninspected with nothing to say so.
    ///
    /// Neither the camera nor the inspection engine is referenced directly -
    /// both arrive as delegates - so the whole pipeline can be driven by the
    /// simulator and a fake frame source with no hardware present.
    /// </summary>
    public sealed class ScanPipeline : IDisposable
    {
        private readonly ScanConfig _cfg;
        private readonly IEncoderSource _encoder;
        private readonly Func<Mat?> _captureFrame;
        private readonly Func<SegmentJob, Task> _inspect;

        private readonly ScanController _controller;

        private Channel<CaptureRequest>? _requests;
        private Channel<SegmentJob>? _jobs;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private Task? _consumerTask;

        private readonly object _lock = new();
        private readonly List<SegmentRecord> _records = new();

        private double _lastMovementMm;
        private DateTime _lastMovementUtc;
        private bool _running;

        public ScanPipeline(
            ScanConfig cfg,
            IEncoderSource encoder,
            Func<Mat?> captureFrame,
            Func<SegmentJob, Task> inspect)
        {
            _cfg = cfg;
            _encoder = encoder;
            _captureFrame = captureFrame;
            _inspect = inspect;
            _controller = new ScanController(cfg);
        }

        private readonly record struct CaptureRequest(
            int Segment, double StartMm, double EndMm,
            double EncoderMm, double TriggerErrorMm, double SpeedMmPerSec);

        // ── observation ───────────────────────────────────────────────────
        public event Action<SegmentRecord>? SegmentFinished;
        public event Action<ScanEndReason>? RunFinished;
        public event Action<string>? Fault;

        public ScanState State => _controller.State;
        public int NextSegment => _controller.NextSegment;
        public double PositionMm => _controller.PositionMm;
        public double SpeedMmPerSec => _controller.SpeedMmPerSec;

        public IReadOnlyList<SegmentRecord> Records
        {
            get { lock (_lock) return _records.ToArray(); }
        }

        public int CapturedCount => CountOf(SegmentOutcome.Inspected);
        public int DroppedCount => CountOf(SegmentOutcome.Dropped);
        public int FailedCount => CountOf(SegmentOutcome.InspectionFailed)
                                + CountOf(SegmentOutcome.CaptureFailed);
        public int SkippedCount => CountOf(SegmentOutcome.Skipped);

        /// <summary>
        /// True only if every trigger produced an inspected frame. This is the
        /// field an operator has to be able to trust: false means some part of
        /// this strip was never actually looked at.
        /// </summary>
        public bool CoverageComplete
        {
            get
            {
                lock (_lock)
                {
                    foreach (SegmentRecord r in _records)
                        if (r.Outcome != SegmentOutcome.Inspected) return false;
                    return true;
                }
            }
        }

        private int CountOf(SegmentOutcome outcome)
        {
            lock (_lock)
            {
                int n = 0;
                foreach (SegmentRecord r in _records) if (r.Outcome == outcome) n++;
                return n;
            }
        }

        // ── lifecycle ─────────────────────────────────────────────────────

        /// <summary>
        /// Arm the run: start the threads and wait for the operator's button
        /// press - unless the PREVIOUS run ended because the operator pressed
        /// the button again mid-run, in which case that same press already
        /// counts as this run's start (see ScanController.Arm's doc comment)
        /// so scanning begins immediately rather than waiting for a second,
        /// separate press nobody was asked to make.
        /// </summary>
        public void Arm()
        {
            if (_running) throw new InvalidOperationException("A run is already in progress.");

            lock (_lock) _records.Clear();

            int? knownZeroCount = _controller.EndReason == ScanEndReason.RestartedByOperator
                ? _controller.RestartZeroCount
                : null;
            _controller.Arm(knownZeroCount);

            _running = true;
            _lastMovementUtc = DateTime.UtcNow;
            _lastMovementMm = double.NaN;

            _cts = new CancellationTokenSource();

            int capacity = Math.Max(1, _cfg.Run.QueueCapacity);

            // FullMode.Wait, NOT DropWrite. DropWrite makes TryWrite return
            // TRUE while quietly throwing the item away - which is precisely
            // the silent segment loss this whole design exists to prevent.
            // With Wait, a non-blocking TryWrite returns false when the queue
            // is full, so the drop gets recorded and the frame released.
            //
            // Two queues in series so the two bottlenecks stay distinguishable:
            // a drop here means capture couldn't keep up with the trigger rate,
            // a drop on _jobs means inspection couldn't keep up with capture.
            _requests = Channel.CreateBounded<CaptureRequest>(
                new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });

            _jobs = Channel.CreateBounded<SegmentJob>(
                new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });

            _encoder.SampleReceived += OnSample;
            _encoder.Faulted += OnEncoderFault;

            _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
            _consumerTask = Task.Run(() => ConsumerLoopAsync(_cts.Token));
        }

        /// <summary>Operator pressed End Strip.</summary>
        public void End() => Finish(ScanEndReason.Operator);

        // ── encoder thread: decide when to capture ────────────────────────
        private void OnSample(EncoderSample sample)
        {
            if (!_running) return;

            TriggerDecision decision;
            try
            {
                decision = _controller.OnSample(sample);
            }
            catch (Exception ex)
            {
                Fault?.Invoke($"Trigger logic failed: {ex.Message}");
                Finish(ScanEndReason.Fault);
                return;
            }

            // Segments whose trigger point went by without a capture. Recorded
            // as gaps rather than captured late - that material is already past
            // the camera, so a late frame would carry the wrong position.
            foreach (int skipped in decision.SkippedSegments)
            {
                (double s, double e) = ScanGeometry.SegmentRangeMm(_cfg, skipped);
                Record(new SegmentRecord(skipped, s, e, 0, 0, 0,
                    SegmentOutcome.Skipped,
                    "Trigger point passed without a capture.", DateTime.UtcNow));
            }

            if (decision.Fire)
            {
                (double startMm, double endMm) = ScanGeometry.SegmentRangeMm(_cfg, decision.Segment);
                var request = new CaptureRequest(
                    decision.Segment, startMm, endMm,
                    decision.ActualMm, decision.TriggerErrorMm, _controller.SpeedMmPerSec);

                if (_requests is null || !_requests.Writer.TryWrite(request))
                {
                    Record(new SegmentRecord(decision.Segment, startMm, endMm,
                        decision.ActualMm, decision.TriggerErrorMm, _controller.SpeedMmPerSec,
                        SegmentOutcome.Dropped,
                        "Capture could not keep up with the trigger rate.", DateTime.UtcNow));
                }
            }

            CheckIdleTimeout(sample);

            // The controller may have ended the run itself (known length
            // reached, or the operator pressed the button again).
            if (_controller.State == ScanState.Finalising && _running)
                Finish(_controller.EndReason);
        }

        /// <summary>
        /// A line that has stopped moving ends the run, so a forgotten strip
        /// never leaves a run open forever. Note this is about the strip not
        /// moving - a dead encoder is a fault, handled separately, because the
        /// two must never be confused.
        /// </summary>
        private void CheckIdleTimeout(in EncoderSample sample)
        {
            if (_controller.State != ScanState.Scanning) return;
            if (_cfg.Run.IdleTimeoutSec <= 0) return;

            double mm = sample.PositionMm;
            if (double.IsNaN(_lastMovementMm) || Math.Abs(mm - _lastMovementMm) > 0.5)
            {
                _lastMovementMm = mm;
                _lastMovementUtc = DateTime.UtcNow;
                return;
            }

            if ((DateTime.UtcNow - _lastMovementUtc).TotalSeconds >= _cfg.Run.IdleTimeoutSec)
                Finish(ScanEndReason.IdleTimeout);
        }

        private void OnEncoderFault(string why)
        {
            // Never carry on with a timer: a stale reading and a stopped line
            // look identical, and guessing would mean claiming coverage that
            // was never measured.
            Fault?.Invoke(why);
            Finish(ScanEndReason.Fault);
        }

        // ── capture thread: grab the frame, hand it on, go back ───────────
        private async Task CaptureLoopAsync(CancellationToken token)
        {
            if (_requests is null || _jobs is null) return;

            try
            {
                await foreach (CaptureRequest r in _requests.Reader.ReadAllAsync(token))
                {
                    Mat? frame = null;
                    try
                    {
                        frame = _captureFrame();
                    }
                    catch (Exception ex)
                    {
                        Record(new SegmentRecord(r.Segment, r.StartMm, r.EndMm,
                            r.EncoderMm, r.TriggerErrorMm, r.SpeedMmPerSec,
                            SegmentOutcome.CaptureFailed, ex.Message, DateTime.UtcNow));
                        continue;
                    }

                    if (frame is null || frame.Empty())
                    {
                        frame?.Dispose();
                        Record(new SegmentRecord(r.Segment, r.StartMm, r.EndMm,
                            r.EncoderMm, r.TriggerErrorMm, r.SpeedMmPerSec,
                            SegmentOutcome.CaptureFailed,
                            "Camera returned no frame.", DateTime.UtcNow));
                        continue;
                    }

                    var job = new SegmentJob
                    {
                        Frame = frame,
                        Segment = r.Segment,
                        StartMm = r.StartMm,
                        EndMm = r.EndMm,
                        EncoderMm = r.EncoderMm,
                        TriggerErrorMm = r.TriggerErrorMm,
                        SpeedMmPerSec = r.SpeedMmPerSec,
                        CapturedUtc = DateTime.UtcNow,
                    };

                    // The one place a drop can happen quietly if we let it -
                    // so it is recorded loudly instead, and the frame released.
                    if (!_jobs.Writer.TryWrite(job))
                    {
                        job.Dispose();
                        Record(new SegmentRecord(r.Segment, r.StartMm, r.EndMm,
                            r.EncoderMm, r.TriggerErrorMm, r.SpeedMmPerSec,
                            SegmentOutcome.Dropped,
                            "Inspection queue full - inspection is slower than the trigger rate.",
                            DateTime.UtcNow));
                    }
                }
            }
            catch (OperationCanceledException) { /* run ended */ }
            finally
            {
                _jobs?.Writer.TryComplete();
            }
        }

        // ── consumer thread: inspect one frame at a time ──────────────────
        private async Task ConsumerLoopAsync(CancellationToken token)
        {
            if (_jobs is null) return;

            try
            {
                await foreach (SegmentJob job in _jobs.Reader.ReadAllAsync(token))
                {
                    SegmentOutcome outcome = SegmentOutcome.Inspected;
                    string? error = null;

                    try
                    {
                        await _inspect(job);
                    }
                    catch (Exception ex)
                    {
                        // One bad segment must not abort a 200 m strip - but it
                        // must not be silently forgotten either.
                        outcome = SegmentOutcome.InspectionFailed;
                        error = ex.Message;
                    }
                    finally
                    {
                        job.Dispose();
                    }

                    Record(new SegmentRecord(job.Segment, job.StartMm, job.EndMm,
                        job.EncoderMm, job.TriggerErrorMm, job.SpeedMmPerSec,
                        outcome, error, job.CapturedUtc));
                }
            }
            catch (OperationCanceledException) { /* run ended */ }
        }

        // ── finishing ─────────────────────────────────────────────────────
        private void Finish(ScanEndReason reason)
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
            }

            _encoder.SampleReceived -= OnSample;
            _encoder.Faulted -= OnEncoderFault;
            _controller.End(reason);

            // Let the queue drain rather than cancelling: those frames are
            // already captured, and throwing them away would put holes in the
            // coverage of a strip that was fully imaged.
            _ = Task.Run(async () =>
            {
                try
                {
                    _requests?.Writer.TryComplete();
                    if (_captureTask is not null) await _captureTask;
                    if (_consumerTask is not null) await _consumerTask;
                }
                catch { /* drain is best-effort */ }
                finally
                {
                    _controller.Reset();
                    RunFinished?.Invoke(reason);
                }
            });
        }

        private void Record(SegmentRecord record)
        {
            lock (_lock) _records.Add(record);
            SegmentFinished?.Invoke(record);
        }

        public void Dispose()
        {
            _encoder.SampleReceived -= OnSample;
            _encoder.Faulted -= OnEncoderFault;
            _running = false;
            _requests?.Writer.TryComplete();
            _jobs?.Writer.TryComplete();
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
