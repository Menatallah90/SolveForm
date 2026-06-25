using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class SectionComponent : GH_Component
    {
        public SectionComponent()
          : base("SolveForm Section", "SFSec",
              "Generates climate-responsive stepped massing based on shading requirements",
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
            pManager.AddBooleanParameter("ShiftSlices", "Shift", "True=shift N-S. False=rotate.", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Massing", "Mass", "Stepped zone masses — wire to SF_Unify", GH_ParamAccess.list);      // 0
            pManager.AddCurveParameter("Profiles", "Prof", "Zone profile curves", GH_ParamAccess.list);                        // 1
            pManager.AddNumberParameter("ZoneHeights", "ZH", "Height per zone", GH_ParamAccess.list);                          // 2
            pManager.AddTextParameter("Report", "Rep", "Section analysis report", GH_ParamAccess.item);                        // 3
            pManager.AddNumberParameter("FloorZLevels", "FZ", "Z height of each floor slab in world space", GH_ParamAccess.list); // 4
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve profile = null;
            double lat = 24.7, orientation = 0.0, baseH = 6.0, maxH = 30.0, spaceToShade = 12.0;
            int slices = 6;
            bool shiftSlices = true;

            if (!DA.GetData(0, ref profile) || profile == null) return;
            DA.GetData(1, ref lat);
            DA.GetData(2, ref orientation);
            DA.GetData(3, ref baseH);
            DA.GetData(4, ref maxH);
            DA.GetData(5, ref spaceToShade);
            DA.GetData(6, ref slices);
            DA.GetData(7, ref shiftSlices);

            slices = Math.Max(2, Math.Min(12, slices));

            // ── SOLAR GEOMETRY ─────────────────────────────────────────────
            double latRad = lat * Math.PI / 180.0;
            double summerAlt = Math.Asin(
                Math.Sin(latRad) * Math.Sin(23.45 * Math.PI / 180.0) +
                Math.Cos(latRad) * Math.Cos(23.45 * Math.PI / 180.0));
            double winterAlt = Math.Asin(
                Math.Sin(latRad) * Math.Sin(-23.45 * Math.PI / 180.0) +
                Math.Cos(latRad) * Math.Cos(-23.45 * Math.PI / 180.0));

            double requiredH = Clamp(spaceToShade * Math.Tan(summerAlt), baseH, maxH);

            // ── SLICE HEIGHTS ──────────────────────────────────────────────
            var sliceHeights = new List<double>();
            for (int i = 0; i < slices; i++)
            {
                double t = (double)i / (slices - 1);
                double h = baseH + (requiredH - baseH) * Math.Pow(t, 0.6);
                sliceHeights.Add(Math.Round(h, 1));
            }

            // ── ORIENTATION + PROFILE ANALYSIS ────────────────────────────
            double orientRad = orientation * Math.PI / 180.0;
            var northDir = new Vector3d(-Math.Sin(orientRad), Math.Cos(orientRad), 0);

            BoundingBox profBB = profile.GetBoundingBox(false);
            double profileDepth = Math.Abs(profBB.Max.Y - profBB.Min.Y);

            // Profile center for rotate mode
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
                        profileCenter = new Point3d(
                            profileCenter.X / ptCount,
                            profileCenter.Y / ptCount, 0);
                }
            }

            // ── BUILD SLICES ───────────────────────────────────────────────
            var massBreps = new List<Brep>();
            var profCurves = new List<Curve>();
            var zoneHOut = new List<double>();
            int solidCount = 0;
            int failCount = 0;

            for (int i = 0; i < slices; i++)
            {
                double zoneH = sliceHeights[i];
                double t = (double)i / Math.Max(1, slices - 1);

                var zoneCrv = profile.DuplicateCurve();

                // Flatten to Z=0 before any transform
                var bb0 = zoneCrv.GetBoundingBox(false);
                if (Math.Abs(bb0.Min.Z) > 0.001)
                    zoneCrv.Transform(Transform.Translation(0, 0, -bb0.Min.Z));

                if (shiftSlices)
                {
                    // Solar N-S offset: taller slices step back (north) proportionally
                    double solarOffset = t * profileDepth * 0.2;
                    if (solarOffset > 0.001)
                        zoneCrv.Transform(Transform.Translation(
                            northDir.X * solarOffset,
                            northDir.Y * solarOffset, 0));
                }
                else
                {
                    // Rotate mode: small rotation around profile centroid
                    double rotAngle = (t - 0.5) * 10.0 * Math.PI / 180.0;
                    zoneCrv.Transform(Transform.Rotation(
                        rotAngle, Vector3d.ZAxis, profileCenter));
                }

                // Rebuild curve to ensure it's closed + planar before extrusion
                if (!zoneCrv.IsClosed)
                    zoneCrv.MakeClosed(0.01);

                // Extrude
                var extrusionVec = new Vector3d(0, 0, zoneH);
                Surface srf = Surface.CreateExtrusion(zoneCrv, extrusionVec);

                if (srf == null) { failCount++; profCurves.Add(zoneCrv); zoneHOut.Add(zoneH); continue; }

                Brep brep = srf.ToBrep();
                if (brep == null) { failCount++; profCurves.Add(zoneCrv); zoneHOut.Add(zoneH); continue; }

                // Cap holes — this is what closes the top and bottom faces
                Brep capped = brep.CapPlanarHoles(0.001);
                if (capped != null) brep = capped;

                // Verify solid
                if (!brep.IsSolid)
                {
                    brep.JoinNakedEdges(0.01);
                    brep.Faces.ShrinkFaces();
                }

                if (brep.IsSolid)
                {
                    massBreps.Add(brep);
                    solidCount++;
                }
                else
                {
                    // Add anyway — Unify will attempt boolean, may still work
                    massBreps.Add(brep);
                    failCount++;
                }

                profCurves.Add(zoneCrv);
                zoneHOut.Add(zoneH);
            }

            // ── FLOOR Z LEVELS ─────────────────────────────────────────────
            // One Z level per floor across all zone heights stacked cumulatively
            var floorZLevels = new List<double>();
            double floorSpacing = 3.5; // fixed f2f
            double totalHeight = sliceHeights[sliceHeights.Count - 1];
            for (double z = 0; z < totalHeight; z += floorSpacing)
                floorZLevels.Add(Math.Round(z, 3));

            // ── REPORT ─────────────────────────────────────────────────────
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("══ SOLVEFORM SECTION ANALYSIS ══");
            sb.AppendLine($"   Latitude:         {lat:F1}°");
            sb.AppendLine($"   Summer sun angle: {summerAlt * 180 / Math.PI:F1}°");
            sb.AppendLine($"   Winter sun angle: {winterAlt * 180 / Math.PI:F1}°");
            sb.AppendLine($"   Space to shade:   {spaceToShade:F1}m");
            sb.AppendLine($"   Required height:  {requiredH:F1}m");
            sb.AppendLine($"   Orientation:      {orientation:F1}°");
            sb.AppendLine($"   Slices:           {slices}");
            sb.AppendLine($"   Mode:             {(shiftSlices ? "Shift" : "Rotate")}");
            sb.AppendLine($"   Solids built:     {solidCount} / {slices}");
            sb.AppendLine($"   Failed slices:    {failCount}");
            sb.AppendLine();
            sb.AppendLine("   Section gradient (S to N):");
            for (int i = 0; i < sliceHeights.Count; i++)
                sb.AppendLine($"     Slice {i + 1,2}: {sliceHeights[i]:F1}m");

            // ── OUTPUT ─────────────────────────────────────────────────────
            DA.SetDataList(0, massBreps);   // Brep list → SF_Unify
            DA.SetDataList(1, profCurves);  // Curve list
            DA.SetDataList(2, zoneHOut);    // double list
            DA.SetData(3, sb.ToString());   // string
            DA.SetDataList(4, floorZLevels); // double list  ← index 4, correct
        }

        private double Clamp(double val, double min, double max)
            => Math.Max(min, Math.Min(max, val));

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("F6A7B8C9-D0E1-2345-FABC-456789012345");
    }
}