using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;

namespace SolveForm.Components
{
    public class DashboardComponent : GH_Component
    {
        private List<double> _scores = new List<double>();
        private List<string> _reports = new List<string>();

        public DashboardComponent()
          : base("SolveForm Dashboard", "SFDash",
              "Visualizes ranked design scores as an inline scorecard",
              "SolveForm", "Visualization")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Scores", "S", "Solar scores from SolveForm optimizer", GH_ParamAccess.list);
            pManager.AddTextParameter("Reports", "R", "Report strings from SolveForm optimizer", GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Summary", "Out", "Dashboard summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var scores = new List<double>();
            var reports = new List<string>();

            if (!DA.GetDataList(0, scores)) return;
            DA.GetDataList(1, reports);

            _scores = scores;
            _reports = reports;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SOLVEFORM — SOLAR RANKING");
            sb.AppendLine(new string('─', 40));

            for (int i = 0; i < scores.Count; i++)
            {
                double score = scores[i];
                int filled = (int)(score / 5.0);
                string bar = new string('█', Math.Min(filled, 20)).PadRight(20, '░');
                sb.AppendLine($"Rank {i + 1}  [{bar}]  {score:F1}");

                if (i < reports.Count)
                {
                    foreach (var line in reports[i].Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Contains("Size") || t.Contains("Orientation") || t.Contains("WWR"))
                            sb.AppendLine($"  {t}");
                    }
                    sb.AppendLine();
                }
            }

            DA.SetData(0, sb.ToString());
        }

        public List<double> GetScores() => _scores ?? new List<double>();
        public List<string> GetReports() => _reports ?? new List<string>();

        protected override Bitmap Icon => null;
        public override Guid ComponentGuid => new Guid("C3D4E5F6-A7B8-9012-CDEF-123456789012");
    }
}