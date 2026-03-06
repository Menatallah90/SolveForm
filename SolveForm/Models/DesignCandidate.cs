using Rhino.Geometry;

namespace SolveForm.Models
{
    public class DesignCandidate
    {
        public int Id { get; set; }
        public string Typology { get; set; } = "Box";

        // Massing geometry
        public double Width { get; set; }
        public double Depth { get; set; }
        public double Height { get; set; }

        // Orientation (degrees, clockwise from North)
        public double OrientationAngle { get; set; }

        // Window-to-wall ratios per facade
        public double WWR_North { get; set; }
        public double WWR_South { get; set; }
        public double WWR_East { get; set; }
        public double WWR_West { get; set; }

        // Computed scores
        public double SolarScore { get; set; }
        public double WindScore { get; set; }
        public double ConstraintPenalty { get; set; }
        public double FinalScore { get; set; }

        // Rhino geometry for display
        public Brep Geometry { get; set; }
    }
}