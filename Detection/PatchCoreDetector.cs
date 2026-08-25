using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors; // Uses ONNX DenseTensor natively
using OpenCvSharp;
namespace copperInspection.Detection
{
    public class PatchCoreDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private float[] _memoryBank = Array.Empty<float>();
        private int _numPatches;
        private int _featureDim;
        public float Threshold { get; private set; }

        public PatchCoreDetector(string onnxPath, string bankBinPath, float threshold)
        {
            var sw = Stopwatch.StartNew();
            Trace("[PatchCore Profile] Loading Detector...");

            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            _session = new InferenceSession(onnxPath, options);
            Threshold = threshold;
            LoadMemoryBank(bankBinPath);

            sw.Stop();
            Trace($"[PatchCore Profile] Initialization total time: {sw.ElapsedMilliseconds} ms");
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
        }

        public (bool IsDefect, float Score, Mat Heatmap) Inspect(Mat croppedContent)
        {
            if (croppedContent == null || croppedContent.Empty())
                return (false, 0.0f, new Mat());

            var totalSw = Stopwatch.StartNew();
            var stepSw = Stopwatch.StartNew();

            // Step 1: Patch Extraction
            List<Mat> patches = Extract256Patches(croppedContent);
            stepSw.Stop();
            long patchExtractionMs = stepSw.ElapsedMilliseconds;

            if (patches.Count == 0)
                return (false, 0.0f, new Mat(croppedContent.Size(), MatType.CV_8UC3, Scalar.All(0)));

            try
            {
                // Step 2: Batch ONNX Feature Extraction
                stepSw.Restart();
                List<float[]> featureMaps = ExtractFeaturesBatch(patches, out int gridH, out int gridW);
                stepSw.Stop();
                long onnxInferenceMs = stepSw.ElapsedMilliseconds;

                // Step 3: Multi-Threaded Anomaly Scoring
                stepSw.Restart();
                float maxImageScore = 0.0f;
                object scoreLock = new object();

                Parallel.For(0, patches.Count, patchIdx =>
                {
                    float[,] anomalyScores = ComputeAnomalyScores(featureMaps[patchIdx], gridH, gridW, _featureDim);

                    float localMax = 0.0f;
                    for (int r = 0; r < gridH; r++)
                    {
                        for (int c = 0; c < gridW; c++)
                        {
                            if (anomalyScores[r, c] > localMax)
                                localMax = anomalyScores[r, c];
                        }
                    }

                    lock (scoreLock)
                    {
                        if (localMax > maxImageScore)
                            maxImageScore = localMax;
                    }
                });
                stepSw.Stop();
                long distanceSearchMs = stepSw.ElapsedMilliseconds;

                totalSw.Stop();

                // Log timing summary
                Trace("==========================================================");
                Trace($"[PatchCore Profile] Total Inspection Time : {totalSw.ElapsedMilliseconds} ms");
                Trace($"  ├── 1. Patch Extraction ({patches.Count} patches)  : {patchExtractionMs} ms");
                Trace($"  ├── 2. ONNX Batch Inference            : {onnxInferenceMs} ms");
                Trace($"  └── 3. Parallel Distance Search         : {distanceSearchMs} ms");
                Trace("==========================================================");

                bool isDefect = maxImageScore > Threshold;
                Mat visualHeatmap = new Mat(croppedContent.Size(), MatType.CV_8UC3, Scalar.All(0));

                return (isDefect, maxImageScore, visualHeatmap);
            }
            finally
            {
                foreach (var p in patches) p.Dispose();
            }
        }

        private List<float[]> ExtractFeaturesBatch(List<Mat> patches, out int gridH, out int gridW)
        {
            int batchSize = patches.Count;
            var batchTensor = new DenseTensor<float>(new[] { batchSize, 3, 256, 256 });

            float[] mean = { 0.485f, 0.456f, 0.406f };
            float[] std = { 0.229f, 0.224f, 0.225f };

            for (int b = 0; b < batchSize; b++)
            {
                using Mat bgr = NormalizePatchToBGR(patches[b]);
                byte[] rawBytes = new byte[256 * 256 * 3];
                System.Runtime.InteropServices.Marshal.Copy(bgr.Data, rawBytes, 0, rawBytes.Length);

                for (int y = 0; y < 256; y++)
                {
                    int rowOffset = y * 256 * 3;
                    for (int x = 0; x < 256; x++)
                    {
                        int pixelIdx = rowOffset + (x * 3);
                        byte blue = rawBytes[pixelIdx];
                        byte green = rawBytes[pixelIdx + 1];
                        byte red = rawBytes[pixelIdx + 2];

                        batchTensor[b, 0, y, x] = ((red / 255.0f) - mean[0]) / std[0];   // R
                        batchTensor[b, 1, y, x] = ((green / 255.0f) - mean[1]) / std[1]; // G
                        batchTensor[b, 2, y, x] = ((blue / 255.0f) - mean[2]) / std[2];  // B
                    }
                }
            }

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", batchTensor) };
            using var results = _session.Run(inputs);

            var l2Tensor = results.First(r => r.Name == "layer2").AsTensor<float>();
            var l3Tensor = results.First(r => r.Name == "layer3").AsTensor<float>();

            return SplitBatchAndAlign(l2Tensor, l3Tensor, batchSize, out gridH, out gridW);
        }

