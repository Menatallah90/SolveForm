// HeatOrientationGraphPanelComponent.cs
// GUID: ee4ecd4b-68f9-4765-991c-12de60fc4a68
//
// FIX THIS SESSION (handoff 2.C): chart title updated now that the baseline
// methodology fix (FacadeHeatAnalyzerComponent, handoff 2.B) is done. The old
// title just said "red=baseline" with no explanation of what that baseline
// actually was. Now that the baseline is (when SiteFootprint+TotalHeight are
// wired) a same-footprint/same-height reference box, the title says so.
//
// Everything else unchanged from last version: dual grouped bars per
// orientation, blue = actual design, red = baseline. Axis scales to the max
// of BOTH datasets so they're comparable on one chart. Same font-disposal
// fix as before applies (GH_FontServer fonts are shared statics, never wrap
// them in `using`).
//
// INPUTS
//   0  HeatValuesActual    (double, list) -- blue bars
//   1  HeatValuesBaseline  (double, list) -- red bars
//   2  Orientations        (string, list)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace SolveForm.Components
{
    public class HeatOrientationGraphPanelComponent : GH_Component
    {
        private List<double> _actual = new List<double>();
        private List<double> _baseline = new List<double>();
        private List<string> _labels = new List<string>();

        public HeatOrientationGraphPanelComponent()
            : base("Heat Orientation Graph", "HeatGraph",
                "In-canvas grouped bar chart -- blue actual design vs red baseline, per compass direction.",
                "SolveForm", "Visualization")
        { }

        public override void CreateAttributes() => m_attributes = new HeatGraphAttributes(this);

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("HeatValuesActual", "HA", "Summed solar heat, actual design", GH_ParamAccess.list);
            pManager.AddNumberParameter("HeatValuesBaseline", "HB", "Summed solar heat, baseline (no design)", GH_ParamAccess.list);
            pManager.AddTextParameter("Orientations", "O", "Compass labels, same order as HeatValues", GH_ParamAccess.list);
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager) { }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var actual = new List<double>();
            var baseline = new List<double>();
            var labels = new List<string>();

            if (!DA.GetDataList(0, actual)) return;
            DA.GetDataList(1, baseline);
            DA.GetDataList(2, labels);

            _actual = actual;
            _baseline = baseline;
            _labels = labels;
        }

        public IReadOnlyList<double> GetActual() => _actual;
        public IReadOnlyList<double> GetBaseline() => _baseline;
        public IReadOnlyList<string> GetLabels() => _labels;

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("ee4ecd4b-68f9-4765-991c-12de60fc4a68");

        private class HeatGraphAttributes : GH_ComponentAttributes
        {
            private const int ChartWidth = 260;
            private const int ChartHeight = 140;
            private const int LeftAxisMargin = 40;
            private const int BottomAxisMargin = 24;

            public HeatGraphAttributes(GH_Component owner) : base(owner) { }

#pragma warning disable CA1416
            protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
            {
                if (channel != GH_CanvasChannel.Objects) { base.Render(canvas, graphics, channel); return; }

                var owner = (HeatOrientationGraphPanelComponent)Owner;
                var actual = owner.GetActual();
                var baseline = owner.GetBaseline();
                var labels = owner.GetLabels();

                base.Render(canvas, graphics, channel);

                float panelX = Bounds.X;
                float panelY = Bounds.Bottom + 4;
                RectangleF panelBounds = new RectangleF(panelX, panelY, ChartWidth, ChartHeight + BottomAxisMargin + 34);

                GH_Palette palette = Owner.Locked ? GH_Palette.Locked : GH_Palette.Normal;
                GH_Capsule capsule = GH_Capsule.CreateCapsule(panelBounds, palette);
                capsule.Render(graphics, false, Owner.Locked, false);
                capsule.Dispose();

                // FIX (2.C): honest label -- explains what red actually represents
                // now that the baseline methodology fix is in (same footprint +
                // same height reference box, when SiteFootprint/TotalHeight are
                // wired into FacadeHeatAnalyzer; degraded bounding-box fallback
                // otherwise -- check that component's BaselineInfo output).
                graphics.DrawString("blue=design  red=reference box (same footprint+height)", GH_FontServer.StandardBold, Brushes.Black,
                    new RectangleF(panelBounds.X, panelBounds.Y + 2, panelBounds.Width, 16),
                    GH_TextRenderingConstants.CenterCenter);

                if (actual == null || actual.Count == 0)
                {
                    graphics.DrawString("No data", GH_FontServer.Standard, Brushes.Gray,
                        new RectangleF(panelBounds.X, panelBounds.Y + 20, panelBounds.Width, 20),
                        GH_TextRenderingConstants.CenterCenter);
                    return;
                }

                bool hasBaseline = baseline != null && baseline.Count == actual.Count;

                float plotX = panelBounds.X + LeftAxisMargin;
                float plotY = panelBounds.Y + 20;
                float plotW = ChartWidth - LeftAxisMargin - 10;
                float plotH = ChartHeight;

                double dataMax = actual.Max();
                if (hasBaseline) dataMax = Math.Max(dataMax, baseline.Max());
                if (dataMax <= 0) dataMax = 1;

                const int targetTicks = 4;
                double tickStep = NiceNumber(dataMax / targetTicks, true);
                double axisMax = Math.Ceiling(dataMax / tickStep) * tickStep;
                if (axisMax <= 0) axisMax = tickStep;

                using (var axisPen = new Pen(Color.Black, 1))
                {
                    graphics.DrawLine(axisPen, plotX, plotY, plotX, plotY + plotH);
                    graphics.DrawLine(axisPen, plotX, plotY + plotH, plotX + plotW, plotY + plotH);
                }

                // FIX: shared canvas font -- never dispose.
                var smallFont = GH_FontServer.Small;

                int tickCount = (int)Math.Round(axisMax / tickStep);
                using (var gridPen = new Pen(Color.FromArgb(50, 0, 0, 0), 1))
                {
                    for (int i = 0; i <= tickCount; i++)
                    {
                        double tickVal = i * tickStep;
                        float ty = plotY + plotH - (float)(tickVal / axisMax) * plotH;
                        graphics.DrawLine(gridPen, plotX, ty, plotX + plotW, ty);
                        string label = tickVal >= 1000 ? $"{tickVal / 1000:0.#}k" : $"{tickVal:0.#}";
                        graphics.DrawString(label, smallFont, Brushes.Black,
                            new RectangleF(panelBounds.X, ty - 7, LeftAxisMargin - 4, 14),
                            new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });
                    }
                }

                int n = actual.Count;
                if (n <= 0) return;

                float barSlot = plotW / n;
                float groupPad = barSlot * 0.15f;
                float barWidth = hasBaseline ? (barSlot - groupPad * 2f) / 2f : barSlot * 0.6f;

                using (var blueBrush = new SolidBrush(Color.FromArgb(255, 60, 130, 235)))
                using (var redBrush = new SolidBrush(Color.FromArgb(255, 225, 70, 60)))
                {
                    for (int i = 0; i < n; i++)
                    {
                        float groupStart = plotX + i * barSlot + groupPad;

                        // Baseline (red) first, actual (blue) second -- consistent left-to-right order.
                        if (hasBaseline)
                        {
                            double vb = baseline[i];
                            float barHb = (float)(vb / axisMax) * plotH;
                            float bxB = groupStart;
                            float byB = plotY + plotH - barHb;
                            graphics.FillRectangle(redBrush, bxB, byB, barWidth, Math.Max(barHb, 1));
                        }

                        double va = actual[i];
                        float barHa = (float)(va / axisMax) * plotH;
                        float bxA = hasBaseline ? groupStart + barWidth : groupStart + (barSlot - groupPad * 2f - barWidth) / 2f;
                        float byA = plotY + plotH - barHa;
                        graphics.FillRectangle(blueBrush, bxA, byA, barWidth, Math.Max(barHa, 1));

                        string lbl = i < labels.Count ? labels[i] : "";
                        graphics.DrawString(lbl, smallFont, Brushes.Black,
                            new RectangleF(plotX + i * barSlot, plotY + plotH + 2, barSlot, BottomAxisMargin - 2),
                            GH_TextRenderingConstants.CenterCenter);
                    }
                }
            }

            private static double NiceNumber(double range, bool round)
            {
                if (range <= 0) return 1;
                double exponent = Math.Floor(Math.Log10(range));
                double fraction = range / Math.Pow(10, exponent);
                double niceFraction;
                if (round) { niceFraction = fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10; }
                else { niceFraction = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10; }
                return niceFraction * Math.Pow(10, exponent);
            }
        }
    }
}