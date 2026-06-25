using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class FloorsComponent : GH_Component
    {
        public FloorsComponent()
          : base("SolveForm Floors", "SFFloors",
              "Builds multi-floor geometry from section zones",
              "SolveForm", "Fabrication")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Profiles", "Prof", "Zone profile curves from SolveForm Section", GH_ParamAccess.list);
            pManager.AddNumberParameter("ZoneHeights", "ZH", "Zone heights from SolveForm Section", GH_ParamAccess.list);
            pManager.AddNumberParameter("FloorHeight", "FH", "Floor-to-floor height in meters", GH_ParamAccess.item, 3.5);
            pManager.AddNumberParameter("SlabThickness", "ST", "Slab thickness in meters", GH_ParamAccess.item, 0.25);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Floors", "Flrs", "Habitable floor volumes", GH_ParamAccess.list);
            pManager.AddBrepParameter("Slabs", "Slabs", "Structural slabs", GH_ParamAccess.list);
            pManager.AddTextParameter("Report", "Rep", "Floor breakdown per zone", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var profiles = new List<Curve>();
            var zoneHeights = new List<double>();
            double floorHeight = 3.5;
            double slabThick = 0.25;

            if (!DA.GetDataList(0, profiles)) return;
            if (!DA.GetDataList(1, zoneHeights)) return;
            DA.GetData(2, ref floorHeight);
            DA.GetData(3, ref slabThick);

            floorHeight = Math.Max(2.0, floorHeight);
            slabThick = Math.Max(0.05, Math.Min(slabThick, floorHeight * 0.25));

            var floorBreps = new List<Brep>();
            var slabBreps = new List<Brep>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🏢 Floor Summary");

            int zoneCount = Math.Min(profiles.Count, zoneHeights.Count);

            for (int z = 0; z < zoneCount; z++)
            {
                var profile = profiles[z];
                double zoneH = zoneHeights[z];

                if (profile == null) continue;

                // Flatten profile to Z=0
                var flat = profile.DuplicateCurve();
                var bb = flat.GetBoundingBox(false);
                if (Math.Abs(bb.Min.Z) > 0.001)
                    flat.Transform(Transform.Translation(0, 0, -bb.Min.Z));

                int floors = Math.Max(1, (int)Math.Floor(zoneH / floorHeight));

                sb.AppendLine($"   Zone {z + 1}: {zoneH:F1}m → {floors} floors");

                for (int f = 0; f < floors; f++)
                {
                    double slabBot = f * floorHeight;
                    double slabTop = slabBot + slabThick;
                    double spaceBot = slabTop;
                    double spaceTop = slabBot + floorHeight;

                    var slab = ExtrudeUp(flat, slabBot, slabTop);
                    var space = ExtrudeUp(flat, spaceBot, spaceTop);

                    if (slab != null) slabBreps.Add(slab);
                    if (space != null) floorBreps.Add(space);
                }

                // Roof slab for this zone
                double roofBot = floors * floorHeight;
                var roof = ExtrudeUp(flat, roofBot, roofBot + slabThick);
                if (roof != null) slabBreps.Add(roof);
            }

            DA.SetDataList(0, floorBreps);
            DA.SetDataList(1, slabBreps);
            DA.SetData(2, sb.ToString());
        }

        private Brep ExtrudeUp(Curve profile, double z0, double z1)
        {
            try
            {
                double h = z1 - z0;
                if (h < 0.001) return null;

                var crv = profile.DuplicateCurve();
                crv.Transform(Transform.Translation(0, 0, z0));

                var srf = Surface.CreateExtrusion(crv, new Vector3d(0, 0, h));
                if (srf == null) return null;

                var brep = srf.ToBrep();
                brep = brep?.CapPlanarHoles(0.01);
                return brep;
            }
            catch { return null; }
        }

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("D4E5F6A7-B8C9-0123-DEFA-234567890123");
    }
}