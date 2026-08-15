using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;
using SolveForm.Core;
using SolveForm.Models;

namespace SolveForm.Components
{
    public class SolveFormComponent : GH_Component
    {
        public SolveFormComponent()
          : base("SolveForm Solar", "SFO",
              "Generates and ranks massing footprints optimized for solar and wind performance",
              "SolveForm", "Optimization")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "Lat", "Site latitude in decimal degrees", GH_ParamAccess.item, 24.7);
            pManager.AddNumberParameter("Longitude", "Lon", "Site longitude in decimal degrees", GH_ParamAccess.item, 46.7);
            pManager.AddNumberParameter("SiteWidth", "SW", "Site width in meters (East-West)", GH_ParamAccess.item, 50.0);
            pManager.AddNumberParameter("SiteDepth", "SD", "Site depth in meters (North-South)", GH_ParamAccess.item, 40.0);
            pManager.AddNumberParameter("NorthOffset", "N°", "True North offset in degrees", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("MaxCoverage", "Cov", "Maximum site coverage ratio (0.0-1.0)", GH_ParamAccess.item, 0.6);
            pManager.AddNumberParameter("StudyHeight", "H", "Reference height for solar/wind scoring (m)", GH_ParamAccess.item, 12.0);
            pManager.AddIntegerParameter("Candidates", "N", "Number of design candidates to generate", GH_ParamAccess.item, 50);
            pManager.AddIntegerParameter("TopResults", "Top", "How many top results to store", GH_ParamAccess.item, 5);
            pManager.AddIntegerParameter("Select", "Sel", "Which result to display (0 = best)", GH_ParamAccess.item, 0);
            pManager.AddNumberParameter("MonthlySolar", "Sol", "[Optional] Monthly solar radiation (12 values)", GH_ParamAccess.list);
            pManager.AddNumberParameter("SolarWeight", "Wsol", "Solar objective weight (0-1)", GH_ParamAccess.item, 0.6);
            pManager.AddNumberParameter("WindWeight", "Wwnd", "Wind objective weight (0-1)", GH_ParamAccess.item, 0.4);
            pManager.AddNumberParameter("WindDirData", "WDirD", "[Optional] Monthly wind directions (12 values)", GH_ParamAccess.list);
            pManager.AddNumberParameter("WindSpdData", "WSpdD", "[Optional] Monthly wind speeds (12 values, m/s)", GH_ParamAccess.list);
            pManager.AddTextParameter("EdgeStyle", "Edge", "Edge style: Orthogonal / Chamfered / Smooth", GH_ParamAccess.item, "Orthogonal");

            pManager[10].Optional = true;
            pManager[13].Optional = true;
            pManager[14].Optional = true;
            pManager[15].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Footprint", "FP", "Selected massing footprint", GH_ParamAccess.item);
            pManager.AddCurveParameter("Profile", "Prof", "Profile curve — wire to SolveForm Section/Floors", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "Report", "Performance report for selected result", GH_ParamAccess.item);
            pManager.AddTextParameter("AllReports", "All", "All ranked results summary", GH_ParamAccess.item);
            pManager.AddNumberParameter("Score", "Score", "Score for selected result (0-100)", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double lat = 24.7, lon = 46.7, sw = 50, sd = 40, north = 0;
            double maxCov = 0.6, studyHeight = 12.0;
            int nCandidates = 50, nTop = 5, selected = 0;
            double solarWeight = 0.6, windWeight = 0.4;
            string edgeStyle = "Orthogonal";

            DA.GetData(0, ref lat);
            DA.GetData(1, ref lon);
            DA.GetData(2, ref sw);
            DA.GetData(3, ref sd);
            DA.GetData(4, ref north);
            DA.GetData(5, ref maxCov);
            DA.GetData(6, ref studyHeight);
            DA.GetData(7, ref nCandidates);
            DA.GetData(8, ref nTop);
            DA.GetData(9, ref selected);
            DA.GetData(11, ref solarWeight);
            DA.GetData(12, ref windWeight);
            DA.GetData(15, ref edgeStyle);

            var monthlySolar = new List<double>();
            var monthlyWindDir = new List<double>();
            var monthlyWindSpd = new List<double>();

            DA.GetDataList(10, monthlySolar);
            DA.GetDataList(13, monthlyWindDir);
            DA.GetDataList(14, monthlyWindSpd);

            // ── WIND ──────────────────────────────────────────────────────
            double prevailingWind = 315.0;
            double avgWindSpeed = 3.5;

            if (monthlyWindDir.Count == 12)
            {
                double totalX = 0, totalY = 0;
                for (int i = 0; i < 12; i++)
                {
                    double rad = monthlyWindDir[i] * Math.PI / 180.0;
                    totalX += Math.Cos(rad);
                    totalY += Math.Sin(rad);
                }
                double avgRad = Math.Atan2(totalY / 12.0, totalX / 12.0);
                prevailingWind = avgRad * 180.0 / Math.PI;
                if (prevailingWind < 0) prevailingWind += 360;
            }

            if (monthlyWindSpd.Count == 12)
            {
                double sum = 0;
                foreach (var s in monthlyWindSpd) sum += s;
                avgWindSpeed = sum / 12.0;
            }

            // ── SITE ──────────────────────────────────────────────────────
            var site = new SiteData
            {
                Latitude = lat,
                Longitude = lon,
                SiteWidth = sw,
                SiteDepth = sd,
                NorthOffset = north,
                City = $"Lat {lat:F1} / Lon {lon:F1}",
                MonthlySolarRadiation = (monthlySolar != null && monthlySolar.Count == 12) ? monthlySolar : new List<double>(),
                MonthlyWindDirection = (monthlyWindDir != null && monthlyWindDir.Count == 12) ? monthlyWindDir : new List<double>(),
                MonthlyWindSpeed = (monthlyWindSpd != null && monthlyWindSpd.Count == 12) ? monthlyWindSpd : new List<double>(),
                PrevailingWindDirection = prevailingWind,
                AvgWindSpeed = avgWindSpeed
            };

            // ── CONSTRAINTS ───────────────────────────────────────────────
            var constraints = new DesignConstraints
            {
                MaxHeightMeters = studyHeight,
                MaxSiteFootprintRatio = maxCov,
                MinFloorplateArea = 80,
                MaxFloorplateArea = sw * sd * maxCov,
                MinWindowToWallRatio = 0.25,
                MaxWindowToWallRatio = 0.75,
                MinFloors = 1,
                MaxFloors = 1,
                FloorToFloor = studyHeight
            };

            // ── OPTIMIZER ─────────────────────────────────────────────────
            // Optimizer always uses Orthogonal — clean geometry for scoring
            // EdgeStyle is applied only to the OUTPUT profile for display
            var optimizer = new GeneticOptimizer(site, constraints, seed: 42)
            {
                PopulationSize = nCandidates,
                Generations = 25,
                MutationRate = 0.2,
                SolarWeight = solarWeight,
                WindWeight = windWeight,
                EdgeStyle = "Orthogonal" // always orthogonal for optimizer
            };

            var results = optimizer.Run();

            nTop = Math.Max(1, Math.Min(nTop, results.Count));
            selected = Math.Max(0, Math.Min(selected, nTop - 1));

            // ── ALL REPORTS ───────────────────────────────────────────────
            var allSb = new System.Text.StringBuilder();
            allSb.AppendLine("══ SOLVEFORM RESULTS ══");
            for (int i = 0; i < nTop && i < results.Count; i++)
            {
                var r = results[i];
                allSb.AppendLine(
                    $"[{i}] Score:{r.FinalScore:F1}  {r.Typology}  " +
                    $"{r.Width:F0}x{r.Depth:F0}m  " +
                    $"Orient:{r.OrientationAngle:F1}°  " +
                    $"☀{r.SolarScore:F0} 💨{r.WindScore:F0}");
            }
            allSb.AppendLine($"\n► Showing [{selected}]");

            // ── SELECTED RESULT ───────────────────────────────────────────
            var c = results[selected];

            // Apply chosen edge style ONLY to the output profile
            var gen = new FormGenerator(site, constraints);
            gen.EdgeStyle = edgeStyle == "Smooth" ? Core.EdgeStyle.Smooth :
                            edgeStyle == "Chamfered" ? Core.EdgeStyle.Chamfered :
                                                       Core.EdgeStyle.Orthogonal;

            MassingTypology typ = MassingTypology.Box;
            if (c.Typology == "LShape") typ = MassingTypology.LShape;
            if (c.Typology == "Courtyard") typ = MassingTypology.Courtyard;
            if (c.Typology == "Cruciform") typ = MassingTypology.Cruciform;
            if (c.Typology == "HShape") typ = MassingTypology.HShape;
            if (c.Typology == "Tower") typ = MassingTypology.Tower;
          

            var profile = gen.GetProfile(c, typ);
            var footprint = profile != null
                ? gen.ExtrudeProfile(profile, 0, studyHeight)
                : null;

            // ── REPORT ────────────────────────────────────────────────────
            string report =
                $"Rank {selected + 1} | Score: {c.FinalScore:F1}/100  [{c.Typology}]\n" +
                $"  Footprint:   {c.Width:F1}m W x {c.Depth:F1}m D\n" +
                $"  Orientation: {c.OrientationAngle:F1}° from North\n" +
                $"  WWR South:   {c.WWR_South:P0}  North: {c.WWR_North:P0}\n" +
                $"  ☀ Solar:     {c.SolarScore:F1}/100\n" +
                $"  💨 Wind:      {c.WindScore:F1}/100\n" +
                $"  Edge Style:  {edgeStyle}\n" +
                $"  Constraints: {(c.ConstraintPenalty == 0 ? "✅ All passed" : "❌ Violated")}";

            // ── OUTPUTS ───────────────────────────────────────────────────
            if (footprint != null) DA.SetData(0, footprint);
            if (profile != null) DA.SetData(1, profile);
            DA.SetData(2, report);
            DA.SetData(3, allSb.ToString());
            DA.SetData(4, Math.Round(c.FinalScore, 1));
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
    }
}