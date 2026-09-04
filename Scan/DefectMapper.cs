using System;
using System.Collections.Generic;

namespace copperInspection.Scan
{
    /// <summary>
    /// Turns a defect's pixel box in one segment's frame into a position on the
    /// strip in millimetres.
    ///
    /// Deliberately free of OpenCV and MongoDB - it takes plain integers - so
    /// the arithmetic that decides where every defect gets reported can be
    /// tested on its own. A silent error here would mislocate every defect in
    /// the database while everything still looked like it worked.
    /// </summary>
    public static class DefectMapper
    {
        /// <summary>
        /// Map one pixel box to strip millimetres.
        /// </summary>
        /// <param name="imageWidthPx">Frame width in pixels.</param>
        /// <param name="imageHeightPx">Frame height in pixels.</param>
        public static DefectBox Map(
            ScanConfig cfg, int segment,
            int pxX, int pxY, int pxW, int pxH,
            int imageWidthPx, int imageHeightPx,
            double score)
        {
            // Which image axis the strip travels along depends on how the
            // camera is bolted on, so it is configuration, not an assumption.
            bool travelIsX = ScanGeometry.TravelIsX(cfg);

            int alongOrigin = travelIsX ? pxX : pxY;
            int alongLength = travelIsX ? pxW : pxH;
            int alongImage = travelIsX ? imageWidthPx : imageHeightPx;

            int acrossOrigin = travelIsX ? pxY : pxX;
            int acrossLength = travelIsX ? pxH : pxW;

            (double startMm, double endMm) = ScanGeometry.PixelToStripMm(
                cfg, segment, alongOrigin, alongLength, alongImage);

            (double acrossStartMm, double acrossEndMm) =
                ScanGeometry.PixelToAcrossMm(cfg, acrossOrigin, acrossLength);

            return new DefectBox
            {
                StartMm = startMm,
                EndMm = endMm,
                AcrossStartMm = acrossStartMm,
                AcrossEndMm = acrossEndMm,
                AreaMm2 = Math.Max(0, endMm - startMm) * Math.Max(0, acrossEndMm - acrossStartMm),
                Score = score,
                PxX = pxX,
                PxY = pxY,
                PxW = pxW,
                PxH = pxH,
                SourceSegments = new List<int> { segment },
                MergedCount = 1,
            };
        }
    }
}
