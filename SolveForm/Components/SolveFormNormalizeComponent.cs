using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    // STANDALONE FIX (2026-06-19): Brep.CreateBooleanUnion (and the mesh-boolean
    // fallback) in SolveForm Unify produces a topologically valid, watertight
    // closed polysurface (_Check passes, no naked edges) — but face normals come
    // out essentially scrambled, confirmed visually via Rhino's _Dir command on
    // the baked Unify output: normals point sideways, inward, every direction,
    // with no consistent outward pattern. This silently broke every downstream
    // component that read face.NormalAt() or BrepFace.FrameAt() normals
    // (Facades orientation tagging, Openings facade-right vectors/placement).
    //
    // Per Mina's direction, Unify itself is NOT being touched — it took two
    // sessions to get its boolean-fallback chain (Brep/Mesh/Pairwise/CoplanarFix)
    // working and is considered stable. This is a separate, standalone component
    // that goes BETWEEN Unify and Facades:
    //
    //   Unify.Unified -> Normalize.Brep -> Normalize.Fixed -> Facades.Unified
    //
    // It does ONE job: take any closed brep and return a copy where every face
    // normal has been corrected to point outward. Nothing else in the pipeline
    // should need to defend against bad normals once this sits in the chain.
    public class SolveFormNormalizeComponent : GH_Component
    {
        public SolveFormNormalizeComponent()
            : base("SolveForm Normalize", "SF_Norm",
                   "Force-corrects all face normals on a closed brep to point outward",
                   "SolveForm", "Facade")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Brep", "B",
                "Closed brep with potentially unreliable face normals (e.g. SolveForm Unify output)",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Fixed", "F",
                "Same brep with all face normals corrected to point outward",
                GH_ParamAccess.item);
            pManager.AddIntegerParameter("FlippedCount", "FC",
                "Number of faces whose normal was flipped",
                GH_ParamAccess.item);
            pManager.AddTextParameter("Status", "S",
                "Diagnostic info",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep input = null;
            if (!DA.GetData(0, ref input) || input == null)
            {
                DA.SetData(2, "No input brep.");
                return;
            }

            Brep b = input.DuplicateBrep();
            var statusLines = new List<string>();

            if (!b.IsValid)
            {
                b.Repair(Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001);
                statusLines.Add("Input brep was invalid — attempted repair.");
            }

            // ── Overall outward-direction reference: volume centroid ───────────
            // For each face, compare (faceCentroid - volumeCentroid) against the
            // face's own normal at that point. If they disagree (dot < 0), the
            // face is pointing inward relative to the shell and gets flipped.
            //
            // This mirrors the position-based logic already proven reliable in
            // SolveForm Facades (orientation-from-centroid-position), just applied
            // per-face as an actual correction instead of a label.
            VolumeMassProperties vmp = VolumeMassProperties.Compute(b);
            Point3d volumeCentroid = (vmp != null) ? vmp.Centroid : b.GetBoundingBox(false).Center;

            // Overall shell volume sign: if VolumeMassProperties reports negative
            // volume, the brep is globally inside-out — every face's local check
            // below would be self-consistent but backwards. Catch that first.
            if (vmp != null && vmp.Volume < 0)
            {
                b.Flip();
                statusLines.Add($"Shell-level volume was negative ({vmp.Volume:F1}) — flipped entire brep before per-face pass.");
                // Recompute centroid post-flip (position doesn't change, but stay safe)
                vmp = VolumeMassProperties.Compute(b);
                volumeCentroid = (vmp != null) ? vmp.Centroid : b.GetBoundingBox(false).Center;
            }

            int flippedCount = 0;
            int checkedCount = 0;
            int ambiguousCount = 0;
            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            // BrepFace.Flip(bool) flips a single face's normal without breaking
            // the shell topology — safe to call per-face on a duplicated brep.
            foreach (BrepFace face in b.Faces)
            {
                checkedCount++;

                double uMid = (face.Domain(0).Min + face.Domain(0).Max) / 2.0;
                double vMid = (face.Domain(1).Min + face.Domain(1).Max) / 2.0;

                AreaMassProperties amp = AreaMassProperties.Compute(face);
                Point3d faceCentroid = (amp != null) ? amp.Centroid : face.PointAt(uMid, vMid);

                Vector3d outwardRef = faceCentroid - volumeCentroid;

                // Near the volume centroid (rare — only on heavily re-entrant/
                // notched shells where a concave face's centroid sits close to
                // the overall centroid), the direction-to-centroid signal is
                // unreliable. Fall back to comparing against the direction from
                // the shell's bounding-box center instead, which is a different
                // (and for axis-aligned stepped massing, usually still valid)
                // outward reference.
                bool usedFallback = false;
                if (outwardRef.Length < tol * 100)
                {
                    BoundingBox bb = b.GetBoundingBox(false);
                    outwardRef = faceCentroid - bb.Center;
                    usedFallback = true;
                    if (outwardRef.Length < tol * 100)
                    {
                        ambiguousCount++;
                        continue; // genuinely ambiguous — leave as-is rather than guess
                    }
                }

                if (!outwardRef.Unitize()) { ambiguousCount++; continue; }

                Vector3d faceNormal = face.NormalAt(uMid, vMid);
                if (!faceNormal.Unitize()) { ambiguousCount++; continue; }

                double dot = faceNormal * outwardRef;

                if (dot < 0)
                {
                    face.OrientationIsReversed = !face.OrientationIsReversed;
                    flippedCount++;
                    if (usedFallback)
                        statusLines.Add($"  Face {checkedCount}: flipped (used bbox-center fallback ref)");
                }
            }

            string status =
                $"Checked {checkedCount} faces | Flipped {flippedCount} | " +
                $"Ambiguous/skipped {ambiguousCount}";
            statusLines.Insert(0, status);

            DA.SetData(0, b);
            DA.SetData(1, flippedCount);
            DA.SetData(2, string.Join("\n", statusLines));
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid =>
            new Guid("E1D2C3B4-A596-4877-8E2F-3D4C5B6A7980");
    }
}