using System;
using System.Collections.Generic;
using System.Linq;

namespace copperInspection.Scan
{
    /// <summary>
    /// Collapses the same physical defect, seen in two or more overlapping
    /// frames, into one entry.
    ///
    /// The frames overlap on purpose, so a defect sitting in a seam is
    /// genuinely visible twice. Both sightings are correct and both stay in
    /// their own segment's report, for tracing back to an image - but when the
    /// operator asks "what is wrong with this strip", it has to be one defect,
    /// not two. That is what this produces, and it is why the merged list lives
    /// on the run rather than replacing the per-segment ones.
    /// </summary>
    public static class DefectMerger
    {
        /// <summary>
        /// Merge raw per-segment sightings into the run's authoritative defect
        /// list, ordered along the strip.
        /// </summary>
        public static List<DefectBox> Merge(ScanConfig cfg, IEnumerable<DefectBox> raw)
        {
            List<DefectBox> boxes = raw.Select(b => b.Clone()).ToList();
            if (boxes.Count <= 1) return boxes;

            double threshold = cfg.Geometry.DefectMergeOverlapFraction;

            // Union-find, so a defect spanning three overlapping frames ends up
            // as one entry rather than a chain of pairs.
            var parent = new int[boxes.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }

            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[rb] = ra;
            }

            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    if (SameDefect(boxes[i], boxes[j], threshold)) Union(i, j);
                }
            }

            var clusters = new Dictionary<int, List<DefectBox>>();
            for (int i = 0; i < boxes.Count; i++)
            {
                int root = Find(i);
                if (!clusters.TryGetValue(root, out List<DefectBox>? list))
                    clusters[root] = list = new List<DefectBox>();
                list.Add(boxes[i]);
            }

            return clusters.Values
                .Select(MergeCluster)
                .OrderBy(b => b.StartMm)
                .ToList();
        }

        /// <summary>
        /// Two sightings are the same defect when they were seen in DIFFERENT
        /// frames and their millimetre footprints overlap enough.
        ///
        /// Different frames is not a detail: two separate contours inside one
        /// frame are two separate defects, however close together, because the
        /// detector already decided they were distinct. Merging those would
        /// under-report.
        /// </summary>
        private static bool SameDefect(DefectBox a, DefectBox b, double threshold)
        {
            if (a.SourceSegments.Count > 0 && b.SourceSegments.Count > 0 &&
                a.SourceSegments.SequenceEqual(b.SourceSegments))
                return false;

            double alongOverlap = Overlap(a.StartMm, a.EndMm, b.StartMm, b.EndMm);
            if (alongOverlap <= 0) return false;

            // Across the strip too: a defect on the left edge and one on the
            // right edge at the same distance along are two different defects.
            double acrossOverlap = Overlap(a.AcrossStartMm, a.AcrossEndMm, b.AcrossStartMm, b.AcrossEndMm);
            if (acrossOverlap <= 0) return false;

            double areaA = Math.Max(1e-9, a.LengthMm * Math.Max(a.WidthMm, 1e-9));
            double areaB = Math.Max(1e-9, b.LengthMm * Math.Max(b.WidthMm, 1e-9));
            double intersection = alongOverlap * acrossOverlap;

            // Fraction of the SMALLER box, not of the union: a defect clipped
            // by a frame edge shows up small in one frame and whole in the
            // other, and an intersection-over-union would refuse to merge them
            // for exactly the case this exists to handle.
            return intersection / Math.Min(areaA, areaB) >= threshold;
        }

        private static double Overlap(double s1, double e1, double s2, double e2)
            => Math.Min(e1, e2) - Math.Max(s1, s2);

        private static DefectBox MergeCluster(List<DefectBox> cluster)
        {
            if (cluster.Count == 1) return cluster[0];

            // The widest view of the defect wins: a sighting clipped by a frame
            // edge under-reports its extent, so taking the extremes recovers
            // the true footprint.
            DefectBox best = cluster.OrderByDescending(b => b.AreaMm2).First();

            var segments = cluster
                .SelectMany(b => b.SourceSegments)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            return new DefectBox
            {
                StartMm = cluster.Min(b => b.StartMm),
                EndMm = cluster.Max(b => b.EndMm),
                AcrossStartMm = cluster.Min(b => b.AcrossStartMm),
                AcrossEndMm = cluster.Max(b => b.AcrossEndMm),
                Score = cluster.Max(b => b.Score),
                PxX = best.PxX,
                PxY = best.PxY,
                PxW = best.PxW,
                PxH = best.PxH,
                SourceSegments = segments,
                MergedCount = cluster.Count,
                AreaMm2 = (cluster.Max(b => b.EndMm) - cluster.Min(b => b.StartMm))
                        * (cluster.Max(b => b.AcrossEndMm) - cluster.Min(b => b.AcrossStartMm)),
            };
        }
    }
}
