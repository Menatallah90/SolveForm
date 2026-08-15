using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class SolveFormCutOpeningsComponent : GH_Component
    {
        public SolveFormCutOpeningsComponent()
            : base("SolveForm Cut Openings", "SF_Cut",
                   "Creates hollow shell with wall thickness, then cuts window openings.",
                   "SolveForm", "Facade")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("UnifiedBrep", "B",
                "Closed mass brep from Normalize or Unify",
                GH_ParamAccess.item);
            pManager.AddBrepParameter("Openings", "O",
                "Window rectangle breps from SolveForm Openings",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("WallThickness", "T",
                "Wall thickness in model units. Default 0.3.",
                GH_ParamAccess.item, 0.3);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("PunchedMass", "P",
                "Hollow mass with window openings",
                GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "R",
                "Diagnostic report",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep mass = null;
            var openings = new List<Brep>();
            double wallT = 0.3;

            if (!DA.GetData(0, ref mass) || mass == null) return;
            if (!DA.GetDataList(1, openings) || openings.Count == 0) return;
            DA.GetData(2, ref wallT);
            if (wallT < 0.05) wallT = 0.05;

            var report = new System.Text.StringBuilder();
            report.AppendLine("══ CUT OPENINGS REPORT ══");
            report.AppendLine($"  {openings.Count} windows | WallThickness={wallT}");

            // ── Step 1: Make hollow shell ───────────────────────────────────
            // Scale the mass inward from its bbox center to approximate inner void.
            // Scale factor = (dim - 2*wallT) / dim for each axis.
            BoundingBox bb = mass.GetBoundingBox(false);
            double sizeX = bb.Max.X - bb.Min.X;
            double sizeY = bb.Max.Y - bb.Min.Y;
            double sizeZ = bb.Max.Z - bb.Min.Z;

            // Scale factors per axis
            double sx = Math.Max(0.1, (sizeX - 2 * wallT) / sizeX);
            double sy = Math.Max(0.1, (sizeY - 2 * wallT) / sizeY);
            double sz = Math.Max(0.1, (sizeZ - 2 * wallT) / sizeZ);

            Brep innerVoid = mass.DuplicateBrep();
            // Scale non-uniformly from bbox center
            Point3d bbCen = bb.Center;
            Transform scale = Transform.Scale(
                new Plane(bbCen, Vector3d.XAxis, Vector3d.YAxis),
                sx, sy, sz);
            innerVoid.Transform(scale);

            // Boolean difference: outer minus inner = hollow shell
            Brep shell;
            Brep[] shellResult = Brep.CreateBooleanDifference(
                new[] { mass.DuplicateBrep() }, new[] { innerVoid }, 0.001);

            if (shellResult != null && shellResult.Length > 0)
            {
                shell = shellResult.OrderByDescending(b => {
                    var v = VolumeMassProperties.Compute(b);
                    return v != null ? v.Volume : 0;
                }).First();
                report.AppendLine("  Shell: hollow OK");
            }
            else
            {
                // Scale failed — try offset
                Brep[] offsetResult = Brep.CreateOffsetBrep(
                    mass, -wallT, true, true, 0.001, out _, out _);
                if (offsetResult != null && offsetResult.Length > 0)
                {
                    Brep innerO = offsetResult.OrderByDescending(b => {
                        var v = VolumeMassProperties.Compute(b);
                        return v != null ? v.Volume : 0;
                    }).First();
                    Brep[] shellO = Brep.CreateBooleanDifference(
                        new[] { mass.DuplicateBrep() }, new[] { innerO }, 0.001);
                    shell = (shellO != null && shellO.Length > 0) ? shellO[0] : mass.DuplicateBrep();
                    report.AppendLine(shell == mass ? "  Shell: both methods failed — solid mass" : "  Shell: offset method OK");
                }
                else
                {
                    shell = mass.DuplicateBrep();
                    report.AppendLine("  Shell: FAILED — using solid mass");
                }
            }

            // ── Step 2: Build cutters ───────────────────────────────────────
            var vmp = VolumeMassProperties.Compute(mass);
            Point3d massCen = vmp != null ? vmp.Centroid : bbCen;
            double cutDepth = wallT * 2 + 0.5;

            var cutters = new List<Brep>();
            int skipped = 0;

            foreach (Brep win in openings)
            {
                if (win == null || win.Faces.Count == 0) { skipped++; continue; }

                var wAmp = AreaMassProperties.Compute(win);
                if (wAmp == null) { skipped++; continue; }
                Point3d wCen = wAmp.Centroid;

                Vector3d inward = massCen - wCen;
                inward.Z = 0;
                if (!inward.Unitize()) { skipped++; continue; }
                Vector3d outward = -inward;

                // Get window boundary
                var edges = win.DuplicateEdgeCurves();
                var joined = Curve.JoinCurves(edges, 0.01);
                if (joined == null || joined.Length == 0) { skipped++; continue; }
                Curve boundary = joined.OrderByDescending(c => c.GetLength()).First();

                // Shift boundary outward so cutter starts outside mass surface
                boundary.Transform(Transform.Translation(outward * (wallT + 0.1)));
                Curve boundary2 = boundary.DuplicateCurve();
                boundary2.Transform(Transform.Translation(inward * cutDepth));

                // Loft start and end boundary = cutter box
                Brep[] lofted = Brep.CreateFromLoft(
                    new[] { boundary, boundary2 },
                    Point3d.Unset, Point3d.Unset,
                    LoftType.Straight, false);

                if (lofted == null || lofted.Length == 0) { skipped++; continue; }

                Brep cutter = lofted[0];
                cutter = cutter.CapPlanarHoles(0.001) ?? cutter;
                if (!cutter.IsSolid)
                    cutter = cutter.CapPlanarHoles(0.001) ?? cutter;

                cutters.Add(cutter);
            }

            report.AppendLine($"  Built {cutters.Count} cutters | {skipped} skipped");

            if (cutters.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No cutters built.");
                DA.SetData(0, shell);
                DA.SetData(1, report.ToString());
                return;
            }

            // ── Step 3: One boolean difference — shell minus all cutters ────
            Brep[] final = Brep.CreateBooleanDifference(
                new[] { shell }, cutters, 0.001);

            if (final != null && final.Length > 0)
            {
                Brep best = final.OrderByDescending(b => {
                    var v = VolumeMassProperties.Compute(b);
                    return v != null ? v.Volume : 0;
                }).First();
                report.AppendLine($"  Windows cut: success ({final.Length} result brep(s))");
                report.AppendLine("══ DONE ══");
                DA.SetData(0, best);
            }
            else
            {
                report.AppendLine("  Windows cut: FAILED — outputting hollow shell without cuts");
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Window boolean failed. Outputting hollow shell.");
                DA.SetData(0, shell);
            }

            DA.SetData(1, report.ToString());
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid =>
            new Guid("D5F8A312-6B94-4E23-9C74-2A0F3B158D77");
    }
}