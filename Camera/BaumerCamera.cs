using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using OpenCvSharp;
using copperInspection.Camera.Protocol;

namespace copperInspection.Camera
{
    /// <summary>One entry from the camera discovery list - enough to show an
    /// operator a pick list and to reconnect to a specific physical camera
    /// later.</summary>
    public sealed class CameraDeviceInfo
    {
        public required string Id { get; init; }
        public required string SerialNumber { get; init; }
        public required string ModelName { get; init; }

        public override string ToString() => $"{ModelName} (S/N {SerialNumber})";
    }

    /// <summary>Client for the separate CameraBridge.exe helper process.
    ///
    /// Why a separate process: referencing Baumer's neoAPI SDK alongside
    /// Microsoft.ML.OnnxRuntime in the SAME process reproduces a real
    /// 0xC0000005 access violation (confirmed Aug 27, 2026, in total
    /// isolation with no app code involved at all - see docs/WORK_LOG.md).
    /// CameraBridge.exe references neoAPI and NOTHING else camera-related;
    /// this class launches it once and talks to it over two named pipes, so
    /// the two SDKs' native code never shares a process address space.
    ///
    /// - Control pipe: request/response for list/connect/start/stop/capture.
    /// - Preview pipe: one-directional stream of JPEG frames for the live
    ///   view. Lossy on purpose - Capture always asks for a fresh, lossless
    ///   frame over the control pipe instead of reusing a decoded preview
    ///   frame, since that one actually feeds detection.</summary>
    public sealed class BaumerCamera : IDisposable
    {
        private Process? _bridgeProcess;
        private NamedPipeClientStream? _controlPipe;
        private NamedPipeClientStream? _previewPipe;
        private Thread? _previewReaderThread;
        private volatile bool _previewReaderRunning;
        private readonly object _previewLock = new();
        private Mat? _latestPreviewFrame;
        private readonly object _controlLock = new();

        public bool IsConnected { get; private set; }
        public bool IsStreaming { get; private set; }
        public string? ConnectedLabel { get; private set; }

        /// <summary>Fires for every line CameraBridge.exe logs (it writes to
        /// stdout/stderr, redirected here) - lets MainWindow show the
        /// bridge's own diagnostics live instead of someone having to go dig
        /// up CameraBridge.log by hand. Raised on a thread-pool thread, not
        /// the UI thread - subscribers must marshal to the UI themselves.</summary>
        public event Action<string>? LogLineReceived;

        /// <summary>Launches CameraBridge.exe (if not already running) and
        /// connects both pipes. Safe to call more than once - a no-op if
        /// already connected. Never throws; returns false (treated the same
        /// as "no camera support available") if the helper exe is missing
        /// or fails to start.</summary>
        public bool EnsureStarted()
        {
            if (_controlPipe is { IsConnected: true }) return true;

            try
            {
                if (_bridgeProcess == null || _bridgeProcess.HasExited)
                {
                    string exePath = Path.Combine(AppContext.BaseDirectory, "CameraBridge", "CameraBridge.exe");
                    if (!File.Exists(exePath)) return false;

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    _bridgeProcess = Process.Start(psi);
                    if (_bridgeProcess == null) return false;

                    _bridgeProcess.OutputDataReceived += (_, e) =>
                    { if (e.Data != null) LogLineReceived?.Invoke(e.Data); };
                    _bridgeProcess.ErrorDataReceived += (_, e) =>
                    { if (e.Data != null) LogLineReceived?.Invoke($"[stderr] {e.Data}"); };
                    _bridgeProcess.BeginOutputReadLine();
                    _bridgeProcess.BeginErrorReadLine();
                }

                _controlPipe = new NamedPipeClientStream(".", PipeNames.Control, PipeDirection.InOut);
                _controlPipe.Connect(5000);

                _previewPipe = new NamedPipeClientStream(".", PipeNames.Preview, PipeDirection.In);
                _previewPipe.Connect(5000);

                _previewReaderRunning = true;
                _previewReaderThread = new Thread(PreviewReaderLoop)
                { IsBackground = true, Name = "CameraBridgePreviewReader" };
                _previewReaderThread.Start();

                return true;
            }
            catch
            {
                return false;
            }
        }

        public List<CameraDeviceInfo> ListCameras()
        {
            var resp = SendRequest(new ControlRequest { Cmd = "list" }, out _);
            var result = new List<CameraDeviceInfo>();
            if (resp?.Cameras == null) return result;
            foreach (CameraDeviceDto c in resp.Cameras)
                result.Add(new CameraDeviceInfo { Id = c.Id, SerialNumber = c.SerialNumber, ModelName = c.ModelName });
            return result;
        }

