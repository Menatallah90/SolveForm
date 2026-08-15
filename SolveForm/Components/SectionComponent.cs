using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;
using SolveForm.Attributes;

namespace SolveForm.Components
{
    public class SectionComponent : GH_Component
    {
        public SectionComponent()
          : base("SolveForm Section", "SFSec",
              "Generates climate-responsive stepped massing. Height is solar-driven " +
              "(shading requirement), snapped to whole floor multiples. Per-slice offset/rotation " +
              "and the top-band notch are wind-driven (boundary-layer wind speed increases with height).",
              "SolveForm", "Optimization")
        { }

        public override void CreateAttributes()
        {
            m_attributes = new BlackComponentAttributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // -- EXISTING (unchanged indices -- do not renumber, Unify/Floors depend on these) --
            pManager.AddCurveParameter("Profile", "Prof", "Profile curve from SolveForm Solar", GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "Lat", "Site latitude", GH_ParamAccess.item, 24.7);
            pManager.AddNumberParameter("Orientation", "Orient", "Building orientation (degrees)", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("BaseHeight", "Hbase", "Minimum zone height (m)", GH_ParamAccess.item, 6.0);
            pManager.AddNumberParameter("MaxHeight", "Hmax", "Maximum zone height (m)", GH_ParamAccess.item, 30.0);
            pManager.AddNumberParameter("SpaceToShade", "Shade", "Width of outdoor space to shade (m)", GH_ParamAccess.item, 12.0);
            pManager.AddIntegerParameter("Slices", "Slices", "Number of height slices (2-12)", GH_ParamAccess.item, 6);
            pManager.AddBooleanParameter("ShiftSlices", "Shift", "True=wind-directed N-S style shift. False=wind-directed rotation.", GH_ParamAccess.item, true);

            // -- wind inputs --
            pManager.AddNumberParameter("PrevailingWindDir", "WDir",
                "Compass bearing wind blows FROM (0=N, 90=E, 180=S, 270=W). From EPW site data.",
                GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("WindSpeedRef10m", "WSpd",
                "Average wind speed (m/s) at 10m reference height, from EPW site data.",
                GH_ParamAccess.item, 6.0);
            pManager.AddNumberParameter("TerrainExponent", "Alpha",
                "Boundary-layer roughness exponent. ~0.10 open water, ~0.22 open/suburban, ~0.33+ dense urban.",
                GH_ParamAccess.item, 0.22);
            pManager.AddNumberParameter("MaxOffsetFraction", "MaxOff",
                "Max shift/taper as a fraction of profile depth, applied at the slice with the highest wind factor.",
                GH_ParamAccess.item, 0.2);
            pManager.AddNumberParameter("MaxRotationDegrees", "MaxRot",
                "Max progressive rotation (degrees) toward wind-optimal alignment, used in Rotate mode only.",
                GH_ParamAccess.item, 10.0);

            // -- top-band notch (Pearl River Tower / Shanghai WFC style through-opening) --
            pManager.AddBooleanParameter("EnableTopNotch", "Notch",
                "If true, cut a through-mass opening in the top slice IF wind at that height exceeds the threshold.",
                GH_ParamAccess.item, true);
            pManager.AddNumberParameter("NotchWindThreshold", "NThresh",
                "Minimum computed wind speed (m/s) at the top slice's height required to justify a through-opening.",
                GH_ParamAccess.item, 10.0);
            pManager.AddNumberParameter("NotchHeightFraction", "NHeight",
                "Notch height as a fraction of the top slice's own height.",
                GH_ParamAccess.item, 0.15);
            pManager.AddNumberParameter("NotchWidthFraction", "NWidth",
                "Notch width as a fraction of the profile's average half-extent, measured across the wind axis.",
                GH_ParamAccess.item, 0.5);

            // -- NEW (index 17) -- floor-to-floor module, everything downstream snaps to this --
            pManager.AddNumberParameter("FloorToFloorHeight", "F2F",
                "Floor-to-floor height (m). All slice heights snap to whole multiples of this, so FloorZLevels " +
                "always lands exactly on a slice boundary.",
                GH_ParamAccess.item, 3.5);

            // -- NEW (index 18) -- site coverage --
            pManager.AddNumberParameter("SiteCoveragePercent", "Cov",
                "Percent of the site boundary (the Profile curve, treated as the site) actually built on, 0-100. " +
                "100 = full site coverage. The footprint is scaled uniformly about the site center to match this area ratio.",
                GH_ParamAccess.item, 100.0);

            // -- NEW (index 19) -- forces genuinely stepped massing instead of a tall block --
            pManager.AddNumberParameter("MaxHeightFraction", "MaxHFrac",
                "Caps the tallest point of the section to this fraction of MaxHeight (default 0.5), so the massing " +
                "actually reads as stepped/terraced rather than a block that merely tapers at the very top. Backed by " +
                "solar-envelope precedent (Knowles, 1978) and setback/shadow studies showing setback depth measurably " +
                "affects neighboring daylight access -- this isn't just a look, it's why terraced massing is treated " +
                "as more climate-responsible than a flat block of the same volume.",
                GH_ParamAccess.item, 0.5);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // -- EXISTING (unchanged indices) --
            pManager.AddBrepParameter("Massing", "Mass", "Stepped zone masses -- wire to SF_Unify", GH_ParamAccess.list);      // 0
            pManager.AddCurveParameter("Profiles", "Prof", "Zone profile curves", GH_ParamAccess.list);                        // 1
            pManager.AddNumberParameter("ZoneHeights", "ZH", "Height per zone", GH_ParamAccess.list);                          // 2
            pManager.AddTextParameter("Report", "Rep", "Section analysis report", GH_ParamAccess.item);                        // 3
            pManager.AddNumberParameter("FloorZLevels", "FZ", "Z height of each floor slab in world space", GH_ParamAccess.list); // 4

            // -- appended, safe for existing wiring --
            pManager.AddNumberParameter("WindFactors", "WF", "Normalized wind factor per slice (0-1, 1 = top slice)", GH_ParamAccess.list); // 5
            pManager.AddBooleanParameter("NotchApplied", "NApp", "Whether the top-band through-notch was actually cut", GH_ParamAccess.item); // 6
            pManager.AddIntegerParameter("FloorCounts", "FC", "Whole floor count per slice, after snapping", GH_ParamAccess.list); // 7
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve profile = null;
            double lat = 24.7, orientation = 0.0, baseH = 6.0, maxH = 30.0, spaceToShade = 12.0;
            int slices = 6;
            bool shiftSlices = true;

            double windFromDir = 0.0, windSpeedRef = 6.0, terrainExp = 0.22, maxOffsetFrac = 0.2, maxRotDeg = 10.0;
            bool enableNotch = true;
            double notchThreshold = 10.0, notchHeightFrac = 0.15, notchWidthFrac = 0.5;
            double floorToFloor = 3.5;

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
            DA.GetData(11, ref maxOffsetFrac);
            DA.GetData(12, ref maxRotDeg);

            DA.GetData(13, ref enableNotch);
            DA.GetData(14, ref notchThreshold);
            DA.GetData(15, ref notchHeightFrac);
            DA.GetData(16, ref notchWidthFrac);

            DA.GetData(17, ref floorToFloor);
            if (floorToFloor <= 0.01) floorToFloor = 3.5; // guard against a bad/zero input killing the whole solve

            double siteCoveragePercent = 100.0;
            DA.GetData(18, ref siteCoveragePercent);
            siteCoveragePercent = Clamp(siteCoveragePercent, 1.0, 100.0);

            double maxHeightFraction = 0.5;
            DA.GetData(19, ref maxHeightFraction);
            maxHeightFraction = Clamp(maxHeightFraction, 0.1, 1.0);

            slices = Math.Max(2, Math.Min(12, slices));

            // -- SITE COVERAGE -- scale the incoming Profile (treated as the site boundary) inward,
            // uniformly about its center, so footprint AREA matches the requested coverage percent.
            // Area scales with the square of linear scale, so we take the square root here.
            if (siteCoveragePercent < 99.99)
            {
                BoundingBox siteBB = profile.GetBoundingBox(false);
                Point3d siteCenter = new Point3d(
                    (siteBB.Min.X + siteBB.Max.X) / 2.0,
                    (siteBB.Min.Y + siteBB.Max.Y) / 2.0, 0);

                double coverageFraction = siteCoveragePercent / 100.0;
                double linearScale = Math.Sqrt(coverageFraction);

                Curve scaledFootprint = profile.DuplicateCurve();
                scaledFootprint.Transform(Transform.Scale(siteCenter, linearScale));
                profile = scaledFootprint;
            }

            // -- SOLAR GEOMETRY (unchanged -- drives total required height) --
            double latRad = lat * Math.PI / 180.0;
            double summerAlt = Math.Asin(
                Math.Sin(latRad) * Math.Sin(23.45 * Math.PI / 180.0) +
                Math.Cos(latRad) * Math.Cos(23.45 * Math.PI / 180.0));
            double winterAlt = Math.Asin(
                Math.Sin(latRad) * Math.Sin(-23.45 * Math.PI / 180.0) +
                Math.Cos(latRad) * Math.Cos(-23.45 * Math.PI / 180.0));

            double requiredH = Clamp(spaceToShade * Math.Tan(summerAlt), baseH, maxH * maxHeightFraction);

            // -- SLICE HEIGHTS -- solar-driven curve, NOW SNAPPED to whole floor-count multiples --
            // This is the actual fix: previously this loop produced arbitrary decimals with zero
            // relationship to floor-to-floor spacing, and FloorZLevels below walked up in fixed 3.5m
            // steps independently -- so floor slabs weren't even guaranteed to land on slice
            // boundaries. Now both come from the same snapped floor-count list.
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
                if (floors < prevFloors) floors = prevFloors; // monotonic non-decreasing -- never step down
                floors = Math.Min(floors, maxFloors);
                prevFloors = floors;

                double h = floors * floorToFloor;
                floorCounts.Add(floors);
                sliceHeights.Add(Math.Round(h, 2));
            }
            double topZ = sliceHeights[sliceHeights.Count - 1];

            // -- WIND VECTOR (compass bearing -> world XY, independent of building Orientation) --
            double windFromRad = windFromDir * Math.PI / 180.0;
            Vector3d windFromVec = new Vector3d(Math.Sin(windFromRad), Math.Cos(windFromRad), 0);
            Vector3d windToVec = -windFromVec; // direction wind blows TOWARD
            if (!windToVec.Unitize()) windToVec = new Vector3d(0, -1, 0);

            // -- ORIENTATION + PROFILE ANALYSIS (unchanged) --
            double orientRad = orientation * Math.PI / 180.0;
            var buildingAxis = new Vector3d(-Math.Sin(orientRad), Math.Cos(orientRad), 0); // "depth" axis reference

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
                    foreach (var pt in poly)
                    {
                        profileCenter += new Point3d(pt.X, pt.Y, 0);
                        ptCount++;
                    }
                    if (ptCount > 0)
                        profileCenter = new Point3d(profileCenter.X / ptCount, profileCenter.Y / ptCount, 0);
                }
            }

