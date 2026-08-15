// SolveFormCutOpeningsComponent.cs
// GUID: D5F8A312-6B94-4E23-9C74-2A0F3B158D77
//
// UNCHANGED LOGIC THIS SESSION -- the real fix for the 0/515 "boolean
// null/empty" failure lives in HorizontalOpeningStripsComponent.cs
// (CutterOvershoot). Cutters and this shell's outer face were built from
// the exact same coincident surface with zero gap -- classic Rhino boolean
// failure mode, fails silently and uniformly for every cutter, which is
// exactly what you saw. Overshoot fixes it at the source.
//
// ADDED THIS SESSION -- shell diagnostics. If cuts still fail after the
// overshoot fix, the Report below will now tell us WHY instead of us
// guessing again: shell IsValid/IsSolid state and a naked-edge count. A
// non-zero naked edge count means the panel union did not produce a
// watertight shell -- gaps at slice-to-slice or step edges where the
// per-face offset panels didn't quite meet -- which would explain boolean
// failures independently of the overshoot fix.

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
                   "Builds shell from per-face thickened panels (robust on stepped geometry), cuts window openings one at a time.",
                   "SolveForm", "Facade")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("UnifiedBrep", "B", "Closed mass brep from Unify", GH_ParamAccess.item);
            pManager.AddBrepParameter("Openings", "O", "Solid window openings (WindowSolids from Opening Strips)", GH_ParamAccess.list);
            pManager.AddNumberParameter("WallThickness", "T", "Wall thickness in model units. Default 0.3.", GH_ParamAccess.item, 0.3);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("PunchedMass", "P", "Hollow mass with window openings", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "R", "Diagnostic report", GH_ParamAccess.item);
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

            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            var report = new System.Text.StringBuilder();
            report.AppendLine("══ CUT OPENINGS REPORT ══");
            report.AppendLine($"  {openings.Count} window solids | WallThickness={wallT}");

            // ── Step 1: build shell from thickened per-face panels ──────────
            var vmp = VolumeMassProperties.Compute(mass);
            Point3d volumeCentroid = (vmp != null) ? vmp.Centroid : mass.GetBoundingBox(false).Center;

            var panels = new List<Brep>();
            int panelFail = 0, panelFlip = 0;

            foreach (BrepFace face in mass.Faces)
            {
                double uMid = face.Domain(0).Mid, vMid = face.Domain(1).Mid;
                var amp = AreaMassProperties.Compute(face.DuplicateFace(false));
                if (amp == null) { panelFail++; continue; }
                Point3d faceCentroid = amp.Centroid;
                double faceDistToCenter = (faceCentroid - volumeCentroid).Length;

                Brep panel = Brep.CreateFromOffsetFace(face, -wallT, tol, false, true);
                if (panel != null && panel.IsValid)
                {
                    var pvmp = VolumeMassProperties.Compute(panel);
                    if (pvmp != null)
                    {
                        double panelDistToCenter = (pvmp.Centroid - volumeCentroid).Length;
                        if (panelDistToCenter >= faceDistToCenter)
                        {
                            Brep flipped = Brep.CreateFromOffsetFace(face, wallT, tol, false, true);
                            if (flipped != null && flipped.IsValid) { panel = flipped; panelFlip++; }
                        }
                    }
                }

                if (panel == null || !panel.IsValid) { panelFail++; continue; }
                panels.Add(panel);
            }

            report.AppendLine($"  Panels built: {panels.Count}/{mass.Faces.Count} | flipped: {panelFlip} | failed: {panelFail}");

            if (panels.Count == 0)
            {
                report.AppendLine("  FATAL: no shell panels built. Aborting.");
                DA.SetData(0, mass);
                DA.SetData(1, report.ToString());
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No shell panels could be built.");
                return;
            }

            Brep shell = null;
            Brep[] unionResult = null;
            try { unionResult = Brep.CreateBooleanUnion(panels, tol * 5); }
            catch (Exception ex) { report.AppendLine($"  Panel union threw: {ex.Message}"); }

            if (unionResult != null && unionResult.Length > 0)
            {
                shell = unionResult.OrderByDescending(b => {
                    var v = VolumeMassProperties.Compute(b);
                    return v != null ? Math.Abs(v.Volume) : 0;
                }).First();
                report.AppendLine($"  Shell: batch union OK ({unionResult.Length} fragment(s), kept largest)");
            }
            else
            {
                report.AppendLine("  Batch union failed -- trying sequential pairwise union.");
                Brep current = panels[0];
                int mergedCount = 1;
                for (int i = 1; i < panels.Count; i++)
                {
                    Brep[] step = null;
                    try { step = Brep.CreateBooleanUnion(new List<Brep> { current, panels[i] }, tol * 5); }
                    catch { }
                    if (step != null && step.Length > 0)
                    {
                        current = step.OrderByDescending(b => {
                            var v = VolumeMassProperties.Compute(b);
                            return v != null ? Math.Abs(v.Volume) : 0;
                        }).First();
                        mergedCount++;
                    }
                }
                shell = current;
                report.AppendLine($"  Shell: pairwise union merged {mergedCount}/{panels.Count} panels");
            }

            if (shell != null && !shell.IsValid) shell.Repair(tol);

            // ── NEW: shell health diagnostics ────────────────────────────
            // If cuts still fail after the overshoot fix in Opening Strips,
            // this tells us whether the shell itself is the problem (not
            // watertight) rather than the cutter/wall coincidence issue.
            if (shell != null)
            {
                Curve[] nakedEdges = shell.DuplicateNakedEdgeCurves(true, true);
                int nakedCount = nakedEdges != null ? nakedEdges.Length : 0;
                var shellVmp = VolumeMassProperties.Compute(shell);
                double shellVol = shellVmp != null ? Math.Abs(shellVmp.Volume) : 0;
                report.AppendLine($"  Shell health: IsValid={shell.IsValid} | IsSolid={shell.IsSolid} | NakedEdgeLoops={nakedCount} | Volume={shellVol:F2}");
                if (!shell.IsSolid || nakedCount > 0)
                    report.AppendLine("  WARNING: shell is not watertight. Cuts are likely to fail regardless of cutter geometry. " +
                                       "This means the per-face panel union is leaving gaps, most likely at step/tilt edges where " +
                                       "adjacent panels don't meet exactly -- a separate problem from the cutter overshoot fix.");
            }
            else
            {
                report.AppendLine("  Shell health: shell is NULL after union attempts.");
            }

            // ── Step 2: cut windows one at a time, tolerance retry ladder ────
            Brep resultBrep = shell;
            int cutOK = 0, cutFailedNull = 0, cutFailedSanity = 0, cutFailedInvalid = 0;
            var failLog = new List<string>();
            double[] tolLadder = { tol, tol * 5, tol * 20 };

            for (int i = 0; i < openings.Count; i++)
            {
                Brep cutter = openings[i];
                if (cutter == null || !cutter.IsValid)
                {
                    if (cutter != null) cutter.Repair(tol);
                    if (cutter == null || !cutter.IsValid) { cutFailedInvalid++; failLog.Add($"[{i}] invalid cutter"); continue; }
                }

                bool succeeded = false;
                string lastFailReason = "";

                foreach (double t in tolLadder)
                {
                    Brep[] step = null;
                    try { step = Brep.CreateBooleanDifference(new[] { resultBrep }, new[] { cutter.DuplicateBrep() }, t); }
                    catch (Exception ex) { lastFailReason = $"threw: {ex.Message}"; continue; }

                    if (step == null || step.Length == 0) { lastFailReason = "boolean null/empty"; continue; }

                    Brep candidate = step.OrderByDescending(b => {
                        var v = VolumeMassProperties.Compute(b);
                        return v != null ? Math.Abs(v.Volume) : 0;
                    }).First();

                    var curVol = VolumeMassProperties.Compute(resultBrep);
                    var newVol = VolumeMassProperties.Compute(candidate);
                    double cv = curVol != null ? Math.Abs(curVol.Volume) : 0;
                    double nv = newVol != null ? Math.Abs(newVol.Volume) : 0;

                    if (nv > cv * 0.5)
                    {
                        resultBrep = candidate;
                        cutOK++; succeeded = true;
                        break;
                    }
                    else lastFailReason = $"sanity-rejected (result vol {nv:F2} vs current {cv:F2})";
                }

                if (!succeeded)
                {
                    if (lastFailReason.StartsWith("sanity")) cutFailedSanity++;
                    else cutFailedNull++;
                    failLog.Add($"[{i}] {lastFailReason}");
                }
            }

            report.AppendLine($"  Cuts: {cutOK} OK | {cutFailedNull} boolean-null | {cutFailedSanity} sanity-rejected | {cutFailedInvalid} invalid-cutter");
            if (failLog.Count > 0)
            {
                report.AppendLine("  First failures:");
                foreach (var l in failLog.Take(10)) report.AppendLine("    " + l);
                if (failLog.Count > 10) report.AppendLine($"    ... {failLog.Count - 10} more");
            }

            if (cutOK == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "All window cuts failed -- see Report.");
            else if (cutFailedNull + cutFailedSanity + cutFailedInvalid > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{cutOK}/{openings.Count} windows cut -- see Report.");

            report.AppendLine("══ DONE ══");
            DA.SetData(0, resultBrep);
            DA.SetData(1, report.ToString());
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("D5F8A312-6B94-4E23-9C74-2A0F3B158D77");
    }
}