// HorizontalOpeningStripsComponent.cs
// GUID: b3d9ec2c-e6ca-4103-ab1c-b883cd15ef50
//
// REWRITE 2026-08-14 -- fixes two related problems:
//
// (A) "SLIM VS THICK WINDOWS" -- the previous version, when a wall run
// couldn't fit even one full TargetWindowWidth window, SHRANK the window to
// fit (actualWindowWidth = Max(MinWindowWidth, totalLen)). That's the bug:
// it silently produced a different, narrower window than everywhere else on
// the building. FIXED: windows are now ALWAYS exactly TargetWindowWidth.
// No resizing, ever. A run that can't fit one is skipped and logged, full
// stop. Centering behavior (extra leftover space pushed to both edges,
// never squeezing windows against the wall bounds) was already correct and
// is unchanged.
//
// (B) SMOOTH / FLUID EDGES GETTING CHOPPED UP -- the previous version
// converted the ENTIRE section loop to a polyline first (TryGetPolyline /
// ToPolyline at 0.01 tolerance) before doing any collinear merging. On a
// curved facade this pre-shreds the curve into dozens of short straight
// pieces that are each *almost* but not quite collinear with their
// neighbor, so they never fully re-merge -- a smooth section reads as
// choppy fragments instead of one continuous surface.
//
// FIXED: the loop is exploded into its REAL underlying curve segments first
// (DuplicateSegments, no polyline approximation), and each segment is
// classified as straight or curved via Curve.IsLinear() BEFORE any merging:
//   - Consecutive STRAIGHT segments merge by angle tolerance into a wall
//     run (same logic as before), then get windowed with the equal-width
//     rule from fix (A).
//   - Consecutive CURVED segments join into one continuous curve run and
//     become ONE continuous ribbon opening spanning the whole run -- no
//     subdivision, no repeated windows, no gaps. This is the "fluid form,
//     strip continuous throughout" behavior.
//
// CARRIED OVER UNCHANGED:
// - Whole-envelope plan sectioning per floor band (Intersection.BrepPlane
//   on the full unified envelope, not per-Brep-face) -- this was the
//   previous session's core fix and is still correct and necessary.
// - CutterOvershoot: pushes each opening's outer boundary slightly outside
//   the true wall skin before extrusion, so it fully penetrates instead of
//   starting flush/coincident with the skin (avoids Rhino boolean
//   solver failures on exactly-tangent faces).
// - Direction verification: solidifies via Brep.CreateFromOffsetFace, then
//   checks centroid movement toward/away from the building's volume
//   centroid and flips if the result went the wrong way.
//
// NOTE ON NORMAL DIRECTION FOR CURVED RUNS: a single approximate outward
// normal (tangent at the run's midpoint, rotated 90 degrees, oriented away
// from the volume centroid) is used to apply CutterOvershoot uniformly
// along a curved run. On a strongly curved run this is an approximation --
// it's only used to nudge the base curve outward before offsetting, not to
// compute the actual opening geometry, so it's a safe simplification. The
// depth extrusion itself still uses Brep.CreateFromOffsetFace, which
// respects the real per-face normal regardless.
//
// NOTE ON VerticalThreshold: still unused, still kept only so existing
// wiring doesn't break. See prior session's note -- whole-envelope plan
// sectioning can only return vertical wall boundaries.
//
// INPUTS
//   0  Envelope, 1 FloorElevations, 2 FloorToFloorHeight, 3 Openness,
//   4  WidthFraction, 5 VerticalThreshold (UNUSED, kept for compatibility),
//   6  TargetWindowWidth (double) default 1.2 -- ALWAYS honored exactly on
//      straight runs, never shrunk,
//   7  MullionGap (double) default 0.3,
//   8  WindowDepth (double) default 0.6,
//   9  ExtrudeInward (bool) default true,
//   10 MinWindowWidth (double) default 0.4 -- minimum run length to even
//      attempt a window; below this the run is skipped, not shrunk into,
//   11 CutterOvershoot (double) default 0.15,
//   12 CollinearAngleToleranceDeg (double) default 3.0 -- angle tolerance
//      for merging consecutive STRAIGHT segments into one wall run,
//   13 CurveLinearityTolerance (double) default 0.001 -- distance tolerance
//      (m) used by Curve.IsLinear() to decide straight vs curved per
//      segment. Raise slightly if genuinely straight facade segments are
//      being misread as curved due to tiny modeling noise.
//
// OUTPUTS
//   0 WindowSolids  1 StripBottoms  2 StripTops  3 Report

