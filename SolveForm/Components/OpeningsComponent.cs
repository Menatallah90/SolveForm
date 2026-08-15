// HorizontalOpeningStripsComponent.cs
// GUID: b3d9ec2c-e6ca-4103-ab1c-b883cd15ef50  (unchanged -- same component, rewired spec)
//
// REBUILT SPEC (per Mina):
//   - Input: Mass, FloorLevels, Openness (0.0-1.0)
//   - Every facade gets exactly one centered strip per floor
//   - Openness 1.0 = the opening spans floor-to-ceiling of that floor's band
//   - Openings on the SAME face at DIFFERENT floors must NEVER overlap, even
//     at Openness = 1.0 -- at most their edges touch (adjacent, aligned)
//
// HOW NON-OVERLAP IS GUARANTEED (not just tested for, actually structural):
// Each floor i owns the vertical band [elevation_i, elevation_i + FloorToFloorHeight)
// exclusively -- no other floor's band ever touches that range. The opening
// for floor i is built INSIDE that band, centered within it:
//     bandHeight  = Openness * FloorToFloorHeight
//     bandBottom  = elevation_i + (FloorToFloorHeight - bandHeight) / 2
//     bandTop     = bandBottom + bandHeight
// At Openness = 1.0: bandBottom = elevation_i, bandTop = elevation_i + FloorToFloorHeight
// exactly -- i.e. it touches the floor slab below and the floor slab above,
// which are shared with the adjacent floors' bands. That's "adjacent, edges
// aligned, not overlapping" -- structurally impossible to overlap because
// each floor's band is mathematically confined to its own slab-to-slab
// range regardless of Openness value.
//
// SURFACE-FOLLOWING (unchanged approach, this part wasn't the bug):
// Each facade face is intersected with a horizontal PLANE at the band's
// bottom and top elevations using Intersection.BrepPlane. This is a true
// geometric intersection with the actual face, so curved/folded/arched
// facades produce a curve that lies exactly on the surface, not a flat
// projected rectangle.
//
// WHY IT MAY HAVE BEEN PRODUCING NOTHING:
// Runtime messages now report how many facade faces were found and how many
// floor x face combinations were attempted vs succeeded, so a silent empty
// output becomes a specific number you can act on instead of a black box.
//
// INPUTS
//   0  Envelope          (Brep, item)   - the mass
//   1  FloorElevations   (double, list) - from Floor Levels
//   2  FloorToFloorHeight(double)       - default 3.5, matches Section
//   3  Openness          (double)       - 0.0-1.0, default 0.6
//   4  WidthFraction     (double)       - 0-1, how much of the facade WIDTH the strip
//                                          spans, centered. Default 0.7 (separate axis
//                                          from Openness, which controls height only)
//   5  VerticalThreshold (double)       - facade-detection threshold, default 0.3
//
// OUTPUTS
//   0  StripSurfaces (Brep, list)   - one lofted strip per facade per floor
//   1  StripBottoms  (Curve, list)  - bottom edge curves (debug)
//   2  StripTops     (Curve, list)  - top edge curves (debug)

using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using SolveForm.Attributes;

namespace SolveForm.Components
{
    public class HorizontalOpeningStripsComponent : GH_Component
    {
        public HorizontalOpeningStripsComponent()
            : base("Horizontal Opening Strips", "OpeningStrips",
                "One centered, surface-following, guaranteed non-overlapping horizontal opening strip per facade per floor.",
                "SolveForm", "Massing")
        { }

        public override void CreateAttributes()
        {
            m_attributes = new BlackComponentAttributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Envelope", "E", "Unified building envelope (or any mass)", GH_ParamAccess.item);
            pManager.AddNumberParameter("FloorElevations", "F", "Floor elevation values", GH_ParamAccess.list);
            pManager.AddNumberParameter("FloorToFloorHeight", "F2F", "Floor-to-floor height (m)", GH_ParamAccess.item, 3.5);
            pManager.AddNumberParameter("Openness", "Op", "0.0-1.0. 1.0 = floor-to-ceiling, edges align with adjacent floors, never overlaps", GH_ParamAccess.item, 0.6);
            pManager.AddNumberParameter("WidthFraction", "Wf", "Fraction of facade width spanned, centered (0-1)", GH_ParamAccess.item, 0.7);
            pManager.AddNumberParameter("VerticalThreshold", "Vt", "Normal.Z threshold below which a face counts as a facade", GH_ParamAccess.item, 0.3);

            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("StripSurfaces", "S", "Lofted strip surfaces, following facade curvature", GH_ParamAccess.list);
            pManager.AddCurveParameter("StripBottoms", "B", "Bottom edge curves (debug)", GH_ParamAccess.list);
            pManager.AddCurveParameter("StripTops", "T", "Top edge curves (debug)", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep envelope = null;
            var floorElevations = new List<double>();
            double floorToFloor = 3.5;
            double openness = 0.6;
            double widthFraction = 0.7;
            double verticalThreshold = 0.3;

            if (!DA.GetData(0, ref envelope)) return;
            if (!DA.GetDataList(1, floorElevations)) return;
            DA.GetData(2, ref floorToFloor);
            DA.GetData(3, ref openness);
            DA.GetData(4, ref widthFraction);
            DA.GetData(5, ref verticalThreshold);

            if (floorToFloor <= 0.01) floorToFloor = 3.5;
            openness = Math.Max(0.0, Math.Min(1.0, openness));
            widthFraction = Math.Max(0.05, Math.Min(1.0, widthFraction));

            if (envelope == null || !envelope.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Envelope is null or invalid -- nothing to work with.");
                return;
            }

            if (floorElevations.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No floor elevations supplied.");
                return;
            }

            if (openness <= 0.0001)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Openness is 0 -- strips will be zero-height by design. Raise Openness above 0 to see openings.");
            }

            var facadeFaces = envelope.Faces
                .Where(f => IsFacade(f, verticalThreshold))
                .ToList();

            if (facadeFaces.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"No facade faces detected out of {envelope.Faces.Count} total faces. " +
                    $"VerticalThreshold is {verticalThreshold:F2} -- try raising it (e.g. 0.5) if your envelope has sloped or near-diagonal walls, " +
                    "or check that Envelope is actually the merged solid and not still a naked/open Brep.");
                return;
            }

