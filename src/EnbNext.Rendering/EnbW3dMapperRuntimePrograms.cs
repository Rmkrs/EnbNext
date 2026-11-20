using System.Globalization;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Backend-neutral texture-coordinate source selected by the current retail
/// mapper apply programs. The generated-coordinate names match the observable
/// D3D8 TCI values used by the current binary, but no D3D object leaks into the
/// production renderer contract.
/// </summary>
public enum EnbW3dMapperCoordinateSource : byte
{
    PassThrough = 0,
    CameraSpaceNormal = 1,
    CameraSpacePosition = 2,
    CameraSpaceReflectionVector = 3,
}

public enum EnbW3dMapperTransformShape : byte
{
    Count2 = 2,
    ProjectedCount3 = 3,
}

public enum EnbW3dMapperProjectionAxis : byte
{
    X = 0,
    Y = 1,
    Z = 2,
}

/// <summary>
/// Finite runtime program inventory established from the selector-specific
/// current-retail vtables. Names describe observed matrix/state behavior rather
/// than importing historical W3D class names.
/// </summary>
public enum EnbW3dMapperRuntimeProgramKind : byte
{
    Disabled = 0,
    FixedHalfBias = 1,
    ProjectedLinear = 2,
    Linear = 3,
    StaticScale = 4,
    GridAtlas = 5,
    CenteredRotation = 6,
    SinusoidalOffset = 7,
    SteppedOffset = 8,
    PingPongOffset = 9,
    AxisProjection = 10,
    Randomized = 11,
    VerticalEnvironmentScroll = 12,
    BumpEnvironment = 13,
    GridAtlasAxisProjection = 14,
}

public readonly record struct EnbW3dMapperAtlasParameters(
    float FramesPerSecond,
    int Log2Width,
    int Last,
    int Offset);

public readonly record struct EnbW3dMapperRotationParameters(
    Vector2 Scale,
    float RadiansPerMillisecond,
    Vector2 Center);

public readonly record struct EnbW3dMapperSineParameters(
    Vector2 Scale,
    float UAmplitude,
    float UFrequency,
    float UPhase,
    float VAmplitude,
    float VFrequency,
    float VPhase);

public readonly record struct EnbW3dMapperStepParameters(
    Vector2 Scale,
    Vector2 Step,
    float StepsPerMillisecond,
    bool ClampFix);

public readonly record struct EnbW3dMapperPingPongParameters(
    Vector2 Scale,
    Vector2 PerMillisecond,
    float PeriodMilliseconds);

public readonly record struct EnbW3dMapperRandomizedParameters(
    Vector2 Scale,
    float EventsPerMillisecond,
    Vector2 PerMillisecond);

public readonly record struct EnbW3dMapperEnvironmentScrollParameters(
    float VPerSecond,
    float VStart,
    bool UseReflection);

public readonly record struct EnbW3dMapperBumpParameters(
    float RadiansPerSecond,
    float Scale);

/// <summary>
/// Complete selector-level runtime contract needed by a future programmable
/// backend. Mutable clocks/RNG/view state deliberately remain runtime inputs;
/// immutable W3D material data only chooses a finite program and its constructor
/// parameters.
/// </summary>
public readonly record struct EnbW3dMapperRuntimeProgram(
    EnbW3dMapperRuntimeProgramKind Kind,
    EnbW3dMapperCoordinateSource CoordinateSource,
    EnbW3dMapperTransformShape TransformShape,
    bool UsesElapsedTime,
    bool RequiresMutableState,
    bool RequiresViewTransform,
    bool RequiresRandomSource,
    bool ConfiguresBumpEnvironment,
    EnbW3dMapperUniforms BaseUniforms,
    EnbW3dMapperAtlasParameters? Atlas,
    EnbW3dMapperRotationParameters? Rotation,
    EnbW3dMapperSineParameters? Sine,
    EnbW3dMapperStepParameters? Step,
    EnbW3dMapperPingPongParameters? PingPong,
    EnbW3dMapperProjectionAxis Axis,
    EnbW3dMapperRandomizedParameters? Randomized,
    EnbW3dMapperEnvironmentScrollParameters? EnvironmentScroll,
    EnbW3dMapperBumpParameters? Bump)
{
    public static EnbW3dMapperRuntimeProgram Disabled => new(
        EnbW3dMapperRuntimeProgramKind.Disabled,
        EnbW3dMapperCoordinateSource.PassThrough,
        EnbW3dMapperTransformShape.Count2,
        UsesElapsedTime: false,
        RequiresMutableState: false,
        RequiresViewTransform: false,
        RequiresRandomSource: false,
        ConfiguresBumpEnvironment: false,
        BaseUniforms: EnbW3dMapperUniforms.Identity,
        Atlas: null,
        Rotation: null,
        Sine: null,
        Step: null,
        PingPong: null,
        Axis: EnbW3dMapperProjectionAxis.Z,
        Randomized: null,
        EnvironmentScroll: null,
        Bump: null);
}

