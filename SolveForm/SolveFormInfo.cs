using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace SolveForm
{
    public class SolveFormInfo : GH_AssemblyInfo
    {
        public override string Name => "SolveForm";
        public override Bitmap Icon => null;
        public override string Description => "Constraint-aware solar form optimizer for early-stage massing";
        public override Guid Id => new Guid("AAAABBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF");
        public override string AuthorName => "Menatallah Abdulrhman";
        public override string AuthorContact => "minatabdulrhman@gmail.com";
    }
}