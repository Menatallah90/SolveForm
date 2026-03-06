namespace SolveForm.Models
{
    /// <summary>
    /// Human-readable scorecard for one design candidate.
    /// </summary>
    public class PerformanceScore
    {
        public int CandidateId { get; set; }
        public double SolarScore { get; set; }        // 0–100
        public double FinalScore { get; set; }        // 0–100
        public bool PassesConstraints { get; set; }
        public string Summary { get; set; }
    }
}