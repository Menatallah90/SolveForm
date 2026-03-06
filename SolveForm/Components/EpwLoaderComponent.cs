using System;
using System.Text;
using Grasshopper.Kernel;
using SolveForm.Core;

namespace SolveForm.Components
{
    public class EpwLoaderComponent : GH_Component
    {
        public EpwLoaderComponent()
          : base("EPW Loader", "EPW",
              "Reads an EnergyPlus Weather file and extracts solar and wind data",
              "SolveForm", "Data")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("FilePath", "Path",
                "Full path to your .epw weather file (download from climate.onebuilding.org)",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("City", "City", "City name from EPW header", GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "Lat", "Latitude extracted from EPW", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "Lon", "Longitude extracted from EPW", GH_ParamAccess.item);
            pManager.AddNumberParameter("MonthlySolar", "Sol", "Monthly avg solar radiation kWh/m²/day (12 values)", GH_ParamAccess.list);
            pManager.AddNumberParameter("WindDir", "WDir", "Monthly prevailing wind directions (12 values, °)", GH_ParamAccess.list);
            pManager.AddNumberParameter("WindSpeed", "WSpd", "Monthly average wind speeds (12 values, m/s)", GH_ParamAccess.list);
            pManager.AddNumberParameter("PrevWind", "PW", "Annual prevailing wind direction (degrees)", GH_ParamAccess.item);
            pManager.AddTextParameter("Summary", "Info", "Human-readable climate summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string filePath = "";
            if (!DA.GetData(0, ref filePath)) return;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No file path provided.");
                return;
            }

            SolveForm.Models.SiteData site;
            try
            {
                var reader = new EpwReader();
                site = reader.ReadEpw(filePath);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"EPW read failed: {ex.Message}");
                return;
            }

            // Build summary
            string[] monthNames = { "Jan","Feb","Mar","Apr","May","Jun",
                                    "Jul","Aug","Sep","Oct","Nov","Dec" };
            string[] compassDir = { "N","NNE","NE","ENE","E","ESE","SE","SSE",
                                    "S","SSW","SW","WSW","W","WNW","NW","NNW" };

            var sb = new StringBuilder();
            sb.AppendLine($"📍 City:      {site.City}");
            sb.AppendLine($"🌐 Location:  {site.Latitude:F2}°, {site.Longitude:F2}°");
            sb.AppendLine($"💨 Prevailing Wind: {site.PrevailingWindDirection:F0}° at {site.AvgWindSpeed:F1} m/s");
            sb.AppendLine($"☀️  Monthly Solar + Wind:");

            for (int m = 0; m < 12; m++)
            {
                double sol = m < site.MonthlySolarRadiation.Count ? site.MonthlySolarRadiation[m] : 0;
                double wSpd = m < site.MonthlyWindSpeed.Count ? site.MonthlyWindSpeed[m] : 0;
                double wDir = m < site.MonthlyWindDirection.Count ? site.MonthlyWindDirection[m] : 0;

                int compassIdx = (int)((wDir + 11.25) / 22.5) % 16;
                string compass = compassDir[compassIdx];

                sb.AppendLine($"  {monthNames[m]}: ☀ {sol:F2} kWh/m²  💨 {wSpd:F1}m/s from {compass}");
            }

            sb.AppendLine($"\n📊 Annual avg solar: {site.AvgWindSpeed:F2} m/s wind");

            DA.SetData(0, site.City);
            DA.SetData(1, site.Latitude);
            DA.SetData(2, site.Longitude);
            DA.SetDataList(3, site.MonthlySolarRadiation);
            DA.SetDataList(4, site.MonthlyWindDirection);
            DA.SetDataList(5, site.MonthlyWindSpeed);
            DA.SetData(6, site.PrevailingWindDirection);
            DA.SetData(7, sb.ToString());
        }

        protected override System.Drawing.Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("B2C3D4E5-F6A7-8901-BCDE-F12345678901");
    }
}