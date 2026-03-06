using System;
using SolveForm.Models;

namespace SolveForm.Core
{
    public class WindAnalyzer
    {
        private readonly SiteData _site;

        public WindAnalyzer(SiteData site)
        {
            _site = site;
        }

        public double ComputeWindScore(DesignCandidate candidate)
        {
            double prevailing = _site.PrevailingWindDirection;

            double southAngle = NormalizeAngle(180.0 + candidate.OrientationAngle + _site.NorthOffset);
            double northAngle = NormalizeAngle(0.0 + candidate.OrientationAngle + _site.NorthOffset);
            double eastAngle = NormalizeAngle(90.0 + candidate.OrientationAngle + _site.NorthOffset);
            double westAngle = NormalizeAngle(270.0 + candidate.OrientationAngle + _site.NorthOffset);

            double longFacadeArea = candidate.Width * candidate.Height;
            double shortFacadeArea = candidate.Depth * candidate.Height;

            double southWindFactor = WindIncidenceFactor(southAngle, prevailing);
            double northWindFactor = WindIncidenceFactor(northAngle, prevailing);
            double eastWindFactor = WindIncidenceFactor(eastAngle, prevailing);
            double westWindFactor = WindIncidenceFactor(westAngle, prevailing);

            double windPressure = GetWindPressure();
            double totalExposure =
                (longFacadeArea * southWindFactor) +
                (longFacadeArea * northWindFactor) +
                (shortFacadeArea * eastWindFactor) +
                (shortFacadeArea * westWindFactor);

            double maxExposure = (longFacadeArea * 2 + shortFacadeArea * 2) * windPressure;
            double exposureScore = Math.Max(0, 100.0 - (totalExposure / Math.Max(1, maxExposure) * 100.0));

            // Cross-ventilation: reward when wind hits narrow face
            double buildingDepthAxis = NormalizeAngle(candidate.OrientationAngle + _site.NorthOffset);
            double windAlignAngle = Math.Abs(buildingDepthAxis - NormalizeAngle(prevailing + 180));
            if (windAlignAngle > 180) windAlignAngle = 360 - windAlignAngle;
            double crossVentScore = Math.Max(0, Math.Cos(windAlignAngle * Math.PI / 180.0) * 100.0);

            // Aspect ratio: gentle penalty for very wide buildings
            double aspectRatio = candidate.Width / Math.Max(1, candidate.Depth);
            double aspectPenalty = Math.Max(0, (aspectRatio - 4.0) * 5.0);

            // Combined: 50% exposure, 50% cross-vent
            double combined = (exposureScore * 0.5) + (crossVentScore * 0.5) - aspectPenalty;

            // Remap to 40–90 range so it competes meaningfully with solar
            double remapped = 40.0 + (Math.Max(0, Math.Min(100, combined)) * 0.5);
            return Math.Max(0, Math.Min(100, remapped));
        }

        private double WindIncidenceFactor(double facadeAngle, double windFromDir)
        {
            double windToDir = NormalizeAngle(windFromDir + 180);
            double diff = Math.Abs(facadeAngle - windToDir);
            if (diff > 180) diff = 360 - diff;
            return Math.Max(0, Math.Cos(diff * Math.PI / 180.0));
        }

        private double GetWindPressure()
        {
            double v = _site.AvgWindSpeed > 0 ? _site.AvgWindSpeed : 3.5;
            return 0.5 * 1.2 * v * v;
        }

        private double NormalizeAngle(double angle)
        {
            angle = angle % 360;
            if (angle < 0) angle += 360;
            return angle;
        }
    }
}