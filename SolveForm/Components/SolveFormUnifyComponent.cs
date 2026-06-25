using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class SolveFormUnifyComponent : GH_Component
    {
        public SolveFormUnifyComponent()
            : base("SolveForm Unify", "SF_Unify",
                   "Boolean union of section masses into one clean exterior shell",
                   "SolveForm", "Facade")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Masses", "M",
                "List of closed breps from SolveForm Section",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Unified", "U",
                "Single closed exterior shell",
                GH_ParamAccess.item);
            pManager.AddTextParameter("Status", "S",
                "What happened", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var masses = new List<Brep>();
            if (!DA.GetDataList(0, masses) || masses.Count == 0)
            { DA.SetData(1, "No input."); return; }

            masses.RemoveAll(b => b == null);

            if (masses.Count == 1)
            {
                DA.SetData(0, masses[0].DuplicateBrep());
                DA.SetData(1, "Single brep — passed through.");
                return;
            }

            double tol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            // Fix normals — inverted normals cause boolean union to subtract instead of add
            // VolumeMassProperties returns negative volume when normals point inward
            foreach (var b in masses)
            {
                var vmp = VolumeMassProperties.Compute(b);
                if (vmp != null && vmp.Volume < 0)
                    b.Flip();
            }

            // Compute individual volumes for sanity check later
            var inputVolumes = masses.Select(b => BrepVolume(b)).ToList();
            double totalInputVol = inputVolumes.Sum();
            // Max possible union volume = sum of all (before subtracting overlaps)
            // Min acceptable union volume = largest single input
            double largestInputVol = inputVolumes.Max();

            var statusLines = new List<string>();
            statusLines.Add($"Inputs: {masses.Count} | IndivVols: [{string.Join(", ", inputVolumes.Select(v => v.ToString("F0")))}]");

            // ── ATTEMPT 1: n-way Brep Boolean Union ───────────────────────
            var dups = masses.Select(b => b.DuplicateBrep()).ToList();
            Brep[] boolResult = null;
            try { boolResult = Brep.CreateBooleanUnion(dups, tol); }
            catch (Exception ex) { statusLines.Add($"BrepBoolean threw: {ex.Message}"); }

            if (boolResult != null && boolResult.Length > 0)
            {
                Brep candidate = LargestByVolume(boolResult);
                double resultVol = BrepVolume(candidate);
                statusLines.Add($"BrepBoolean: {boolResult.Length} fragments | Vol: {resultVol:F2}");

                // Sanity check: for same-footprint slices, union = tallest slice,
                // so result vol will be ~= largest input. Accept at >= 90%.
                if (resultVol >= largestInputVol * 0.90)
                {
                    DA.SetData(0, candidate);
                    statusLines.Add("Method: BrepBooleanUnion OK");
                    DA.SetData(1, string.Join(" | ", statusLines));
                    return;
                }
                else
                {
                    statusLines.Add($"BrepBoolean REJECTED — result vol {resultVol:F2} <= largest input {largestInputVol:F2} — coplanar base face issue, falling through");
                }
            }
            else
            {
                statusLines.Add("BrepBoolean returned null");
            }

            // ── ATTEMPT 2: Mesh Boolean Union ─────────────────────────────
            // Mesh boolean doesn't care about coplanar faces — robust fallback
            string meshStatus;
            Brep meshResult = TryMeshBoolean(masses, tol, out meshStatus, statusLines);
            statusLines.Add(meshStatus);

            if (meshResult != null)
            {
                double meshVol = BrepVolume(meshResult);
                // Accept if result is at least 90% of the largest input.
                // For same-footprint slices the union = tallest slice, so
                // result vol will be close to (not larger than) the largest input.
                if (meshVol >= largestInputVol * 0.90)
                {
                    DA.SetData(0, meshResult);
                    statusLines.Add($"Method: MeshBoolean OK | Vol: {meshVol:F2}");
                    DA.SetData(1, string.Join("\n", statusLines));
                    return;
                }
                else
                {
                    statusLines.Add($"MeshBoolean REJECTED — vol {meshVol:F2} < 90% of largest input {largestInputVol:F2}");
                }
            }

            // ── ATTEMPT 3: Pairwise Brep accumulation ─────────────────────
            // Sort by volume descending — union largest first
            var sorted = masses
                .Select(b => b.DuplicateBrep())
                .OrderByDescending(b => BrepVolume(b))
                .ToList();

            Brep pairResult = TryPairwiseBrep(sorted, tol, statusLines);
            if (pairResult != null)
            {
                double pairVol = BrepVolume(pairResult);
                if (pairVol > largestInputVol * 1.05)
                {
                    DA.SetData(0, pairResult);
                    statusLines.Add($"Method: PairwiseBrep OK | Vol: {pairVol:F2}");
                    DA.SetData(1, string.Join("\n", statusLines));
                    return;
                }
                else
                {
                    statusLines.Add($"PairwiseBrep REJECTED — vol {pairVol:F2} too small");
                }
            }

            // ── ATTEMPT 4: Coplanar fix — micro Z offsets ─────────────────
            // Lift each slice slightly so bases don't coincide, union, bring back down
            double microStep = tol * 50; // 0.05 at default tol
            var lifted = new List<Brep>();
            for (int i = 0; i < masses.Count; i++)
            {
                var b = masses[i].DuplicateBrep();
                b.Transform(Transform.Translation(0, 0, i * microStep));
                lifted.Add(b);
            }

            Brep[] liftedResult = null;
            try { liftedResult = Brep.CreateBooleanUnion(lifted, tol * 10); }
            catch (Exception ex) { statusLines.Add($"LiftedBoolean threw: {ex.Message}"); }

            if (liftedResult != null && liftedResult.Length > 0)
            {
                Brep candidate = LargestByVolume(liftedResult);
                double liftedVol = BrepVolume(candidate);
                statusLines.Add($"LiftedBoolean: {liftedResult.Length} fragments | Vol: {liftedVol:F2}");

                if (liftedVol > largestInputVol * 1.05)
                {
                    // Translate back down to restore original Z
                    candidate.Transform(Transform.Translation(0, 0, -microStep));
                    DA.SetData(0, candidate);
                    statusLines.Add("Method: CoplanarFix+BooleanUnion OK");
                    DA.SetData(1, string.Join("\n", statusLines));
                    return;
                }
                else
                {
                    statusLines.Add($"LiftedBoolean REJECTED — vol {liftedVol:F2} too small");
                }
            }

            // ── ALL FAILED ────────────────────────────────────────────────
            statusLines.Add("ALL METHODS FAILED — returning null");
            DA.SetData(0, null);
            DA.SetData(1, string.Join("\n", statusLines));
        }

        // ── MESH BOOLEAN ──────────────────────────────────────────────────
        private Brep TryMeshBoolean(List<Brep> breps, double tol, out string status, List<string> log)
        {
            var mp = new MeshingParameters(0.1);
            mp.MaximumEdgeLength = 0.3;
            mp.MinimumEdgeLength = 0.05;
            mp.SimplePlanes = true;

            var meshes = new List<Mesh>();
            for (int i = 0; i < breps.Count; i++)
            {
                Mesh[] ms = Mesh.CreateFromBrep(breps[i], mp);
                if (ms == null || ms.Length == 0)
                {
                    log.Add($"  Brep[{i}] failed to mesh");
                    continue;
                }
                var combined = new Mesh();
                foreach (var m in ms) combined.Append(m);
                combined.Weld(0.01);
                combined.UnifyNormals();
                combined.Normals.ComputeNormals();
                // Ensure mesh normals point outward — check vs volume centroid
                var mBB = combined.GetBoundingBox(false);
                Point3d mCenter = mBB.Center;
                // Sample first face normal direction vs centroid
                if (combined.Faces.Count > 0)
                {
                    Point3d faceCenter = combined.Faces.GetFaceCenter(0);
                    Vector3d toFace = faceCenter - mCenter;
                    Vector3d faceNorm = combined.FaceNormals[0];
                    if (toFace * faceNorm < 0)
                        combined.Flip(true, true, true, true);
                }
                meshes.Add(combined);
                log.Add($"  Brep[{i}] meshed: {combined.Vertices.Count}v {combined.Faces.Count}f");
            }

            if (meshes.Count < 2)
            {
                status = $"MeshBoolean: only {meshes.Count} mesh(es) generated — need at least 2";
                return null;
            }

            Mesh[] unionResult = null;
            try { unionResult = Mesh.CreateBooleanUnion(meshes); }
            catch (Exception ex)
            {
                status = $"MeshBoolean threw: {ex.Message}";
                return null;
            }

            if (unionResult == null || unionResult.Length == 0)
            {
                status = "MeshBoolean returned null/empty";
                return null;
            }

            // Pick largest by face count
            Mesh bestMesh = unionResult.OrderByDescending(m => m.Faces.Count).First();
            bestMesh.Weld(0.01);
            bestMesh.UnifyNormals();
            bestMesh.Normals.ComputeNormals();

            Brep result = Brep.CreateFromMesh(bestMesh, true);
            if (result == null)
            {
                status = $"MeshBoolean: union OK ({unionResult.Length} frags) but Brep conversion failed";
                return null;
            }
            status = $"MeshBoolean: {meshes.Count} meshes → {unionResult.Length} union frags → Brep OK";
            return result;
        }

        // ── PAIRWISE BREP ACCUMULATION ────────────────────────────────────
        private Brep TryPairwiseBrep(List<Brep> solids, double tol, List<string> log)
        {
            Brep current = solids[0];
            for (int i = 1; i < solids.Count; i++)
            {
                Brep[] step = null;
                try
                {
                    step = Brep.CreateBooleanUnion(
                        new List<Brep> { current, solids[i] }, tol);
                }
                catch { }

                if (step != null && step.Length > 0)
                {
                    Brep next = LargestByVolume(step);
                    if (next != null)
                    {
                        log.Add($"  Pairwise[{i}]: OK vol={BrepVolume(next):F2}");
                        current = next;
                    }
                }
                else
                {
                    log.Add($"  Pairwise[{i}]: FAILED — keeping current");
                }
            }
            return current;
        }

        // ── UTILITIES ─────────────────────────────────────────────────────
        private double BrepVolume(Brep b)
        {
            if (b == null) return 0;
            var vmp = VolumeMassProperties.Compute(b);
            return vmp != null ? Math.Abs(vmp.Volume) : 0;
        }

        private Brep LargestByVolume(Brep[] breps)
        {
            Brep best = null; double bestVol = 0;
            foreach (var b in breps)
            {
                if (b == null) continue;
                double v = BrepVolume(b);
                if (v > bestVol) { bestVol = v; best = b; }
            }
            return best;
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid =>
            new Guid("B1C2D3E4-F5A6-7890-BCDE-F12345678901");
    }
}