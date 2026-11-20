using System;
using System.Collections.Generic;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private readonly AnimationDiagnosticScratch _animationDiagnosticScratch=new();
    private sealed class AnimationDiagnosticScratch : IDisposable
    {
        private bool active;
        public readonly List<RslAnimationDiagnostic> Rows=[];
        public AnimationDiagnosticScratch Rent()
        {if(active)return new AnimationDiagnosticScratch().Rent();active=true;return this;}
        public void Dispose(){Rows.Clear();active=false;}
    }
}
