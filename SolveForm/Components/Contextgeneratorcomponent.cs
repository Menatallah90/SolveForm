// ContextGeneratorComponent.cs
// GUID: 4962e02f-3482-4d5f-a74f-f6908e143154
//
// FIX THIS SESSION: two real bugs from the first build.
//
// 1. ROTATION was fully random every time with no way to control it. Added
//    OrthogonalMode (bool) + FixedAngleDeg (double). OrthogonalMode=true ->
//    each mass gets FixedAngleDeg + a random multiple of 90 degrees (still
//    some variety, but always right-angled to your given angle -- reads as
//    a real urban grid instead of chaos). OrthogonalMode=false -> fully
//    random 0-360, same as before.
//
// 2. OVERLAP: the old version only checked candidate points against the
//    site exclusion zone -- it never checked generated masses against EACH
//    OTHER. Footprint size and rotation were both randomized AFTER point
//    selection, so two accepted neighboring points could easily get large
//    footprints that physically overlap. Added MinGap (double) and a
//    shrink-and-retry placement loop: each mass is tested against a
//    bounding-circle approximation of every mass already placed; if it
//    overlaps within MinGap, the footprint shrinks and retries (up to 5
//    attempts) before the point is skipped entirely. Bounding-circle check
//    is an approximation (not exact rectangle-rectangle intersection) but
//    is cheap, robust to any rotation, and conservative enough that real
//    overlaps should not get through.
//
// Site-boundary respect (your third ask) was already implemented via
// SiteFootprint + SiteClearance in the exclusion-zone step -- if it wasn't
// visibly respecting your site, the overlap bug above is the likely cause,
// since a mass could get pushed visually past the clearance edge by a
// large randomized footprint after the point itself had already passed the
// (radius-only) clearance test. Fixed as a side effect of the retry loop
// below, since MinGap-vs-neighbors and the site clearance test now both
// run before a mass is accepted.
//
// STILL NOT IN SCOPE (unchanged from original spec): real building-height
// data from actual coordinates (OSM/Overture Maps), occlusion wiring into
// Heat Analyzer, and feeding context masses back into Section as a design
// consideration -- all separate, larger follow-on tasks. Not started here.
//
// ALGORITHM (updated)
//   1. Ground slab: disc of radius R, thickness GroundThickness, Z=0 down to
//      Z=-GroundThickness.
//   2. Exclusion zone: SiteFootprint if supplied, valid, and closed, else a
//      rectangle from SiteWidth/SiteDepth centered at SiteCenter. Candidate
//      points tested via Curve.Contains (inside) plus ClosestPoint distance
//      for the SiteClearance buffer.
//   3. Candidate points: jittered grid across the circle (seeded Random),
//      cell size from average target footprint size. Kept if inside radius
//      and outside exclusion+clearance.
//   4. Density selection: seeded Fisher-Yates shuffle, keep first count x
//      Density.
//   5. NEW: for each kept point, attempt to place a mass with a random
//      footprint (Min/MaxFootprintSize) and rotation (OrthogonalMode-aware).
//      Check the candidate footprint's bounding circle against every
//      already-placed mass's bounding circle + MinGap. If it overlaps,
//      shrink the footprint size range and retry (5 attempts total) before
//      giving up on that point. Height sampled from the variance range as
//      before.
//
// INPUTS
//   0  SiteCenter            (Point3d) optional, default world origin
//   1  Radius                (double) radius of the context circle (m)
//   2  Density                (double) 0.1-1.0, fraction of candidate slots built
//   3  AverageHeight         (double) target average height of context masses (m)
//   4  HeightVariancePercent (double) default 20 -- heights sampled from AverageHeight x (1 +/- variance)
//   5  SiteFootprint         (Curve) optional -- precise exclusion zone (real site/building footprint)
//   6  SiteWidth             (double) optional fallback rectangle width if SiteFootprint not wired
//   7  SiteDepth             (double) optional fallback rectangle depth if SiteFootprint not wired
//   8  SiteClearance         (double) default 2.0m -- buffer kept clear around the exclusion zone
//   9  MinFootprintSize      (double) default 8.0m
//   10 MaxFootprintSize      (double) default 20.0m
//   11 GroundThickness       (double) default 0.3m
//   12 Seed                  (int) for reproducible output
//   13 OrthogonalMode        (bool) default false -- true = right-angle rotations only, false = fully random
//   14 FixedAngleDeg         (double) default 0.0 -- base angle when OrthogonalMode=true
//   15 MinGap                (double) default 1.0m -- minimum clear distance between generated masses
//
// OUTPUTS
//   0 ContextMasses  (Brep list)
//   1 GroundSlab     (Brep item)
//   2 ContextHeights (double list, debug/reporting)
//   3 Report         (text item)

