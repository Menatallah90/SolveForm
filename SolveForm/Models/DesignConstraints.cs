namespace SolveForm.Models
{
    /// <summary>
    /// Hard limits the designer controls — violating these = disqualified design.
    /// </summary>
    public class DesignConstraints
    {
        public double MaxHeightMeters { get; set; } = 30.0;
        public double MinFloorplateArea { get; set; } = 200.0;   // m²
        public double MaxFloorplateArea { get; set; } = 2000.0;  // m²
        public double MaxSiteFootprintRatio { get; set; } = 0.7; // 70% site coverage max
        public double MinWindowToWallRatio { get; set; } = 0.3;
        public double MaxWindowToWallRatio { get; set; } = 0.8;
    }
}