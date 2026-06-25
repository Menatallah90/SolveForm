using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class SolveFormFacadesComponent : GH_Component
    {
        public SolveFormFacadesComponent()
            : base("SolveForm Facades", "SF_Fac",
                   "Extracts vertical exterior faces from unified brep and tags orientation",
                   "SolveForm", "Facade")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Unified", "U",
                "One unified brep from SolveForm Unify",
                GH_ParamAccess.item);
            pManager.AddNumberParameter("NorthAngle", "N",
                "North rotation in degrees (0 = World Y is North)",
                GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Facades", "F",
                "Vertical exterior faces — one per fragment",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Orientations", "O",
                "N/S/E/W per face (relative to NorthAngle)",
                GH_ParamAccess.list);
            pManager.AddVectorParameter("RightVectors", "FR",
                "Horizontal in-plane axis per face, derived from orientation — pass this to Openings, do NOT recompute from normals",
                GH_ParamAccess.list);
            pManager.AddPointParameter("Centers", "FC",
                "Centroid per face",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Status", "S",
                "Debug info",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep mass = null;
            double northAngle = 0.0;

            if (!DA.GetData(0, ref mass) || mass == null)
            {
                DA.SetData(4, "No input brep.");
                return;
            }
            DA.GetData(1, ref northAngle);

            // ── Rotated N/E frame ───────────────────────────────────────────
            double northRad = northAngle * Math.PI / 180.0;
            Vector3d northVec = new Vector3d(Math.Sin(northRad), Math.Cos(northRad), 0);
            northVec.Unitize();
            Vector3d eastVec = new Vector3d(northVec.Y, -northVec.X, 0);
            eastVec.Unitize();

            // ── Building center, in plan, for position-based orientation ────
            // IMPORTANT: do not use face.NormalAt() or BrepFace.FrameAt() normals
            // anywhere in this component. Boolean union (Brep or Mesh fallback)
            // in SolveForm Unify scrambles per-face normal direction — confirmed
            // root cause, see session handoff. Orientation here is derived purely
            // from face-centroid position relative to the building's plan center.
            BoundingBox bb = mass.GetBoundingBox(false);
            Point3d buildingCenter = new Point3d(
                (bb.Min.X + bb.Max.X) / 2.0,
                (bb.Min.Y + bb.Max.Y) / 2.0,
                0); // plan center only — Z ignored for orientation purposes

            double planRadius = Math.Max(
                (bb.Max.X - bb.Min.X) / 2.0,
                (bb.Max.Y - bb.Min.Y) / 2.0);
            // Faces whose centroid sits within this fraction of the plan radius
            // from the building center are treated as interior/ambiguous and skipped.
            double interiorThreshold = planRadius * 0.05;

            int totalFaces = mass.Faces.Count;
            int skippedFlat = 0;
            int skippedTiny = 0;
            int skippedInterior = 0;

            var rawFacades = new List<Brep>();
            var rawOrientations = new List<string>();
            var rawCenters = new List<Point3d>();

            foreach (BrepFace face in mass.Faces)
            {
                double uMid = (face.Domain(0).Min + face.Domain(0).Max) / 2.0;
                double vMid = (face.Domain(1).Min + face.Domain(1).Max) / 2.0;

                AreaMassProperties amp = AreaMassProperties.Compute(face);
                if (amp == null || amp.Area < 0.1) { skippedTiny++; continue; }

                Point3d centroid = amp.Centroid;

                // ── Verticality check WITHOUT trusting face normal direction ──
                // Sample a small patch of the surface and check how flat it is
                // in Z by comparing point spread vertically vs horizontally,
                // using the surface's own frame orientation (not sign-sensitive).
                bool frameOk = face.FrameAt(uMid, vMid, out Plane pl);
                if (!frameOk) { skippedFlat++; continue; }

                // Use the ABSOLUTE value / axis of the normal only — never its sign.
                // A scrambled normal can point either way but its axis (which world
                // direction it's most aligned with) still distinguishes "wall" from
                // "floor/roof" reliably, since boolean ops corrupt direction, not axis.
                Vector3d nAxisCheck = pl.Normal;
                if (!nAxisCheck.Unitize()) { skippedFlat++; continue; }
                if (Math.Abs(nAxisCheck.Z) > 0.5) { skippedFlat++; continue; } // floor/roof

                // ── Position-based orientation ───────────────────────────────
                Point3d planCentroid = new Point3d(centroid.X, centroid.Y, 0);
                Vector3d toFace = planCentroid - buildingCenter;

                if (toFace.Length < interiorThreshold)
                {
                    skippedInterior++;
                    continue;
                }
                toFace.Unitize();

                string orient = GetOrientationFromPosition(toFace, northVec, eastVec);

                rawFacades.Add(face.DuplicateFace(false));
                rawOrientations.Add(orient);
                rawCenters.Add(centroid);
            }

            // ── Filter mesh/boolean slivers ──────────────────────────────────
            var finalFacades = new List<Brep>();
            var finalOrientations = new List<string>();
            var finalRightVectors = new List<Vector3d>();
            var finalCenters = new List<Point3d>();

            for (int i = 0; i < rawFacades.Count; i++)
            {
                var amp = AreaMassProperties.Compute(rawFacades[i]);
                if (amp == null || amp.Area < 1.0) continue; // skip mesh slivers

                string orient = rawOrientations[i];
                Vector3d faceRight = GetRightVector(orient, northVec, eastVec);

                finalFacades.Add(rawFacades[i]);
                finalOrientations.Add(orient);
                finalRightVectors.Add(faceRight);
                finalCenters.Add(rawCenters[i]);
            }

            string status =
                $"Total faces: {totalFaces} | " +
                $"Skipped flat/floor-roof: {skippedFlat} | " +
                $"Skipped tiny: {skippedTiny} | " +
                $"Skipped interior(<{interiorThreshold:F2}m from center): {skippedInterior} | " +
                $"Output faces: {finalFacades.Count} " +
                $"({string.Join(", ", finalOrientations)})";

            DA.SetDataList(0, finalFacades);
            DA.SetDataList(1, finalOrientations);
            DA.SetDataList(2, finalRightVectors);
            DA.SetDataList(3, finalCenters);
            DA.SetData(4, status);
        }

        // Orientation purely from "which way is this face's centroid from the
        // building's plan center", projected onto the rotated N/E frame.
        // This is immune to scrambled brep/mesh normals because it never reads
        // a normal at all — it's pure position geometry.
        private string GetOrientationFromPosition(Vector3d toFaceDir, Vector3d northVec, Vector3d eastVec)
        {
            double northDot = toFaceDir * northVec;
            double eastDot = toFaceDir * eastVec;

            double angle = Math.Atan2(eastDot, northDot) * 180.0 / Math.PI;
            if (angle < 0) angle += 360.0;

            // Axis-aligned massing under a rotated north: bucket into 4 quadrants
            // (45° boundaries) rather than 8, since this pipeline's typologies
            // (Box/L-Shape/Courtyard/cruciform) do not produce diagonal facades —
            // only the north/east frame itself rotates.
            if (angle < 45.0 || angle >= 315.0) return "N";
            if (angle < 135.0) return "E";
            if (angle < 225.0) return "S";
            return "W";
        }

        // Horizontal in-plane axis for a facade of a given orientation, in the
        // SAME rotated N/E frame used to compute that orientation. This is what
        // Openings should use as faceRight — it is derived from the orientation
        // tag (which is itself position-derived), never from a face normal.
        //
        // Convention: facing the wall from outside, "right" sweeps clockwise
        // when viewed from above — i.e. faceRight = normal × worldZ for a wall
        // whose outward normal is the orientation's direction vector.
        private Vector3d GetRightVector(string orient, Vector3d northVec, Vector3d eastVec)
        {
            Vector3d outwardDir;
            switch (orient)
            {
                case "N": outwardDir = northVec; break;
                case "S": outwardDir = -northVec; break;
                case "E": outwardDir = eastVec; break;
                case "W": outwardDir = -eastVec; break;
                default: outwardDir = northVec; break; // safe fallback, should not occur per typology
            }

            Vector3d right = Vector3d.CrossProduct(outwardDir, Vector3d.ZAxis);
            if (!right.Unitize()) right = eastVec; // degenerate fallback
            return right;
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid =>
            new Guid("A7F3C291-5E84-4B12-9D63-2F8E1A047B55");
    }
}