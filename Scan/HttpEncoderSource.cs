using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace copperInspection.Scan
{
    /// <summary>
    /// The wire shape of one GET /encoder response, exactly as specified in
    /// docs/ENCODER_MOCK_API_PROMPT.md and docs/STRIP_SCAN_BUILD_PLAN.md §5.
    ///
    /// Deliberately permissive about EXTRA fields (firmware will grow over
    /// time, and an unknown field here should never break polling) but the
    /// two Tier-1 fields are nullable so a response missing them can be told
    /// apart from a response genuinely reporting zero - see
    /// <see cref="HttpEncoderSource.ParseSample"/>.
    /// </summary>
    internal sealed class EncoderWireSample
    {
        [JsonPropertyName("seq")] public long? Seq { get; set; }
        [JsonPropertyName("mm")] public double? Mm { get; set; }
        [JsonPropertyName("zeroCount")] public int ZeroCount { get; set; }
        [JsonPropertyName("zeroMm")] public double ZeroMm { get; set; }
        [JsonPropertyName("button")] public int Button { get; set; }
        [JsonPropertyName("uptimeMs")] public long UptimeMs { get; set; }
        [JsonPropertyName("dir")] public int Dir { get; set; } = 1;
    }

    /// <summary>
    /// Polls the encoder PCB's <c>GET /encoder</c> endpoint over HTTP and turns
    /// each response into an <see cref="EncoderSample"/>.
    ///
    /// This is the ONLY thing that changes between a desk build and the real
    /// line: everything downstream (<see cref="ScanController"/>,
    /// <see cref="ScanPipeline"/>, <see cref="StripRunRecorder"/>) talks to
    /// <see cref="IEncoderSource"/>, the same interface
    /// <see cref="SimulatedEncoderSource"/> implements, and cannot tell the
    /// difference. See docs/ENCODER_MOCK_API_PROMPT.md for how to stand up
    /// something to point this at before the real PCB exists.
    ///
    /// Two failure classes are told apart on purpose, because they mean
    /// different things:
    ///   - a single slow or failed request is logged and the poll loop just
    ///     tries again next tick - a network blip is not a reason to abort a
    ///     strip;
    ///   - no ADVANCING reading for longer than MaxStaleMs raises
    ///     <see cref="Faulted"/> and the run must abort. This covers both "the
    ///     PCB stopped answering" and "the PCB keeps answering but seq froze"
    ///     with the same mechanism, because both mean the feed can no longer
    ///     be trusted - see the "seq" rationale in the build plan.
    ///
    /// What this deliberately does NOT cover: mm not advancing while seq
    /// keeps advancing is a STOPPED LINE, not a fault, and must never raise
    /// Faulted - <see cref="ScanController"/> already does the right thing
    /// with a flat reading (fires nothing), so this class doesn't even look at
    /// whether mm changed.
    /// </summary>
    public sealed class HttpEncoderSource : IEncoderSource
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly ScanConfig _cfg;
        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;
        private readonly Uri _uri;

        private CancellationTokenSource? _cts;
        private Task? _pollTask;

        private bool _haveLastSeq;
        private long _lastSeq;
        private DateTime _lastFreshUtc;
        private bool _faultRaised;

        /// <param name="httpClient">Supply one to reuse across the app or to
        /// inject a test double; omit to let this instance own its own (and
        /// dispose it).</param>
        public HttpEncoderSource(ScanConfig cfg, HttpClient? httpClient = null)
        {
            _cfg = cfg;

            // Same method ScanConfig.Validate() calls at pre-flight, so the
            // two can never disagree about what counts as a valid address -
            // a bad URL is meant to be caught there, with a clear operator
            // message, not here as a raw exception.
            if (!cfg.Encoder.TryBuildUri(out Uri? uri, out string? error))
                throw new InvalidOperationException(
                    $"{error} This should have been caught by ScanConfig.Validate() before the run started.");
            _uri = uri!;

            if (httpClient != null)
            {
                _http = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                // UseProxy = false is the important part: by default HttpClient
                // asks Windows for the system proxy (WPAD auto-detection) on
                // every connection, which on some networks adds hundreds of ms
                // per request - enough on its own to blow through a couple-
                // hundred-ms TimeoutMs even though the PCB/mock answers fast
                // (this is why `curl`, which doesn't do this by default, can
                // look fine while HttpClient times out against the identical
                // URL). The encoder is always a fixed LAN address, never
                // something that should go through a proxy, so this is safe to
                // turn off unconditionally rather than trying to detect when
                // it's the culprit.
                var handler = new SocketsHttpHandler { UseProxy = false };

                // Timeout left infinite on purpose: the per-request deadline is
                // enforced by the linked CancellationTokenSource in
                // PollOnceAsync instead, so a slow response fails just THAT
                // poll rather than tearing down the HttpClient's connection
                // pool (which HttpClient.Timeout would do).
                _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                _ownsHttpClient = true;
            }
        }

        public event Action<EncoderSample>? SampleReceived;
        public event Action<string>? Faulted;

        /// <summary>Fires for EVERY poll - success and failure alike - one line
        /// each, so the poll loop's activity is directly visible rather than
        /// inferred from silence. A failure line here (timeout, connection
        /// refused, bad JSON) doesn't by itself mean the run should abort -
        /// watch <see cref="Faulted"/> for that. Diagnostic only; also mirrored
        /// to Debug.WriteLine regardless of whether anything is listening.</summary>
        public event Action<string>? LogLineReceived;

        /// <summary>Whether the most recent poll succeeded. A single failed
        /// poll flips this to false; it does not by itself mean the run should
        /// abort - watch <see cref="Faulted"/> for that.</summary>
        public bool IsConnected { get; private set; }

        public EncoderSample? Latest { get; private set; }

        public void Start()
        {
            if (_pollTask != null) return;

            _cts = new CancellationTokenSource();
            _haveLastSeq = false;
            _faultRaised = false;
            _lastFreshUtc = DateTime.UtcNow;
            IsConnected = false;

            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            CancellationTokenSource? cts = _cts;
            _cts = null;
            cts?.Cancel();

            try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* cancellation - expected */ }

            cts?.Dispose();
            _pollTask = null;
            IsConnected = false;
        }

        public void Dispose()
        {
            Stop();
            if (_ownsHttpClient) _http.Dispose();
        }

        // ── the poll loop ─────────────────────────────────────────────────
        private async Task PollLoopAsync(CancellationToken token)
        {
            int periodMs = Math.Max(1, 1000 / Math.Max(1, _cfg.Encoder.PollHz));

            // A best-effort target rate, not a guarantee: if a poll itself
            // takes longer than one period (a slow PCB, or TimeoutMs set
            // generously), the loop simply runs at whatever rate the network
            // allows rather than queuing up missed ticks.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));

            while (!token.IsCancellationRequested)
            {
                await PollOnceAsync(token).ConfigureAwait(false);

                // Checked every tick, success or failure, so a feed that keeps
                // answering but stops ADVANCING is caught exactly as fast as
                // one that stops answering outright.
                CheckStale();

                try { await timer.WaitForNextTickAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task PollOnceAsync(CancellationToken outerToken)
        {
            using CancellationTokenSource reqCts =
                CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            reqCts.CancelAfter(Math.Max(1, _cfg.Encoder.TimeoutMs));

            long startTicks = Stopwatch.GetTimestamp();
            try
            {
                using HttpResponseMessage resp =
                    await _http.GetAsync(_uri, reqCts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                string body = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);

                // How old this reading is by the time it's used: the request's
                // own round trip. Not the same delay as CaptureDelay - that
                // one is downstream, between the trigger decision and the
                // shutter opening; this one is upstream, between the PCB
                // taking the reading and this app finding out about it. Both
                // get added into the trigger prediction in ScanController, for
                // two genuinely different reasons.
                double ageSeconds = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;

                EncoderSample sample = ParseSample(body, ageSeconds);

                IsConnected = true;
                bool advanced = !_haveLastSeq || sample.Seq != _lastSeq;
                _lastSeq = sample.Seq;
                _haveLastSeq = true;
                if (advanced)
                {
                    _lastFreshUtc = DateTime.UtcNow;
                    _faultRaised = false;   // a recovered feed can fault again later
                }

                Latest = sample;
                SampleReceived?.Invoke(sample);

                // One line per poll, success or failure (see the catch below),
                // so "is the poll loop even running" is answerable by looking,
                // not by inferring it from silence. Deliberately unconditional
                // rather than throttled - at development poll rates this is
                // the whole point; dial it back once a real status readout
                // (segment counter, live position) exists in the UI.
                Log($"poll ok   seq={sample.Seq,-8} mm={sample.Mm,10:F2}  pos={sample.PositionMm,10:F2}  " +
                    $"zeroCount={sample.ZeroCount,-4} age={sample.AgeSeconds * 1000:F0}ms");
            }
            catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
            {
                // Stop() was called mid-request - not a poll failure.
            }
            catch (Exception ex)
            {
                // Timeout, refused connection, malformed JSON, a response
                // missing the required fields - all funnel here. None of them
                // update _lastFreshUtc, so CheckStale (called right after this
                // returns) is what decides whether it's serious enough yet.
                IsConnected = false;

                // Elapsed time is the key diagnostic here: a genuine timeout
                // fails at ~TimeoutMs; anything failing in a handful of ms
                // instead is NOT the network or the PCB being slow - it's
                // something cancelling the request early (a bug, or something
                // external killing the connection immediately), and needs a
                // completely different fix.
                double elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                Log($"poll FAILED   {Describe(ex)}   (after {elapsedMs:F0}ms, budget was {_cfg.Encoder.TimeoutMs}ms)");
            }
        }

        private void CheckStale()
        {
            if (_faultRaised) return;

            double staleMs = (DateTime.UtcNow - _lastFreshUtc).TotalMilliseconds;
            if (staleMs < _cfg.Encoder.MaxStaleMs) return;

            // Never fall back to a timer past this point: a stale reading and
            // a stopped line look identical, and guessing which one it is
            // would mean claiming coverage nobody actually measured.
            _faultRaised = true;
            IsConnected = false;
            string reason = $"Encoder feed stale for {staleMs:F0} ms (limit {_cfg.Encoder.MaxStaleMs} ms) - " +
                             "no advancing reading arrived in time.";
            Log($"FAULT   {reason}");
            Faulted?.Invoke(reason);
        }

        /// <summary>Every poll outcome goes through here: to Debug.WriteLine,
        /// visible in the IDE's Output window with zero setup, and to
        /// <see cref="LogLineReceived"/> for whoever wants to route it into a
        /// visible log the way MainWindow already does for the camera bridge
        /// (see OnCameraLogLine).</summary>
        private void Log(string line)
        {
            Debug.WriteLine($"[Encoder] {line}");
            LogLineReceived?.Invoke(line);
        }

        // ── parsing ───────────────────────────────────────────────────────

        /// <summary>
        /// Turn one response body into a sample, or throw. Kept as a pure,
        /// static, directly-testable function - the one place a wire-format
        /// mistake could mislocate every defect on a strip, so it is exercised
        /// by unit tests with no network involved.
        /// </summary>
        internal static EncoderSample ParseSample(string json, double ageSeconds)
        {
            EncoderWireSample? wire = JsonSerializer.Deserialize<EncoderWireSample>(json, JsonOptions);
            if (wire == null)
                throw new JsonException("Empty or null encoder response.");

            // mm and seq are nullable on the wire type specifically so a
            // response missing them throws here, rather than silently being
            // accepted as "the encoder is at position 0" - which would look
            // like a real, trustworthy reading right up until it mislocated
            // every defect for the rest of the run.
            if (wire.Seq is not long seq)
                throw new JsonException("Encoder response is missing required field \"seq\".");
            if (wire.Mm is not double mm)
                throw new JsonException("Encoder response is missing required field \"mm\".");

            return new EncoderSample(
                Seq: seq,
                Mm: mm,
                ZeroMm: wire.ZeroMm,
                ZeroCount: wire.ZeroCount,
                Button: wire.Button != 0,
                AgeSeconds: ageSeconds);
        }

        private static string Describe(Exception ex) => ex switch
        {
            OperationCanceledException => "timed out",
            HttpRequestException hre => hre.Message,
            JsonException je => $"malformed response ({je.Message})",
            _ => ex.Message,
        };
    }
}
