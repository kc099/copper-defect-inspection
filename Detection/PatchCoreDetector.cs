using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace copperInspection.Detection
{
    /// <summary>
    /// High-performance C# PatchCore Detector optimized for ResNet-50 (layer2 + layer4).
    /// Uses SIMD vectorization via TensorPrimitives for sub-second CPU distance searches.
    /// </summary>
    public class PatchCoreDetector : IDisposable
    {
        private const int Tile = 256;
        private const double MinFill = 0.10;

        private readonly InferenceSession _session;
        private float[] _memoryBank = Array.Empty<float>();
        private int _numPatches;
        private int _featureDim;
        private float _imageMin = 0.0f;
        private float _imageMax = 1.0f;
        public float Threshold { get; private set; }

        private sealed class TileLayout
        {
            public List<Mat> Tiles { get; } = new();
            public List<(int Tx, int Ty)> Offsets { get; } = new();
            public int ScaledW, ScaledH;
            public Rect CropBox;
        }

        public PatchCoreDetector(string onnxPath, string bankBinPath, float threshold)
        {
            var sw = Stopwatch.StartNew();
            Trace("[PatchCore Profile] Loading ResNet-50 Detector...");

            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            _session = new InferenceSession(onnxPath, options);
            Threshold = threshold;
            LoadMemoryBank(bankBinPath);

            sw.Stop();
            Trace($"[PatchCore Profile] Initialization complete in {sw.ElapsedMilliseconds} ms");
        }

        private void LoadMemoryBank(string binPath)
        {
            using var stream = File.Open(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            _numPatches = reader.ReadInt32();
            _featureDim = reader.ReadInt32();

            int totalElements = _numPatches * _featureDim;
            _memoryBank = new float[totalElements];

            byte[] byteBuffer = reader.ReadBytes(totalElements * sizeof(float));
            Buffer.BlockCopy(byteBuffer, 0, _memoryBank, 0, byteBuffer.Length);

            Trace($"[PatchCore Profile] Memory bank loaded: {_numPatches} vectors x {_featureDim} channels");
        }

        public (bool IsDefect, float Score, Mat HeatmapRaw, Mat HeatmapOnOriginal) Inspect(
            Mat croppedContent, Mat? overlayBase = null)
        {
            if (croppedContent == null || croppedContent.Empty())
                return (false, 0.0f, new Mat(), new Mat());

            var totalSw = Stopwatch.StartNew();
            var stepSw = Stopwatch.StartNew();

            // Step 1: Preprocess content into 256x256 tiles
            TileLayout layout = PreprocessToTiles(croppedContent);
            stepSw.Stop();
            long patchExtractionMs = stepSw.ElapsedMilliseconds;

            if (layout.Tiles.Count == 0)
            {
                Mat blank = new(croppedContent.Size(), MatType.CV_8UC3, Scalar.All(0));
                Mat blankOnOriginal = (overlayBase ?? croppedContent).Clone();
                return (false, 0.0f, blank, blankOnOriginal);
            }

            List<Mat> patches = layout.Tiles;
            try
            {
                // Step 2: Extract ResNet-50 (layer2 + layer4) Features via ONNX
                stepSw.Restart();
                List<float[]> featureMaps = ExtractFeaturesBatch(patches, out int gridH, out int gridW);
                stepSw.Stop();
                long onnxInferenceMs = stepSw.ElapsedMilliseconds;

                // Step 3: Fast Parallel SIMD Euclidean Distance Search
                stepSw.Restart();
                var tileGrids = new float[patches.Count][,];
                float maxImageScore = 0.0f;

                for (int t = 0; t < patches.Count; t++)
                {
                    float[,] anomalyScores = ComputeAnomalyScores(featureMaps[t], gridH, gridW, _featureDim);
                    tileGrids[t] = anomalyScores;

                    for (int r = 0; r < gridH; r++)
                        for (int c = 0; c < gridW; c++)
                            if (anomalyScores[r, c] > maxImageScore)
                                maxImageScore = anomalyScores[r, c];
                }
                stepSw.Stop();
                long distanceSearchMs = stepSw.ElapsedMilliseconds;

                totalSw.Stop();

                Trace("==========================================================");
                Trace($"[PatchCore Profile] Total Inspection Time : {totalSw.ElapsedMilliseconds} ms");
                Trace($"  ├── 1. Patch Extraction ({patches.Count} tiles)    : {patchExtractionMs} ms");
                Trace($"  ├── 2. ResNet-50 ONNX Inference        : {onnxInferenceMs} ms");
                Trace($"  └── 3. SIMD Vector Distance Search      : {distanceSearchMs} ms");
                Trace("==========================================================");

                bool isDefect = maxImageScore > Threshold;

                // Step 4: Stitch per-tile heatmaps into full-frame heatmap
                using Mat fullHeat = StitchHeatmap(layout, tileGrids, gridH, gridW, croppedContent.Size());
                Mat heatmapRaw = ColorizeHeat(fullHeat);

                Mat baseImg = overlayBase ?? croppedContent;
                Mat heatmapOnOriginal = new();
                Cv2.AddWeighted(baseImg, 0.5, heatmapRaw, 0.5, 0, heatmapOnOriginal);

                return (isDefect, maxImageScore, heatmapRaw, heatmapOnOriginal);
            }
            finally
            {
                foreach (var p in patches) p.Dispose();
            }
        }

        private List<float[]> ExtractFeaturesBatch(List<Mat> patches, out int gridH, out int gridW)
        {
            int batchSize = patches.Count;
            var batchTensor = new DenseTensor<float>(new[] { batchSize, 3, Tile, Tile });

            float[] mean = { 0.485f, 0.456f, 0.406f };
            float[] std = { 0.229f, 0.224f, 0.225f };

            for (int b = 0; b < batchSize; b++)
            {
                using Mat bgr = NormalizePatchToBGR(patches[b]);
                byte[] rawBytes = new byte[Tile * Tile * 3];
                Marshal.Copy(bgr.Data, rawBytes, 0, rawBytes.Length);

                for (int y = 0; y < Tile; y++)
                {
                    int rowOffset = y * Tile * 3;
                    for (int x = 0; x < Tile; x++)
                    {
                        int pixelIdx = rowOffset + (x * 3);
                        byte blue = rawBytes[pixelIdx];
                        byte green = rawBytes[pixelIdx + 1];
                        byte red = rawBytes[pixelIdx + 2];

                        // Convert RGB to Greyscale R=G=B matching Python training
                        float gray = (red * 0.299f + green * 0.587f + blue * 0.114f) / 255.0f;

                        batchTensor[b, 0, y, x] = (gray - mean[0]) / std[0];
                        batchTensor[b, 1, y, x] = (gray - mean[1]) / std[1];
                        batchTensor[b, 2, y, x] = (gray - mean[2]) / std[2];
                    }
                }
            }

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", batchTensor) };
            using var results = _session.Run(inputs);

            // Fetch layer2 (512 dims) and layer4 (2048 dims) from ResNet-50
            var l2Tensor = results.First(r => r.Name == "layer2").AsTensor<float>();
            var l4Tensor = results.First(r => r.Name == "layer4").AsTensor<float>();

            return SplitBatchAndAlign(l2Tensor, l4Tensor, batchSize, out gridH, out gridW);
        }

        private static List<float[]> SplitBatchAndAlign(Tensor<float> l2, Tensor<float> l4, int batchSize, out int gridH, out int gridW)
        {
            gridH = l2.Dimensions[2]; // Usually 32 for 256x256 input
            gridW = l2.Dimensions[3];
            int c2 = l2.Dimensions[1]; // 512
            int c4 = l4.Dimensions[1]; // 2048
            int totalDim = c2 + c4;    // 2560 total channels before pooling

            int l4H = l4.Dimensions[2]; // Usually 8
            int l4W = l4.Dimensions[3];

            float[] l2Flat = l2.ToArray();
            float[] l4Flat = l4.ToArray();

            // 1. Zero-padded 3x3 Average Pool matching Anomalib
            float[] l2Smooth = Smooth3x3(l2Flat, batchSize, c2, gridH, gridW);
            float[] l4Smooth = Smooth3x3(l4Flat, batchSize, c4, l4H, l4W);

            // 2. Bilinear upsample layer4 to match layer2 grid size
            float[] l4Aligned = (l4H == gridH && l4W == gridW)
                ? l4Smooth
                : BilinearResize(l4Smooth, batchSize, c4, l4H, l4W, gridH, gridW);

            List<float[]> patchFeatureMaps = new List<float[]>(batchSize);

            int patchSpatialSize = gridH * gridW;
            int l2BatchOffset = c2 * patchSpatialSize;
            int l3BatchOffset = c4 * patchSpatialSize;

            for (int b = 0; b < batchSize; b++)
            {
                float[] patchConcat = new float[patchSpatialSize * totalDim];
                int bL2Start = b * l2BatchOffset;
                int bL4Start = b * l3BatchOffset;

                for (int r = 0; r < gridH; r++)
                {
                    for (int c = 0; c < gridW; c++)
                    {
                        int spatialIdx = (r * gridW + c) * totalDim;

                        for (int ch = 0; ch < c2; ch++)
                        {
                            int idx = bL2Start + (ch * patchSpatialSize) + (r * gridW) + c;
                            patchConcat[spatialIdx + ch] = l2Smooth[idx];
                        }

                        for (int ch = 0; ch < c4; ch++)
                        {
                            int idx = bL4Start + (ch * patchSpatialSize) + (r * gridW) + c;
                            patchConcat[spatialIdx + c2 + ch] = l4Aligned[idx];
                        }
                    }
                }

                patchFeatureMaps.Add(patchConcat);
            }

            return patchFeatureMaps;
        }

        private static float[] Smooth3x3(float[] src, int batch, int channels, int h, int w)
        {
            float[] dst = new float[src.Length];
            int planeSize = h * w;

            for (int b = 0; b < batch; b++)
            {
                int bOff = b * channels * planeSize;
                for (int c = 0; c < channels; c++)
                {
                    int cOff = bOff + c * planeSize;
                    for (int r = 0; r < h; r++)
                    {
                        int rMin = Math.Max(0, r - 1), rMax = Math.Min(h - 1, r + 1);
                        for (int col = 0; col < w; col++)
                        {
                            int cMin = Math.Max(0, col - 1), cMax = Math.Min(w - 1, col + 1);
                            float sum = 0f;
                            for (int rr = rMin; rr <= rMax; rr++)
                            {
                                int rowOff = cOff + rr * w;
                                for (int cc = cMin; cc <= cMax; cc++)
                                    sum += src[rowOff + cc];
                            }
                            dst[cOff + r * w + col] = sum / 9f;
                        }
                    }
                }
            }
            return dst;
        }

        private static float[] BilinearResize(float[] src, int batch, int channels, int hIn, int wIn, int hOut, int wOut)
        {
            float[] dst = new float[batch * channels * hOut * wOut];
            float scaleY = (float)hIn / hOut;
            float scaleX = (float)wIn / wOut;
            int inPlane = hIn * wIn;
            int outPlane = hOut * wOut;

            for (int b = 0; b < batch; b++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int inOff = (b * channels + c) * inPlane;
                    int outOff = (b * channels + c) * outPlane;

                    for (int oy = 0; oy < hOut; oy++)
                    {
                        float iy = (oy + 0.5f) * scaleY - 0.5f;
                        int y0 = (int)Math.Floor(iy);
                        float wy = iy - y0;
                        int y0c = Math.Clamp(y0, 0, hIn - 1);
                        int y1c = Math.Clamp(y0 + 1, 0, hIn - 1);

                        for (int ox = 0; ox < wOut; ox++)
                        {
                            float ix = (ox + 0.5f) * scaleX - 0.5f;
                            int x0 = (int)Math.Floor(ix);
                            float wx = ix - x0;
                            int x0c = Math.Clamp(x0, 0, wIn - 1);
                            int x1c = Math.Clamp(x0 + 1, 0, wIn - 1);

                            float v00 = src[inOff + y0c * wIn + x0c];
                            float v01 = src[inOff + y0c * wIn + x1c];
                            float v10 = src[inOff + y1c * wIn + x0c];
                            float v11 = src[inOff + y1c * wIn + x1c];

                            float top = v00 + (v01 - v00) * wx;
                            float bot = v10 + (v11 - v10) * wx;

                            dst[outOff + oy * wOut + ox] = top + (bot - top) * wy;
                        }
                    }
                }
            }
            return dst;
        }
        public void LoadMetadata(string jsonPath)
        {
            string jsonText = File.ReadAllText(jsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (root.TryGetProperty("image_min", out var minElem))
                _imageMin = minElem.GetSingle();
            if (root.TryGetProperty("image_max", out var maxElem))
                _imageMax = maxElem.GetSingle();
        }
        private float[,] ComputeAnomalyScores(float[] features, int h, int w, int dim)
        {
            float[,] scores = new float[h, w];
            int numSpatialPoints = h * w;

            Parallel.For(0, numSpatialPoints, i =>
            {
                int r = i / w;
                int c = i % w;

                ReadOnlySpan<float> queryVec = new ReadOnlySpan<float>(features, i * dim, dim);
                float minDistance = float.MaxValue;

                for (int b = 0; b < _numPatches; b++)
                {
                    ReadOnlySpan<float> bankVec = new ReadOnlySpan<float>(_memoryBank, b * dim, dim);
                    float dist = System.Numerics.Tensors.TensorPrimitives.Distance(queryVec, bankVec);

                    if (dist < minDistance)
                    {
                        minDistance = dist;
                    }
                }

                // Normalize raw Euclidean distance to [0, 1] range matching Python Anomalib
                float normalizedScore = (minDistance - _imageMin) / ((_imageMax - _imageMin) + 1e-6f);
                scores[r, c] = Math.Clamp(normalizedScore, 0.0f, 1.0f);
            });

            return scores;
        }

        private static Mat NormalizePatchToBGR(Mat src)
        {
            Mat bgr = new Mat();
            if (src.Channels() == 1)
                Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            else if (src.Channels() == 4)
                Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
            else
                bgr = src.Clone();

            if (bgr.Width != Tile || bgr.Height != Tile)
            {
                Mat resized = new Mat();
                Cv2.Resize(bgr, resized, new Size(Tile, Tile));
                bgr.Dispose();
                bgr = resized;
            }

            if (!bgr.IsContinuous())
            {
                Mat continuousMat = bgr.Clone();
                bgr.Dispose();
                bgr = continuousMat;
            }

            return bgr;
        }

        private static TileLayout PreprocessToTiles(Mat bgr)
        {
            var layout = new TileLayout();

            if (!TryContentBoundingBox(bgr, out Rect bbox))
                return layout;

            layout.CropBox = bbox;
            using Mat crop = new(bgr, bbox);
            int h = crop.Rows, w = crop.Cols;

            if (h <= Tile && w <= Tile)
            {
                Mat canvas = Mat.Zeros(Tile, Tile, MatType.CV_8UC3);
                using (Mat dst = new(canvas, new Rect(0, 0, w, h)))
                    crop.CopyTo(dst);
                layout.Tiles.Add(canvas);
                layout.Offsets.Add((0, 0));
                layout.ScaledW = Tile;
                layout.ScaledH = Tile;
                return layout;
            }

            double scale = (double)Tile / Math.Min(h, w);
            int newW = Math.Max(1, (int)Math.Round(w * scale));
            int newH = Math.Max(1, (int)Math.Round(h * scale));
            layout.ScaledW = newW;
            layout.ScaledH = newH;

            using Mat resized = new();
            Cv2.Resize(crop, resized, new Size(newW, newH), interpolation: InterpolationFlags.Lanczos4);

            foreach (int ty in Starts(newH))
            {
                foreach (int tx in Starts(newW))
                {
                    int tw = Math.Min(Tile, newW - tx);
                    int th = Math.Min(Tile, newH - ty);

                    Mat tile;
                    using (Mat view = new(resized, new Rect(tx, ty, tw, th)))
                    {
                        if (tw == Tile && th == Tile)
                        {
                            tile = view.Clone();
                        }
                        else
                        {
                            tile = Mat.Zeros(Tile, Tile, MatType.CV_8UC3);
                            using Mat dst = new(tile, new Rect(0, 0, tw, th));
                            view.CopyTo(dst);
                        }
                    }

                    if (FillRatio(tile) >= MinFill)
                    {
                        layout.Tiles.Add(tile);
                        layout.Offsets.Add((tx, ty));
                    }
                    else
                    {
                        tile.Dispose();
                    }
                }
            }

            return layout;
        }

        private static Mat StitchHeatmap(TileLayout layout, float[][,] tileGrids, int gridH, int gridW, Size fullSize)
        {
            using Mat heatScaled = new(layout.ScaledH, layout.ScaledW, MatType.CV_32FC1, Scalar.All(0));

            for (int i = 0; i < layout.Tiles.Count; i++)
            {
                using Mat small = GridToMat(tileGrids[i], gridH, gridW);
                using Mat tileHeat = new();
                Cv2.Resize(small, tileHeat, new Size(Tile, Tile), interpolation: InterpolationFlags.Cubic);

                var (tx, ty) = layout.Offsets[i];
                int tw = Math.Min(Tile, layout.ScaledW - tx);
                int th = Math.Min(Tile, layout.ScaledH - ty);

                using Mat region = new(heatScaled, new Rect(tx, ty, tw, th));
                using Mat tileHeatRegion = new(tileHeat, new Rect(0, 0, tw, th));
                Cv2.Max(region, tileHeatRegion, region);
            }

            using Mat heatCrop = new();
            Cv2.Resize(heatScaled, heatCrop, new Size(layout.CropBox.Width, layout.CropBox.Height),
                interpolation: InterpolationFlags.Linear);

            Mat fullHeat = new(fullSize, MatType.CV_32FC1, Scalar.All(0));
            using (Mat dst = new(fullHeat, layout.CropBox))
                heatCrop.CopyTo(dst);

            return fullHeat;
        }

        private static Mat GridToMat(float[,] grid, int h, int w)
        {
            Mat m = new(h, w, MatType.CV_32FC1);
            float[] flat = new float[h * w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    flat[r * w + c] = grid[r, c];
            Marshal.Copy(flat, 0, m.Data, flat.Length);
            return m;
        }

        private static Mat ColorizeHeat(Mat heat32f)
        {
            Cv2.MinMaxLoc(heat32f, out double lo, out double hi);
            if (hi - lo < 1e-6) hi = lo + 1e-6;

            using Mat norm = new();
            heat32f.ConvertTo(norm, MatType.CV_8UC1, 255.0 / (hi - lo), -255.0 * lo / (hi - lo));

            Mat colored = new();
            Cv2.ApplyColorMap(norm, colored, ColormapTypes.Inferno);
            return colored;
        }

        private static double FillRatio(Mat bgrTile)
        {
            Mat[] ch = Cv2.Split(bgrTile);
            using Mat maxCh = new();
            Cv2.Max(ch[0], ch[1], maxCh);
            Cv2.Max(maxCh, ch[2], maxCh);
            foreach (var c in ch) c.Dispose();
            return (double)Cv2.CountNonZero(maxCh) / (bgrTile.Rows * bgrTile.Cols);
        }

        private static int[] Starts(int length)
        {
            if (length <= Tile) return new[] { 0 };
            var positions = new List<int>();
            for (int p = 0; p <= length - Tile; p += Tile)
                positions.Add(p);
            if (positions[^1] != length - Tile)
                positions.Add(length - Tile);
            return positions.ToArray();
        }

        private static bool TryContentBoundingBox(Mat bgr, out Rect box)
        {
            Mat[] ch = Cv2.Split(bgr);
            using Mat maxCh = new();
            Cv2.Max(ch[0], ch[1], maxCh);
            Cv2.Max(maxCh, ch[2], maxCh);
            foreach (var c in ch) c.Dispose();

            using Mat cont = maxCh.IsContinuous() ? maxCh : maxCh.Clone();
            int rows = cont.Rows, cols = cont.Cols;
            byte[] data = new byte[rows * cols];
            Marshal.Copy(cont.Data, data, 0, data.Length);

            int minX = cols, maxX = -1, minY = rows, maxY = -1;
            for (int y = 0; y < rows; y++)
            {
                int rowOff = y * cols;
                for (int x = 0; x < cols; x++)
                {
                    if (data[rowOff + x] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0)
            {
                box = default;
                return false;
            }
            box = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        private static void Trace(string msg)
        {
            Debug.WriteLine(msg);
            Console.WriteLine(msg);
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}