            // Signed angle from buildingAxis to windToVec, about world Z (radians, -pi..pi)
            double rawAngle = Vector3d.VectorAngle(buildingAxis, windToVec, Plane.WorldXY);
            if (rawAngle > Math.PI) rawAngle -= 2 * Math.PI;
            double windAlignAngleRad = rawAngle; // how far building axis would need to rotate to face the wind

            // -- BUILD SLICES --
            var massBreps = new List<Brep>();
            var profCurves = new List<Curve>();
            var zoneHOut = new List<double>();
            var windFactorsOut = new List<double>();
            int solidCount = 0;
            int failCount = 0;
            bool notchApplied = false;

            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            for (int i = 0; i < slices; i++)
            {
                double zoneH = sliceHeights[i];
                double t = (double)i / Math.Max(1, slices - 1);

                // Wind factor: boundary-layer power law, normalized so top slice = 1.0
                double windFactor = Math.Pow(zoneH / Math.Max(topZ, 0.01), terrainExp);
                windFactorsOut.Add(Math.Round(windFactor, 3));

                var zoneCrv = profile.DuplicateCurve();

                var bb0 = zoneCrv.GetBoundingBox(false);
                if (Math.Abs(bb0.Min.Z) > 0.001)
                    zoneCrv.Transform(Transform.Translation(0, 0, -bb0.Min.Z));

                if (shiftSlices)
                {
                    // Wind-directed shift: leeward offset, magnitude scaled by wind factor at this height
                    double windOffset = windFactor * profileDepth * maxOffsetFrac;
                    if (windOffset > 0.001)
                        zoneCrv.Transform(Transform.Translation(
                            windToVec.X * windOffset, windToVec.Y * windOffset, 0));
                }
                else
                {
                    // Wind-directed progressive rotation toward wind-optimal alignment (Burj Khalifa-style twist)
                    double maxRotRad = maxRotDeg * Math.PI / 180.0;
                    double cappedAlign = Clamp(windAlignAngleRad, -maxRotRad, maxRotRad);
                    double rotAngle = windFactor * cappedAlign;
                    zoneCrv.Transform(Transform.Rotation(rotAngle, Vector3d.ZAxis, profileCenter));
                }

                if (!zoneCrv.IsClosed)
                    zoneCrv.MakeClosed(0.01);

                var extrusionVec = new Vector3d(0, 0, zoneH);
                Surface srf = Surface.CreateExtrusion(zoneCrv, extrusionVec);

                if (srf == null) { failCount++; profCurves.Add(zoneCrv); zoneHOut.Add(zoneH); continue; }

                Brep brep = srf.ToBrep();
                if (brep == null) { failCount++; profCurves.Add(zoneCrv); zoneHOut.Add(zoneH); continue; }

                Brep capped = brep.CapPlanarHoles(0.001);
                if (capped != null) brep = capped;

                if (!brep.IsSolid)
                {
                    brep.JoinNakedEdges(0.01);
                    brep.Faces.ShrinkFaces();
                }

                // -- TOP-BAND NOTCH (only on the tallest slice, only if wind qualifies) --
                bool isTopSlice = (i == slices - 1);
                if (isTopSlice && enableNotch)
                {
                    double topWindSpeed = windSpeedRef * Math.Pow(Math.Max(topZ, 0.01) / 10.0, terrainExp);
                    if (topWindSpeed >= notchThreshold)
                    {
                        Brep notched = TryCutTopNotch(brep, profileCenter, avgHalfExtent, profBB,
                            windToVec, zoneH, notchHeightFrac, notchWidthFrac, tol);
                        if (notched != null)
                        {
                            brep = notched;
                            notchApplied = true;
                        }
                    }
                }

                if (brep.IsSolid || notchApplied) { massBreps.Add(brep); solidCount++; }
                else { massBreps.Add(brep); failCount++; }

                profCurves.Add(zoneCrv);
                zoneHOut.Add(zoneH);
            }

