using System;

namespace copperInspection.Scan
{
    /// <summary>
    /// One reading from the encoder PCB.
    ///
    /// <see cref="Mm"/> is ABSOLUTE travel since the PCB powered up, already
    /// scaled to true millimetres on the PCB - this app does no unit
    /// conversion. <see cref="ZeroMm"/> is what <see cref="Mm"/> read at the
    /// moment of the last button press, and <see cref="ZeroCount"/> counts the
    /// presses.
    ///
    /// Why absolute-plus-remembered-zero rather than a PCB that zeroes its own
    /// counter: polling at ~50 Hz means we routinely will not be looking at the
    /// instant of the press. Computing the distance ourselves from these two
    /// numbers gives the exactly right answer even if we missed the press,
    /// dropped twenty responses, or reconnected mid-strip.
    /// </summary>
    public readonly record struct EncoderSample(
        long Seq,
        double Mm,
        double ZeroMm,
        int ZeroCount,
        bool Button,
        double AgeSeconds)
    {
        /// <summary>Distance travelled since the button press.</summary>
        public double PositionMm => Mm - ZeroMm;
    }

    /// <summary>
    /// Where encoder readings come from. Two implementations: the real PCB over
    /// HTTP, and a software ramp for building and testing with no hardware.
    /// The scan controller cannot tell them apart, which is the point.
    /// </summary>
    public interface IEncoderSource : IDisposable
    {
        /// <summary>A fresh reading arrived.</summary>
        event Action<EncoderSample>? SampleReceived;

        /// <summary>
        /// The feed became untrustworthy - disconnected, timed out, or "seq"
        /// stopped advancing. A run in progress must abort: a stale reading and
        /// a stopped line look identical, and guessing which one it is would
        /// mean claiming coverage nobody measured.
        /// </summary>
        event Action<string>? Faulted;

        bool IsConnected { get; }

        /// <summary>Most recent reading, or null before the first one arrives.</summary>
        EncoderSample? Latest { get; }

        /// <summary>
        /// Begin polling. Called once by the owner (app startup, or when the
        /// scan panel opens) - NOT once per strip. <see cref="ScanController"/>'s
        /// Arm/End states are what gate whether a reading turns into a
        /// capture; the feed itself keeps running underneath, across many
        /// strips, so it never has to reconnect between one strip ending and
        /// the next being armed.
        /// </summary>
        void Start();

        /// <summary>Stop polling. Called by the owner at shutdown (or to swap
        /// encoders) - not automatically by a fault or a finished run.</summary>
        void Stop();
    }
}
