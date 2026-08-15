// FacadeHeatAnalyzerComponent.cs
// GUID: 832275be-b51e-4413-b652-53dbe741c6bf
//
// THIS IS WHAT FEEDS HeatOrientationGraphPanelComponent. You were right that
// nothing produced HeatValues/Orientations automatically -- you were expected
// to type them in by hand, which is backwards. This component takes your
// massing + sun position and does the orientation tagging itself.
//
// ORIENTATION IS AUTOMATIC, NOT SET BY YOU:
// For each facade face, take its outward normal, get its compass bearing
// (atan2 in the XY plane), and bucket it into one of 8 compass directions
// (N/NE/E/SE/S/SW/W/NW). This is the same normal-based approach already used
// in SolveFormFacadesComponent for orientation tagging -- reused here, not
// reinvented, so both components agree on what "facing south" means.
//
// HEAT VALUE PER ORIENTATION:
// For each facade face, heat = solar incidence factor * face area, where
// incidence factor = max(0, dot(face normal, sun vector)) -- a face facing
// straight at the sun gets full exposure, a face perpendicular to the sun
// gets zero, a face facing away gets clamped to zero (no negative heat).
// This is the standard cosine law used for direct solar gain estimation.
// Values are SUMMED per compass bucket across every facade face lying in
// that bucket, so a bucket with more wall area facing the sun shows more
// heat -- which is the actual point of the chart.
//
// Sun vector comes from the same solar geometry your SectionComponent
// already uses (latitude + a single representative sun angle), not a full
// EPW annual simulation -- this is a design-stage snapshot tool, matched to
// whatever moment you want to check (e.g. summer solstice noon).
//
// INPUTS
//   0  Envelope         (Brep, item)   - unified building envelope
//   1  Latitude          (double)       - site latitude, default 24.7 (matches Section default)
//   2  HourAngleDegrees  (double)       - sun hour angle, 0 = solar noon, default 0
//   3  SunDeclinationDeg (double)       - solar declination, default 23.45 (summer solstice) --
//                                          use -23.45 for winter solstice, 0 for equinox
//   4  VerticalThreshold (double)       - facade-detection threshold, default 0.3 (matches Openings)
//
// OUTPUTS
//   0  HeatValues    (double, list)  - summed heat per compass bucket, ready to wire straight
//                                       into HeatOrientationGraphPanelComponent
//   1  Orientations  (string, list)  - "N","NE","E","SE","S","SW","W","NW" -- fixed order,
//                                       always all 8, zero-filled if a facade has no faces there
//   2  FaceCount     (int, list)     - how many facade faces contributed to each bucket (debug)

using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class FacadeHeatAnalyzerComponent : GH_Component
    {
        private static readonly string[] CompassOrder = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        public FacadeHeatAnalyzerComponent()
            : base("Facade Heat Analyzer", "HeatAnalyze",
                "Auto-tags facade orientation from face normals and sums solar heat per compass direction. Feeds directly into Heat Orientation Graph.",
                "SolveForm", "Analysis")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Envelope", "E", "Unified building envelope", GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "Lat", "Site latitude", GH_ParamAccess.item, 24.7);
            pManager.AddNumberParameter("HourAngleDegrees", "Hr", "Sun hour angle, 0 = solar noon", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("SunDeclinationDeg", "Dec", "Solar declination (23.45 = summer solstice, -23.45 = winter, 0 = equinox)", GH_ParamAccess.item, 23.45);
            pManager.AddNumberParameter("VerticalThreshold", "Vt", "Normal.Z threshold below which a face counts as a facade", GH_ParamAccess.item, 0.3);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("HeatValues", "H", "Summed solar heat per compass direction", GH_ParamAccess.list);
            pManager.AddTextParameter("Orientations", "O", "Compass labels, fixed N..NW order", GH_ParamAccess.list);
            pManager.AddIntegerParameter("FaceCount", "FC", "Facade face count contributing to each bucket", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep envelope = null;
            double lat = 24.7, hourAngle = 0.0, declination = 23.45, verticalThreshold = 0.3;

            if (!DA.GetData(0, ref envelope)) return;
            DA.GetData(1, ref lat);
            DA.GetData(2, ref hourAngle);
            DA.GetData(3, ref declination);
            DA.GetData(4, ref verticalThreshold);

            if (envelope == null || !envelope.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Envelope is invalid.");
                return;
            }

            Vector3d sunVec = ComputeSunVector(lat, hourAngle, declination);

            var heatByBucket = new double[8];
            var countByBucket = new int[8];

            foreach (var face in envelope.Faces)
            {
                Interval uDom = face.Domain(0);
                Interval vDom = face.Domain(1);
                Vector3d normal = face.NormalAt(uDom.Mid, vDom.Mid);
                if (!normal.IsValid || normal.IsZero) continue;
                normal.Unitize();

                // Facade detection: mostly-vertical faces only (skip roof/floor).
                if (Math.Abs(normal.Z) >= verticalThreshold) continue;

                int bucket = BearingToBucket(normal);

                var areaProps = AreaMassProperties.Compute(face.DuplicateFace(false));
                double area = areaProps != null ? areaProps.Area : 0.0;

                double incidence = Math.Max(0.0, Vector3d.Multiply(normal, sunVec));
                double heat = incidence * area;

                heatByBucket[bucket] += heat;
                countByBucket[bucket]++;
            }

            DA.SetDataList(0, heatByBucket.Select(h => Math.Round(h, 2)));
            DA.SetDataList(1, CompassOrder);
            DA.SetDataList(2, countByBucket);
        }

        private static Vector3d ComputeSunVector(double latDeg, double hourAngleDeg, double declinationDeg)
        {
            double lat = latDeg * Math.PI / 180.0;
            double dec = declinationDeg * Math.PI / 180.0;
            double hour = hourAngleDeg * Math.PI / 180.0;

            // Standard solar position equations (altitude/azimuth from lat/declination/hour angle).
            double sinAlt = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hour);
            double altitude = Math.Asin(Clamp(sinAlt, -1.0, 1.0));

            double cosAz = (Math.Sin(dec) - Math.Sin(altitude) * Math.Sin(lat)) / Math.Max(Math.Cos(altitude) * Math.Cos(lat), 1e-9);
            double azimuth = Math.Acos(Clamp(cosAz, -1.0, 1.0));
            if (hour > 0) azimuth = 2 * Math.PI - azimuth; // afternoon mirrors morning

            // Convert altitude/azimuth (compass bearing, 0=N) into a world XY+Z unit vector
            // pointing FROM the facade TOWARD the sun.
            double x = Math.Cos(altitude) * Math.Sin(azimuth);
            double y = Math.Cos(altitude) * Math.Cos(azimuth);
            double z = Math.Sin(altitude);

            var v = new Vector3d(x, y, z);
            v.Unitize();
            return v;
        }

        private static int BearingToBucket(Vector3d normal)
        {
            double bearingRad = Math.Atan2(normal.X, normal.Y); // 0 = N, clockwise
            double bearingDeg = bearingRad * 180.0 / Math.PI;
            if (bearingDeg < 0) bearingDeg += 360.0;

            int bucket = (int)Math.Round(bearingDeg / 45.0) % 8;
            return bucket;
        }

        private static double Clamp(double val, double min, double max)
            => Math.Max(min, Math.Min(max, val));

        public override Guid ComponentGuid => new Guid("832275be-b51e-4413-b652-53dbe741c6bf");
    }
}