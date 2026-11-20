using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public static class EnbCurrentRetailBloodParticleConfiguration
{
    //006FE7D6-006FE88A passes environment mode0=false, modes1/5=true.
    //00811D1F/00812080 clears each clone's buffer+A4/A8/AC, not its
    //emitter velocity inheritance.00962070 consumes those acceleration fields.
    public static EnbCurrentRetailParticleEmitterDefinition ForEnvironment(EnbCurrentRetailParticleEmitterDefinition definition,int? presentationMode)
        => presentationMode!=0?definition:definition.Copy().SetAccelerationPerSecondSquared(Vector3.Zero);
}
