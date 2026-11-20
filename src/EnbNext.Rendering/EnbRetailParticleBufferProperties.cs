using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Buffer-owned random arrays, constructed in0095BA90 order and copied by0095C360.</summary>
public sealed class EnbRetailParticleBufferProperties
{
    public readonly record struct Offsets(Vector3 Color, float Opacity, float Size,
        float RotationRatePerMillisecond, float InitialOrientation, float Frame, float BlurTime);

    private Vector3[] _color;
    private readonly float[] _opacity, _size, _rotation, _orientation, _frame, _blur;
    public uint Capacity { get; }
    internal ulong Revision { get; private set; }

    public EnbRetailParticleBufferProperties(uint capacity, Vector3 colorRandom, float opacityRandom,
        float sizeRandom, float rotationRandom, float orientationRandom, float frameRandom, float blurRandom,
        EnbRetailParticlePropertyRandomStream stream, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Capacity = capacity;
        _color = EnbRetailParticlePropertyRandomTables.CreateColor(colorRandom, capacity, stream, precision);
        _opacity = EnbRetailParticlePropertyRandomTables.CreateScalar(EnbRetailParticleRandomProperty.Opacity, opacityRandom, capacity, stream, precision);
        _size = EnbRetailParticlePropertyRandomTables.CreateScalar(EnbRetailParticleRandomProperty.Size, sizeRandom, capacity, stream, precision);
        _rotation = EnbRetailParticlePropertyRandomTables.CreateScalar(EnbRetailParticleRandomProperty.RotationRate, rotationRandom, capacity, stream, precision);
        _orientation = EnbRetailParticlePropertyRandomTables.CreateScalar(EnbRetailParticleRandomProperty.InitialOrientation, orientationRandom, capacity, stream, precision);
        _frame = EnbRetailParticlePropertyRandomTables.CreateScalar(EnbRetailParticleRandomProperty.Frame, frameRandom, capacity, stream, precision);
        _blur = EnbRetailParticlePropertyRandomTables.CreateScalar(EnbRetailParticleRandomProperty.BlurTime, blurRandom, capacity, stream, precision);
    }

    public static EnbRetailParticleBufferProperties Construct(EnbCurrentRetailParticleEmitterDefinition definition,
        uint capacity, EnbRetailParticlePropertyRandomStream stream, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
        => new(capacity, definition.Color.Random, definition.Opacity.Random, definition.Size.Random,
            definition.Rotation.Random, definition.InitialOrientationRandom, definition.Frame.Random,
            definition.BlurTime?.Random ?? 0, stream, precision);

    private EnbRetailParticleBufferProperties(EnbRetailParticleBufferProperties source)
    {
        Capacity = source.Capacity;
        _color = (Vector3[])source._color.Clone();
        _opacity = (float[])source._opacity.Clone();
        _size = (float[])source._size.Clone();
        _rotation = (float[])source._rotation.Clone();
        _orientation = (float[])source._orientation.Clone();
        _frame = (float[])source._frame.Clone();
        _blur = (float[])source._blur.Clone();
    }

    public EnbRetailParticleBufferProperties Clone() => new(this);
    public void SetColor(Vector3 amplitude,EnbRetailParticlePropertyRandomStream stream)
    {
        _color=EnbRetailParticlePropertyRandomTables.CreateColor(amplitude,Capacity,stream);
        Revision++;
    }
    public void ScaleSize(float scale)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        for(int i=0;i<_size.Length;i++)_size[i]=(float)((EnbRetailLineArithmetic.Register)_size[i]*scale);
        Revision++;
    }

    //00960F80 indexes each independent table by physical slot & its mask.
    public Offsets AtSlot(int slot)
    {
        if (slot < 0 || (uint)slot >= Capacity) throw new ArgumentOutOfRangeException(nameof(slot));
        return new(_color[slot & (_color.Length - 1)], _opacity[slot & (_opacity.Length - 1)],
            _size[slot & (_size.Length - 1)], _rotation[slot & (_rotation.Length - 1)],
            _orientation[slot & (_orientation.Length - 1)], _frame[slot & (_frame.Length - 1)],
            _blur[slot & (_blur.Length - 1)]);
    }
}
