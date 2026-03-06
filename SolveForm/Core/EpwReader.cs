using System;
using System.Collections.Generic;
using System.IO;
using SolveForm.Models;

namespace SolveForm.Core
{
    public class EpwReader
    {
        public SiteData ReadEpw(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"EPW file not found: {filePath}");

            var lines = File.ReadAllLines(filePath);

            // Parse header
            var locationParts = lines[0].Split(',');
            string city = locationParts.Length > 1 ? locationParts[1].Trim() : "Unknown";
            double lat = locationParts.Length > 6 ? ParseDouble(locationParts[6]) : 0;
            double lon = locationParts.Length > 7 ? ParseDouble(locationParts[7]) : 0;

            // Monthly accumulators
            var monthlyDNI = new double[12];
            var monthlyDHI = new double[12];
            var monthlyWindSpeed = new double[12];
            var monthlyWindDir = new double[12]; // we'll use vector averaging
            var monthlyWindDirX = new double[12]; // cos components for vector avg
            var monthlyWindDirY = new double[12]; // sin components for vector avg
            var monthlyCount = new int[12];

            for (int i = 8; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length < 21) continue;

                int month = ParseInt(parts[1]) - 1;
                if (month < 0 || month > 11) continue;

                double dni = ParseDouble(parts[14]);
                double dhi = ParseDouble(parts[15]);
                double windDir = ParseDouble(parts[20]); // degrees
                double windSpd = ParseDouble(parts[21]); // m/s

                if (dni >= 0 && dhi >= 0)
                {
                    monthlyDNI[month] += dni;
                    monthlyDHI[month] += dhi;
                }

                if (windSpd >= 0)
                {
                    monthlyWindSpeed[month] += windSpd;
                    // Vector averaging for direction
                    double rad = windDir * Math.PI / 180.0;
                    monthlyWindDirX[month] += Math.Cos(rad);
                    monthlyWindDirY[month] += Math.Sin(rad);
                    monthlyCount[month]++;
                }
            }

            int[] daysInMonth = { 31, 28, 29, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

            var monthlySolar = new List<double>();
            var windSpeeds = new List<double>();
            var windDirections = new List<double>();

            for (int m = 0; m < 12; m++)
            {
                // Solar
                double totalWh = monthlyDNI[m] + monthlyDHI[m];
                double dailyAvg = monthlyCount[m] > 0 ? (totalWh / 1000.0) / daysInMonth[m] : 0;
                monthlySolar.Add(Math.Round(dailyAvg, 2));

                // Wind speed
                double avgSpd = monthlyCount[m] > 0 ? monthlyWindSpeed[m] / monthlyCount[m] : 0;
                windSpeeds.Add(Math.Round(avgSpd, 2));

                // Wind direction (vector average)
                double avgDirRad = Math.Atan2(
                    monthlyWindDirY[m] / Math.Max(1, monthlyCount[m]),
                    monthlyWindDirX[m] / Math.Max(1, monthlyCount[m]));
                double avgDir = avgDirRad * 180.0 / Math.PI;
                if (avgDir < 0) avgDir += 360;
                windDirections.Add(Math.Round(avgDir, 1));
            }

            // Annual prevailing wind = vector average of all monthly vectors
            double totalX = 0, totalY = 0;
            for (int m = 0; m < 12; m++)
            {
                double rad = windDirections[m] * Math.PI / 180.0;
                totalX += Math.Cos(rad);
                totalY += Math.Sin(rad);
            }
            double prevailingRad = Math.Atan2(totalY / 12.0, totalX / 12.0);
            double prevailingDir = prevailingRad * 180.0 / Math.PI;
            if (prevailingDir < 0) prevailingDir += 360;

            double annualAvgWindSpeed = 0;
            foreach (var s in windSpeeds) annualAvgWindSpeed += s;
            annualAvgWindSpeed /= 12.0;

            return new SiteData
            {
                City = city,
                Latitude = lat,
                Longitude = lon,
                MonthlySolarRadiation = monthlySolar,
                MonthlyWindSpeed = windSpeeds,
                MonthlyWindDirection = windDirections,
                PrevailingWindDirection = Math.Round(prevailingDir, 1),
                AvgWindSpeed = Math.Round(annualAvgWindSpeed, 2)
            };
        }

        private double ParseDouble(string s)
        {
            double.TryParse(s.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double result);
            return result;
        }

        private int ParseInt(string s)
        {
            int.TryParse(s.Trim(), out int result);
            return result;
        }
    }
}