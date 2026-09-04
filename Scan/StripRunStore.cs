using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace copperInspection.Scan
{
    /// <summary>
    /// MongoDB access for strip runs, alongside the existing per-frame reports
    /// collection (see <see cref="ReportStore"/>).
    /// </summary>
    public static class StripRunStore
    {
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName = "defect_detection_db";
        private const string CollectionName = "strip_runs";

        private static IMongoCollection<StripRun> Collection()
        {
            var client = new MongoClient(ConnectionString);
            return client.GetDatabase(DatabaseName).GetCollection<StripRun>(CollectionName);
        }

        /// <summary>
        /// Create the indexes the strip-run queries rely on. Safe to call
        /// repeatedly - Mongo ignores an index that already exists. Called once
        /// at the start of a run rather than at app start, so an unreachable
        /// database only breaks scanning, not the whole app.
        /// </summary>
        public static async Task EnsureIndexesAsync()
        {
            IMongoCollection<StripRun> runs = Collection();
            var r = Builders<StripRun>.IndexKeys;
            await runs.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<StripRun>(r.Ascending(x => x.BatchId).Descending(x => x.StartedUtc)),
                new CreateIndexModel<StripRun>(r.Descending(x => x.StartedUtc)),
                new CreateIndexModel<StripRun>(r.Ascending(x => x.CoverageComplete)),
            });

            IMongoCollection<DefectReport> reports = ReportStore.Collection();
            var p = Builders<DefectReport>.IndexKeys;
            await reports.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<DefectReport>(p.Ascending(x => x.StripRunId).Ascending(x => x.SegmentIndex)),
                new CreateIndexModel<DefectReport>(p.Ascending("defects.startMm")),
            });
        }

        public static async Task<ObjectId> InsertAsync(StripRun run)
        {
            await Collection().InsertOneAsync(run);
            return run.Id;
        }

        public static Task ReplaceAsync(StripRun run)
            => Collection().ReplaceOneAsync(r => r.Id == run.Id, run);

        /// <summary>The most recent runs, newest first.</summary>
        public static Task<List<StripRun>> QueryRecentAsync(int limit)
            => Collection()
                .Find(Builders<StripRun>.Filter.Empty)
                .SortByDescending(r => r.StartedUtc)
                .Limit(limit)
                .ToListAsync();

        public static Task<List<StripRun>> QueryByDateAsync(DateTime fromUtc, DateTime toUtc)
        {
            var f = Builders<StripRun>.Filter;
            return Collection()
                .Find(f.Gte(r => r.StartedUtc, fromUtc) & f.Lt(r => r.StartedUtc, toUtc))
                .SortByDescending(r => r.StartedUtc)
                .ToListAsync();
        }

        /// <summary>
        /// Every segment report for one run, in order along the strip. The
        /// image bytes are deliberately excluded - a run can be hundreds of
        /// segments and the strip map only needs the metadata. Fetch one
        /// segment's image with <see cref="QuerySegmentAsync"/> when it is
        /// actually going to be shown.
        /// </summary>
        public static Task<List<DefectReport>> QuerySegmentsAsync(ObjectId stripRunId)
            => ReportStore.Collection()
                .Find(Builders<DefectReport>.Filter.Eq(r => r.StripRunId, stripRunId))
                .Project<DefectReport>(Builders<DefectReport>.Projection
                    .Exclude("imageData")
                    .Exclude("resultImageBase64"))
                .SortBy(r => r.SegmentIndex)
                .ToListAsync();

        /// <summary>One segment's full report, image included.</summary>
        public static Task<DefectReport?> QuerySegmentAsync(ObjectId stripRunId, int segment)
        {
            var f = Builders<DefectReport>.Filter;
            return ReportStore.Collection()
                .Find(f.Eq(r => r.StripRunId, stripRunId) & f.Eq(r => r.SegmentIndex, segment))
                .FirstOrDefaultAsync()!;
        }

        /// <summary>Defects anywhere in a stretch of strip, across all runs -
        /// the query the millimetre positions exist to make possible.</summary>
        public static Task<List<DefectReport>> QueryByPositionAsync(double fromMm, double toMm)
        {
            var f = Builders<DefectReport>.Filter;
            return ReportStore.Collection()
                .Find(f.ElemMatch(r => r.Defects,
                    Builders<DefectBox>.Filter.Gte(d => d.StartMm, fromMm) &
                    Builders<DefectBox>.Filter.Lte(d => d.StartMm, toMm)))
                .SortByDescending(r => r.Timestamp)
                .ToListAsync();
        }
    }
}
