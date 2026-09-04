using System;
using OpenCvSharp;

namespace copperInspection.Scan
{
    /// <summary>
    /// One captured frame on its way to inspection, carrying everything needed
    /// to say where on the strip it came from. The position travels WITH the
    /// frame rather than being looked up later, because by the time the
    /// consumer gets to it the encoder has moved on.
    /// </summary>
    public sealed class SegmentJob : IDisposable
    {
        /// <summary>The captured frame. The job owns it and disposes it.</summary>
        public required Mat Frame { get; init; }

        /// <summary>1-based segment index within the run.</summary>
        public required int Segment { get; init; }

        /// <summary>Where this frame's strip coverage starts and ends, mm from
        /// the strip's leading edge.</summary>
        public required double StartMm { get; init; }
        public required double EndMm { get; init; }

        /// <summary>Encoder reading at the moment of capture.</summary>
        public required double EncoderMm { get; init; }

        /// <summary>Actual minus ideal trigger position. A quality signal: if
        /// this starts drifting, something is wrong with the encoder, the line,
        /// or the capture-delay estimate.</summary>
        public required double TriggerErrorMm { get; init; }

        public required double SpeedMmPerSec { get; init; }
        public required DateTime CapturedUtc { get; init; }

        public void Dispose() => Frame.Dispose();
    }

    /// <summary>What happened to every segment the run tried to capture.
    /// Anything other than <see cref="Inspected"/> means the strip it covers
    /// was not actually verified.</summary>
    public enum SegmentOutcome
    {
        Inspected,

        /// <summary>The camera returned nothing.</summary>
        CaptureFailed,

        /// <summary>The queue was full - inspection could not keep up.</summary>
        Dropped,

        /// <summary>Inspection threw.</summary>
        InspectionFailed,

        /// <summary>Its trigger point went past without a capture.</summary>
        Skipped,
    }
}