public static class EnbW3dMapperRuntimeProgramCompiler
{
    private const float MillisecondsPerSecond = 1000.0f;

    public static bool TryCompile(
        EnbW3dRenderMapper? mapper,
        out EnbW3dMapperRuntimeProgram program,
        out string? diagnostic)
    {
        if (mapper is null)
        {
            program = EnbW3dMapperRuntimeProgram.Disabled;
            diagnostic = null;
            return true;
        }

        if (!mapper.TypeSelector.IsInstantiatedByCurrentRetail())
        {
            program = default;
            diagnostic = $"W3D mapper selector 0x{(byte)mapper.TypeSelector:X2} is not instantiated by the current retail material loader.";
            return false;
        }

        MapperArgumentReader args = new(mapper.Raw);
        Vector2 scale = new(mapper.UScale, mapper.VScale);
        EnbW3dMapperUniforms linear = LinearBase(mapper);
        EnbW3dMapperUniforms staticScale = StaticScaleBase(scale);

        program = mapper.TypeSelector switch
        {
            EnbW3dMapperTypeSelector.Mode01 => Program(
                EnbW3dMapperRuntimeProgramKind.FixedHalfBias,
                EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector,
                view: true),

            EnbW3dMapperTypeSelector.Mode02 => Program(
                EnbW3dMapperRuntimeProgramKind.FixedHalfBias,
                EnbW3dMapperCoordinateSource.CameraSpaceNormal,
                view: true),

            EnbW3dMapperTypeSelector.Mode03 => Program(
                EnbW3dMapperRuntimeProgramKind.ProjectedLinear,
                EnbW3dMapperCoordinateSource.CameraSpacePosition,
                shape: EnbW3dMapperTransformShape.ProjectedCount3,
                time: true,
                mutable: true,
                view: true,
                baseUniforms: linear),

            EnbW3dMapperTypeSelector.Mode04 => Program(
                EnbW3dMapperRuntimeProgramKind.Linear,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                baseUniforms: linear),

            EnbW3dMapperTypeSelector.Mode06 => Program(
                EnbW3dMapperRuntimeProgramKind.StaticScale,
                EnbW3dMapperCoordinateSource.PassThrough,
                baseUniforms: staticScale),

            EnbW3dMapperTypeSelector.Mode07 => Program(
                EnbW3dMapperRuntimeProgramKind.GridAtlas,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                atlas: Atlas(args)),

            EnbW3dMapperTypeSelector.Mode08 => Program(
                EnbW3dMapperRuntimeProgramKind.CenteredRotation,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                baseUniforms: staticScale,
                rotation: new EnbW3dMapperRotationParameters(
                    scale,
                    EnbCurrentRetailMapperRotationRuntime.Rate(args.Float("Speed", 0.1f)),
                    new Vector2(args.Float("UCenter", 0.0f), args.Float("VCenter", 0.0f)))),

            EnbW3dMapperTypeSelector.Mode09 => Program(
                EnbW3dMapperRuntimeProgramKind.SinusoidalOffset,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                baseUniforms: staticScale,
                sine: new EnbW3dMapperSineParameters(
                    scale,
                    args.Float("UAmp", 1.0f),
                    args.Float("UFreq", 1.0f),
                    args.Float("UPhase", 0.0f),
                    args.Float("VAmp", 1.0f),
                    args.Float("VFreq", 1.0f),
                    args.Float("VPhase", 0.0f))),

            EnbW3dMapperTypeSelector.Mode0A => Program(
                EnbW3dMapperRuntimeProgramKind.SteppedOffset,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                baseUniforms: staticScale,
                step: new EnbW3dMapperStepParameters(
                    scale,
                    new Vector2(args.Float("UStep", 0.0f), args.Float("VStep", 0.0f)),
                    EnbCurrentRetailMapperSteppingRuntime.Rate(args.Text("SPS"),0),
                    args.Boolean("ClampFix", false))),

            EnbW3dMapperTypeSelector.Mode0B => Program(
                EnbW3dMapperRuntimeProgramKind.PingPongOffset,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                baseUniforms: staticScale,
                pingPong: new EnbW3dMapperPingPongParameters(
                    scale,
                    new Vector2(EnbCurrentRetailMapperSteppingRuntime.Rate(mapper.UPerSecond),EnbCurrentRetailMapperSteppingRuntime.Rate(mapper.VPerSecond)),
                    EnbCurrentRetailMapperSteppingRuntime.Period(args.Text("Period"),0))),

            EnbW3dMapperTypeSelector.Mode0C => Program(
                EnbW3dMapperRuntimeProgramKind.AxisProjection,
                EnbW3dMapperCoordinateSource.CameraSpaceNormal,
                view: true,
                axis: args.Axis()),

            EnbW3dMapperTypeSelector.Mode0D => Program(
                EnbW3dMapperRuntimeProgramKind.AxisProjection,
                EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector,
                view: true,
                axis: args.Axis()),

            EnbW3dMapperTypeSelector.Mode0E => Program(
                EnbW3dMapperRuntimeProgramKind.GridAtlas,
                EnbW3dMapperCoordinateSource.CameraSpaceNormal,
                time: true,
                mutable: true,
                view: true,
                atlas: Atlas(args)),

            EnbW3dMapperTypeSelector.Mode0F => Program(
                EnbW3dMapperRuntimeProgramKind.GridAtlas,
                EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector,
                time: true,
                mutable: true,
                view: true,
                atlas: Atlas(args)),

            EnbW3dMapperTypeSelector.Mode10 => Program(
                EnbW3dMapperRuntimeProgramKind.Randomized,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                random: true,
                baseUniforms: staticScale,
                randomized: new EnbW3dMapperRandomizedParameters(
                    scale,
                    EnbCurrentRetailMapperSteppingRuntime.Rate(args.Text("FPS"),0),
                    new Vector2(EnbCurrentRetailMapperSteppingRuntime.Rate(args.Text("UPerSec"),mapper.UPerSecond),
                        EnbCurrentRetailMapperSteppingRuntime.Rate(args.Text("VPerSec"),mapper.VPerSecond)))),

            EnbW3dMapperTypeSelector.Mode11 => CompileMode11(mapper, args),

            EnbW3dMapperTypeSelector.Mode12 => Program(
                EnbW3dMapperRuntimeProgramKind.BumpEnvironment,
                EnbW3dMapperCoordinateSource.PassThrough,
                time: true,
                mutable: true,
                bump: true,
                baseUniforms: linear,
                bumpParameters: new EnbW3dMapperBumpParameters(
                    EnbCurrentRetailMapperBumpRuntime.Rate(args.Float("BumpRotation", 0.0f)),
                    args.Float("BumpScale", 1.0f))),

            EnbW3dMapperTypeSelector.Mode13 => Program(
                EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection,
                EnbW3dMapperCoordinateSource.CameraSpaceNormal,
                time: true,
                mutable: true,
                view: true,
                atlas: Atlas(args),
                axis: args.Axis()),

            EnbW3dMapperTypeSelector.Mode14 => Program(
                EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection,
                EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector,
                time: true,
                mutable: true,
                view: true,
                atlas: Atlas(args),
                axis: args.Axis()),

            _ => default,
        };

        diagnostic = null;
        return true;
    }

