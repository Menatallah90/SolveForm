using System;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace SolveForm.Components
{
    public class NorthArrowComponent : GH_Component
    {
        public NorthArrowComponent()
          : base("North Arrow", "North",
              "Draws a North arrow at a given location in the Rhino viewport",
              "SolveForm", "Utilities")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("Origin", "O", "Base point for the compass", GH_ParamAccess.item, new Point3d(0, 0, 0));
            pManager.AddNumberParameter("Scale", "S", "Scale of the arrow (meters)", GH_ParamAccess.item, 5.0);
            pManager.AddNumberParameter("NorthOffset", "N°", "True North offset in degrees (from Y-axis)", GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("NorthArrow", "N", "North arrow curve", GH_ParamAccess.item);
            pManager.AddCurveParameter("Compass", "C", "Compass circle", GH_ParamAccess.item);
            pManager.AddCurveParameter("CardinalLines", "CL", "N/S/E/W lines", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var origin = new Point3d(0, 0, 0);
            double scale = 5.0;
            double northOffset = 0.0;

            DA.GetData(0, ref origin);
            DA.GetData(1, ref scale);
            DA.GetData(2, ref northOffset);

            double offsetRad = northOffset * Math.PI / 180.0;

            // North direction vector (rotated by offset)
            double nx = -Math.Sin(offsetRad);
            double ny = Math.Cos(offsetRad);

            // North arrow — tip, left base, right base
            var tip = new Point3d(origin.X + nx * scale, origin.Y + ny * scale, origin.Z);
            var left = new Point3d(origin.X - ny * scale * 0.15, origin.Y + nx * scale * 0.15, origin.Z);
            var right = new Point3d(origin.X + ny * scale * 0.15, origin.Y - nx * scale * 0.15, origin.Z);

            var arrowPts = new Point3d[] { tip, left, origin, right, tip };
            var northArrow = new Polyline(arrowPts).ToNurbsCurve();

            // Compass circle
            var compassCircle = new Circle(
                new Plane(origin, Vector3d.ZAxis), scale * 1.2).ToNurbsCurve();

            // Cardinal lines N/S/E/W
            var cardinalLines = new System.Collections.Generic.List<Curve>();
            double[] angles = { 0, 90, 180, 270 };

            foreach (double a in angles)
            {
                double rad = (a + northOffset) * Math.PI / 180.0;
                double dx = -Math.Sin(rad);
                double dy = Math.Cos(rad);

                double innerR = scale * 0.9;
                double outerR = scale * 1.2;

                var p1 = new Point3d(origin.X + dx * innerR, origin.Y + dy * innerR, origin.Z);
                var p2 = new Point3d(origin.X + dx * outerR, origin.Y + dy * outerR, origin.Z);

                cardinalLines.Add(new Line(p1, p2).ToNurbsCurve());
            }

            DA.SetData(0, northArrow);
            DA.SetData(1, compassCircle);
            DA.SetDataList(2, cardinalLines);
        }

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("E5F6A7B8-C9D0-1234-EFAB-345678901234");
    }
}