            // -- FLOOR Z LEVELS -- now guaranteed aligned to slice boundaries, since sliceHeights
            // above are themselves whole multiples of floorToFloor.
            var floorZLevels = new List<double>();
            double floorSpacing = floorToFloor; // was hardcoded 3.5, now the actual live input
            double totalHeight = sliceHeights[sliceHeights.Count - 1];
            for (double z = 0; z < totalHeight; z += floorSpacing)
                floorZLevels.Add(Math.Round(z, 3));

            // -- REPORT --
            double reportTopWindSpeed = windSpeedRef * Math.Pow(Math.Max(topZ, 0.01) / 10.0, terrainExp);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== SOLVEFORM SECTION ANALYSIS ==");
            sb.AppendLine($"   [SOLAR -- drives total height]");
            sb.AppendLine($"   Latitude:         {lat:F1}deg");
            sb.AppendLine($"   Summer sun angle: {summerAlt * 180 / Math.PI:F1}deg");
            sb.AppendLine($"   Winter sun angle: {winterAlt * 180 / Math.PI:F1}deg");
            sb.AppendLine($"   Space to shade:   {spaceToShade:F1}m");
            sb.AppendLine($"   Required height:  {requiredH:F1}m (capped at {maxHeightFraction * 100:F0}% of MaxHeight={maxH:F1}m for genuine stepping)");
            sb.AppendLine($"   Floor-to-floor:   {floorToFloor:F2}m");
            sb.AppendLine($"   Site coverage:    {siteCoveragePercent:F0}% (footprint scaled accordingly)");
            sb.AppendLine();
            sb.AppendLine($"   [WIND -- drives per-slice offset/rotation + top notch]");
            sb.AppendLine($"   Wind from:        {windFromDir:F0}deg (blowing toward {NormalizeDeg(windFromDir + 180):F0}deg)");
            sb.AppendLine($"   Terrain exponent: {terrainExp:F2}");
            sb.AppendLine($"   Wind speed @top:  {reportTopWindSpeed:F1} m/s (ref {windSpeedRef:F1} m/s @10m)");
            sb.AppendLine($"   Notch threshold:  {notchThreshold:F1} m/s -> {(reportTopWindSpeed >= notchThreshold ? "QUALIFIES" : "does not qualify")}");
            sb.AppendLine($"   Notch applied:    {notchApplied}");
            sb.AppendLine();
            sb.AppendLine($"   Mode:             {(shiftSlices ? "Wind-directed shift" : "Wind-directed rotation")}");
            sb.AppendLine($"   Slices:           {slices}");
            sb.AppendLine($"   Solids built:     {solidCount} / {slices}");
            sb.AppendLine($"   Failed slices:    {failCount}");
            sb.AppendLine();
            sb.AppendLine("   Per-slice (floors, height, wind factor):");
            for (int i = 0; i < sliceHeights.Count; i++)
                sb.AppendLine($"     Slice {i + 1,2}: {floorCounts[i],2} floors  ({sliceHeights[i]:F1}m)  | windFactor {windFactorsOut[i]:F2}");