using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace SolveForm.Components
{
    public class HorizontalOpeningStripsComponent : GH_Component
    {
        public HorizontalOpeningStripsComponent()
            : base("Horizontal Opening Strips", "OpeningStrips",
                "Windows each floor's real wall runs. Straight runs get repeated windows always exactly " +
                "TargetWindowWidth wide, centered, extra space at the edges -- never shrunk to fit. Curved/smooth " +
                "runs become one continuous ribbon strip instead of being subdivided.",
                "SolveForm", "Massing")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Envelope", "E", "Unified building envelope", GH_ParamAccess.item);
            pManager.AddNumberParameter("FloorElevations", "F", "Floor elevation values", GH_ParamAccess.list);
            pManager.AddNumberParameter("FloorToFloorHeight", "F2F", "Floor-to-floor height (m)", GH_ParamAccess.item, 3.5);
            pManager.AddNumberParameter("Openness", "Op", "0.0-1.0", GH_ParamAccess.item, 0.6);
            pManager.AddNumberParameter("WidthFraction", "Wf", "Fraction of each run's length spanned, centered (0-1)", GH_ParamAccess.item, 0.7);
            pManager.AddNumberParameter("VerticalThreshold", "Vt",
                "UNUSED -- kept only so existing wiring doesn't break.", GH_ParamAccess.item, 0.3);
            pManager.AddNumberParameter("TargetWindowWidth", "Tw",
                "Fixed width (m) for every window on straight runs. Always honored exactly -- a run that can't fit " +
                "one is skipped, never shrunk into a narrower window.", GH_ParamAccess.item, 1.2);
            pManager.AddNumberParameter("MullionGap", "Mg", "Gap in meters between adjacent windows on straight runs", GH_ParamAccess.item, 0.3);
            pManager.AddNumberParameter("WindowDepth", "Wd", "Extrusion depth (m), measured inward from the true outer skin", GH_ParamAccess.item, 0.6);
            pManager.AddBooleanParameter("ExtrudeInward", "In", "True = extrude into the mass", GH_ParamAccess.item, true);
            pManager.AddNumberParameter("MinWindowWidth", "MinW",
                "Minimum run length (m) worth attempting. Runs shorter than this are skipped entirely, not shrunk into.",
                GH_ParamAccess.item, 0.4);
            pManager.AddNumberParameter("CutterOvershoot", "Over",
                "Distance (m) each opening extends OUTSIDE the true outer skin before cutting inward. Prevents " +
                "boolean failures against Cut Openings from coincident faces.", GH_ParamAccess.item, 0.15);
            pManager.AddNumberParameter("CollinearAngleToleranceDeg", "AngTol",
                "Consecutive STRAIGHT segments within this angle (degrees) merge into one wall run.", GH_ParamAccess.item, 3.0);
            pManager.AddNumberParameter("CurveLinearityTolerance", "LinTol",
                "Distance tolerance (m) used to decide whether a section segment counts as straight or curved. " +
                "Raise slightly if straight segments are being misread as curved due to modeling noise.",
                GH_ParamAccess.item, 0.001);

            for (int i = 2; i <= 13; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("WindowSolids", "S", "Solid extruded window/strip openings", GH_ParamAccess.list);
            pManager.AddCurveParameter("StripBottoms", "B", "Bottom edge curves (debug)", GH_ParamAccess.list);
            pManager.AddCurveParameter("StripTops", "T", "Top edge curves (debug)", GH_ParamAccess.list);
            pManager.AddTextParameter("Report", "R", "Diagnostic breakdown, with per-run skip reasons", GH_ParamAccess.item);
        }

        private class WallRun
        {
            public bool IsCurved;
            public Curve Geometry;
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep envelope = null;
            var floorElevations = new List<double>();
            double floorToFloor = 3.5, openness = 0.6, widthFraction = 0.7, verticalThreshold = 0.3;
            double targetWidth = 1.2, mullionGap = 0.3, windowDepth = 0.6, minWindowWidth = 0.4;
            double cutterOvershoot = 0.15, collinearAngleTolDeg = 3.0, curveLinTol = 0.001;
            bool extrudeInward = true;

            if (!DA.GetData(0, ref envelope)) return;
            if (!DA.GetDataList(1, floorElevations)) return;
            DA.GetData(2, ref floorToFloor);
            DA.GetData(3, ref openness);
            DA.GetData(4, ref widthFraction);
            DA.GetData(5, ref verticalThreshold); // unused, see header note
            DA.GetData(6, ref targetWidth);
            DA.GetData(7, ref mullionGap);
            DA.GetData(8, ref windowDepth);
            DA.GetData(9, ref extrudeInward);
            DA.GetData(10, ref minWindowWidth);
            DA.GetData(11, ref cutterOvershoot);
            DA.GetData(12, ref collinearAngleTolDeg);
            DA.GetData(13, ref curveLinTol);

            targetWidth = Math.Max(0.1, targetWidth);
            mullionGap = Math.Max(0.0, mullionGap);
            windowDepth = Math.Max(0.01, windowDepth);
            minWindowWidth = Math.Max(0.05, minWindowWidth);
            cutterOvershoot = Math.Max(0.0, cutterOvershoot);
            collinearAngleTolDeg = Clamp(collinearAngleTolDeg, 0.1, 20.0);
            curveLinTol = Math.Max(0.0001, curveLinTol);
            if (floorToFloor <= 0.01) floorToFloor = 3.5;
            openness = Clamp(openness, 0.0, 1.0);
            widthFraction = Clamp(widthFraction, 0.05, 1.0);

            var report = new System.Text.StringBuilder();
            report.AppendLine("══ OPENING STRIPS REPORT ══");

            if (envelope == null || !envelope.IsValid)
            {
                report.AppendLine("Envelope is null or invalid.");
                DA.SetData(3, report.ToString());
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Envelope is null or invalid.");
                return;
            }
            if (floorElevations.Count == 0)
            {
                report.AppendLine("No floor elevations supplied.");
                DA.SetData(3, report.ToString());
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No floor elevations supplied.");
                return;
            }

            report.AppendLine($"FloorElevations ({floorElevations.Count}): " + string.Join(", ", floorElevations.Select(f => f.ToString("F2"))));
            report.AppendLine($"TargetWindowWidth={targetWidth:F2}m (fixed, never shrunk) | MullionGap={mullionGap:F2}m | MinWindowWidth={minWindowWidth:F2}m | CutterOvershoot={cutterOvershoot:F2}m | CollinearAngTol={collinearAngleTolDeg:F1}deg | CurveLinTol={curveLinTol:F4}m");

            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            var envVmp = VolumeMassProperties.Compute(envelope);
            Point3d volumeCentroid = (envVmp != null) ? envVmp.Centroid : envelope.GetBoundingBox(false).Center;

            var windowSolids = new List<Brep>();
            var stripBottoms = new List<Curve>();
            var stripTops = new List<Curve>();
            var skipLog = new List<string>();

            int noIntersection = 0, loftFailures = 0, solidifyFailures = 0, directionFlips = 0, tooShortSkips = 0;
            int straightRunsFound = 0, curvedRunsFound = 0, elevationsProcessed = 0;
            int straightWindowCount = 0, curvedStripCount = 0;

            foreach (double elevation in floorElevations)
            {
                double bandHeight = openness * floorToFloor;
                double bandBottom = elevation + (floorToFloor - bandHeight) / 2.0;
                double bandTop = bandBottom + bandHeight;

                Plane sectionPlane = new Plane(new Point3d(0, 0, bandBottom), Vector3d.ZAxis);
                Curve[] sectionCurves; Point3d[] sectionPoints;
                bool ok = Intersection.BrepPlane(envelope, sectionPlane, 0.001, out sectionCurves, out sectionPoints);

                if (!ok || sectionCurves == null || sectionCurves.Length == 0)
                {
                    noIntersection++;
                    skipLog.Add($"Z={elevation:F2}: no section at this height (expected above/below a stepped mass's extent)");
                    continue;
                }

                elevationsProcessed++;

                foreach (var loop in sectionCurves)
                {
                    if (loop == null || loop.GetLength() < 0.1) continue;

                    var runs = ClassifyLoopIntoRuns(loop, collinearAngleTolDeg, curveLinTol);

                    foreach (var run in runs)
                    {
                        if (!run.IsCurved)
                        {
                            straightRunsFound++;
                            ProcessStraightRun(run.Geometry, elevation, bandHeight, widthFraction, targetWidth,
                                mullionGap, minWindowWidth, cutterOvershoot, windowDepth, extrudeInward,
                                volumeCentroid, tol,
                                windowSolids, stripBottoms, stripTops, skipLog,
                                ref loftFailures, ref solidifyFailures, ref directionFlips, ref tooShortSkips,
                                ref straightWindowCount);
                        }
                        else
                        {
                            curvedRunsFound++;
                            ProcessCurvedRun(run.Geometry, elevation, bandHeight, widthFraction, minWindowWidth,
                                cutterOvershoot, windowDepth, extrudeInward, volumeCentroid, tol,
                                windowSolids, stripBottoms, stripTops, skipLog,
                                ref loftFailures, ref solidifyFailures, ref directionFlips, ref tooShortSkips,
                                ref curvedStripCount);
                        }
                    }
                }
            }

            report.AppendLine($"Elevations processed: {elevationsProcessed}/{floorElevations.Count}");
            report.AppendLine($"Straight wall runs: {straightRunsFound} -> {straightWindowCount} windows, each exactly {targetWidth:F2}m wide");
            report.AppendLine($"Curved/fluid runs: {curvedRunsFound} -> {curvedStripCount} continuous ribbon strips (not subdivided)");
            report.AppendLine($"no-intersection: {noIntersection} (expected above/below a stepped mass) | loft-fail: {loftFailures} | solidify-fail: {solidifyFailures} | too-short-skips: {tooShortSkips}");
            report.AppendLine($"Direction auto-corrections: {directionFlips}");
            if (skipLog.Count > 0)
            {
                report.AppendLine("Skip details (first 20):");
                foreach (var l in skipLog.Take(20)) report.AppendLine("  " + l);
                if (skipLog.Count > 20) report.AppendLine($"  ... {skipLog.Count - 20} more");
            }
            report.AppendLine($"TOTAL WINDOW/STRIP SOLIDS: {windowSolids.Count}");

            if (windowSolids.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Zero solids produced -- see Report.");
            else
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{windowSolids.Count} solids built -- see Report for breakdown.");

            DA.SetDataList(0, windowSolids);
            DA.SetDataList(1, stripBottoms);
            DA.SetDataList(2, stripTops);
            DA.SetData(3, report.ToString());
        }

        /// <summary>
        /// Repeated, always-TargetWindowWidth windows on a straight wall run.
        /// Never shrinks a window to fit -- if the run can't fit at least one
        /// full-width window, it's skipped, not squeezed.
        /// </summary>
        private static void ProcessStraightRun(
            Curve runCurve, double elevation, double bandHeight, double widthFraction, double targetWidth,
            double mullionGap, double minWindowWidth, double cutterOvershoot, double windowDepth, bool extrudeInward,
            Point3d volumeCentroid, double tol,
            List<Brep> windowSolids, List<Curve> stripBottoms, List<Curve> stripTops, List<string> skipLog,
            ref int loftFailures, ref int solidifyFailures, ref int directionFlips, ref int tooShortSkips,
            ref int windowCount)
        {
            Curve trimmedRun = TrimCentered(runCurve, widthFraction);
            if (trimmedRun == null) return;

            double totalLen = trimmedRun.GetLength();
            if (totalLen < targetWidth)
            {
                tooShortSkips++;
                skipLog.Add($"Z={elevation:F2}: straight run len {totalLen:F2}m can't fit even one {targetWidth:F2}m window -- skipped, not shrunk");
                return;
            }

            Vector3d normal = ApproxOutwardNormal(runCurve, volumeCentroid, tol);

            int n = (int)Math.Floor((totalLen + mullionGap) / (targetWidth + mullionGap));
            if (n < 1) n = 1; // guaranteed >=1 by the totalLen check above

            double groupLen = n * targetWidth + (n - 1) * mullionGap;
            if (groupLen > totalLen) groupLen = totalLen; // shouldn't happen given the floor() above, kept as a safety clamp
            double startOffset = Math.Max(0, (totalLen - groupLen) / 2.0); // leftover space -> both edges, windows never squeezed

            double cursor = startOffset;
            for (int w = 0; w < n; w++)
            {
                double segStart = cursor;
                double segEnd = cursor + targetWidth;
                cursor = segEnd + mullionGap;
                if (segEnd > totalLen) break; // never produce a partial/narrower window

                Curve segBottom = TrimByLength(trimmedRun, segStart, segEnd);
                if (segBottom == null) continue;

                bool built = BuildOpeningSolid(segBottom, bandHeight, normal, cutterOvershoot, windowDepth,
                    extrudeInward, volumeCentroid, tol,
                    out Brep solid, out Curve segTop, ref loftFailures, ref solidifyFailures, ref directionFlips);

                if (!built) continue;

                windowSolids.Add(solid);
                stripBottoms.Add(segBottom);
                stripTops.Add(segTop);
                windowCount++;
            }
        }

        /// <summary>
        /// A curved/smooth run becomes ONE continuous ribbon opening spanning
        /// its full (WidthFraction-trimmed) length. No subdivision -- this is
        /// the "fluid form, strip continuous throughout" behavior.
        /// </summary>
        private static void ProcessCurvedRun(
            Curve runCurve, double elevation, double bandHeight, double widthFraction, double minWindowWidth,
            double cutterOvershoot, double windowDepth, bool extrudeInward,
            Point3d volumeCentroid, double tol,
            List<Brep> windowSolids, List<Curve> stripBottoms, List<Curve> stripTops, List<string> skipLog,
            ref int loftFailures, ref int solidifyFailures, ref int directionFlips, ref int tooShortSkips,
            ref int curvedStripCount)
        {
            Curve trimmedRun = TrimCentered(runCurve, widthFraction);
            if (trimmedRun == null) return;

            double totalLen = trimmedRun.GetLength();
            if (totalLen < minWindowWidth)
            {
                tooShortSkips++;
                skipLog.Add($"Z={elevation:F2}: curved run len {totalLen:F2}m < MinWindowWidth {minWindowWidth:F2}m -- skipped");
                return;
            }

            Vector3d normal = ApproxOutwardNormal(runCurve, volumeCentroid, tol);

            bool built = BuildOpeningSolid(trimmedRun, bandHeight, normal, cutterOvershoot, windowDepth,
                extrudeInward, volumeCentroid, tol,
                out Brep solid, out Curve segTop, ref loftFailures, ref solidifyFailures, ref directionFlips);

            if (!built) return;

            windowSolids.Add(solid);
            stripBottoms.Add(trimmedRun);
            stripTops.Add(segTop);
            curvedStripCount++;
        }

        /// <summary>
        /// Shared loft -> overshoot-push -> offset -> direction-check pipeline,
        /// used by both straight windows and curved ribbon strips.
        /// </summary>
        private static bool BuildOpeningSolid(
            Curve segBottomIn, double bandHeight, Vector3d normal, double cutterOvershoot, double windowDepth,
            bool extrudeInward, Point3d volumeCentroid, double tol,
            out Brep solid, out Curve segTop,
            ref int loftFailures, ref int solidifyFailures, ref int directionFlips)
        {
            solid = null;
            Curve segBottom = segBottomIn.DuplicateCurve();
            segTop = segBottom.DuplicateCurve();
            segTop.Transform(Transform.Translation(0, 0, bandHeight));

            if (cutterOvershoot > 1e-6)
            {
                var pushOut = Transform.Translation(normal.X * cutterOvershoot, normal.Y * cutterOvershoot, 0);
                segBottom.Transform(pushOut);
                segTop.Transform(pushOut);
            }

            Brep[] lofted = Brep.CreateFromLoft(
                new List<Curve> { segBottom, segTop },
                Point3d.Unset, Point3d.Unset, LoftType.Straight, false);

            if (lofted == null || lofted.Length == 0 || lofted[0].Faces.Count == 0)
            { loftFailures++; return false; }

            var panelAmp = AreaMassProperties.Compute(lofted[0].Faces[0]);
            Point3d panelCentroid = panelAmp != null ? panelAmp.Centroid : (segBottom.PointAtStart + segTop.PointAtEnd) / 2.0;
            double panelDistToCenter = (panelCentroid - volumeCentroid).Length;

            double totalDepth = windowDepth + cutterOvershoot;
            double offsetDist = extrudeInward ? -totalDepth : totalDepth;
            Brep built = Brep.CreateFromOffsetFace(lofted[0].Faces[0], offsetDist, tol, false, true);

            if (built != null && built.IsValid)
            {
                var svmp = VolumeMassProperties.Compute(built);
                if (svmp != null)
                {
                    double solidDistToCenter = (svmp.Centroid - volumeCentroid).Length;
                    bool wentInward = solidDistToCenter < panelDistToCenter;
                    if (wentInward != extrudeInward)
                    {
                        Brep flipped = Brep.CreateFromOffsetFace(lofted[0].Faces[0], -offsetDist, tol, false, true);
                        if (flipped != null && flipped.IsValid) { built = flipped; directionFlips++; }
                    }
                }
            }

            if (built == null || !built.IsValid) { solidifyFailures++; return false; }

            solid = built;
            return true;
        }

        private static Vector3d ApproxOutwardNormal(Curve runCurve, Point3d volumeCentroid, double tol)
        {
            double tMid = runCurve.Domain.Mid;
            Vector3d tangent = runCurve.TangentAt(tMid);
            if (!tangent.Unitize()) tangent = Vector3d.XAxis;
            Vector3d normal = Vector3d.CrossProduct(tangent, Vector3d.ZAxis);
            if (!normal.Unitize()) normal = Vector3d.XAxis;

            Point3d runMid = runCurve.PointAt(tMid);
            Vector3d towardMid = runMid - volumeCentroid;
            if (towardMid.Length > tol * 100)
            {
                towardMid.Unitize();
                if (normal * towardMid < 0) normal = -normal;
            }
            return normal;
        }

        /// <summary>
        /// Explodes a closed section loop into its real underlying curve
        /// segments (no polyline approximation) and classifies each as
        /// straight or curved via Curve.IsLinear(). Consecutive straight
        /// segments merge by angle tolerance into wall runs; consecutive
        /// curved segments join into one continuous curved run.
        /// </summary>
        private static List<WallRun> ClassifyLoopIntoRuns(Curve loop, double angleTolDeg, double curveLinTol)
        {
            var result = new List<WallRun>();

            Curve[] segments = null;
            if (loop is PolyCurve pc)
                segments = pc.DuplicateSegments();

            if (segments == null || segments.Length == 0)
                segments = new Curve[] { loop };

            var isCurved = new bool[segments.Length];
            for (int i = 0; i < segments.Length; i++)
                isCurved[i] = !segments[i].IsLinear(curveLinTol);

            int idx = 0;
            while (idx < segments.Length)
            {
                bool curved = isCurved[idx];

                if (!curved)
                {
                    var lineSegs = new List<Line>();
                    while (idx < segments.Length && !isCurved[idx])
                    {
                        lineSegs.Add(new Line(segments[idx].PointAtStart, segments[idx].PointAtEnd));
                        idx++;
                    }
                    foreach (var merged in MergeCollinearLines(lineSegs, angleTolDeg))
                        result.Add(new WallRun { IsCurved = false, Geometry = new LineCurve(merged) });
                }
                else
                {
                    var curvedSegs = new List<Curve>();
                    while (idx < segments.Length && isCurved[idx])
                    {
                        curvedSegs.Add(segments[idx]);
                        idx++;
                    }
                    var joined = Curve.JoinCurves(curvedSegs, 0.01);
                    if (joined != null)
                        foreach (var j in joined)
                            result.Add(new WallRun { IsCurved = true, Geometry = j });
                }
            }

            return result;
        }

        private static List<Line> MergeCollinearLines(List<Line> rawSegments, double angleTolDeg)
        {
            var result = new List<Line>();
            rawSegments = rawSegments.Where(l => l.Length > 1e-6).ToList();
            if (rawSegments.Count == 0) return result;

            double angleTolRad = angleTolDeg * Math.PI / 180.0;

            Point3d runStart = rawSegments[0].From;
            Point3d runEnd = rawSegments[0].To;
            Vector3d runDir = rawSegments[0].Direction; runDir.Unitize();

            for (int i = 1; i < rawSegments.Count; i++)
            {
                Vector3d dir = rawSegments[i].Direction;
                if (!dir.Unitize()) continue;

                double angle = Vector3d.VectorAngle(runDir, dir);
                if (angle <= angleTolRad)
                {
                    runEnd = rawSegments[i].To;
                }
                else
                {
                    result.Add(new Line(runStart, runEnd));
                    runStart = rawSegments[i].From;
                    runEnd = rawSegments[i].To;
                    runDir = dir;
                }
            }
            result.Add(new Line(runStart, runEnd));
            return result;
        }

        private static Curve TrimByLength(Curve curve, double startLen, double endLen)
        {
            if (curve == null || endLen <= startLen) return null;
            double tStart, tEnd;
            if (!curve.LengthParameter(startLen, out tStart)) return null;
            if (!curve.LengthParameter(endLen, out tEnd)) return null;
            if (tEnd <= tStart) return null;
            return curve.Trim(tStart, tEnd);
        }

        private static Curve TrimCentered(Curve curve, double widthFraction)
        {
            if (curve == null) return null;
            double totalLength = curve.GetLength();
            if (totalLength < 1e-6) return null;
            if (widthFraction >= 0.999) return curve;
            double trimEachSide = totalLength * (1.0 - widthFraction) / 2.0;
            double tStart, tEnd;
            if (!curve.LengthParameter(trimEachSide, out tStart)) return null;
            if (!curve.LengthParameter(totalLength - trimEachSide, out tEnd)) return null;
            if (tEnd <= tStart) return null;
            return curve.Trim(tStart, tEnd);
        }

        private static double Clamp(double val, double min, double max) => Math.Max(min, Math.Min(max, val));

        public override Guid ComponentGuid => new Guid("b3d9ec2c-e6ca-4103-ab1c-b883cd15ef50");
    }
}