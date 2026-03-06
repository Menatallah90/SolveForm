using System;
using System.Collections.Generic;
using SolveForm.Models;

namespace SolveForm.Core
{
    public class SolarAnalyzer
    {
        private readonly SiteData _site;

        public SolarAnalyzer(SiteData site)
        {
            _site = site;
        }

        public double ComputeSolarScore(DesignCandidate candidate)
        {
            double southFacadeAngle = NormalizeAngle(180.0 + candidate.OrientationAngle + _site.NorthOffset);
            double eastFacadeAngle = NormalizeAngle(90.0 + candidate.OrientationAngle + _site.NorthOffset);
            double westFacadeAngle = NormalizeAngle(270.0 + candidate.OrientationAngle + _site.NorthOffset);

            double southArea = candidate.Width * candidate.Height;
            double eastArea = candidate.Depth * candidate.Height;
            double westArea = candidate.Depth * candidate.Height;

            double southGlazing = southArea * candidate.WWR_South;
            double eastGlazing = eastArea * candidate.WWR_East;
            double westGlazing = westArea * candidate.WWR_West;

            double annualAvgRadiation = GetAnnualAvgRadiation();

            double southFactor = GetDirectionSolarFactor(southFacadeAngle, _site.Latitude);
            double eastFactor = GetDirectionSolarFactor(eastFacadeAngle, _site.Latitude);
            double westFactor = GetDirectionSolarFactor(westFacadeAngle, _site.Latitude);

            double usefulGain = southGlazing * southFactor * annualAvgRadiation;
            double overheating = (eastGlazing * eastFactor + westGlazing * westFactor) * annualAvgRadiation * 0.4;
            double rawScore = usefulGain - overheating;

            // Normalize against theoretical best
            double bestPossibleGain = southArea * 1.0 * annualAvgRadiation;
            double worstPenalty = southArea * 0.8 * annualAvgRadiation * 0.4;
            double range = bestPossibleGain + worstPenalty;
            double normalizedScore = Math.Max(0, Math.Min(100, ((rawScore + worstPenalty) / range) * 100));

            // Remap so optimal designs score 85–95
            double remapped = 40.0 + (normalizedScore * 0.6);
            return Math.Max(0, Math.Min(100, remapped));
        }

        private double GetDirectionSolarFactor(double compassAngle, double latitude)
        {
            double optimalAngle = latitude >= 0 ? 180.0 : 0.0;
            double angleDiff = Math.Abs(compassAngle - optimalAngle);
            if (angleDiff > 180) angleDiff = 360 - angleDiff;
            return Math.Max(0, Math.Cos(angleDiff * Math.PI / 180.0));
        }

        private double GetAnnualAvgRadiation()
        {
            if (_site.MonthlySolarRadiation != null && _site.MonthlySolarRadiation.Count == 12)
            {
                double sum = 0;
                foreach (var v in _site.MonthlySolarRadiation) sum += v;
                return sum / 12.0;
            }
            double lat = Math.Abs(_site.Latitude);
            return Math.Max(2.0, 6.0 - (lat / 90.0) * 3.0);
        }

        private double NormalizeAngle(double angle)
        {
            angle = angle % 360;
            if (angle < 0) angle += 360;
            return angle;
        }
    }
}