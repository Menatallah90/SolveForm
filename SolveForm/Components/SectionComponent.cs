using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class SectionComponent : GH_Component
    {
        public SectionComponent()
          : base("SolveForm Section", "SFSec",
              "Generates climate-responsive stepped massing. Height is solar-driven, snapped to whole floor multiples. " +
              "Per-slice shift/rotation and optional tilt are wind- and solar-driven.",
              "SolveForm", "Optimization")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Profile", "Prof", "Profile curve from SolveForm Solar", GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "Lat", "Site latitude", GH_ParamAccess.item, 24.7);
            pManager.AddNumberParameter("Orientation", "Orient", "Building orientation (degrees)", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("BaseHeight", "Hbase", "Minimum zone height (m)", GH_ParamAccess.item, 6.0);
            pManager.AddNumberParameter("MaxHeight", "Hmax", "Maximum zone height (m)", GH_ParamAccess.item, 30.0);
            pManager.AddNumberParameter("SpaceToShade", "Shade", "Width of outdoor space to shade (m)", GH_ParamAccess.item, 12.0);
            pManager.AddIntegerParameter("Slices", "Slices", "Number of height slices (2-12)", GH_ParamAccess.item, 6);
            pManager.AddBooleanParameter("ShiftSlices", "Shift", "True=wind-directed N-S style shift. False=wind-directed rotation.", GH_ParamAccess.item, true);

            pManager.AddNumberParameter("PrevailingWindDir", "WDir", "Compass bearing wind blows FROM (0=N). From EPW.", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("WindSpeedRef10m", "WSpd", "Average wind speed (m/s) at 10m ref height.", GH_ParamAccess.item, 6.0);
            pManager.AddNumberParameter("TerrainExponent", "Alpha", "Boundary-layer roughness exponent.", GH_ParamAccess.item, 0.22);

            pManager.AddNumberParameter("ShiftIntensity", "ShiftInt",
                "0.0-1.0. At 1.0, the most wind-exposed slice shifts sideways by up to 50% of profile depth -- " +
                "i.e. half the mass reads as a visible step. Scales with each slice's own wind factor, so lower " +
                "slices shift less automatically -- this isn't a flat offset, it's driven by the same boundary-layer " +
                "wind data as everything else in this component.",
                GH_ParamAccess.item, 0.4);

            pManager.AddNumberParameter("MaxRotationDegrees", "MaxRot", "Max progressive rotation (degrees), Rotate mode only.", GH_ParamAccess.item, 10.0);

            pManager.AddBooleanParameter("EnableTopNotch", "Notch", "If true, cut a through-mass opening in the top slice if wind qualifies.", GH_ParamAccess.item, true);
            pManager.AddNumberParameter("NotchWindThreshold", "NThresh", "Minimum wind speed (m/s) at top slice to justify a through-opening.", GH_ParamAccess.item, 10.0);
            pManager.AddNumberParameter("NotchHeightFraction", "NHeight", "Notch height as a fraction of the top slice's height.", GH_ParamAccess.item, 0.15);
            pManager.AddNumberParameter("NotchWidthFraction", "NWidth", "Notch width as a fraction of the profile's average half-extent.", GH_ParamAccess.item, 0.5);

            pManager.AddNumberParameter("FloorToFloorHeight", "F2F", "Floor-to-floor height (m). Slice heights snap to whole multiples.", GH_ParamAccess.item, 3.5);
            pManager.AddNumberParameter("SiteCoveragePercent", "Cov", "Percent of site boundary built on, 0-100.", GH_ParamAccess.item, 100.0);
            pManager.AddNumberParameter("MaxHeightFraction", "MaxHFrac", "Caps the tallest point of the section to this fraction of MaxHeight.", GH_ParamAccess.item, 0.5);

            pManager.AddNumberParameter("MinTerraceDepth", "MinTerrace",
                "Minimum usable shift depth (m) for any slice that shifts at all. Shifts smaller than this get " +
                "snapped to either 0 (flush, no fake-step) or this minimum -- keeps every visible step large enough " +
                "to actually function as a terrace, not just read as noise.",
                GH_ParamAccess.item, 1.2);

            pManager.AddBooleanParameter("EnableTilt", "Tilt",
                "If true, each slice's PLAN twists slightly about a SHARED pivot point (the plan center at ground " +
                "level), rotating the flat curve about the vertical (Z) axis BEFORE extrusion -- an in-plan twist, " +
                "not a section-view lean. Magnitude driven by that slice's own wind factor and the current solar " +
                "altitude. Off by default -- purely optional, does not affect geometry when false.",
                GH_ParamAccess.item, false);
            pManager.AddNumberParameter("MaxTiltDegrees", "MaxTilt",
                "Maximum in-plan twist angle (degrees) applied to the most wind-exposed slice when EnableTilt is true.",
                GH_ParamAccess.item, 3.0);

            for (int i = 1; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Massing", "Mass", "Stepped zone masses -- wire to SF_Unify", GH_ParamAccess.list);
            pManager.AddCurveParameter("Profiles", "Prof", "Zone profile curves", GH_ParamAccess.list);
            pManager.AddNumberParameter("ZoneHeights", "ZH", "Height per zone", GH_ParamAccess.list);
            pManager.AddTextParameter("Report", "Rep", "Section analysis report", GH_ParamAccess.item);
            pManager.AddNumberParameter("FloorZLevels", "FZ", "Z height of each floor slab in world space", GH_ParamAccess.list);
            pManager.AddNumberParameter("WindFactors", "WF", "Normalized wind factor per slice (0-1)", GH_ParamAccess.list);
            pManager.AddBooleanParameter("NotchApplied", "NApp", "Whether the top-band through-notch was cut", GH_ParamAccess.item);
            pManager.AddIntegerParameter("FloorCounts", "FC", "Whole floor count per slice", GH_ParamAccess.list);
            pManager.AddNumberParameter("TiltDegrees", "TiltD", "Applied tilt angle per slice, degrees (0 if EnableTilt is false)", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve profile = null;
            double lat = 24.7, orientation = 0.0, baseH = 6.0, maxH = 30.0, spaceToShade = 12.0;
            int slices = 6;
            bool shiftSlices = true;

            double windFromDir = 0.0, windSpeedRef = 6.0, terrainExp = 0.22, shiftIntensity = 0.4, maxRotDeg = 10.0;
            bool enableNotch = true;
            double notchThreshold = 10.0, notchHeightFrac = 0.15, notchWidthFrac = 0.5;
            double floorToFloor = 3.5;
            double minTerraceDepth = 1.2;
            bool enableTilt = false;
            double maxTiltDegrees = 3.0;

            if (!DA.GetData(0, ref profile) || profile == null) return;
            DA.GetData(1, ref lat);
            DA.GetData(2, ref orientation);
            DA.GetData(3, ref baseH);
            DA.GetData(4, ref maxH);
            DA.GetData(5, ref spaceToShade);
            DA.GetData(6, ref slices);
            DA.GetData(7, ref shiftSlices);

            DA.GetData(8, ref windFromDir);
            DA.GetData(9, ref windSpeedRef);
            DA.GetData(10, ref terrainExp);
            DA.GetData(11, ref shiftIntensity);
            DA.GetData(12, ref maxRotDeg);

            DA.GetData(13, ref enableNotch);
            DA.GetData(14, ref notchThreshold);
            DA.GetData(15, ref notchHeightFrac);
            DA.GetData(16, ref notchWidthFrac);

            DA.GetData(17, ref floorToFloor);
            if (floorToFloor <= 0.01) floorToFloor = 3.5;

            double siteCoveragePercent = 100.0;
            DA.GetData(18, ref siteCoveragePercent);
            siteCoveragePercent = Clamp(siteCoveragePercent, 1.0, 100.0);

            double maxHeightFraction = 0.5;
            DA.GetData(19, ref maxHeightFraction);
            maxHeightFraction = Clamp(maxHeightFraction, 0.1, 1.0);

            DA.GetData(20, ref minTerraceDepth);
            minTerraceDepth = Math.Max(0.0, minTerraceDepth);

            DA.GetData(21, ref enableTilt);
            DA.GetData(22, ref maxTiltDegrees);
            maxTiltDegrees = Math.Max(0.0, maxTiltDegrees);

            shiftIntensity = Clamp(shiftIntensity, 0.0, 1.0);
            double maxOffsetFrac = shiftIntensity * 0.5; // ShiftIntensity=1.0 -> 50% of profile depth

            slices = Math.Max(2, Math.Min(12, slices));

            if (siteCoveragePercent < 99.99)
            {
                BoundingBox siteBB = profile.GetBoundingBox(false);
                Point3d siteCenter = new Point3d((siteBB.Min.X + siteBB.Max.X) / 2.0, (siteBB.Min.Y + siteBB.Max.Y) / 2.0, 0);
                double linearScale = Math.Sqrt(siteCoveragePercent / 100.0);
                Curve scaledFootprint = profile.DuplicateCurve();
                scaledFootprint.Transform(Transform.Scale(siteCenter, linearScale));
                profile = scaledFootprint;
            }

            double latRad = lat * Math.PI / 180.0;
            double summerAlt = Math.Asin(Math.Sin(latRad) * Math.Sin(23.45 * Math.PI / 180.0) + Math.Cos(latRad) * Math.Cos(23.45 * Math.PI / 180.0));
            double winterAlt = Math.Asin(Math.Sin(latRad) * Math.Sin(-23.45 * Math.PI / 180.0) + Math.Cos(latRad) * Math.Cos(-23.45 * Math.PI / 180.0));

            double requiredH = Clamp(spaceToShade * Math.Tan(summerAlt), baseH, maxH * maxHeightFraction);

            int maxFloors = Math.Max(1, (int)Math.Floor(maxH / floorToFloor));
            int baseFloors = Math.Max(1, (int)Math.Round(baseH / floorToFloor));
            baseFloors = Math.Min(baseFloors, maxFloors);

            var sliceHeights = new List<double>();
            var floorCounts = new List<int>();
            int prevFloors = 0;
            for (int i = 0; i < slices; i++)
            {
                double t = (double)i / (slices - 1);
                double hRaw = baseH + (requiredH - baseH) * Math.Pow(t, 0.6);
                int floors = Math.Max(1, (int)Math.Round(hRaw / floorToFloor));
                floors = Clamp(floors, baseFloors, maxFloors);
                if (floors < prevFloors) floors = prevFloors;
                floors = Math.Min(floors, maxFloors);
                prevFloors = floors;

                double h = floors * floorToFloor;
                floorCounts.Add(floors);
                sliceHeights.Add(Math.Round(h, 2));
            }
            double topZ = sliceHeights[sliceHeights.Count - 1];

            double windFromRad = windFromDir * Math.PI / 180.0;
            Vector3d windFromVec = new Vector3d(Math.Sin(windFromRad), Math.Cos(windFromRad), 0);
            Vector3d windToVec = -windFromVec;
            if (!windToVec.Unitize()) windToVec = new Vector3d(0, -1, 0);

            double orientRad = orientation * Math.PI / 180.0;
            var buildingAxis = new Vector3d(-Math.Sin(orientRad), Math.Cos(orientRad), 0);

            BoundingBox profBB = profile.GetBoundingBox(false);
            double profileDepth = Math.Abs(profBB.Max.Y - profBB.Min.Y);
            double avgHalfExtent = ((profBB.Max.X - profBB.Min.X) + (profBB.Max.Y - profBB.Min.Y)) / 4.0;

            var polyline = profile.ToPolyline(0.01, 0.5, 0.001, 1000);
            Point3d profileCenter = Point3d.Origin;
            if (polyline != null)
            {
                var poly = polyline.ToPolyline();
                if (poly != null)
                {
                    int ptCount = 0;
                    foreach (var pt in poly) { profileCenter += new Point3d(pt.X, pt.Y, 0); ptCount++; }
                    if (ptCount > 0) profileCenter = new Point3d(profileCenter.X / ptCount, profileCenter.Y / ptCount, 0);
                }
            }

            double rawAngle = Vector3d.VectorAngle(buildingAxis, windToVec, Plane.WorldXY);
            if (rawAngle > Math.PI) rawAngle -= 2 * Math.PI;
            double windAlignAngleRad = rawAngle;

            double solarWeight = Clamp(Math.Sin(summerAlt), 0.0, 1.0);
            Point3d sharedPivot = new Point3d(profileCenter.X, profileCenter.Y, 0); // same point, every slice -- tilt pivot

            var massBreps = new List<Brep>();
            var profCurves = new List<Curve>();
            var zoneHOut = new List<double>();
            var windFactorsOut = new List<double>();
            var tiltDegreesOut = new List<double>();
            int solidCount = 0;
            int failCount = 0;
            bool notchApplied = false;

            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            // ── PER-SLICE MASSING LOOP ──────────────────────────────────────
            // Everything that depends on slice index i lives in here. Nothing
            // that reuses "i" as a loop variable is allowed inside this block --
            // that's exactly what caused CS0136 last time (the report loop got
            // pasted in here by mistake). massBreps.Add(brep) MUST happen inside
            // this loop, once per slice, or Massing comes out empty.
            for (int i = 0; i < slices; i++)
            {
                double zoneH = sliceHeights[i];
                double windFactor = Math.Pow(zoneH / Math.Max(topZ, 0.01), terrainExp);
                windFactorsOut.Add(Math.Round(windFactor, 3));

                var zoneCrv = profile.DuplicateCurve();
                var bb0 = zoneCrv.GetBoundingBox(false);
                if (Math.Abs(bb0.Min.Z) > 0.001)
                    zoneCrv.Transform(Transform.Translation(0, 0, -bb0.Min.Z));

                if (shiftSlices)
                {
                    double windOffset = windFactor * profileDepth * maxOffsetFrac;
                    if (windOffset > 0.001 && windOffset < minTerraceDepth)
                        windOffset = (windFactor > 0.35) ? minTerraceDepth : 0.0;

                    if (windOffset > 0.001)
                        zoneCrv.Transform(Transform.Translation(windToVec.X * windOffset, windToVec.Y * windOffset, 0));
                }
                else
                {
                    double maxRotRad = maxRotDeg * Math.PI / 180.0;
                    double cappedAlign = Clamp(windAlignAngleRad, -maxRotRad, maxRotRad);
                    double rotAngle = windFactor * cappedAlign;
                    zoneCrv.Transform(Transform.Rotation(rotAngle, Vector3d.ZAxis, profileCenter));
                }

                // TILT: in-plan twist about the shared ground pivot, applied to
                // the flat curve BEFORE extrusion -- rotates about Z (vertical
                // axis), independent of and additive to the shift/rotate mode
                // above. Produces "each floor twists left/right sharing a
                // common center," NOT a section-view lean.
                double appliedTiltDeg = 0.0;
                if (enableTilt && maxTiltDegrees > 0.001)
                {
                    double maxTiltRad = maxTiltDegrees * Math.PI / 180.0;
                    double cappedAlign = Clamp(windAlignAngleRad, -maxTiltRad, maxTiltRad);
                    double tiltRad = cappedAlign * windFactor * solarWeight;

                    zoneCrv.Transform(Transform.Rotation(tiltRad, Vector3d.ZAxis, sharedPivot));
                    appliedTiltDeg = tiltRad * 180.0 / Math.PI;
                }
                tiltDegreesOut.Add(Math.Round(appliedTiltDeg, 2));

                if (!zoneCrv.IsClosed) zoneCrv.MakeClosed(0.01);

                var extrusionVec = new Vector3d(0, 0, zoneH);
                Surface srf = Surface.CreateExtrusion(zoneCrv, extrusionVec);
                if (srf == null) { failCount++; profCurves.Add(zoneCrv); zoneHOut.Add(zoneH); continue; }

                Brep brep = srf.ToBrep();
                if (brep == null) { failCount++; profCurves.Add(zoneCrv); zoneHOut.Add(zoneH); continue; }

                Brep capped = brep.CapPlanarHoles(0.001);
                if (capped != null) brep = capped;
                if (!brep.IsSolid) { brep.JoinNakedEdges(0.01); brep.Faces.ShrinkFaces(); }

                bool isTopSlice = (i == slices - 1);
                if (isTopSlice && enableNotch)
                {
                    double topWindSpeed = windSpeedRef * Math.Pow(Math.Max(topZ, 0.01) / 10.0, terrainExp);
                    if (topWindSpeed >= notchThreshold)
                    {
                        Brep notched = TryCutTopNotch(brep, profileCenter, avgHalfExtent, profBB, windToVec, zoneH, notchHeightFrac, notchWidthFrac, tol);
                        if (notched != null) { brep = notched; notchApplied = true; }
                    }
                }

                // THE LINE THAT WAS MISSING: without this, massBreps stays
                // empty forever regardless of everything else working.
                massBreps.Add(brep);
                if (brep.IsSolid || notchApplied) solidCount++; else failCount++;

                profCurves.Add(zoneCrv);
                zoneHOut.Add(zoneH);
            }
            // ── END PER-SLICE LOOP ──────────────────────────────────────────

            var floorZLevels = new List<double>();
            double totalHeight = sliceHeights[sliceHeights.Count - 1];
            for (double z = 0; z < totalHeight; z += floorToFloor)
                floorZLevels.Add(Math.Round(z, 3));

            double reportTopWindSpeed = windSpeedRef * Math.Pow(Math.Max(topZ, 0.01) / 10.0, terrainExp);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== SOLVEFORM SECTION ANALYSIS ==");
            sb.AppendLine($"   [SOLAR]");
            sb.AppendLine($"   Latitude: {lat:F1}deg | Summer sun angle: {summerAlt * 180 / Math.PI:F1}deg | Winter: {winterAlt * 180 / Math.PI:F1}deg");
            sb.AppendLine($"   Required height: {requiredH:F1}m | Floor-to-floor: {floorToFloor:F2}m | Site coverage: {siteCoveragePercent:F0}%");
            sb.AppendLine();
            sb.AppendLine($"   [WIND / SHIFT]");
            sb.AppendLine($"   Wind from: {windFromDir:F0}deg | Terrain exp: {terrainExp:F2} | Wind@top: {reportTopWindSpeed:F1} m/s");
            sb.AppendLine($"   ShiftIntensity: {shiftIntensity:F2} (max offset = {maxOffsetFrac * 100:F0}% of profile depth) | MinTerraceDepth: {minTerraceDepth:F2}m");
            sb.AppendLine($"   Notch threshold: {notchThreshold:F1} m/s -> {(reportTopWindSpeed >= notchThreshold ? "QUALIFIES" : "does not qualify")} | Applied: {notchApplied}");
            sb.AppendLine();
            sb.AppendLine($"   [TILT]  EnableTilt: {enableTilt} | MaxTiltDegrees: {maxTiltDegrees:F1} | SolarWeight: {solarWeight:F2}");
            sb.AppendLine();
            sb.AppendLine($"   Mode: {(shiftSlices ? "Wind-directed shift" : "Wind-directed rotation")} | Slices: {slices} | Solids: {solidCount}/{slices}");
            sb.AppendLine();
            sb.AppendLine("   Per-slice (floors, height, windFactor, tilt):");
            for (int i = 0; i < sliceHeights.Count; i++)
                sb.AppendLine($"     Slice {i + 1,2}: {floorCounts[i],2} floors ({sliceHeights[i]:F1}m) | wf {windFactorsOut[i]:F2} | tilt {tiltDegreesOut[i]:F2}deg");

            DA.SetDataList(0, massBreps);
            DA.SetDataList(1, profCurves);
            DA.SetDataList(2, zoneHOut);
            DA.SetData(3, sb.ToString());
            DA.SetDataList(4, floorZLevels);
            DA.SetDataList(5, windFactorsOut);
            DA.SetData(6, notchApplied);
            DA.SetDataList(7, floorCounts);
            DA.SetDataList(8, tiltDegreesOut);
        }

        private Brep TryCutTopNotch(Brep slabBrep, Point3d profileCenter, double avgHalfExtent, BoundingBox profBB,
            Vector3d windToVec, double zoneH, double notchHeightFrac, double notchWidthFrac, double tol)
        {
            try
            {
                double notchHeight = zoneH * Math.Max(0.01, Math.Min(0.9, notchHeightFrac));
                double notchBaseZ = zoneH - notchHeight;
                double diag = profBB.Diagonal.Length;
                double longHalf = Math.Max(diag, 1.0);
                double widthHalf = Math.Max(avgHalfExtent * notchWidthFrac, 0.5);

                Vector3d xAxis = windToVec; if (xAxis.IsZero) xAxis = Vector3d.YAxis; xAxis.Unitize();
                Vector3d yAxis = Vector3d.CrossProduct(Vector3d.ZAxis, xAxis); if (yAxis.IsZero) yAxis = Vector3d.XAxis; yAxis.Unitize();

                Point3d cutterOrigin = new Point3d(profileCenter.X, profileCenter.Y, notchBaseZ);
                Plane cutterPlane = new Plane(cutterOrigin, xAxis, yAxis);
                var cutterBox = new Box(cutterPlane, new Interval(-longHalf, longHalf), new Interval(-widthHalf, widthHalf), new Interval(0, notchHeight));
                Brep cutterBrep = cutterBox.ToBrep();
                if (cutterBrep == null) return null;

                Brep[] result = Brep.CreateBooleanDifference(new List<Brep> { slabBrep }, new List<Brep> { cutterBrep }, tol);
                if (result == null || result.Length == 0) return null;

                Brep best = null; double bestVol = 0;
                foreach (var b in result)
                {
                    if (b == null) continue;
                    var vmp = VolumeMassProperties.Compute(b);
                    double v = vmp != null ? Math.Abs(vmp.Volume) : 0;
                    if (v > bestVol) { bestVol = v; best = b; }
                }
                return best;
            }
            catch { return null; }
        }

        private double Clamp(double val, double min, double max) => Math.Max(min, Math.Min(max, val));
        private int Clamp(int val, int min, int max) => Math.Max(min, Math.Min(max, val));

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("F6A7B8C9-D0E1-2345-FABC-456789012345");
    }
}