    private static EnbW3dMapperRuntimeProgram CompileMode11(EnbW3dRenderMapper mapper, MapperArgumentReader args)
    {
        bool reflect = args.Boolean("UseReflect", false);
        return Program(
            EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll,
            reflect
                ? EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector
                : EnbW3dMapperCoordinateSource.CameraSpaceNormal,
            time: true,
            mutable: true,
            view: true,
            environmentScroll: new EnbW3dMapperEnvironmentScrollParameters(
                mapper.VPerSecond,
                args.Float("VStart", 0.0f),
                reflect));
    }

    private static EnbW3dMapperAtlasParameters Atlas(MapperArgumentReader args)
        => new(
            args.Float("FPS", 1.0f),
            args.Integer("Log2Width", 1),
            args.Integer("Last", 0),
            args.Integer("Offset", 0));

    private static EnbW3dMapperUniforms LinearBase(EnbW3dRenderMapper mapper)
        => new(
            new Vector2(mapper.UScale, mapper.VScale),
            new Vector2(mapper.UPerSecond * -0.001f, mapper.VPerSecond * -0.001f),
            new Vector2(mapper.UOffset, mapper.VOffset),
            mapper.ClampFix,
            HasClosedBaseParameters: true);

    private static EnbW3dMapperUniforms StaticScaleBase(Vector2 scale)
        => new(
            scale,
            Vector2.Zero,
            Vector2.Zero,
            ClampFix: false,
            HasClosedBaseParameters: true);

