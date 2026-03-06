using System;
using System.Collections.Generic;
using Rhino.Geometry;
using SolveForm.Models;

namespace SolveForm.Core
{
    public enum MassingTypology { Box, LShape, Courtyard }

    public class FormGenerator
    {
        private readonly SiteData _site;
        private readonly DesignConstraints _constraints;
        private readonly Random _rng;

        public FormGenerator(SiteData site, DesignConstraints constraints, int seed = 42)
        {
            _site = site;
            _constraints = constraints;
            _rng = new Random(seed);
        }

        public List<DesignCandidate> GeneratePopulation(int count)
        {
            var population = new List<DesignCandidate>();
            for (int i = 0; i < count; i++)
                population.Add(GenerateRandom(i));
            return population;
        }

        private DesignCandidate GenerateRandom(int id)
        {
            // Pick typology: 50% box, 25% L-shape, 25% courtyard
            MassingTypology typology;
            double t = _rng.NextDouble();
            if (t < 0.50) typology = MassingTypology.Box;
            else if (t < 0.75) typology = MassingTypology.LShape;
            else typology = MassingTypology.Courtyard;

            double width = _site.SiteWidth * (0.3 + _rng.NextDouble() * 0.5);
            double depth = _site.SiteDepth * (0.3 + _rng.NextDouble() * 0.4);
            double height = 6.0 + _rng.NextDouble() * (_constraints.MaxHeightMeters - 6.0);
            double orientation = -45.0 + _rng.NextDouble() * 90.0;

            double wwrS = _constraints.MinWindowToWallRatio + _rng.NextDouble() *
                          (_constraints.MaxWindowToWallRatio - _constraints.MinWindowToWallRatio);
            double wwrN = _constraints.MinWindowToWallRatio + _rng.NextDouble() * 0.2;
            double wwrE = _constraints.MinWindowToWallRatio + _rng.NextDouble() * 0.2;
            double wwrW = _constraints.MinWindowToWallRatio + _rng.NextDouble() * 0.2;

            var candidate = new DesignCandidate
            {
                Id = id,
                Width = width,
                Depth = depth,
                Height = height,
                OrientationAngle = orientation,
                WWR_South = wwrS,
                WWR_North = wwrN,
                WWR_East = wwrE,
                WWR_West = wwrW,
                Typology = typology.ToString()
            };

            candidate.Geometry = CreateBrep(candidate, typology);
            return candidate;
        }

        public Brep RebuildBrep(DesignCandidate c)
        {
            MassingTypology typology = MassingTypology.Box;
            if (c.Typology == "LShape") typology = MassingTypology.LShape;
            if (c.Typology == "Courtyard") typology = MassingTypology.Courtyard;
            return CreateBrep(c, typology);
        }

        public Brep CreateBrep(DesignCandidate c, MassingTypology typology)
        {
            Brep result;

            switch (typology)
            {
                case MassingTypology.LShape:
                    result = CreateLShape(c);
                    break;
                case MassingTypology.Courtyard:
                    result = CreateCourtyard(c);
                    break;
                default:
                    result = CreateBox(c);
                    break;
            }

            if (result == null) return CreateBox(c);

            // Rotate around world Z by orientation angle
            double radians = c.OrientationAngle * Math.PI / 180.0;
            var xform = Transform.Rotation(radians, Vector3d.ZAxis, Point3d.Origin);
            result.Transform(xform);

            return result;
        }

        // ── BOX ──────────────────────────────────────────────────────────────
        private Brep CreateBox(DesignCandidate c)
        {
            var box = new Box(
                Plane.WorldXY,
                new Interval(-c.Width / 2, c.Width / 2),
                new Interval(-c.Depth / 2, c.Depth / 2),
                new Interval(0, c.Height));
            return box.ToBrep();
        }

        // ── L-SHAPE ──────────────────────────────────────────────────────────
        // Two boxes joined: main wing (full width, 60% depth)
        //                 + side wing (40% width, remaining 40% depth)
        private Brep CreateLShape(DesignCandidate c)
        {
            double mainDepth = c.Depth * 0.6;
            double wingDepth = c.Depth * 0.4;
            double wingWidth = c.Width * 0.45;

            // Main wing — south facing, full width
            var mainBox = new Box(
                Plane.WorldXY,
                new Interval(-c.Width / 2, c.Width / 2),
                new Interval(-c.Depth / 2, -c.Depth / 2 + mainDepth),
                new Interval(0, c.Height));

            // Side wing — east side, steps north
            var sideBox = new Box(
                Plane.WorldXY,
                new Interval(c.Width / 2 - wingWidth, c.Width / 2),
                new Interval(-c.Depth / 2 + mainDepth, c.Depth / 2),
                new Interval(0, c.Height * 0.75)); // slightly lower wing

            var mainBrep = mainBox.ToBrep();
            var sideBrep = sideBox.ToBrep();

            if (mainBrep == null || sideBrep == null) return CreateBox(c);

            var joined = Brep.CreateBooleanUnion(
                new List<Brep> { mainBrep, sideBrep }, 0.01);

            return (joined != null && joined.Length > 0) ? joined[0] : mainBrep;
        }

        // ── COURTYARD ────────────────────────────────────────────────────────
        // Outer shell minus inner void — U-shape open to south for solar access
        private Brep CreateCourtyard(DesignCandidate c)
        {
            double wallThickness = Math.Max(4.0, c.Width * 0.2);
            double voidWidth = c.Width - wallThickness * 2;
            double voidDepth = c.Depth * 0.45;

            // Full outer box
            var outerBox = new Box(
                Plane.WorldXY,
                new Interval(-c.Width / 2, c.Width / 2),
                new Interval(-c.Depth / 2, c.Depth / 2),
                new Interval(0, c.Height));

            // Inner void — open toward south (removed from north side)
            var voidBox = new Box(
                Plane.WorldXY,
                new Interval(-voidWidth / 2, voidWidth / 2),
                new Interval(c.Depth / 2 - voidDepth, c.Depth / 2 + 1), // +1 to cut through
                new Interval(-1, c.Height + 1));

            var outerBrep = outerBox.ToBrep();
            var voidBrep = voidBox.ToBrep();

            if (outerBrep == null || voidBrep == null) return CreateBox(c);

            var result = Brep.CreateBooleanDifference(
                new List<Brep> { outerBrep },
                new List<Brep> { voidBrep }, 0.01);

            return (result != null && result.Length > 0) ? result[0] : outerBrep;
        }
    }
}