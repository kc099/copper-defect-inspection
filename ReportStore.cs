using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace copperInspection
{
    /// <summary>One saved inspection result (image + defect count + when it ran).</summary>
    public sealed class DefectReport
    {
        [BsonId]
        public ObjectId Id { get; set; }

        /// <summary>Run time, stored as UTC. Display with <see cref="DateTime.ToLocalTime"/>.</summary>
        [BsonElement("timestamp")]
        public DateTime Timestamp { get; set; }

        [BsonElement("defectCount")]
        public int DefectCount { get; set; }

        [BsonElement("method")]
        public string Method { get; set; } = string.Empty;

        [BsonElement("mode")]
        public string Mode { get; set; } = string.Empty;

        [BsonElement("referenceImage")]
        public string ReferenceImage { get; set; } = string.Empty;

        [BsonElement("targetImage")]
        public string TargetImage { get; set; } = string.Empty;

        /// <summary>Result image (target + green defect boxes) encoded as PNG bytes.</summary>
        [BsonElement("imageData")]
        public byte[] ImageData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Same result image (target + green defect boxes) as a viewable Base64
        /// PNG data URI — paste into a browser or Mongo GUI to see the picture.
        /// </summary>
        [BsonElement("resultImageBase64")]
        public string ResultImageBase64 { get; set; } = string.Empty;

        // ══ Continuous strip scan ══
        // Set only for reports produced by a strip run; a manually captured
        // frame leaves them at their defaults. Together they answer "which
        // part of which strip is this?" - see docs/STRIP_SCAN_BUILD_PLAN.md.

        /// <summary>The run this segment belongs to, or empty for a manual capture.</summary>
        [BsonElement("stripRunId")]
        public ObjectId StripRunId { get; set; }

        /// <summary>1-based position of this frame within the run.</summary>
        [BsonElement("segmentIndex")]
        public int SegmentIndex { get; set; }

        /// <summary>The stretch of strip this frame covers, mm from the leading edge.</summary>
        [BsonElement("segmentStartMm")] public double SegmentStartMm { get; set; }
        [BsonElement("segmentEndMm")] public double SegmentEndMm { get; set; }

        /// <summary>Encoder reading at the moment of capture.</summary>
        [BsonElement("encoderMmAtCapture")] public double EncoderMmAtCapture { get; set; }

        /// <summary>Actual minus ideal trigger position. Watch it for drift.</summary>
        [BsonElement("triggerErrorMm")] public double TriggerErrorMm { get; set; }

        [BsonElement("lineSpeedMmPerSec")] public double LineSpeedMmPerSec { get; set; }

        /// <summary>Defects as seen in THIS frame, raw and un-deduplicated. A
        /// defect in a seam legitimately appears here and in the neighbouring
        /// segment too; the run's merged list is the deduplicated one.</summary>
        [BsonElement("defects")]
        public List<Scan.DefectBox> Defects { get; set; } = new();

        /// <summary>Row label shown in the reports list (local time + defect count).</summary>
        [BsonIgnore]
        public string DisplayText =>
            $"{Timestamp.ToLocalTime():yyyy-MM-dd  HH:mm:ss}   ·   {DefectCount} defect{(DefectCount == 1 ? "" : "s")}";

        /// <summary>
        /// Where the defect(s) are, in millimetres along the strip - only
        /// populated for reports a strip scan produced (see
        /// Scan.StripRunRecorder); a manually captured report has no position
        /// data, so this is empty for those, not a placeholder.
        /// </summary>
        [BsonIgnore]
        public string PositionsText
        {
            get
            {
                if (Defects.Count == 0) return "";
                const int shown = 3;
                var parts = new List<string>(shown);
                foreach (Scan.DefectBox d in Defects.GetRange(0, Math.Min(shown, Defects.Count)))
                    parts.Add($"{d.StartMm:F0}–{d.EndMm:F0}mm");
                string text = "📍 " + string.Join(",  ", parts);
                if (Defects.Count > shown) text += $"  +{Defects.Count - shown} more";
                return text;
            }
        }

        /// <summary>
        /// True for an empty frame that pads the live gallery out to its fixed slot
        /// count. Never read from or written to Mongo.
        /// </summary>
        [BsonIgnore]
        public bool IsPlaceholder { get; init; }

        /// <summary>An empty frame standing in for an unfilled live slot.</summary>
        public static DefectReport Placeholder() => new() { IsPlaceholder = true };
    }

    /// <summary>MongoDB access for inspection reports.</summary>
    public static class ReportStore
    {
        // Change here if Mongo runs elsewhere.
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName     = "defect_detection_db";
        private const string CollectionName   = "reports";

        /// <summary>Internal to the app: StripRunStore queries and indexes the
        /// same collection, and one definition of where it lives is better than
        /// two that can drift.</summary>
        internal static IMongoCollection<DefectReport> Collection()
        {
            var client = new MongoClient(ConnectionString);
            var db     = client.GetDatabase(DatabaseName);
            return db.GetCollection<DefectReport>(CollectionName);
        }

        public static Task InsertAsync(DefectReport report)
            => Collection().InsertOneAsync(report);

        /// <summary>The most recent <paramref name="limit"/> reports, newest first.</summary>
        public static async Task<List<DefectReport>> QueryRecentAsync(int limit)
        {
            return await Collection()
                .Find(Builders<DefectReport>.Filter.Empty)
                .SortByDescending(r => r.Timestamp)
                .Limit(limit)
                .ToListAsync();
        }

        /// <summary>Reports whose timestamp falls in [fromUtc, toUtc), newest first.</summary>
        public static async Task<List<DefectReport>> QueryByDateAsync(DateTime fromUtc, DateTime toUtc)
        {
            var f = Builders<DefectReport>.Filter;
            var filter = f.Gte(r => r.Timestamp, fromUtc) & f.Lt(r => r.Timestamp, toUtc);
            return await Collection()
                .Find(filter)
                .SortByDescending(r => r.Timestamp)
                .ToListAsync();
        }
    }
}
