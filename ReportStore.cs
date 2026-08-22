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

        /// <summary>Row label shown in the reports list (local time + defect count).</summary>
        [BsonIgnore]
        public string DisplayText =>
            $"{Timestamp.ToLocalTime():yyyy-MM-dd  HH:mm:ss}   ·   {DefectCount} defect{(DefectCount == 1 ? "" : "s")}";
    }

    /// <summary>MongoDB access for inspection reports.</summary>
    public static class ReportStore
    {
        // Change here if Mongo runs elsewhere.
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName     = "defect_detection_db";
        private const string CollectionName   = "reports";

        private static IMongoCollection<DefectReport> Collection()
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
