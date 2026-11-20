using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>9661C0's shared emitter/buffer callback list for avatar teleport aggregates.</summary>
public sealed class EnbRetailTeleportScene
{
    private readonly EnbCurrentRetailSceneCallbackList<(EnbRetailParticleSceneLifetime State,bool Buffer)> _callbacks=new();
    private readonly List<EnbRetailParticleSceneLifetime> _particles=[];
    private readonly List<EnbRetailParticleSceneLifetime> _completed=[];

    public void Attach(EnbRetailParticleSceneLifetime particle)
    {
        if(_particles.Contains(particle))return;
        _particles.Add(particle);
        particle.SceneAttachmentChanged+=attached=>
        {
            if(attached)_callbacks.Register((particle,false));
            else _callbacks.Remove((particle,false));
        };
        particle.SetSceneAttached(true);
    }

    public void Advance(uint clock,uint frameDelta,Action<Exception>? diagnostic=null)
    {
        //965F40 queries bounds before9661C0 walks the registered callbacks.
        foreach(var particle in _particles)particle.EnsureBounds(clock);
        _callbacks.Visit(entry=>
        {
            var particle=entry.State;
            try
            {
                if(entry.Buffer)particle.BufferCallback(clock,frameDelta,particle.Emitter.CurrentWorld,1);
                else if(particle.EmitterCallback(particle.Emitter.CurrentWorld))
                    _callbacks.Register((particle,true));
                //97DEF0's default F0=1 requests type2 removal when E4 is
                //complete.9662E0 removes the child only after scene rendering.
                if(!entry.Buffer && particle.SceneAttached && particle.Emitter.HasCompletedFiniteRun &&
                    !_completed.Contains(particle))_completed.Insert(0,particle);
            }
            catch(Exception error) when(diagnostic is not null &&
                error is InvalidDataException or ArgumentException or InvalidOperationException)
            {Destroy(particle,clock);diagnostic(error);}
        });
        foreach(var particle in _particles.Where(p=>p.CanRetire).ToArray())
        {
            _callbacks.Remove((particle,false));
            _callbacks.Remove((particle,true));
            _particles.Remove(particle);
        }
    }

    public void Destroy(EnbRetailParticleSceneLifetime particle,uint clock)
        =>particle.Destroy(clock,particle.Emitter.CurrentWorld,1);

    public void CompleteRender(uint clock)
    {
        foreach(var particle in _completed)Destroy(particle,clock);
        _completed.Clear();
    }
}
