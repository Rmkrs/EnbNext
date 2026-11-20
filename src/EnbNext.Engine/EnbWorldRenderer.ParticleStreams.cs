using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbW3dTransientGeometryArena _particleStreamArena=new();
    private EnbW3dTransientGeometryArena.Frame? _particleStreamFrame;
    private readonly bool _immutableParticleStreams=Environment.GetEnvironmentVariable("ENB_IMMUTABLE_PARTICLE_STREAMS")=="1";
    private readonly struct ParticleStreamFrameScope : IDisposable
    {
        private readonly EnbWorldRenderer _view;
        private readonly EnbW3dTransientGeometryArena.Frame? _previous;
        private readonly EnbW3dTransientGeometryArena.Frame? _current;
        public ParticleStreamFrameScope(EnbWorldRenderer view)
        {
            _view=view;_previous=view._particleStreamFrame;
            _current=view._retainedOrdinaryMeshes&&!view._immutableParticleStreams?view._particleStreamArena.BeginFrame():null;
            view._particleStreamFrame=_current;
        }
        public void Dispose(){_current?.Dispose();_view._particleStreamFrame=_previous;}
    }
}
