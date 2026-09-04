using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace copperInspection.Scan
{
    /// <summary>
    /// One inspected strip, start to finish.
    ///
    /// This exists because "is this strip good, and where are the defects" is a
    /// strip-level question that a pile of per-frame reports cannot answer on
    /// its own - nothing in them says how many frames there should have been,
    /// or whether any went missing.
    /// </summary>
    public sealed class StripRun
    {
        [BsonId]
        public ObjectId Id { get; set; }

        /// <summary>Operator-entered batch or coil identifier.</summary>
        [BsonElement("batchId")]
        public string BatchId { get; set; } = string.Empty;

        [BsonElement("startedUtc")] public DateTime StartedUtc { get; set; }
        [BsonElement("endedUtc")] public DateTime? EndedUtc { get; set; }

        /// <summary>How much strip was actually scanned, mm.</summary>
        [BsonElement("totalLengthMm")] public double TotalLengthMm { get; set; }

        [BsonElement("segmentCount")] public int SegmentCount { get; set; }
        [BsonElement("defectSegmentCount")] public int DefectSegmentCount { get; set; }

        /// <summary>
        /// True only if every segment that should have been inspected was
        /// inspected. THE field an operator has to be able to trust: false
        /// means part of this strip was never actually looked at, whatever the
        /// verdict says.
        /// </summary>
        [BsonElement("coverageComplete")] public bool CoverageComplete { get; set; }

        /// <summary>Segments that were skipped, dropped or failed - the parts
        /// of the strip that were not verified.</summary>
        [BsonElement("missingSegments")] public List<int> MissingSegments { get; set; } = new();

        /// <summary>Why the run ended: Operator, LengthReached, IdleTimeout,
        /// Fault, RestartedByOperator.</summary>
        [BsonElement("endReason")] public string EndReason { get; set; } = string.Empty;

        /// <summary>"GOOD", "BAD", or "INCOMPLETE" when coverage was not
        /// complete and nothing was found - because "no defects seen" is not
        /// the same claim as "no defects present" if frames went missing.</summary>
        [BsonElement("verdict")] public string Verdict { get; set; } = "N/A";

        /// <summary>The run's defects, merged across frame overlaps. The
        /// authoritative "where are the defects on this strip" answer.</summary>
        [BsonElement("defects")] public List<DefectBox> Defects { get; set; } = new();

        /// <summary>The scan settings this run used, as JSON. Snapshotted so a
        /// report from six months ago is still interpretable after someone
        /// retunes the line.</summary>
        [BsonElement("configSnapshot")] public string ConfigSnapshot { get; set; } = string.Empty;

        [BsonIgnore] public int DefectCount => Defects.Count;

        [BsonIgnore]
        public string DisplayText =>
            $"{StartedUtc.ToLocalTime():yyyy-MM-dd  HH:mm:ss}" +
            (string.IsNullOrWhiteSpace(BatchId) ? "" : $"   ·   {BatchId}");

        [BsonIgnore]
        public string SummaryText =>
            $"{SegmentCount} segment{(SegmentCount == 1 ? "" : "s")}   ·   " +
            $"{TotalLengthMm / 1000.0:F2} m   ·   " +
            $"{DefectCount} defect{(DefectCount == 1 ? "" : "s")}";
    }
}
