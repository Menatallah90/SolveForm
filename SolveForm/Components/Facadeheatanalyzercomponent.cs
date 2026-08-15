// FacadeHeatAnalyzerComponent.cs
// GUID: 832275be-b51e-4413-b652-53dbe741c6bf
//
// FIX THIS SESSION -- baseline methodology overhaul (see handoff 2.B).
//
// THE PROBLEM: the old auto-baseline was the bounding box of the ACTUAL,
// already-stepped-and-shifted design. Shifting slices sideways expands the
// overall footprint, so that bounding box could end up bigger in both
// footprint and height than a fair reference -- meaning the "baseline"
// wasn't a clean apples-to-apples comparison, it was an uncontrolled
// reference that could swing the graph either direction for reasons that
// have nothing to do with whether the form itself is smart. That's why the
// chart read as untrustworthy -- it was, as previously built, not a fair
// comparison.
//
// THE FIX: two new optional inputs, SiteFootprint and TotalHeight. If both
// are supplied and valid, baseline = a straight extrusion of SiteFootprint
// up by TotalHeight. Zero setbacks, zero shift, zero tilt -- literally the
// plain glass-box everyone else is turning in, at the SAME footprint and
// SAME height as the real design. That isolates the effect of form
// decisions on solar heat gain, which is the actual comparison wanted for
// the portfolio narrative.
//
// PRIORITY ORDER for baseline generation:
//   1. Manual BaselineEnvelope input, if supplied and valid -- top priority,
//      always wins, for hand-modeled custom comparisons.
//   2. SiteFootprint + TotalHeight straight extrusion, if both valid --
//      the correct default now.
//   3. Degraded bounding-box-of-ActualEnvelope fallback, ONLY if neither of
//      the above is available -- now clearly flagged via a runtime Warning
//      and the new BaselineInfo text output, so it can never silently
//      mislead a portfolio review again.
//
// BEING DIRECT: this fixes the METHODOLOGY so the comparison is defensible
// and clearly labeled. It does not guarantee the design comes out looking
// better in every orientation -- if the real geometry genuinely has more
// exposure than a plain box somewhere, that's real data, not a graph bug.
//
// HONEST NOTE (unchanged from last version): sun position here comes from
// Latitude + SunDeclinationDeg (a solar-position formula), not annual EPW
// weather data. There's no EPW reader wired into this pipeline -- that's a
// separate component from the earlier v0.1 build. Flagging this rather than
// pretending it's already there.

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
                "Sums solar heat per compass direction for actual design vs baseline. Baseline is a manual override, " +
                "an equivalent-footprint/equivalent-height extrusion (preferred), or a degraded bounding-box fallback " +
                "(flagged) -- see BaselineInfo output.",
                "SolveForm", "Analysis")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("ActualEnvelope", "E", "Your designed building envelope", GH_ParamAccess.item);
            pManager.AddBrepParameter("BaselineEnvelope", "Base",
                "Optional manual override. Top priority if supplied and valid -- use for a hand-modeled custom comparison mass.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "Lat", "Site latitude", GH_ParamAccess.item, 24.7);
            pManager.AddNumberParameter("SunDeclinationDeg", "Dec", "Solar declination", GH_ParamAccess.item, 23.45);
            pManager.AddNumberParameter("VerticalThreshold", "Vt", "Normal.Z threshold for facade detection", GH_ParamAccess.item, 0.3);
            pManager.AddBooleanParameter("UseDailyIntegration", "Daily", "True = sum exposure across sunrise-to-sunset", GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("DailySampleCount", "N", "Sun positions sampled across the day", GH_ParamAccess.item, 9);
            pManager.AddNumberParameter("HourAngleDegrees", "Hr", "Only used if UseDailyIntegration=false", GH_ParamAccess.item, 0.0);
            pManager.AddCurveParameter("SiteFootprint", "SF",
                "Optional. The original, unstepped site/ground-floor footprint curve (wire in BEFORE SolveForm Section does " +
                "any shifting/tilting). Used with TotalHeight to build the correct equivalent-footprint baseline.",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("TotalHeight", "TH",
                "Optional. The design's actual total height (e.g. sum of Section's ZoneHeights, or top FloorZLevels value). " +
                "Used with SiteFootprint to build the correct equivalent-footprint baseline.",
                GH_ParamAccess.item, 0.0);

            pManager[1].Optional = true;
            for (int i = 2; i <= 9; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("HeatValuesActual", "HA", "Summed solar heat -- actual design", GH_ParamAccess.list);
            pManager.AddNumberParameter("HeatValuesBaseline", "HB", "Summed solar heat -- baseline", GH_ParamAccess.list);
            pManager.AddTextParameter("Orientations", "O", "Compass labels, fixed N..NW order", GH_ParamAccess.list);
            pManager.AddIntegerParameter("FaceCount", "FC", "Facade face count per bucket -- actual envelope only", GH_ParamAccess.list);
            pManager.AddTextParameter("BaselineInfo", "BI",
                "Which baseline method was actually used this run, and why. Always check this before trusting the graph.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep actualEnv = null, baselineEnv = null;
            double lat = 24.7, declination = 23.45, verticalThreshold = 0.3, hourAngle = 0.0;
            bool useDailyIntegration = true;
            int sampleCount = 9;
            Curve siteFootprint = null;
            double totalHeight = 0.0;

            if (!DA.GetData(0, ref actualEnv)) return;
            DA.GetData(1, ref baselineEnv);
            DA.GetData(2, ref lat);
            DA.GetData(3, ref declination);
            DA.GetData(4, ref verticalThreshold);
            DA.GetData(5, ref useDailyIntegration);
            DA.GetData(6, ref sampleCount);
            DA.GetData(7, ref hourAngle);
            DA.GetData(8, ref siteFootprint);
            DA.GetData(9, ref totalHeight);
            sampleCount = Math.Max(3, sampleCount);

            if (actualEnv == null || !actualEnv.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "ActualEnvelope is invalid.");
                return;
            }

            string baselineInfo;
            bool manualOverride = false, footprintBaseline = false, degradedFallback = false;

            if (baselineEnv != null && baselineEnv.IsValid)
            {
                manualOverride = true;
                baselineInfo = "Using manually supplied BaselineEnvelope (top-priority override). No automatic methodology applied.";
            }
            else
            {
                string footprintFailReason;
                Brep generated = BuildFootprintBaseline(siteFootprint, totalHeight, out footprintFailReason);

                if (generated != null)
                {
                    baselineEnv = generated;
                    footprintBaseline = true;
                    baselineInfo = $"Baseline = straight extrusion of SiteFootprint up to TotalHeight={totalHeight:F2}m. " +
                        "Equivalent footprint, equivalent height, zero setbacks/shift/tilt -- isolates the effect of your " +
                        "form decisions on solar heat gain.";
                }
                else
                {
                    BoundingBox bb = actualEnv.GetBoundingBox(false);
                    baselineEnv = new Box(bb).ToBrep();
                    degradedFallback = true;
                    baselineInfo = "WARNING: using degraded bounding-box fallback, not a true equivalent-footprint comparison " +
                        "-- wire SiteFootprint + TotalHeight for accurate results." +
                        (string.IsNullOrEmpty(footprintFailReason) ? "" : $" (Reason: {footprintFailReason})");
                }
            }

            var (heatActual, countActual) = ComputeHeatForEnvelope(actualEnv, lat, declination, verticalThreshold, useDailyIntegration, sampleCount, hourAngle);
            var (heatBaseline, _) = ComputeHeatForEnvelope(baselineEnv, lat, declination, verticalThreshold, useDailyIntegration, sampleCount, hourAngle);

            if (degradedFallback)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, baselineInfo);
            else if (footprintBaseline)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Baseline = equivalent-footprint/equivalent-height extrusion. See BaselineInfo.");
            else if (manualOverride)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Baseline = manually supplied BaselineEnvelope.");

            DA.SetDataList(0, heatActual.Select(h => Math.Round(h, 2)));
            DA.SetDataList(1, heatBaseline.Select(h => Math.Round(h, 2)));
            DA.SetDataList(2, CompassOrder);
            DA.SetDataList(3, countActual);
            DA.SetData(4, baselineInfo);
        }

        // Builds the correct equivalent-footprint baseline: a straight extrusion of the
        // original, unstepped site footprint up to the design's actual total height.
        // Returns null (with a reason string) if SiteFootprint/TotalHeight weren't usable,
        // so the caller can fall back cleanly.
        private static Brep BuildFootprintBaseline(Curve siteFootprint, double totalHeight, out string failReason)
        {
            failReason = "";

            if (siteFootprint == null || !siteFootprint.IsValid)
            {
                failReason = "SiteFootprint not supplied or invalid.";
                return null;
            }
            if (!siteFootprint.IsClosed)
            {
                failReason = "SiteFootprint is not a closed curve.";
                return null;
            }
            if (totalHeight <= 0.01)
            {
                failReason = "TotalHeight not supplied or <= 0.";
                return null;
            }

            Curve footprint = siteFootprint.DuplicateCurve();

            Extrusion extrusion = Extrusion.Create(footprint, totalHeight, true);
            if (extrusion == null)
            {
                failReason = "Extrusion.Create failed on SiteFootprint -- check it is planar.";
                return null;
            }

            Brep brep = extrusion.ToBrep();
            if (brep == null || !brep.IsValid)
            {
                failReason = "Extruded SiteFootprint produced an invalid Brep.";
                return null;
            }

            return brep;
        }

        private (double[] heat, int[] count) ComputeHeatForEnvelope(
            Brep envelope, double lat, double declination, double verticalThreshold,
            bool useDailyIntegration, int sampleCount, double hourAngle)
        {
            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            var vmp = VolumeMassProperties.Compute(envelope);
            Point3d volumeCentroid = (vmp != null) ? vmp.Centroid : envelope.GetBoundingBox(false).Center;

            var facadeData = new List<(Vector3d normal, Point3d centroid, double area)>();
            foreach (var face in envelope.Faces)
            {
                double uMid = face.Domain(0).Mid, vMid = face.Domain(1).Mid;
                Vector3d normal = face.NormalAt(uMid, vMid);
                if (!normal.IsValid || normal.IsZero) continue;
                normal.Unitize();

                var areaProps = AreaMassProperties.Compute(face.DuplicateFace(false));
                if (areaProps == null) continue;
                Point3d faceCentroid = areaProps.Centroid;
                double area = areaProps.Area;

                Vector3d outwardRef = faceCentroid - volumeCentroid;
                if (outwardRef.Length > tol * 100 && outwardRef.Unitize())
                    if (normal * outwardRef < 0) normal = -normal;

                if (Math.Abs(normal.Z) >= verticalThreshold) continue;
                facadeData.Add((normal, faceCentroid, area));
            }

            var heatByBucket = new double[8];
            var countByBucket = new int[8];

            if (useDailyIntegration)
            {
                double latRad = lat * Math.PI / 180.0;
                double decRad = declination * Math.PI / 180.0;
                double cosHourLimit = Clamp(-Math.Tan(latRad) * Math.Tan(decRad), -1.0, 1.0);
                double hourLimitRad = Math.Acos(cosHourLimit);

                for (int s = 0; s < sampleCount; s++)
                {
                    double frac = (s + 0.5) / sampleCount;
                    double sampleHourRad = -hourLimitRad + frac * (2 * hourLimitRad);
                    double sampleHourDeg = sampleHourRad * 180.0 / Math.PI;
                    Vector3d sunVec = ComputeSunVector(lat, sampleHourDeg, declination);
                    Accumulate(facadeData, sunVec, heatByBucket, countByBucket, onlyCountOnce: s == 0);
                }
            }
            else
            {
                Vector3d sunVec = ComputeSunVector(lat, hourAngle, declination);
                Accumulate(facadeData, sunVec, heatByBucket, countByBucket, onlyCountOnce: true);
            }

            return (heatByBucket, countByBucket);
        }

        private static void Accumulate(List<(Vector3d normal, Point3d centroid, double area)> facadeData,
            Vector3d sunVec, double[] heatByBucket, int[] countByBucket, bool onlyCountOnce)
        {
            foreach (var (normal, centroid, area) in facadeData)
            {
                int bucket = BearingToBucket(normal);
                double incidence = Math.Max(0.0, Vector3d.Multiply(normal, sunVec));
                heatByBucket[bucket] += incidence * area;
                if (onlyCountOnce) countByBucket[bucket]++;
            }
        }

        private static Vector3d ComputeSunVector(double latDeg, double hourAngleDeg, double declinationDeg)
        {
            double lat = latDeg * Math.PI / 180.0, dec = declinationDeg * Math.PI / 180.0, hour = hourAngleDeg * Math.PI / 180.0;
            double sinAlt = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hour);
            double altitude = Math.Asin(Clamp(sinAlt, -1.0, 1.0));
            if (altitude <= 0) return Vector3d.Zero;

            double cosAz = (Math.Sin(dec) - Math.Sin(altitude) * Math.Sin(lat)) / Math.Max(Math.Cos(altitude) * Math.Cos(lat), 1e-9);
            double azimuth = Math.Acos(Clamp(cosAz, -1.0, 1.0));
            if (hour > 0) azimuth = 2 * Math.PI - azimuth;

            double x = Math.Cos(altitude) * Math.Sin(azimuth), y = Math.Cos(altitude) * Math.Cos(azimuth), z = Math.Sin(altitude);
            var v = new Vector3d(x, y, z); v.Unitize(); return v;
        }

        private static int BearingToBucket(Vector3d normal)
        {
            double bearingRad = Math.Atan2(normal.X, normal.Y);
            double bearingDeg = bearingRad * 180.0 / Math.PI;
            if (bearingDeg < 0) bearingDeg += 360.0;
            return (int)Math.Round(bearingDeg / 45.0) % 8;
        }

        private static double Clamp(double val, double min, double max) => Math.Max(min, Math.Min(max, val));

        public override Guid ComponentGuid => new Guid("832275be-b51e-4413-b652-53dbe741c6bf");
    }
}