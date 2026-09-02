// CameraBridge - a standalone helper process that owns the Baumer camera via
// neoAPI, and ONLY that. It deliberately never references
// Microsoft.ML.OnnxRuntime (see the header comment on
// Camera/CameraProtocol.cs for why this has to be a separate process at
// all). copperInspection.exe launches one instance of this per session and
// talks to it over two named pipes; when the control pipe disconnects
// (the app closed, or killed this process), this process exits.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeoAPI;
using OpenCvSharp;
using copperInspection.Camera.Protocol;

string logPath = Path.Combine(AppContext.BaseDirectory, "CameraBridge.log");

// Every line goes both to stdout - which copperInspection.exe redirects and
// shows live in its own UI, see BaumerCamera.cs - and to a file, so there's
// still a record if the app closes before you get to read it live.
void Log(string msg)
{
    string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + Environment.NewLine); }
    catch { /* logging must never be the thing that crashes this process */ }
}

// Fresh log per run, and catch anything that would otherwise kill this
// process silently.
try { File.Delete(logPath); } catch { }
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log($"[FATAL] Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");

Log("Starting...");

Cam? cam = null;
Thread? grabThread = null;
StreamState state = new();
object frameLock = new();
Mat? latestFrame = null;

using var controlPipe = new NamedPipeServerStream(PipeNames.Control, PipeDirection.InOut, 1);
using var previewPipe = new NamedPipeServerStream(PipeNames.Preview, PipeDirection.Out, 1);

Log("Waiting for control connection...");
controlPipe.WaitForConnection();
Log("Control connected.");

// Preview is best-effort - accept it in the background so a client that's
// slow to open it (or never does) doesn't block command handling.
_ = Task.Run(() =>
{
    try
    {
        previewPipe.WaitForConnection();
        Log("Preview connected.");
    }
    catch (Exception ex) { Log($"Preview connect failed: {ex}"); }
});

try
{
    while (true)
    {
        byte[]? reqBytes;
        try { reqBytes = FrameIO.ReadFrame(controlPipe); }
        catch (IOException) { break; }
        if (reqBytes == null) break; // client disconnected

        ControlRequest req;
        try { req = JsonSerializer.Deserialize<ControlRequest>(reqBytes) ?? new ControlRequest(); }
        catch (Exception ex) { Log($"Bad request JSON: {ex.Message}"); continue; }

        Log($"Request: {req.Cmd} (deviceId={req.DeviceId})");

        var resp = new ControlResponse();
        byte[]? extra = null;

        try
        {
            switch (req.Cmd)
            {
                case "list":
                    resp.Cameras = ListCameras();
                    Log($"  -> {resp.Cameras.Count} camera(s) found.");
                    break;

                case "connect":
                    Disconnect();
                    if (string.IsNullOrEmpty(req.DeviceId))
                    {
                        resp.Ok = false;
                        resp.Error = "No device id given.";
                        break;
                    }
                    var newCam = new Cam();
                    newCam.Connect(req.DeviceId);
                    cam = newCam;
                    resp.ConnectedLabel = req.DeviceId;
                    Log($"  -> connected to {req.DeviceId}, IsConnected={cam.IsConnected}");
                    StartGrabbing();
                    resp.Streaming = state.Streaming;
                    Log($"  -> streaming={state.Streaming}");
                    break;

                case "start":
                    StartGrabbing();
                    resp.Streaming = state.Streaming;
                    Log($"  -> streaming={state.Streaming}");
                    break;

                case "stop":
                    StopGrabbing();
                    resp.Streaming = state.Streaming;
                    break;

                case "capture":
                    Mat? snap;
                    lock (frameLock) { snap = latestFrame?.Clone(); }
                    if (snap == null || snap.Empty())
                    {
                        snap?.Dispose();
                        resp.Ok = false;
                        resp.Error = "No frame available yet.";
                        Log("  -> capture: no frame available yet.");
                    }
                    else
                    {
                        using (snap)
                        {
                            resp.Width = snap.Width;
                            resp.Height = snap.Height;
                            extra = MatToBgrBytes(snap);
                        }
                        Log($"  -> capture: {resp.Width}x{resp.Height}, {extra.Length} bytes.");
                    }
                    break;

                default:
                    resp.Ok = false;
                    resp.Error = $"Unknown command '{req.Cmd}'.";
                    break;
            }
        }
        catch (Exception ex)
        {
            resp.Ok = false;
            resp.Error = ex.Message;
            Log($"  -> EXCEPTION handling '{req.Cmd}': {ex}");
        }

        FrameIO.WriteFrame(controlPipe, JsonSerializer.SerializeToUtf8Bytes(resp));
        if (extra != null) FrameIO.WriteFrame(controlPipe, extra);
    }
}
finally
{
    StopGrabbing();
    Disconnect();
    Log("Control disconnected - exiting.");
}

// ── local functions ─────────────────────────────────────────────────────

List<CameraDeviceDto> ListCameras()
{
    var result = new List<CameraDeviceDto>();
    using CamInfoList list = CamInfoList.Get();
    list.Refresh();
    foreach (CamInfo info in list.List)
    {
        result.Add(new CameraDeviceDto
        {
            Id = info.Id,
            SerialNumber = info.SerialNumber,
            ModelName = info.ModelName,
        });
    }
    return result;
}

