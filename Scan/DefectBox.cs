using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;

namespace copperInspection.Scan
{
    /// <summary>
    /// One defect, positioned on the strip in millimetres.
    ///
    /// <see cref="StartMm"/>/<see cref="EndMm"/> are the answer to "where is
    /// it" - absolute distance from the strip's leading edge, not a position
    /// within some frame. A defect from 5 cm to 10 cm is stored as
    /// StartMm 50, EndMm 100.
    ///
    /// Millimetres everywhere, always double. Centimetres and metres are a
    /// display choice in the UI; mixing units in stored data is how a defect
    /// at 5 cm ends up recorded at 5 mm.
    /// </summary>
    public sealed class DefectBox
    {
        /// <summary>Position along the strip, mm from the leading edge.</summary>
        [BsonElement("startMm")] public double StartMm { get; set; }
        [BsonElement("endMm")] public double EndMm { get; set; }

        /// <summary>Position across the strip's width, mm from the image edge -
        /// which side of the strip the defect is on.</summary>
        [BsonElement("acrossStartMm")] public double AcrossStartMm { get; set; }
        [BsonElement("acrossEndMm")] public double AcrossEndMm { get; set; }

        [BsonElement("areaMm2")] public double AreaMm2 { get; set; }

        /// <summary>Anomaly score for PatchCore, defect-area percentage for
        /// ColorDiff.</summary>
        [BsonElement("score")] public double Score { get; set; }

        // Raw pixel box, kept so a millimetre position can always be traced
        // back to a place in an actual image.
        [BsonElement("px")] public int PxX { get; set; }
        [BsonElement("py")] public int PxY { get; set; }
        [BsonElement("pw")] public int PxW { get; set; }
        [BsonElement("ph")] public int PxH { get; set; }

        /// <summary>Which segment's frame this was seen in. On a merged defect,
        /// every segment it was seen in.</summary>
        [BsonElement("segments")] public List<int> SourceSegments { get; set; } = new();

        /// <summary>How many raw sightings were merged into this one. 1 means
        /// it was only ever seen once.</summary>
        [BsonElement("mergedCount")] public int MergedCount { get; set; } = 1;

        [BsonIgnore] public double LengthMm => EndMm - StartMm;
        [BsonIgnore] public double WidthMm => AcrossEndMm - AcrossStartMm;

        /// <summary>Human-readable position, e.g. "50.0 - 100.0 mm".</summary>
        [BsonIgnore] public string RangeText => $"{StartMm:F1} – {EndMm:F1} mm";

        [BsonIgnore] public string AcrossText => $"{AcrossStartMm:F1} – {AcrossEndMm:F1} mm";

        [BsonIgnore] public string SegmentsText => string.Join(", ", SourceSegments);

        public DefectBox Clone() => new()
        {
            StartMm = StartMm,
            EndMm = EndMm,
            AcrossStartMm = AcrossStartMm,
            AcrossEndMm = AcrossEndMm,
            AreaMm2 = AreaMm2,
            Score = Score,
            PxX = PxX,
            PxY = PxY,
            PxW = PxW,
            PxH = PxH,
            SourceSegments = new List<int>(SourceSegments),
            MergedCount = MergedCount,
        };
    }
}