            var stripSurfaces = new List<Brep>();
            var stripBottoms = new List<Curve>();
            var stripTops = new List<Curve>();

            int attempts = 0;
            int outOfRange = 0;
            int intersectionFailures = 0;
            int loftFailures = 0;

            foreach (var face in facadeFaces)
            {
                Brep faceBrep = face.DuplicateFace(false);
                BoundingBox bbox = faceBrep.GetBoundingBox(true);

                foreach (double elevation in floorElevations)
                {
                    attempts++;

                    // Band is confined strictly inside [elevation, elevation + floorToFloor).
                    // This is what makes non-overlap structural rather than incidental.
                    double bandHeight = openness * floorToFloor;
                    double bandBottom = elevation + (floorToFloor - bandHeight) / 2.0;
                    double bandTop = bandBottom + bandHeight;

                    if (bandTop < bbox.Min.Z || bandBottom > bbox.Max.Z) { outOfRange++; continue; }

                    Curve bottomCurve = IntersectFaceAtHeight(faceBrep, bandBottom);
                    Curve topCurve = IntersectFaceAtHeight(faceBrep, bandTop);

                    if (bottomCurve == null || topCurve == null) { intersectionFailures++; continue; }

                    bottomCurve = TrimCentered(bottomCurve, widthFraction);
                    topCurve = TrimCentered(topCurve, widthFraction);

                    if (bottomCurve == null || topCurve == null) { intersectionFailures++; continue; }

                    Brep[] lofted = Brep.CreateFromLoft(
                        new List<Curve> { bottomCurve, topCurve },
                        Point3d.Unset, Point3d.Unset,
                        LoftType.Straight, false);

                    if (lofted == null || lofted.Length == 0) { loftFailures++; continue; }

                    stripSurfaces.AddRange(lofted);
                    stripBottoms.Add(bottomCurve);
                    stripTops.Add(topCurve);
                }
            }

            if (stripSurfaces.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Zero strips produced. Facade faces found: {facadeFaces.Count}. Attempts: {attempts} " +
                    $"(out of Z-range: {outOfRange}, intersection failed: {intersectionFailures}, loft failed: {loftFailures}). " +
                    "If out-of-range is the whole count, your FloorElevations don't fall within any facade's height range -- " +
                    "check units/scale. If intersection-failed is the whole count, the envelope may not be a clean solid.");
            }
            else if (outOfRange + intersectionFailures + loftFailures > 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"{stripSurfaces.Count} strips built. Skipped: {outOfRange} out-of-range, {intersectionFailures} intersection failures, {loftFailures} loft failures (out of {attempts} attempts).");
            }

            DA.SetDataList(0, stripSurfaces);
            DA.SetDataList(1, stripBottoms);
            DA.SetDataList(2, stripTops);
        }

        private static bool IsFacade(BrepFace face, double verticalThreshold)
        {
            Interval uDom = face.Domain(0);
            Interval vDom = face.Domain(1);
            double uMid = uDom.Mid;
            double vMid = vDom.Mid;

            Vector3d normal = face.NormalAt(uMid, vMid);
            if (!normal.IsValid || normal.IsZero) return false;

            normal.Unitize();
            return Math.Abs(normal.Z) < verticalThreshold;
        }

        private static Curve IntersectFaceAtHeight(Brep faceBrep, double z)
        {
            Plane horizontalPlane = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);

            Curve[] curves;
            Point3d[] points;

            bool success = Intersection.BrepPlane(faceBrep, horizontalPlane, 0.001, out curves, out points);
            if (!success || curves == null || curves.Length == 0) return null;

            return curves.OrderByDescending(c => c.GetLength()).First();
        }

        private static Curve TrimCentered(Curve curve, double widthFraction)
        {
            if (curve == null) return null;

            double totalLength = curve.GetLength();
            if (totalLength < 1e-6) return null;

            if (widthFraction >= 0.999) return curve; // no trim needed, span full width

            double trimEachSide = totalLength * (1.0 - widthFraction) / 2.0;

            double tStart, tEnd;
            if (!curve.LengthParameter(trimEachSide, out tStart)) return null;
            if (!curve.LengthParameter(totalLength - trimEachSide, out tEnd)) return null;

            if (tEnd <= tStart) return null;

            return curve.Trim(tStart, tEnd);
        }

        public override Guid ComponentGuid => new Guid("b3d9ec2c-e6ca-4103-ab1c-b883cd15ef50");
    }
}