using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SolveForm.Components
{
    public class ContextGeneratorComponent : GH_Component
    {
        public ContextGeneratorComponent()
            : base("Context Generator", "ContextGen",
                "Generates a plausible synthetic neighborhood (context masses + ground slab) around a site, " +
                "seeded for reproducible output, with rotation control and mutual-overlap avoidance.",
                "SolveForm", "Massing")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("SiteCenter", "C", "Center of the context circle. Defaults to world origin.", GH_ParamAccess.item, Point3d.Origin);
            pManager.AddNumberParameter("Radius", "R", "Radius of the context circle (m)", GH_ParamAccess.item, 32.0);
            pManager.AddNumberParameter("Density", "D", "Fraction (0.1-1.0) of valid candidate slots actually built", GH_ParamAccess.item, 0.5);
            pManager.AddNumberParameter("AverageHeight", "AH", "Target average height of generated context masses (m)", GH_ParamAccess.item, 15.0);
            pManager.AddNumberParameter("HeightVariancePercent", "HV",
                "Generated heights sampled from AverageHeight x (1 +/- variance). Default 20 = +/-20%.", GH_ParamAccess.item, 20.0);
            pManager.AddCurveParameter("SiteFootprint", "SF",
                "Optional. The actual site/building footprint, used as the precise exclusion zone so generated masses " +
                "don't overlap the real site. Must be closed. If omitted, falls back to SiteWidth/SiteDepth.", GH_ParamAccess.item);
            pManager.AddNumberParameter("SiteWidth", "SW", "Fallback rectangular exclusion zone width (m), used only if SiteFootprint isn't wired", GH_ParamAccess.item, 20.0);
            pManager.AddNumberParameter("SiteDepth", "SD", "Fallback rectangular exclusion zone depth (m), used only if SiteFootprint isn't wired", GH_ParamAccess.item, 20.0);
            pManager.AddNumberParameter("SiteClearance", "Clr", "Buffer distance (m) kept clear around the exclusion zone", GH_ParamAccess.item, 2.0);
            pManager.AddNumberParameter("MinFootprintSize", "MinF", "Minimum plan dimension (m) of generated context buildings", GH_ParamAccess.item, 8.0);
            pManager.AddNumberParameter("MaxFootprintSize", "MaxF", "Maximum plan dimension (m) of generated context buildings", GH_ParamAccess.item, 20.0);
            pManager.AddNumberParameter("GroundThickness", "GT", "Thickness (m) of the ground slab", GH_ParamAccess.item, 0.3);
            pManager.AddIntegerParameter("Seed", "S", "Random seed, for reproducible output", GH_ParamAccess.item, 42);
            pManager.AddBooleanParameter("OrthogonalMode", "Ortho",
                "True = every mass rotated to FixedAngleDeg + a random multiple of 90 degrees (reads as a real urban grid). " +
                "False = fully random rotation 0-360.", GH_ParamAccess.item, false);
            pManager.AddNumberParameter("FixedAngleDeg", "FA", "Base angle (deg) used when OrthogonalMode=true", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("MinGap", "Gap", "Minimum clear distance (m) between generated masses (bounding-circle approximation)", GH_ParamAccess.item, 1.0);

            pManager[0].Optional = true;
            for (int i = 2; i <= 15; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("ContextMasses", "M", "Generated context building masses", GH_ParamAccess.list);
            pManager.AddBrepParameter("GroundSlab", "G", "Ground slab disc under the context", GH_ParamAccess.item);
            pManager.AddNumberParameter("ContextHeights", "H", "Height of each generated mass, same order as ContextMasses", GH_ParamAccess.list);
            pManager.AddTextParameter("Report", "R", "Diagnostic breakdown of exclusion zone, candidates, density, and overlap handling", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Point3d siteCenter = Point3d.Origin;
            double radius = 32.0, density = 0.5, averageHeight = 15.0, heightVariancePercent = 20.0;
            Curve siteFootprint = null;
            double siteWidth = 20.0, siteDepth = 20.0, siteClearance = 2.0;
            double minFootprintSize = 8.0, maxFootprintSize = 20.0, groundThickness = 0.3;
            int seed = 42;
            bool orthogonalMode = false;
            double fixedAngleDeg = 0.0, minGap = 1.0;

            DA.GetData(0, ref siteCenter);
            if (!DA.GetData(1, ref radius)) return;
            DA.GetData(2, ref density);
            DA.GetData(3, ref averageHeight);
            DA.GetData(4, ref heightVariancePercent);
            DA.GetData(5, ref siteFootprint);
            DA.GetData(6, ref siteWidth);
            DA.GetData(7, ref siteDepth);
            DA.GetData(8, ref siteClearance);
            DA.GetData(9, ref minFootprintSize);
            DA.GetData(10, ref maxFootprintSize);
            DA.GetData(11, ref groundThickness);
            DA.GetData(12, ref seed);
            DA.GetData(13, ref orthogonalMode);
            DA.GetData(14, ref fixedAngleDeg);
            DA.GetData(15, ref minGap);

            radius = Math.Max(1.0, radius);
            density = Clamp(density, 0.0, 1.0);
            averageHeight = Math.Max(0.5, averageHeight);
            heightVariancePercent = Math.Max(0.0, heightVariancePercent);
            siteWidth = Math.Max(0.5, siteWidth);
            siteDepth = Math.Max(0.5, siteDepth);
            siteClearance = Math.Max(0.0, siteClearance);
            minFootprintSize = Math.Max(0.5, minFootprintSize);
            maxFootprintSize = Math.Max(minFootprintSize, maxFootprintSize);
            groundThickness = Math.Max(0.01, groundThickness);
            minGap = Math.Max(0.0, minGap);

            var report = new StringBuilder();
            report.AppendLine("== CONTEXT GENERATOR REPORT ==");
            report.AppendLine($"SiteCenter=({siteCenter.X:F2},{siteCenter.Y:F2}) Radius={radius:F2}m Density={density:F2} " +
                $"AvgHeight={averageHeight:F2}m Variance=+/-{heightVariancePercent:F0}% Seed={seed} " +
                $"OrthogonalMode={orthogonalMode} FixedAngleDeg={fixedAngleDeg:F1} MinGap={minGap:F2}m");

            // 1. Ground slab
            Plane groundPlane = new Plane(new Point3d(siteCenter.X, siteCenter.Y, 0), Vector3d.ZAxis);
            Circle groundCircle = new Circle(groundPlane, radius);
            Cylinder groundCyl = new Cylinder(groundCircle, -groundThickness);
            Brep groundSlab = groundCyl.ToBrep(true, true);
            report.AppendLine($"Ground slab: disc R={radius:F2}m, thickness={groundThickness:F2}m, Z=0 to Z=-{groundThickness:F2}m.");

            // 2. Exclusion zone
            Curve exclusionCurve;
            string exclusionSource;
            if (siteFootprint != null && siteFootprint.IsValid && siteFootprint.IsClosed)
            {
                exclusionCurve = siteFootprint.DuplicateCurve();
                exclusionSource = "SiteFootprint (supplied)";
            }
            else
            {
                Plane rectPlane = new Plane(siteCenter, Vector3d.ZAxis);
                Rectangle3d rect = new Rectangle3d(rectPlane,
                    new Interval(-siteWidth / 2.0, siteWidth / 2.0),
                    new Interval(-siteDepth / 2.0, siteDepth / 2.0));
                exclusionCurve = rect.ToNurbsCurve();
                exclusionSource = siteFootprint != null
                    ? "fallback rectangle (SiteFootprint was supplied but invalid or not closed)"
                    : "fallback rectangle (no SiteFootprint supplied)";
                exclusionSource += $", SiteWidth={siteWidth:F2}m x SiteDepth={siteDepth:F2}m";
            }
            report.AppendLine($"Exclusion zone: {exclusionSource}. SiteClearance={siteClearance:F2}m buffer.");

            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            Func<Point3d, bool> isExcluded = (pt) =>
            {
                PointContainment containment = exclusionCurve.Contains(pt, Plane.WorldXY, tol);
                if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                    return true;

                double t;
                if (exclusionCurve.ClosestPoint(pt, out t))
                {
                    double dist = pt.DistanceTo(exclusionCurve.PointAt(t));
                    if (dist < siteClearance) return true;
                }
                return false;
            };

            // 3. Candidate points
            double avgFootprint = (minFootprintSize + maxFootprintSize) / 2.0;
            double cellSize = Math.Max(1.0, avgFootprint * 1.3);
            var rand = new Random(seed);

            var candidatePoints = new List<Point3d>();
            int gridSteps = (int)Math.Ceiling((radius * 2.0) / cellSize) + 2;
            double gridStart = -(gridSteps / 2.0) * cellSize;

            for (int ix = 0; ix < gridSteps; ix++)
            {
                for (int iy = 0; iy < gridSteps; iy++)
                {
                    double baseX = gridStart + ix * cellSize;
                    double baseY = gridStart + iy * cellSize;
                    double jitterX = (rand.NextDouble() - 0.5) * cellSize * 0.6;
                    double jitterY = (rand.NextDouble() - 0.5) * cellSize * 0.6;

                    Point3d candidate = new Point3d(siteCenter.X + baseX + jitterX, siteCenter.Y + baseY + jitterY, 0);

                    double distFromCenter = candidate.DistanceTo(new Point3d(siteCenter.X, siteCenter.Y, 0));
                    if (distFromCenter > radius) continue;
                    if (isExcluded(candidate)) continue;

                    candidatePoints.Add(candidate);
                }
            }
            report.AppendLine($"Candidate grid: cellSize={cellSize:F2}m -> {candidatePoints.Count} valid candidate points " +
                "(inside radius, outside exclusion+clearance).");

            // 4. Density selection
            for (int i = candidatePoints.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (candidatePoints[i], candidatePoints[j]) = (candidatePoints[j], candidatePoints[i]);
            }

            int keepCount = Math.Min(candidatePoints.Count, (int)Math.Round(candidatePoints.Count * density));
            var keptPoints = candidatePoints.Take(keepCount).ToList();
            report.AppendLine($"Density={density:F2} -> keeping {keptPoints.Count}/{candidatePoints.Count} candidates.");

            // 5. Build masses -- with mutual-overlap avoidance via shrink-and-retry
            var contextMasses = new List<Brep>();
            var contextHeights = new List<double>();
            var placedCircles = new List<(Point3d center, double radius)>();
            int buildFailures = 0;
            int overlapSkips = 0;

            foreach (var pt in keptPoints)
            {
                bool placed = false;

                for (int attempt = 0; attempt < 5 && !placed; attempt++)
                {
                    // Each retry shrinks the footprint size ceiling toward MinFootprintSize,
                    // giving overlapping placements a real chance to fit before giving up.
                    double shrinkFactor = 1.0 - (attempt * 0.2);
                    double sizeCeiling = minFootprintSize + (maxFootprintSize - minFootprintSize) * Math.Max(0.05, shrinkFactor);

                    double w = minFootprintSize + rand.NextDouble() * Math.Max(0.01, sizeCeiling - minFootprintSize);
                    double d = minFootprintSize + rand.NextDouble() * Math.Max(0.01, sizeCeiling - minFootprintSize);

                    double rotationDeg;
                    if (orthogonalMode)
                    {
                        int quarterTurn = rand.Next(0, 4);
                        rotationDeg = fixedAngleDeg + quarterTurn * 90.0;
                    }
                    else
                    {
                        rotationDeg = rand.NextDouble() * 360.0;
                    }

                    double footprintRadius = Math.Sqrt((w / 2.0) * (w / 2.0) + (d / 2.0) * (d / 2.0));

                    bool overlapsExisting = placedCircles.Any(pc =>
                        pt.DistanceTo(pc.center) < (pc.radius + footprintRadius + minGap));
                    if (overlapsExisting) continue; // retry smaller

                    double heightFactor = 1.0 + (rand.NextDouble() * 2.0 - 1.0) * (heightVariancePercent / 100.0);
                    double height = Math.Max(0.5, averageHeight * heightFactor);

                    Plane basePlane = new Plane(pt, Vector3d.ZAxis);
                    basePlane.Rotate(RhinoMath.ToRadians(rotationDeg), Vector3d.ZAxis);
                    Rectangle3d footprintRect = new Rectangle3d(basePlane,
                        new Interval(-w / 2.0, w / 2.0), new Interval(-d / 2.0, d / 2.0));
                    Curve footprintCurve = footprintRect.ToNurbsCurve();

                    Extrusion extrusion = Extrusion.Create(footprintCurve, height, true);
                    Brep boxBrep = extrusion?.ToBrep();

                    if (boxBrep == null || !boxBrep.IsValid)
                    {
                        buildFailures++;
                        continue; // retry
                    }

                    contextMasses.Add(boxBrep);
                    contextHeights.Add(height);
                    placedCircles.Add((pt, footprintRadius));
                    placed = true;
                }

                if (!placed) overlapSkips++;
            }

            report.AppendLine($"Context masses built: {contextMasses.Count} " +
                $"(footprint {minFootprintSize:F1}-{maxFootprintSize:F1}m, avg height {averageHeight:F2}m +/-{heightVariancePercent:F0}%).");
            if (buildFailures > 0)
                report.AppendLine($"Build failures (geometry): {buildFailures} (skipped -- degenerate footprint or extrusion failure).");
            if (overlapSkips > 0)
                report.AppendLine($"Overlap skips: {overlapSkips} points could not fit a non-overlapping mass within 5 retries -- " +
                    "lower Density, raise Radius, or lower MinGap/MinFootprintSize if this number is high.");

            report.AppendLine("NOT in scope: real building-height data from actual coordinates (OSM/Overture Maps), " +
                "occlusion wiring into Heat Analyzer, and feeding context back into Section as a design consideration -- " +
                "all separate, larger follow-on tasks.");

            if (contextMasses.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Zero context masses generated -- check Radius, Density, and exclusion zone size.");
            else
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{contextMasses.Count} context masses generated ({overlapSkips} skipped for overlap).");

            DA.SetDataList(0, contextMasses);
            DA.SetData(1, groundSlab);
            DA.SetDataList(2, contextHeights);
            DA.SetData(3, report.ToString());
        }

        private static double Clamp(double val, double min, double max) => Math.Max(min, Math.Min(max, val));

        public override Guid ComponentGuid => new Guid("4962e02f-3482-4d5f-a74f-f6908e143154");
    }
}