void Disconnect()
{
    StopGrabbing();
    try { cam?.Disconnect(); } catch (NeoException ex) { Log($"Disconnect: {ex.Message}"); }
    cam?.Dispose();
    cam = null;
}

void StartGrabbing()
{
    if (cam == null || !cam.IsConnected || state.Streaming) return;
    cam.StartStreaming();
    state.Streaming = true;
    grabThread = new Thread(GrabLoop) { IsBackground = true, Name = "CameraBridgeGrab" };
    grabThread.Start();
    Log("Grab thread started.");
}

void StopGrabbing()
{
    if (!state.Streaming) return;
    state.Streaming = false;
    grabThread?.Join(1000);
    grabThread = null;
    try { cam?.StopStreaming(); } catch (NeoException ex) { Log($"StopStreaming: {ex.Message}"); }
    Log("Grab thread stopped.");
}

void GrabLoop()
{
    DateTime lastPreviewSent = DateTime.MinValue;
    int framesGrabbed = 0;
    int timeouts = 0;
    DateTime lastStatusLog = DateTime.UtcNow;

    while (state.Streaming)
    {
        Cam? localCam = cam;
        if (localCam == null) return;
        try
        {
            using Image image = localCam.GetImage(200);
            if (image.IsEmpty)
            {
                timeouts++;
                if ((DateTime.UtcNow - lastStatusLog).TotalSeconds >= 5)
                {
                    Log($"GrabLoop: still waiting - {timeouts} empty GetImage() result(s) " +
                        $"(timeout) since last frame, {framesGrabbed} frame(s) grabbed total. " +
                        "If this keeps happening, the camera likely isn't in free-run/continuous " +
                        "acquisition mode (e.g. TriggerMode is On with no trigger source firing).");
                    lastStatusLog = DateTime.UtcNow;
                }
                continue;
            }

            using Image bgr = image.Convert(PixelFormat.BGR8.ToString());
            int w = (int)bgr.Width;
            int h = (int)bgr.Height;
            int byteCount = w * h * 3;

            byte[] buffer = new byte[byteCount];
            Marshal.Copy(bgr.ImageData, buffer, 0, byteCount);

            // Not mat.SetArray(buffer) - OpenCvSharp's SetArray<T> requires T
            // to be a whole-pixel struct (Vec3b) for a 3-channel Mat, and
            // throws "data type is not compatible" for a flat byte[]
            // (that's only valid against a 1-channel Mat). A freshly
            // allocated Mat(h, w, type) is always continuous with no row
            // padding, so copying the bytes straight into its own buffer is
            // both correct and simpler.
            var mat = new Mat(h, w, MatType.CV_8UC3);
            Marshal.Copy(buffer, 0, mat.Data, byteCount);

            lock (frameLock)
            {
                latestFrame?.Dispose();
                latestFrame = mat;
            }

            framesGrabbed++;
            if (framesGrabbed == 1)
                Log($"GrabLoop: first frame acquired ({w}x{h}, pixel format {bgr.PixelFormat}).");

            // Throttle preview push to ~15fps regardless of the camera's
            // actual frame rate - it's only ever displayed at that rate.
            DateTime now = DateTime.UtcNow;
            if (previewPipe.IsConnected && (now - lastPreviewSent).TotalMilliseconds >= 66)
            {
                lastPreviewSent = now;
                Cv2.ImEncode(".jpg", mat, out byte[] jpg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
                try { FrameIO.WriteFrame(previewPipe, jpg); }
                catch (IOException ex) { Log($"GrabLoop: preview write failed (client gone?): {ex.Message}"); }
            }
            else if (!previewPipe.IsConnected && framesGrabbed == 1)
            {
                Log("GrabLoop: preview pipe not connected yet - frames are being grabbed " +
                    "but won't reach the UI until it connects.");
            }
        }
        catch (NeoException ex)
        {
            Log($"GrabLoop: NeoException: {ex.Message}");
            Thread.Sleep(100); // transient - e.g. grab timeout
        }
        catch (Exception ex)
        {
            // Anything else here would otherwise kill this thread (and the
            // whole preview) silently - log it and keep the loop alive.
            Log($"GrabLoop: unexpected exception: {ex}");
            Thread.Sleep(100);
        }
    }
}

/// <summary>mat must be continuous (e.g. a fresh .Clone(), which OpenCV
/// always makes continuous) - deliberately not doing the "clone if not
/// already continuous" dance here, since aliasing-and-disposing a Mat the
/// caller still owns bit this codebase once already (see docs/WORK_LOG.md).</summary>
static byte[] MatToBgrBytes(Mat mat)
{
    long byteCount = mat.Total() * mat.ElemSize();
    byte[] buffer = new byte[byteCount];
    Marshal.Copy(mat.Data, buffer, 0, buffer.Length);
    return buffer;
}

/// <summary>Top-level-statement locals can't be `volatile` themselves, so
/// the flag lives on a field of this tiny holder instead - read from the
/// grab thread, written from the control loop's thread.</summary>
sealed class StreamState
{
    public volatile bool Streaming;
}
