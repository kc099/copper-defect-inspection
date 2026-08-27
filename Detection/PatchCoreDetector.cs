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
using TP = System.Numerics.Tensors.TensorPrimitives;

namespace copperInspection.Detection
{
    /// <summary>
    /// C# PatchCore detector supporting two, genuinely different, training
    /// pipelines - selected automatically per backbone via its meta.json:
    ///
    /// - ResNet18 (no image_min/image_max in its meta.json): trained by the
    ///   hand-rolled D:\copper_bgsubtraction\copper\Train\sample_patchcore.py -
    ///   content-bbox crop, letterbox-or-tile to 256x256, plain "max raw
    ///   distance across all tiles" scoring. Untouched by the fix below.
    ///
    /// - ResNet50 (has image_min/image_max): trained by REAL Anomalib
    ///   (Train/trainer.py -> anomalib.models.Patchcore). Anomalib's own
    ///   pre-processor (Patchcore.configure_pre_processor in
    ///   anomalib/models/image/patchcore/lightning_model.py) is
    ///   torchvision Resize((256,256)) applied to the WHOLE frame - no
    ///   content-crop, no tiling, aspect ratio squashed if needed - and its
    ///   image-level score (PatchcoreModel.compute_anomaly_score in
    ///   torch_model.py) is NOT simply the worst patch's raw distance: with
    ///   num_neighbors=9 (this model's training config, Train/trainer.py) it
    ///   re-weights that distance by a softmax factor computed from the
    ///   local neighbourhood of the best-matching memory-bank patch.
    ///
    /// See docs/PATCHCORE_R50_FLOW_COMPARISON.md and docs/WORK_LOG.md for the
    /// full investigation this is built from - this file was accidentally
    /// reverted once already by copying an older commit over it; don't do
    /// that again without checking those docs first.
    /// </summary>
    public class PatchCoreDetector : IDisposable
    {
        private const int Tile = 256;
        private const double MinFill = 0.10;

        /// <summary>Anomalib's Train/trainer.py trains this backbone with
        /// num_neighbors=9. Not (yet) recorded in meta.json, so hardcoded
        /// here - if a future export uses a different value, add it to
        /// meta.json and read it in LoadMetadata instead of changing this.</summary>
        private const int NumNeighbors = 9;

        private readonly InferenceSession _session;
        private float[] _memoryBank = Array.Empty<float>();
        private int _numPatches;
        private int _featureDim;

        private float _imageMin;
        private float _imageMax;
        private bool _hasMinMax;
        private bool _isGrayscale;

        /// <summary>True for a backbone trained by real Anomalib (its
        /// meta.json carries image_min/image_max) - selects the
        /// single-whole-frame-resize preprocessing and the num_neighbors
        /// reweighted score, instead of ResNet18's content-crop/tile/
        /// plain-max pipeline.</summary>
        private bool _useAnomalibPipeline;

        /// <summary>Raw threshold as written in the meta.json, in whatever units
        /// the backbone's raw distances are in. Exposed for display/logging;
        /// the actual GOOD/BAD decision uses <c>_normalizedThreshold</c>, which
        /// is this same value put through the identical transform applied to
        /// the final image-level score - so the decision is exactly equivalent
        /// to comparing the two raw values, just expressed in whichever units
        /// the score ends up displayed in.</summary>
        public float Threshold { get; private set; }
        private float _normalizedThreshold;

        /// <summary>
        /// The exact background-subtraction settings this model's training
        /// images were built with (recorded at training time by
        /// patchcore_gui_app.py's SegmentThenTrainRunner, carried through by
        /// export_patchcore_to_onnx.py into meta.json's "segmentation"
        /// object). Null for a model trained before this existed, trained
        /// with segmentation mode "None", or a legacy (non-Anomalib) export.
        /// MainWindow applies this automatically instead of relying on
        /// whatever the operator last had in the UI - that manual-sync gap
        /// is what produced wrong verdicts more than once. See
        /// docs/PATCHCORE_R50_FLOW_COMPARISON.md.
        /// </summary>
        public sealed class RecordedSegmentationSettings
        {
            public string Mode { get; init; } = "";  // "Silhouette" | "ReferenceDiff" | "None"
            public bool? Flatten { get; init; }
            public bool? AutoThreshold { get; init; }
            public int? Threshold { get; init; }
            public int? Morph { get; init; }
            public int? Keep { get; init; }
            public bool? Fill { get; init; }
            public int? DiffBlur { get; init; }
            public int? DiffThreshold { get; init; }
            public int? DiffMorph { get; init; }
            public int? DiffKeep { get; init; }
            public bool? DiffMatchExposure { get; init; }
        }

