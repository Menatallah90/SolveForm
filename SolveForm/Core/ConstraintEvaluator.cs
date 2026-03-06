using SolveForm.Models;

namespace SolveForm.Core
{
    public class ConstraintEvaluator
    {
        private readonly DesignConstraints _constraints;
        private readonly SiteData _site;

        public ConstraintEvaluator(DesignConstraints constraints, SiteData site)
        {
            _constraints = constraints;
            _site = site;
        }

        /// <summary>
        /// Returns 0 if all constraints pass. Returns penalty > 0 if violated.
        /// </summary>
        public double Evaluate(DesignCandidate candidate, out string violationNote)
        {
            double penalty = 0;
            violationNote = "";

            double footprint = candidate.Width * candidate.Depth;
            double siteArea = _site.SiteWidth * _site.SiteDepth;
            double coverageRatio = footprint / siteArea;

            if (candidate.Height > _constraints.MaxHeightMeters)
            {
                penalty += 50;
                violationNote += $"Height {candidate.Height:F1}m exceeds max {_constraints.MaxHeightMeters}m. ";
            }

            if (footprint < _constraints.MinFloorplateArea)
            {
                penalty += 30;
                violationNote += $"Footprint {footprint:F0}m² below min {_constraints.MinFloorplateArea}m². ";
            }

            if (footprint > _constraints.MaxFloorplateArea)
            {
                penalty += 30;
                violationNote += $"Footprint {footprint:F0}m² exceeds max {_constraints.MaxFloorplateArea}m². ";
            }

            if (coverageRatio > _constraints.MaxSiteFootprintRatio)
            {
                penalty += 20;
                violationNote += $"Site coverage {coverageRatio:P0} exceeds max {_constraints.MaxSiteFootprintRatio:P0}. ";
            }

            return penalty;
        }
    }
}