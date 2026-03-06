using System.Collections.Generic;

namespace SolveForm.Models
{
    /// <summary>
    /// Holds all incoming site and climate data for the optimizer.
    /// </summary>
    public class SiteData
    {
        // Location
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string City { get; set; }

        // Monthly average solar radiation (kWh/m²/day) for each month
        // Index 0 = January, 11 = December
        public List<double> MonthlySolarRadiation { get; set; } = new List<double>();

        // Site boundary dimensions (meters)
        public double SiteWidth { get; set; }
        public double SiteDepth { get; set; }

        // True North offset (degrees from Y-axis, clockwise)
        public double NorthOffset { get; set; } = 0.0;

        // Prevailing wind direction (degrees, 0=N, 90=E, 180=S, 270=W)
        // This is the direction wind COMES FROM
        public double PrevailingWindDirection { get; set; } = 315.0; // NW default for Riyadh summer

        // Average wind speed (m/s)
        public double AvgWindSpeed { get; set; } = 3.5;

        // Monthly avg wind speed (12 values, m/s) — optional, from EPW
        public List<double> MonthlyWindSpeed { get; set; } = new List<double>();

        // Monthly prevailing wind direction (12 values, degrees) — optional
        public List<double> MonthlyWindDirection { get; set; } = new List<double>();
    }
}