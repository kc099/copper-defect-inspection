using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace copperInspection
{
    /// <summary>
    /// Everything about HOW the pipeline runs - which background-subtraction
    /// method, which detection method, and their parameters - as one plain,
    /// hand-editable JSON file next to the .exe. No rebuild needed to change a
    /// default; open config.json, change a number, restart the app.
    ///
    /// This mirrors the config-file pattern already used on the Python side
    /// (color_diff_inspector.py's reference_color.json,
    /// sample_patchcore.py's checkpoint metadata) - same idea, applied here.
    /// </summary>
    public sealed class PipelineConfig
    {
        // ── Background subtraction ──────────────────────────────────────────
        /// <summary>"Silhouette" (one image) or "ReferenceDiff" (two images).</summary>
        public string BackgroundMethod { get; set; } = "Silhouette";

        public bool FlattenLighting { get; set; } = true;
        public bool AutoThreshold { get; set; } = true;
        public int SilhouetteThreshold { get; set; } = 60;
        public int RegionsToKeep { get; set; } = 2;

        /// <summary>Which side of the frame the mirror region sits on, same
        /// convention as bg_subtraction_app.py's "Mirror is on" setting -
        /// the fixture doesn't move, so this is a fixed fact about the rig,
        /// not something re-derived per photo. Currently informational only:
        /// Phase 1 keeps the N largest regions as one combined mask and
        /// doesn't split them apart, so nothing reads this yet - it's here
        /// so the value is set correctly once a step that needs it (e.g. a
        /// top/mirror split, or per-side scoring) is added.</summary>
        public bool MirrorOnLeft { get; set; } = true;

        public int SilhouetteMorphKernel { get; set; } = 5;
        public bool FillHoles { get; set; } = true;

        public int DiffBlur { get; set; } = 5;
        public int DiffThreshold { get; set; } = 20;
        public int DiffMorphKernel { get; set; } = 5;
        public bool MatchExposure { get; set; } = true;
        public bool AllowResize { get; set; } = false;

        // ── Detection ────────────────────────────────────────────────────────
        /// <summary>"ColorDiff", "PatchCore-R50", or "PatchCore-R18".</summary>
        public string DetectionMethod { get; set; } = "ColorDiff";

        public double ColorDiffThreshold { get; set; } = 80.0;
        public double ColorDiffMinAreaPct { get; set; } = 0.5;
        public int ColorDiffMorphKernel { get; set; } = 5;
        public string ReferenceColorPath { get; set; } = "Assets/reference_color.json";

        // ── Session ──────────────────────────────────────────────────────────
        public string? LastFolder { get; set; }
    }

    /// <summary>Loads/saves <see cref="PipelineConfig"/> as config.json next to
    /// the executable (not %AppData% - kept visible and hand-editable).</summary>
    public static class ConfigStore
    {
        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "config.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static PipelineConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<PipelineConfig>(File.ReadAllText(FilePath))
                           ?? new PipelineConfig();
            }
            catch
            {
                // Corrupt/unreadable file -> fall back to defaults rather than crash.
            }
            return new PipelineConfig();
        }

        public static void Save(PipelineConfig config)
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
            }
            catch
            {
                // Non-fatal: persistence is best-effort.
            }
        }
    }
}