            // -- OUTPUT --
            DA.SetDataList(0, massBreps);
            DA.SetDataList(1, profCurves);
            DA.SetDataList(2, zoneHOut);
            DA.SetData(3, sb.ToString());
            DA.SetDataList(4, floorZLevels);

            DA.SetDataList(5, windFactorsOut);
            DA.SetData(6, notchApplied);
            DA.SetDataList(7, floorCounts);
        }

        /// <summary>
        /// Cuts a through-mass opening near the top of the tallest slice, aligned to the wind axis.
        /// Uses a solid Box cutter (same reliable technique as FormGenerator's courtyard cutter) --
        /// NOT the loft-based approach that failed in CutOpeningsComponent.
        /// Returns null if the boolean fails, so the caller can fall back to the uncut brep.
        /// </summary>
        private Brep TryCutTopNotch(Brep slabBrep, Point3d profileCenter, double avgHalfExtent,
            BoundingBox profBB, Vector3d windToVec, double zoneH,
            double notchHeightFrac, double notchWidthFrac, double tol)
        {
            try
            {
                double notchHeight = zoneH * Math.Max(0.01, Math.Min(0.9, notchHeightFrac));
                double notchBaseZ = zoneH - notchHeight;

                double diag = profBB.Diagonal.Length;
                double longHalf = Math.Max(diag, 1.0); // oversized -- guarantees a full through-cut
                double widthHalf = Math.Max(avgHalfExtent * notchWidthFrac, 0.5);

                Vector3d xAxis = windToVec;
                if (xAxis.IsZero) xAxis = Vector3d.YAxis;
                xAxis.Unitize();
                Vector3d yAxis = Vector3d.CrossProduct(Vector3d.ZAxis, xAxis);
                if (yAxis.IsZero) yAxis = Vector3d.XAxis;
                yAxis.Unitize();

                Point3d cutterOrigin = new Point3d(profileCenter.X, profileCenter.Y, notchBaseZ);
                Plane cutterPlane = new Plane(cutterOrigin, xAxis, yAxis);

                var cutterBox = new Box(cutterPlane,
                    new Interval(-longHalf, longHalf),
                    new Interval(-widthHalf, widthHalf),
                    new Interval(0, notchHeight));

                Brep cutterBrep = cutterBox.ToBrep();
                if (cutterBrep == null) return null;

                Brep[] result = Brep.CreateBooleanDifference(
                    new List<Brep> { slabBrep }, new List<Brep> { cutterBrep }, tol);

                if (result == null || result.Length == 0) return null;

                // Keep the largest fragment (the notched slab), discard any sliver fragments
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
            catch
            {
                return null; // defensive -- caller falls back to uncut brep
            }
        }

        private double Clamp(double val, double min, double max)
            => Math.Max(min, Math.Min(max, val));

        private int Clamp(int val, int min, int max)
            => Math.Max(min, Math.Min(max, val));

        private double NormalizeDeg(double deg)
        {
            deg = deg % 360;
            if (deg < 0) deg += 360;
            return deg;
        }

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("F6A7B8C9-D0E1-2345-FABC-456789012345");
    }
}