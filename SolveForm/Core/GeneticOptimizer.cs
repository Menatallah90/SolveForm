using System;
using System.Collections.Generic;
using System.Linq;
using SolveForm.Models;

namespace SolveForm.Core
{
    public class GeneticOptimizer
    {
        private readonly SiteData _site;
        private readonly DesignConstraints _constraints;
        private readonly SolarAnalyzer _analyzer;
        private readonly WindAnalyzer _windAnalyzer;
        private readonly ConstraintEvaluator _evaluator;
        private readonly Random _rng;

        public int Generations { get; set; } = 20;
        public int PopulationSize { get; set; } = 30;
        public double MutationRate { get; set; } = 0.15;
        public double SolarWeight { get; set; } = 0.7;
        public double WindWeight { get; set; } = 0.3;

        public GeneticOptimizer(SiteData site, DesignConstraints constraints, int seed = 42)
        {
            _site = site;
            _constraints = constraints;
            _analyzer = new SolarAnalyzer(site);
            _windAnalyzer = new WindAnalyzer(site);
            _evaluator = new ConstraintEvaluator(constraints, site);
            _rng = new Random(seed);
        }

        public List<DesignCandidate> Run()
        {
            var generator = new FormGenerator(_site, _constraints, seed: _rng.Next());
            var population = generator.GeneratePopulation(PopulationSize);
            ScoreAll(population);

            for (int gen = 0; gen < Generations; gen++)
            {
                var ranked = population.OrderByDescending(c => c.FinalScore).ToList();
                var elites = ranked.Take(PopulationSize / 2).ToList();

                var children = new List<DesignCandidate>();
                int id = PopulationSize;

                while (children.Count < PopulationSize / 2)
                {
                    var parentA = elites[_rng.Next(elites.Count)];
                    var parentB = elites[_rng.Next(elites.Count)];
                    var child = Crossover(parentA, parentB, id++);
                    Mutate(child);
                    children.Add(child);
                }

                population = elites.Concat(children).ToList();
                ScoreAll(population);
            }

            return population.OrderByDescending(c => c.FinalScore).ToList();
        }

        private DesignCandidate Crossover(DesignCandidate a, DesignCandidate b, int id)
        {
            return new DesignCandidate
            {
                Id = id,
                Width = Pick(a.Width, b.Width),
                Depth = Pick(a.Depth, b.Depth),
                Height = Pick(a.Height, b.Height),
                OrientationAngle = Pick(a.OrientationAngle, b.OrientationAngle),
                WWR_South = Pick(a.WWR_South, b.WWR_South),
                WWR_North = Pick(a.WWR_North, b.WWR_North),
                WWR_East = Pick(a.WWR_East, b.WWR_East),
                WWR_West = Pick(a.WWR_West, b.WWR_West),
            };
        }

        private void Mutate(DesignCandidate c)
        {
            if (_rng.NextDouble() < MutationRate)
                c.Width = Clamp(c.Width + GaussianNoise(3.0), 5, _site.SiteWidth * 0.9);
            if (_rng.NextDouble() < MutationRate)
                c.Depth = Clamp(c.Depth + GaussianNoise(3.0), 5, _site.SiteDepth * 0.9);
            if (_rng.NextDouble() < MutationRate)
                c.Height = Clamp(c.Height + GaussianNoise(2.0), 3, _constraints.MaxHeightMeters);
            if (_rng.NextDouble() < MutationRate)
                c.OrientationAngle = Clamp(c.OrientationAngle + GaussianNoise(10.0), -45, 45);
            if (_rng.NextDouble() < MutationRate)
                c.WWR_South = Clamp(c.WWR_South + GaussianNoise(0.05),
                    _constraints.MinWindowToWallRatio, _constraints.MaxWindowToWallRatio);

            c.Geometry = new FormGenerator(_site, _constraints).RebuildBrep(c);
        }

        private void ScoreAll(List<DesignCandidate> pop)
        {
            foreach (var c in pop)
            {
                string violation;
                double penalty = _evaluator.Evaluate(c, out violation);
                double solar = _analyzer.ComputeSolarScore(c);
                double wind = _windAnalyzer.ComputeWindScore(c);

                // Typology bonus: L-shape and courtyard shelter better from wind
                double typologyBonus = 0;
                if (c.Typology == "LShape") typologyBonus = wind * 0.08;
                if (c.Typology == "Courtyard") typologyBonus = wind * 0.12;

                c.ConstraintPenalty = penalty;
                c.SolarScore = solar;
                c.WindScore = wind + typologyBonus;

                double combined = (solar * SolarWeight) + (c.WindScore * WindWeight);
                c.FinalScore = penalty > 0 ? 0 : combined;

                if (c.Geometry == null)
                    c.Geometry = new FormGenerator(_site, _constraints).RebuildBrep(c);
            }
        }

        private double Pick(double a, double b) => _rng.NextDouble() < 0.5 ? a : b;

        private double GaussianNoise(double stdDev)
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            return stdDev * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private double Clamp(double val, double min, double max)
            => Math.Max(min, Math.Min(max, val));
    }
}