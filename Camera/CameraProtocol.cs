using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;

namespace copperInspection.Camera.Protocol
{
    /// <summary>Wire protocol shared between the main WPF app and the
    /// separate CameraBridge.exe helper process. Linked into both projects
    /// (see CameraBridge.csproj's &lt;Compile Include&gt;) so there is exactly
    /// one definition of the protocol, never two copies that can drift apart.
    ///
    /// Why a separate process at all: referencing Baumer's neoAPI SDK
    /// alongside Microsoft.ML.OnnxRuntime in the SAME process reproduces a
    /// real 0xC0000005 access violation (see docs/WORK_LOG.md, Aug 27, 2026)
    /// - confirmed in total isolation, not something a DLL-exclusion patch
    /// fixed. Running the camera in its own process means neoAPI's native
    /// code and ONNX Runtime's native code never share an address space, so
    /// that crash class structurally cannot happen here.</summary>
    public static class PipeNames
    {
        /// <summary>Bidirectional request/response: list/connect/start/stop/
        /// capture. One request in flight at a time - the WPF app is the
        /// only client there will ever be.
        ///
        /// Parameterized by channel ("A", "B", ...) so more than one physical
        /// camera can run: each gets its own CameraBridge.exe instance
        /// (launched with the channel as its one command-line argument) and
        /// its own pair of pipes, completely independent of any other
        /// channel's process, pipes, or camera connection.</summary>
        public static string Control(string channel) => $"copperInspection.camera.control.{channel}";

        /// <summary>One-directional (bridge -> app): a continuous stream of
        /// JPEG-encoded preview frames while streaming is active. Lossy on
        /// purpose - it's for on-screen preview only. Capture always goes
        /// through the control pipe instead, for a fresh, lossless frame.</summary>
        public static string Preview(string channel) => $"copperInspection.camera.preview.{channel}";
    }

    /// <summary>Simple length-prefixed framing over a NamedPipe stream: a
    /// 4-byte little-endian length, then exactly that many payload bytes.
    /// Needed because pipe reads can return fewer bytes than asked for -
    /// without this, message boundaries would silently corrupt.</summary>
    public static class FrameIO
    {
        public static void WriteFrame(Stream s, byte[] payload)
        {
            Span<byte> header = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            s.Write(header);
            s.Write(payload, 0, payload.Length);
            s.Flush();
        }

        /// <summary>Returns null if the stream was closed before a full
        /// header could be read (i.e. the other side disconnected).</summary>
        public static byte[]? ReadFrame(Stream s)
        {
            byte[] header = new byte[4];
            if (!ReadExact(s, header, 4)) return null;
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            byte[] payload = new byte[length];
            if (!ReadExact(s, payload, length)) return null;
            return payload;
        }

        private static bool ReadExact(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) return false; // stream closed
                read += n;
            }
            return true;
        }
    }

    public sealed class CameraDeviceDto
    {
        public string Id { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string ModelName { get; set; } = "";
    }

    public sealed class ControlRequest
    {
        /// <summary>"list" | "connect" | "start" | "stop" | "capture"</summary>
        public string Cmd { get; set; } = "";
        public string? DeviceId { get; set; }

        /// <summary>For "connect": fixed exposure (microseconds) and gain to
        /// write to the camera, with the matching auto mode switched off.
        /// Null = leave whatever the camera already has.</summary>
        public double? ExposureUs { get; set; }
        public double? Gain { get; set; }
    }

    public sealed class ControlResponse
    {
        public bool Ok { get; set; } = true;
        public string? Error { get; set; }

        /// <summary>Populated for "list".</summary>
        public List<CameraDeviceDto>? Cameras { get; set; }

        /// <summary>Populated for "connect"/"start"/"stop" status.</summary>
        public string? ConnectedLabel { get; set; }
        public bool Streaming { get; set; }

        /// <summary>For "capture": when Ok is true, a raw BGR24 pixel buffer
        /// of exactly Width*Height*3 bytes follows as a SEPARATE frame
        /// immediately after this response frame on the same pipe.</summary>
        public int? Width { get; set; }
        public int? Height { get; set; }
    }
}