        private static List<float[]> SplitBatchAndAlign(Tensor<float> l2, Tensor<float> l3, int batchSize, out int gridH, out int gridW)
        {
            gridH = l2.Dimensions[2];
            gridW = l2.Dimensions[3];
            int c2 = l2.Dimensions[1];
            int c3 = l3.Dimensions[1];
            int totalDim = c2 + c3;

            int l3H = l3.Dimensions[2];
            int l3W = l3.Dimensions[3];

            float[] l2Flat = l2.ToArray();
            float[] l3Flat = l3.ToArray();

            List<float[]> patchFeatureMaps = new List<float[]>(batchSize);

            int patchSpatialSize = gridH * gridW;
            int l2BatchOffset = c2 * patchSpatialSize;
            int l3BatchOffset = c3 * l3H * l3W;

            for (int b = 0; b < batchSize; b++)
            {
                float[] patchConcat = new float[patchSpatialSize * totalDim];
                int bL2Start = b * l2BatchOffset;
                int bL3Start = b * l3BatchOffset;

                for (int r = 0; r < gridH; r++)
                {
                    for (int c = 0; c < gridW; c++)
                    {
                        int spatialIdx = (r * gridW + c) * totalDim;

                        for (int ch = 0; ch < c2; ch++)
                        {
                            int idx = bL2Start + (ch * patchSpatialSize) + (r * gridW) + c;
                            patchConcat[spatialIdx + ch] = l2Flat[idx];
                        }

                        int l3R = Math.Min((int)((float)r / gridH * l3H), l3H - 1);
                        int l3C = Math.Min((int)((float)c / gridW * l3W), l3W - 1);

                        for (int ch = 0; ch < c3; ch++)
                        {
                            int idx = bL3Start + (ch * l3H * l3W) + (l3R * l3W) + l3C;
                            patchConcat[spatialIdx + c2 + ch] = l3Flat[idx];
                        }
                    }
                }

                patchFeatureMaps.Add(patchConcat);
            }

            return patchFeatureMaps;
        }

        private float[,] ComputeAnomalyScores(float[] features, int h, int w, int dim)
        {
            float[,] scores = new float[h, w];
            int numSpatialPoints = h * w;

            // SIMD accelerated parallel search
            Parallel.For(0, numSpatialPoints, i =>
            {
                int r = i / w;
                int c = i % w;

                // Memory span for current query spatial vector
                ReadOnlySpan<float> queryVec = new ReadOnlySpan<float>(features, i * dim, dim);

                float minSqDistance = float.MaxValue;

                for (int b = 0; b < _numPatches; b++)
                {
                    ReadOnlySpan<float> bankVec = new ReadOnlySpan<float>(_memoryBank, b * dim, dim);

                    // SIMD-optimized Euclidean Distance via TensorPrimitives
                    float dist = System.Numerics.Tensors.TensorPrimitives.Distance(queryVec, bankVec);

                    if (dist < minSqDistance)
                    {
                        minSqDistance = dist;
                    }
                }

                scores[r, c] = minSqDistance;
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

            if (bgr.Width != 256 || bgr.Height != 256)
            {
                Mat resized = new Mat();
                Cv2.Resize(bgr, resized, new Size(256, 256));
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

        private static List<Mat> Extract256Patches(Mat src)
        {
            List<Mat> patches = new List<Mat>();
            if (src.Empty()) return patches;

            float scale = 256.0f / Math.Min(src.Width, src.Height);
            int newW = Math.Max(256, (int)(src.Width * scale));
            int newH = Math.Max(256, (int)(src.Height * scale));

            using Mat resized = new Mat();
            Cv2.Resize(src, resized, new Size(newW, newH));

            for (int y = 0; y <= newH - 256; y += 256)
            {
                for (int x = 0; x <= newW - 256; x += 256)
                {
                    Rect roi = new Rect(x, y, 256, 256);
                    patches.Add(new Mat(resized, roi).Clone());
                }
            }

            return patches;
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