        /// <summary>Throws on failure - caller decides how to surface that
        /// (a status label, not a crash).</summary>
        public void Connect(CameraDeviceInfo device)
        {
            var resp = SendRequest(new ControlRequest { Cmd = "connect", DeviceId = device.Id }, out _)
                ?? throw new InvalidOperationException("No response from camera bridge.");
            if (!resp.Ok) throw new InvalidOperationException(resp.Error ?? "Connect failed.");
            IsConnected = true;
            IsStreaming = resp.Streaming;
            ConnectedLabel = device.ToString();
        }

        public void StartStreaming()
        {
            var resp = SendRequest(new ControlRequest { Cmd = "start" }, out _);
            if (resp is { Ok: true }) IsStreaming = resp.Streaming;
        }

        public void StopStreaming()
        {
            var resp = SendRequest(new ControlRequest { Cmd = "stop" }, out _);
            if (resp is { Ok: true }) IsStreaming = resp.Streaming;
        }

        /// <summary>Cheap - returns a clone of the most recently decoded
        /// preview frame (JPEG-compressed in transit, for the live view
        /// only). Never use this as detection input - use CaptureFullFrame.</summary>
        public Mat? GetLatestPreviewFrame()
        {
            lock (_previewLock) { return _latestPreviewFrame?.Clone(); }
        }

        /// <summary>Blocking round trip to the bridge for a fresh, lossless
        /// (raw BGR24) frame - call this from a background thread, not the
        /// UI thread. Returns null if no frame is available yet or the
        /// bridge/camera isn't ready.</summary>
        public Mat? CaptureFullFrame()
        {
            var resp = SendRequest(new ControlRequest { Cmd = "capture" }, out byte[]? extra);
            if (resp is not { Ok: true } || resp.Width is not int w || resp.Height is not int h || extra == null)
                return null;

            // Not mat.SetArray(extra) - see the matching comment in
            // CameraBridge/Program.cs's GrabLoop for why that throws
            // "data type is not compatible" for a flat byte[] against a
            // 3-channel Mat.
            var mat = new Mat(h, w, MatType.CV_8UC3);
            Marshal.Copy(extra, 0, mat.Data, extra.Length);
            return mat;
        }

        private ControlResponse? SendRequest(ControlRequest req, out byte[]? extraPayload)
        {
            extraPayload = null;
            lock (_controlLock)
            {
                if (_controlPipe is not { IsConnected: true }) return null;
                try
                {
                    FrameIO.WriteFrame(_controlPipe, JsonSerializer.SerializeToUtf8Bytes(req));
                    byte[]? respBytes = FrameIO.ReadFrame(_controlPipe);
                    if (respBytes == null) return null;
                    var resp = JsonSerializer.Deserialize<ControlResponse>(respBytes);
                    if (resp is { Ok: true, Width: not null } && req.Cmd == "capture")
                        extraPayload = FrameIO.ReadFrame(_controlPipe);
                    return resp;
                }
                catch
                {
                    return null;
                }
            }
        }

        private void PreviewReaderLoop()
        {
            while (_previewReaderRunning)
            {
                byte[]? jpg;
                try { jpg = FrameIO.ReadFrame(_previewPipe!); }
                catch { break; }
                if (jpg == null) break;

                Mat decoded = Cv2.ImDecode(jpg, ImreadModes.Color);
                if (decoded.Empty()) { decoded.Dispose(); continue; }

                lock (_previewLock)
                {
                    _latestPreviewFrame?.Dispose();
                    _latestPreviewFrame = decoded;
                }
            }
        }

        public void Dispose()
        {
            _previewReaderRunning = false;
            try { _controlPipe?.Dispose(); } catch { /* best-effort teardown */ }
            try { _previewPipe?.Dispose(); } catch { /* best-effort teardown */ }
            _previewReaderThread?.Join(500);

            lock (_previewLock)
            {
                _latestPreviewFrame?.Dispose();
                _latestPreviewFrame = null;
            }

            try
            {
                if (_bridgeProcess is { HasExited: false })
                {
                    _bridgeProcess.Kill(entireProcessTree: true);
                    _bridgeProcess.WaitForExit(2000);
                }
            }
            catch { /* best-effort teardown */ }
            _bridgeProcess?.Dispose();
        }
    }
}
