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
              "Generates and ranks massing candidates optimized for solar and wind performance",
              "SolveForm", "Optimization")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "Lat", "Site latitude in decimal degrees (e.g. 24.7 for Riyadh)", GH_ParamAccess.item, 24.7);
            pManager.AddNumberParameter("Longitude", "Lon", "Site longitude in decimal degrees", GH_ParamAccess.item, 46.7);
            pManager.AddNumberParameter("SiteWidth", "SW", "Site width in meters (East-West)", GH_ParamAccess.item, 50.0);
            pManager.AddNumberParameter("SiteDepth", "SD", "Site depth in meters (North-South)", GH_ParamAccess.item, 40.0);
            pManager.AddNumberParameter("NorthOffset", "N°", "True North offset in degrees (clockwise from Y-axis)", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("MaxHeight", "Hmax", "Maximum building height in meters", GH_ParamAccess.item, 24.0);
            pManager.AddNumberParameter("MaxCoverage", "Cov", "Maximum site coverage ratio (0.0–1.0)", GH_ParamAccess.item, 0.6);
            pManager.AddIntegerParameter("Candidates", "N", "Number of design candidates to generate and rank", GH_ParamAccess.item, 30);
            pManager.AddIntegerParameter("TopResults", "Top", "How many top results to output", GH_ParamAccess.item, 3);
            pManager.AddNumberParameter("MonthlySolar", "Sol", "[Optional] Monthly solar radiation from EPW Loader (12 values)", GH_ParamAccess.list);
            pManager.AddNumberParameter("SolarWeight", "Wsol", "Weight for solar objective (0–1). Solar + Wind should sum to 1.", GH_ParamAccess.item, 0.7);
            pManager.AddNumberParameter("WindWeight", "Wwnd", "Weight for wind objective (0–1).", GH_ParamAccess.item, 0.3);
            pManager.AddNumberParameter("WindDirection", "WDir", "[Optional] Prevailing wind direction override in degrees. -1 = EPW.", GH_ParamAccess.item, -1.0);
            pManager.AddNumberParameter("WindDirData", "WDirD", "[Optional] Monthly wind directions from EPW Loader (12 values)", GH_ParamAccess.list);
            pManager.AddNumberParameter("WindSpdData", "WSpdD", "[Optional] Monthly wind speeds from EPW Loader (12 values, m/s)", GH_ParamAccess.list);

            pManager[9].Optional = true;
            pManager[12].Optional = true;
            pManager[13].Optional = true;
            pManager[14].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Geometry", "Geo", "Top ranked massing geometries", GH_ParamAccess.list);
            pManager.AddNumberParameter("Scores", "Score", "Combined scores for top results (0–100)", GH_ParamAccess.list);
            pManager.AddTextParameter("Report", "Report", "Performance summary for each top result", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- Read inputs ---
            double lat = 24.7, lon = 46.7, sw = 50, sd = 40, north = 0;
            double maxH = 24, maxCov = 0.6;
            int nCandidates = 30, nTop = 3;
            double solarWeight = 0.7, windWeight = 0.3, windDirOverride = -1.0;

            DA.GetData(0, ref lat);
            DA.GetData(1, ref lon);
            DA.GetData(2, ref sw);
            DA.GetData(3, ref sd);
            DA.GetData(4, ref north);
            DA.GetData(5, ref maxH);
            DA.GetData(6, ref maxCov);
            DA.GetData(7, ref nCandidates);
            DA.GetData(8, ref nTop);

            var monthlySolar = new List<double>();
            var monthlyWindDir = new List<double>();
            var monthlyWindSpd = new List<double>();

            DA.GetDataList(9, monthlySolar);
            DA.GetData(10, ref solarWeight);
            DA.GetData(11, ref windWeight);
            DA.GetData(12, ref windDirOverride);
            DA.GetDataList(13, monthlyWindDir);
            DA.GetDataList(14, monthlyWindSpd);

            // --- Compute prevailing wind from best available source ---
            double prevailingWind = 315.0; // fallback NW
            double avgWindSpeed = 3.5;

            if (windDirOverride >= 0)
            {
                prevailingWind = windDirOverride;
            }
            else if (monthlyWindDir.Count == 12)
            {
                double totalX = 0, totalY = 0;
                for (int m = 0; m < 12; m++)
                {
                    double rad = monthlyWindDir[m] * Math.PI / 180.0;
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

            // --- Build site data ---
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

            // --- Build constraints ---
            var constraints = new DesignConstraints
            {
                MaxHeightMeters = maxH,
                MaxSiteFootprintRatio = maxCov,
                MinFloorplateArea = 80,
                MaxFloorplateArea = sw * sd * maxCov,
                MinWindowToWallRatio = 0.25,
                MaxWindowToWallRatio = 0.75
            };

            // --- Run genetic optimizer ---
            var optimizer = new GeneticOptimizer(site, constraints, seed: 42)
            {
                PopulationSize = nCandidates,
                Generations = 20,
                MutationRate = 0.15,
                SolarWeight = solarWeight,
                WindWeight = windWeight
            };

            var results = optimizer.Run();
            int outputCount = Math.Min(nTop, results.Count);

            var geoOut = new List<Brep>();
            var scoreOut = new List<double>();
            var reportOut = new List<string>();

            for (int i = 0; i < outputCount; i++)
            {
                var c = results[i];
                if (c.Geometry == null) continue;

                geoOut.Add(c.Geometry);
                scoreOut.Add(Math.Round(c.FinalScore, 1));
                reportOut.Add(
                    $"Rank {i + 1} | Score: {c.FinalScore:F1}/100  [{c.Typology}]\n" +
                    $"  Size:        {c.Width:F1}m W × {c.Depth:F1}m D × {c.Height:F1}m H\n" +
                    $"  Orientation: {c.OrientationAngle:F1}° from North\n" +
                    $"  WWR South:   {c.WWR_South:P0}  North: {c.WWR_North:P0}\n" +
                    $"  ☀ Solar:     {c.SolarScore:F1}/100\n" +
                    $"  💨 Wind:      {c.WindScore:F1}/100\n" +
                    $"  Constraints: {(c.ConstraintPenalty == 0 ? "✅ All passed" : "❌ Violated")}"
                );
            }

            DA.SetDataList(0, geoOut);
            DA.SetDataList(1, scoreOut);
            DA.SetDataList(2, reportOut);
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
    }
}