        public RecordedSegmentationSettings? SegmentationSettings { get; private set; }

        private sealed class TileLayout
        {
            public List<Mat> Tiles { get; } = new();
            public List<(int Tx, int Ty)> Offsets { get; } = new();
            public int ScaledW, ScaledH;
            public Rect CropBox;
        }

        public PatchCoreDetector(string onnxPath, string bankBinPath, string metaPath)
        {
            var sw = Stopwatch.StartNew();
            Trace("[PatchCore Profile] Loading Detector...");

            LoadMetadata(metaPath);

            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            _session = new InferenceSession(onnxPath, options);
            LoadMemoryBank(bankBinPath);

            sw.Stop();
            Trace($"[PatchCore Profile] Initialization complete in {sw.ElapsedMilliseconds} ms");
            Trace($"[PatchCore Profile] threshold={Threshold} hasMinMax={_hasMinMax} " +
                  $"imageMin={_imageMin} imageMax={_imageMax} isGrayscale={_isGrayscale} " +
                  $"useAnomalibPipeline={_useAnomalibPipeline} normalizedThreshold={_normalizedThreshold}");
        }

        /// <summary>
        /// Reads threshold / image_min / image_max / is_grayscale from the
        /// backbone's meta.json. image_min/image_max being present is also
        /// what selects the Anomalib preprocessing+scoring pipeline (see
        /// class remarks) - a backbone without them (ResNet18) is scored and
        /// thresholded entirely in raw Euclidean-distance units, unchanged.
        /// </summary>
        private void LoadMetadata(string jsonPath)
        {
            string jsonText = File.ReadAllText(jsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            Threshold = root.TryGetProperty("threshold", out var t) ? t.GetSingle() : 0.5f;

            bool hasMin = root.TryGetProperty("image_min", out var minElem);
            bool hasMax = root.TryGetProperty("image_max", out var maxElem);
            _hasMinMax = hasMin && hasMax;
            _useAnomalibPipeline = _hasMinMax;

            if (_hasMinMax)
            {
                _imageMin = minElem.GetSingle();
                _imageMax = maxElem.GetSingle();
                float range = (_imageMax - _imageMin) + 1e-6f;
                _normalizedThreshold = Math.Clamp((Threshold - _imageMin) / range, 0f, 1f);
            }
            else
            {
                _imageMin = 0f;
                _imageMax = 0f;
                _normalizedThreshold = Threshold;
            }

            _isGrayscale = root.TryGetProperty("is_grayscale", out var g) && g.GetBoolean();

            SegmentationSettings = null;
            if (root.TryGetProperty("segmentation", out var seg))
            {
                SegmentationSettings = new RecordedSegmentationSettings
                {
                    Mode = seg.TryGetProperty("mode", out var mode) ? (mode.GetString() ?? "") : "",
                    Flatten = seg.TryGetProperty("flatten", out var fl) ? fl.GetBoolean() : null,
                    AutoThreshold = seg.TryGetProperty("auto_threshold", out var at) ? at.GetBoolean() : null,
                    Threshold = seg.TryGetProperty("threshold", out var th) ? th.GetInt32() : null,
                    Morph = seg.TryGetProperty("morph", out var mo) ? mo.GetInt32() : null,
                    Keep = seg.TryGetProperty("keep", out var kp) ? kp.GetInt32() : null,
                    Fill = seg.TryGetProperty("fill", out var fi) ? fi.GetBoolean() : null,
                    DiffBlur = seg.TryGetProperty("diff_blur", out var db) ? db.GetInt32() : null,
                    DiffThreshold = seg.TryGetProperty("diff_threshold", out var dt) ? dt.GetInt32() : null,
                    DiffMorph = seg.TryGetProperty("diff_morph", out var dm) ? dm.GetInt32() : null,
                    DiffKeep = seg.TryGetProperty("diff_keep", out var dk) ? dk.GetInt32() : null,
                    DiffMatchExposure = seg.TryGetProperty("diff_match_exposure", out var dme) ? dme.GetBoolean() : null,
                };
            }
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

            // Step 1: preprocessing - the two pipelines diverge here (see
            // class remarks). Anomalib: one whole-frame resize, no crop, no
            // tiling. Legacy (ResNet18): content-crop + letterbox/tile.
            List<Mat> patches;
            TileLayout? tiledLayout = null;

            if (_useAnomalibPipeline)
            {
                patches = new List<Mat> { PreprocessSingleResize(croppedContent) };
            }
            else
            {
                tiledLayout = PreprocessToTiles(croppedContent);
                patches = tiledLayout.Tiles;
            }
            stepSw.Stop();
            long patchExtractionMs = stepSw.ElapsedMilliseconds;

            if (patches.Count == 0)
            {
                Mat blank = new(croppedContent.Size(), MatType.CV_8UC3, Scalar.All(0));
                Mat blankOnOriginal = (overlayBase ?? croppedContent).Clone();
                return (false, 0.0f, blank, blankOnOriginal);
            }

            try
            {
                // Step 2: Batch ONNX Feature Extraction
                stepSw.Restart();
                List<float[]> featureMaps = ExtractFeaturesBatch(patches, out int gridH, out int gridW);
                stepSw.Stop();
                long onnxInferenceMs = stepSw.ElapsedMilliseconds;

                // Step 3: nearest-neighbour distance search, per tile/patch.
                // Keeps each patch's raw distance grid (for the heatmap) AND
                // which memory-bank vector it matched (needed for the
                // Anomalib reweighting step below).
                stepSw.Restart();
                var scoreGrids = new float[patches.Count][,];
                var nnIndexGrids = new int[patches.Count][,];
                float rawMaxScore = 0f;
                int bestTile = 0, bestR = 0, bestC = 0;

                for (int tIdx = 0; tIdx < patches.Count; tIdx++)
                {
                    var (scores, nnIdx) = ComputeAnomalyScores(featureMaps[tIdx], gridH, gridW, _featureDim);
                    scoreGrids[tIdx] = scores;
                    nnIndexGrids[tIdx] = nnIdx;

                    for (int r = 0; r < gridH; r++)
                        for (int c = 0; c < gridW; c++)
                            if (scores[r, c] > rawMaxScore)
                            {
                                rawMaxScore = scores[r, c];
                                bestTile = tIdx; bestR = r; bestC = c;
                            }
                }
                stepSw.Stop();
                long distanceSearchMs = stepSw.ElapsedMilliseconds;

                // Step 4: image-level score. Anomalib's num_neighbours
                // softmax reweighting for the Anomalib-trained backbone
                // (matches PatchcoreModel.compute_anomaly_score exactly);
                // the plain worst-patch distance for the legacy backbone
                // (matches sample_patchcore.py, which has no such step).
                float finalRawScore = rawMaxScore;
                if (_useAnomalibPipeline)
                {
                    int bestNnIndex = nnIndexGrids[bestTile][bestR, bestC];
                    var worstFeatures = new ReadOnlySpan<float>(
                        featureMaps[bestTile], (bestR * gridW + bestC) * _featureDim, _featureDim);
                    finalRawScore = ReweightScore(worstFeatures, bestNnIndex, rawMaxScore);
                }

                totalSw.Stop();
                Trace("==========================================================");
                Trace($"[PatchCore Profile] Total Inspection Time : {totalSw.ElapsedMilliseconds} ms");
                Trace($"  ├── 1. Preprocess ({patches.Count} patch(es))    : {patchExtractionMs} ms");
                Trace($"  ├── 2. ONNX Inference                    : {onnxInferenceMs} ms");
                Trace($"  └── 3. Distance Search                    : {distanceSearchMs} ms");
                Trace($"[PatchCore Profile] rawMaxScore={rawMaxScore} finalRawScore={finalRawScore} " +
                      $"threshold={Threshold} normalizedThreshold={_normalizedThreshold}");
                Trace("==========================================================");

                // Normalize the FINAL image-level score exactly once (matching
                // where Anomalib's own min-max normalizer sits - on pred_score,
                // not on individual patches) and compare it against the
                // threshold run through the identical transform - equivalent
                // to comparing the two raw values directly either way.
                float displayScore = _hasMinMax
                    ? Math.Clamp((finalRawScore - _imageMin) / ((_imageMax - _imageMin) + 1e-6f), 0f, 1f)
                    : finalRawScore;
                bool isDefect = displayScore > _normalizedThreshold;

                // Step 5: heatmap - single direct upsample for the Anomalib
                // path (no tiles to stitch), tile-stitching for the legacy path.
                Mat fullHeatFloat = _useAnomalibPipeline
                    ? BuildSingleTileHeatmap(scoreGrids[0], gridH, gridW, croppedContent.Size())
                    : StitchHeatmap(tiledLayout!, scoreGrids, gridH, gridW, croppedContent.Size());

                Mat heatmapRaw;
                using (fullHeatFloat)
                {
                    heatmapRaw = ColorizeHeat(fullHeatFloat);
                }

                Mat baseImg = overlayBase ?? croppedContent;
                Mat heatmapOnOriginal = new();
                Cv2.AddWeighted(baseImg, 0.5, heatmapRaw, 0.5, 0, heatmapOnOriginal);

                return (isDefect, displayScore, heatmapRaw, heatmapOnOriginal);
            }
            finally
            {
                foreach (var p in patches) p.Dispose();
            }
        }

        /// <summary>
        /// Ports PatchcoreModel.compute_anomaly_score's num_neighbors>1
        /// branch (anomalib/models/image/patchcore/torch_model.py): re-weighs
        /// the worst patch's raw nearest-neighbour distance by how "isolated"
        /// its best memory-bank match is relative to that match's OWN
        /// neighbourhood within the bank. Concretely: find the best match's
        /// own k nearest neighbours in the bank (support samples - it is
        /// always support[0], at distance 0 to itself), measure the TEST
        /// patch's distance to each of those k support samples, and weight
        /// the raw score by `1 - softmax(those k distances)[0]`.
        /// </summary>
        private float ReweightScore(ReadOnlySpan<float> worstPatchFeatures, int nnIndex, float rawScore)
        {
            int k = Math.Min(NumNeighbors, _numPatches);
            if (k <= 1) return rawScore;

            var nnSample = new float[_featureDim];
            Array.Copy(_memoryBank, nnIndex * _featureDim, nnSample, 0, _featureDim);

            // nnIndex's own k nearest neighbours WITHIN the memory bank.
            // The bank here is small (coreset-subsampled), so a brute-force
            // sort over it is negligible next to the main per-patch search.
            var byDistance = new (float dist, int idx)[_numPatches];
            Parallel.For(0, _numPatches, b =>
            {
                var bankVec = new ReadOnlySpan<float>(_memoryBank, b * _featureDim, _featureDim);
                byDistance[b] = (TP.Distance(nnSample, bankVec), b);
            });
            Array.Sort(byDistance, (a, b) => a.dist.CompareTo(b.dist));

            // Distance from the TEST patch (not nnSample) to each support sample.
            var distances = new float[k];
            for (int i = 0; i < k; i++)
            {
                var support = new ReadOnlySpan<float>(_memoryBank, byDistance[i].idx * _featureDim, _featureDim);
                distances[i] = TP.Distance(worstPatchFeatures, support);
            }

            float maxD = distances[0];
            for (int i = 1; i < k; i++) if (distances[i] > maxD) maxD = distances[i];
            float sumExp = 0f;
            for (int i = 0; i < k; i++) sumExp += MathF.Exp(distances[i] - maxD);
            float softmax0 = MathF.Exp(distances[0] - maxD) / sumExp;
            float weight = 1f - softmax0;

            return weight * rawScore;
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

                        if (_isGrayscale)
                        {
                            // Convert RGB to Greyscale R=G=B matching Python training
                            float gray = (red * 0.299f + green * 0.587f + blue * 0.114f) / 255.0f;
                            batchTensor[b, 0, y, x] = (gray - mean[0]) / std[0];
                            batchTensor[b, 1, y, x] = (gray - mean[1]) / std[1];
                            batchTensor[b, 2, y, x] = (gray - mean[2]) / std[2];
                        }
                        else
                        {
                            batchTensor[b, 0, y, x] = ((red / 255.0f) - mean[0]) / std[0];   // R
                            batchTensor[b, 1, y, x] = ((green / 255.0f) - mean[1]) / std[1]; // G
                            batchTensor[b, 2, y, x] = ((blue / 255.0f) - mean[2]) / std[2];  // B
                        }
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

        /// <summary>
        /// Matches PatchcoreModel.forward + generate_embedding: BOTH layers
        /// go through the 3x3 AvgPool2d(3,1,1) feature_pooler FIRST, THEN
        /// layer4 is bilinear-upsampled onto layer2's grid and concatenated.
        /// </summary>
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

            // 1. Zero-padded 3x3 Average Pool matching Anomalib's feature_pooler
            float[] l2Smooth = Smooth3x3(l2Flat, batchSize, c2, gridH, gridW);
            float[] l4Smooth = Smooth3x3(l4Flat, batchSize, c4, l4H, l4W);

            // 2. Bilinear upsample layer4 to match layer2 grid size
            float[] l4Aligned = (l4H == gridH && l4W == gridW)
                ? l4Smooth
                : BilinearResize(l4Smooth, batchSize, c4, l4H, l4W, gridH, gridW);

            List<float[]> patchFeatureMaps = new List<float[]>(batchSize);

            int patchSpatialSize = gridH * gridW;
            int l2BatchOffset = c2 * patchSpatialSize;
            int l4BatchOffset = c4 * patchSpatialSize;

            for (int b = 0; b < batchSize; b++)
            {
                float[] patchConcat = new float[patchSpatialSize * totalDim];
                int bL2Start = b * l2BatchOffset;
                int bL4Start = b * l4BatchOffset;

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

        /// <summary>Per-patch nearest-neighbour distance AND which memory-bank
        /// vector achieved it (needed for the Anomalib reweighting step).
        /// Always raw Euclidean distance - normalization happens once, on the
        /// final image-level score, not per patch (see Inspect).</summary>
        private (float[,] Scores, int[,] NnIndices) ComputeAnomalyScores(float[] features, int h, int w, int dim)
        {
            float[,] scores = new float[h, w];
            int[,] nnIndices = new int[h, w];
            int numSpatialPoints = h * w;

            Parallel.For(0, numSpatialPoints, i =>
            {
                int r = i / w;
                int c = i % w;

                ReadOnlySpan<float> queryVec = new ReadOnlySpan<float>(features, i * dim, dim);
                float minDistance = float.MaxValue;
                int minIndex = 0;

                for (int b = 0; b < _numPatches; b++)
                {
                    ReadOnlySpan<float> bankVec = new ReadOnlySpan<float>(_memoryBank, b * dim, dim);
                    float dist = TP.Distance(queryVec, bankVec);

                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        minIndex = b;
                    }
                }

                scores[r, c] = minDistance;
                nnIndices[r, c] = minIndex;
            });

            return (scores, nnIndices);
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

        /// <summary>
        /// Anomalib's actual pre-processor for this model
        /// (Patchcore.configure_pre_processor, lightning_model.py): torchvision
        /// Resize((256,256), antialias=True) applied to the WHOLE frame - no
        /// content-crop, no tiling, aspect ratio squashed if the source isn't
        /// square. The antialias=True matters a lot here: PatchCore's own
        /// ImageLoader.LoadFull feeds this a NATIVE-resolution photo (often
        /// ~4000px), so this is commonly a ~16x downscale - plain fixed-support
        /// bilinear (Cv2.Resize's default) aliases badly at that ratio.
        /// Confirmed empirically: for one test image, that inflated the raw
        /// anomaly score from 3.89 (correct) to 9.05 - enough to push every
        /// borderline-good image over this model's narrow threshold, since its
        /// calibrated GOOD band was only ~0.4 units wide to begin with.
        /// AntialiasedResize below replicates torchvision's real algorithm
        /// (separable triangle filter, support scaled by the downsample ratio -
        /// the same thing Pillow's BILINEAR resize does), not just a "closer"
        /// OpenCV flag: Cv2.Resize's INTER_AREA was tried and measured too -
        /// still ~0.5-1.0 off given how thin this model's margin is.
        /// </summary>
        private static Mat PreprocessSingleResize(Mat bgr)
        {
            return AntialiasedResize(bgr, Tile, Tile);
        }

        /// <summary>
        /// Separable antialiased (triangle-filter) resize matching
        /// torchvision/Pillow's antialias=True bilinear resize - see
        /// PreprocessSingleResize's remarks for why this exists instead of
        /// Cv2.Resize. For each output pixel, this is a normalized weighted
        /// sum of input pixels within a triangle kernel whose support widens
        /// proportionally to the downscale ratio (no widening when upsampling).
        /// Validated against the real torchvision output on real test images:
        /// within ~2-3% of the reference raw score, versus ~130% off for plain
        /// bilinear and ~15-25% off for INTER_AREA.
        /// </summary>
        private static Mat AntialiasedResize(Mat srcBgr, int outW, int outH)
        {
            int inW = srcBgr.Cols, inH = srcBgr.Rows;
            const int channels = 3;

            // NOT a `using` on srcBgr itself: when it's already continuous,
            // cont would alias the CALLER's Mat, and disposing it here would
            // dispose that Mat out from under the caller (this exact bug
            // produced "Cannot access a disposed object" on croppedContent
            // later in Inspect(), since croppedContent is typically
            // continuous). Only dispose the clone we ourselves allocated.
            bool ownsCont = !srcBgr.IsContinuous();
            Mat cont = ownsCont ? srcBgr.Clone() : srcBgr;
            byte[] srcBytes;
            try
            {
                srcBytes = new byte[inH * inW * channels];
                Marshal.Copy(cont.Data, srcBytes, 0, srcBytes.Length);
            }
            finally
            {
                if (ownsCont) cont.Dispose();
            }

            var (leftX, wX) = ComputeAxisWeights(inW, outW);
            var (leftY, wY) = ComputeAxisWeights(inH, outH);

            // Pass 1: horizontal resize (inW -> outW), height stays inH.
            float[] temp = new float[inH * outW * channels];
            Parallel.For(0, inH, y =>
            {
                int srcRowOff = y * inW * channels;
                int dstRowOff = y * outW * channels;
                for (int ox = 0; ox < outW; ox++)
                {
                    float[] w = wX[ox];
                    int l = leftX[ox];
                    float b = 0, g = 0, r = 0;
                    for (int k = 0; k < w.Length; k++)
                    {
                        int srcIdx = srcRowOff + (l + k) * channels;
                        float wk = w[k];
                        b += srcBytes[srcIdx] * wk;
                        g += srcBytes[srcIdx + 1] * wk;
                        r += srcBytes[srcIdx + 2] * wk;
                    }
                    int dstIdx = dstRowOff + ox * channels;
                    temp[dstIdx] = b; temp[dstIdx + 1] = g; temp[dstIdx + 2] = r;
                }
            });

            // Pass 2: vertical resize (inH -> outH), width stays outW.
            byte[] outBytes = new byte[outH * outW * channels];
            Parallel.For(0, outH, oy =>
            {
                float[] w = wY[oy];
                int l = leftY[oy];
                int dstRowOff = oy * outW * channels;
                for (int ox = 0; ox < outW; ox++)
                {
                    float b = 0, g = 0, r = 0;
                    for (int k = 0; k < w.Length; k++)
                    {
                        int srcIdx = (l + k) * outW * channels + ox * channels;
                        float wk = w[k];
                        b += temp[srcIdx] * wk;
                        g += temp[srcIdx + 1] * wk;
                        r += temp[srcIdx + 2] * wk;
                    }
                    int dstIdx = dstRowOff + ox * channels;
                    outBytes[dstIdx] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
                    outBytes[dstIdx + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                    outBytes[dstIdx + 2] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                }
            });

            Mat result = new(outH, outW, MatType.CV_8UC3);
            Marshal.Copy(outBytes, 0, result.Data, outBytes.Length);
            return result;
        }

        /// <summary>Per-axis triangle-filter weights for AntialiasedResize:
        /// for each output index, which input indices contribute (starting at
        /// Left[i]) and their normalized weights.</summary>
        private static (int[] Left, float[][] Weights) ComputeAxisWeights(int inSize, int outSize)
        {
            double scale = (double)inSize / outSize;
            double filterScale = Math.Max(scale, 1.0); // only widen support when downsampling
            double support = filterScale;              // triangle kernel half-width = 1.0, scaled

            var lefts = new int[outSize];
            var weightsList = new float[outSize][];

            for (int i = 0; i < outSize; i++)
            {
                double center = (i + 0.5) * scale;
                int left = (int)Math.Floor(center - support);
                int right = (int)Math.Ceiling(center + support);
                left = Math.Max(left, 0);
                right = Math.Min(right, inSize);
                if (right <= left) right = Math.Min(left + 1, inSize);

                int count = right - left;
                var w = new float[count];
                double sum = 0;
                for (int k = 0; k < count; k++)
                {
                    int j = left + k;
                    double x = (j + 0.5 - center) / filterScale;
                    double tri = Math.Max(0.0, 1.0 - Math.Abs(x));
                    w[k] = (float)tri;
                    sum += tri;
                }
                if (sum > 1e-8)
                    for (int k = 0; k < count; k++) w[k] = (float)(w[k] / sum);
                else
                    w[0] = 1f;

                lefts[i] = left;
                weightsList[i] = w;
            }
            return (lefts, weightsList);
        }

        /// <summary>
        /// Legacy (ResNet18 / sample_patchcore.py) preprocessing: crop to the
        /// bounding box of non-black content, then either letterbox it onto
        /// one Tile x Tile canvas if it already fits, or scale so the short
        /// side is Tile and slide non-overlapping (but edge-inclusive) tiles
        /// down the long axis - dropping any tile that's still mostly black
        /// (MinFill). Unrelated to and unused by the Anomalib pipeline above.
        /// </summary>
        private static TileLayout PreprocessToTiles(Mat bgr)
        {
            var layout = new TileLayout();

            if (!TryContentBoundingBox(bgr, out Rect bbox))
                return layout; // fully black - nothing to score

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

        /// <summary>Single-patch heatmap: the whole image was one resized
        /// tile, so placing its score grid back is a straight upsample - no
        /// tile stitching needed (compare StitchHeatmap, used by the legacy
        /// multi-tile pipeline).</summary>
        private static Mat BuildSingleTileHeatmap(float[,] grid, int gridH, int gridW, Size fullSize)
        {
            using Mat small = GridToMat(grid, gridH, gridW);
            Mat full = new();
            Cv2.Resize(small, full, fullSize, interpolation: InterpolationFlags.Cubic);
            return full;
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

            bool ownsCont = !maxCh.IsContinuous();
            Mat cont = ownsCont ? maxCh.Clone() : maxCh;
            int rows = cont.Rows, cols = cont.Cols;
            byte[] data = new byte[rows * cols];
            try
            {
                Marshal.Copy(cont.Data, data, 0, data.Length);
            }
            finally
            {
                if (ownsCont) cont.Dispose();
            }

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
