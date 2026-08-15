// BlackComponentAttributes.cs
// Reusable attributes class that forces a component's canvas capsule to render
// using GH_Palette.Black instead of the normal grey/green/orange states.
//
// HOW TO APPLY TO AN EXISTING COMPONENT (e.g. SectionComponent):
//   1. Add this file to the project (same namespace as your components, or add a using).
//   2. In the target component class, add this override:
//
//      public override void CreateAttributes()
//      {
//          m_attributes = new BlackComponentAttributes(this);
//      }
//
//   That's it. No other changes needed. Locked/selected states still show
//   (dimmed / white outline) so you don't lose that feedback.
//
// NOTE: GH_Capsule's exact grip-drawing API has shifted slightly across
// Grasshopper SDK versions. This is written against the commonly-used pattern.
// If it doesn't compile as-is, the likely fix is renaming AddInputGrip/
// AddOutputGrip calls to match whatever overload your installed GH_IO/
// Grasshopper.dll expects — the capsule creation + palette line is the part
// that actually matters.

using System.Drawing;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace SolveForm.Attributes
{
    public class BlackComponentAttributes : GH_ComponentAttributes
    {
        public BlackComponentAttributes(GH_Component owner) : base(owner) { }

#pragma warning disable CA1416
        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel != GH_CanvasChannel.Objects)
            {
                base.Render(canvas, graphics, channel);
                return;
            }

            GH_Palette palette = Owner.Locked ? GH_Palette.Locked : GH_Palette.Black;

            GH_Capsule capsule = GH_Capsule.CreateCapsule(Bounds, palette);

            for (int i = 0; i < Owner.Params.Input.Count; i++)
                capsule.AddInputGrip(Owner.Params.Input[i].Attributes.InputGrip.Y);

            for (int i = 0; i < Owner.Params.Output.Count; i++)
                capsule.AddOutputGrip(Owner.Params.Output[i].Attributes.OutputGrip.Y);

            capsule.Render(graphics, Selected, Owner.Locked, true);
            capsule.Dispose();

            // Name label — white text since the capsule is now black.
            graphics.DrawString(
                Owner.NickName,
                GH_FontServer.StandardBold,
                Brushes.White,
                Bounds,
                GH_TextRenderingConstants.CenterCenter);
        }
    }
}