    private static EnbW3dMapperRuntimeProgram Program(
        EnbW3dMapperRuntimeProgramKind kind,
        EnbW3dMapperCoordinateSource source,
        EnbW3dMapperTransformShape shape = EnbW3dMapperTransformShape.Count2,
        bool time = false,
        bool mutable = false,
        bool view = false,
        bool random = false,
        bool bump = false,
        EnbW3dMapperUniforms? baseUniforms = null,
        EnbW3dMapperAtlasParameters? atlas = null,
        EnbW3dMapperRotationParameters? rotation = null,
        EnbW3dMapperSineParameters? sine = null,
        EnbW3dMapperStepParameters? step = null,
        EnbW3dMapperPingPongParameters? pingPong = null,
        EnbW3dMapperProjectionAxis axis = EnbW3dMapperProjectionAxis.Z,
        EnbW3dMapperRandomizedParameters? randomized = null,
        EnbW3dMapperEnvironmentScrollParameters? environmentScroll = null,
        EnbW3dMapperBumpParameters? bumpParameters = null)
        => new(
            kind,
            source,
            shape,
            time,
            mutable,
            view,
            random,
            bump,
            baseUniforms ?? EnbW3dMapperUniforms.Identity,
            atlas,
            rotation,
            sine,
            step,
            pingPong,
            axis,
            randomized,
            environmentScroll,
            bumpParameters);

    private sealed class MapperArgumentReader
    {
        private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        public MapperArgumentReader(string raw)
        {
            foreach (string line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0 || equals == line.Length - 1)
                    continue;

                string key = line[..equals].Trim();
                if (key.Length == 0)
                    continue;

                values[key] = line[(equals + 1)..].Trim();
            }
        }

        public string Text(string key)=>values.TryGetValue(key,out var text)?text:string.Empty;

        public float Float(string key, float defaultValue)
        {
            if (!values.TryGetValue(key, out string? text))
                return defaultValue;

            return EnbNext.Assets.EnbRetailMapperNumber.TryParse(text, out float value) ? value : defaultValue;
        }

        public int Integer(string key, int defaultValue)
        {
            if (!values.TryGetValue(key, out string? text))
                return defaultValue;

            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : defaultValue;
        }

        public bool Boolean(string key, bool defaultValue)
        {
            if (!values.TryGetValue(key, out string? text) || text.Length == 0)
                return defaultValue;

            return char.ToUpperInvariant(text[0]) switch
            {
                '0' or 'F' or 'N' => false,
                '1' or 'T' or 'Y' => true,
                _ => defaultValue,
            };
        }

        public EnbW3dMapperProjectionAxis Axis()
        {
            if (!values.TryGetValue("Axis", out string? text) || text.Length == 0)
                return EnbW3dMapperProjectionAxis.Z;

            return char.ToUpperInvariant(text[0]) switch
            {
                'X' => EnbW3dMapperProjectionAxis.X,
                'Y' => EnbW3dMapperProjectionAxis.Y,
                _ => EnbW3dMapperProjectionAxis.Z,
            };
        }
    }
}
