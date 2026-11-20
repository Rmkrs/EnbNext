using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Mutable backend-neutral state for the complete mapper program inventory
/// realized through. Current-retail clocks are unsigned 32-bit millisecond
/// counters; selector-specific reset/state ownership is kept separate instead of
/// forcing every mapper through one generic accumulator. Mode10 receives the
/// mapper-owned current-retail random stream explicitly.
/// </summary>
public sealed class EnbW3dMapperRuntimeState
{
    private readonly EnbW3dMapperRuntimeProgram _program;
    private readonly EnbW3dRetailMapperRandomSource? _randomSource;
    private readonly uint _initialMilliseconds;

    private Vector2 _linearOffset;
    private uint _linearLastMilliseconds;

    private uint _atlasElapsedMilliseconds;
    private uint _atlasCurrentFrame;
    private uint _atlasLastMilliseconds;
    private readonly int _atlasDirection;
    private readonly uint _atlasMillisecondsPerFrame;
    private readonly uint _atlasFrameCount;
    private readonly int _atlasLog2Width;
    private readonly float _atlasCellSize;

    private float _rotationAngleRadians;
    private uint _rotationLastMilliseconds;

    private float _sineTimeRadians;
    private uint _sineLastMilliseconds;

    private Vector2 _stepOffset;
    private float _stepElapsedMilliseconds;
    private uint _stepLastMilliseconds;

    private float _pingPongElapsedMilliseconds;
    private uint _pingPongLastMilliseconds;

    private float _randomElapsedMilliseconds;
    private uint _randomLastMilliseconds;
    private float _randomAngleRadians;
    private float _randomFirstOffset;
    private float _randomSecondOffset;

    private uint _bumpLastMilliseconds;
    private float _bumpAngleRadians;

    private float _environmentV;
    private uint _environmentLastMilliseconds;

    private EnbW3dMapperRuntimeState(
        EnbW3dMapperRuntimeProgram program,
        uint initialMilliseconds,
        int atlasDirection = 0,
        uint atlasMillisecondsPerFrame = 1,
        uint atlasFrameCount = 1,
        int atlasLog2Width = 0,
        float atlasCellSize = 1f,
        uint atlasCurrentFrame = 0,
        EnbW3dRetailMapperRandomSource? randomSource = null)
    {
        _program = program;
        _randomSource = randomSource;
        _initialMilliseconds = initialMilliseconds;

        _linearOffset = program.BaseUniforms.Offset;
        _linearLastMilliseconds = initialMilliseconds;

        _atlasElapsedMilliseconds = 0;
        _atlasCurrentFrame = atlasCurrentFrame;
        _atlasLastMilliseconds = initialMilliseconds;
        _atlasDirection = atlasDirection;
        _atlasMillisecondsPerFrame = atlasMillisecondsPerFrame;
        _atlasFrameCount = atlasFrameCount;
        _atlasLog2Width = atlasLog2Width;
        _atlasCellSize = atlasCellSize;

        _rotationAngleRadians = 0f;
        _rotationLastMilliseconds = initialMilliseconds;

        _sineTimeRadians = 0f;
        _sineLastMilliseconds = initialMilliseconds;

        _stepOffset = Vector2.Zero;
        _stepElapsedMilliseconds = 0f;
        _stepLastMilliseconds = initialMilliseconds;

        _pingPongElapsedMilliseconds = 0f;
        _pingPongLastMilliseconds = initialMilliseconds;

        _randomElapsedMilliseconds = 0f;
        _randomLastMilliseconds = initialMilliseconds;
        _randomAngleRadians = 0f;
        _randomFirstOffset = 0f;
        _randomSecondOffset = 0f;
        if (program.Kind == EnbW3dMapperRuntimeProgramKind.Randomized)
            RefreshRandomizedState();

        _bumpLastMilliseconds = initialMilliseconds;
        _bumpAngleRadians = 0f;

        _environmentV = program.EnvironmentScroll?.VStart ?? 0f;
        _environmentLastMilliseconds = initialMilliseconds;
    }

    public EnbW3dMapperRuntimeProgram Program => _program;
    public ulong StateRevision { get; private set; }


    /// <summary>
    /// Current-retail mapper clock captured when this material-owned mapper was
    /// constructed. A secondary renderer of the same RenderObj must reuse this
    /// epoch instead of restarting animated UV state when it creates GPU state.
    /// </summary>
    public uint InitialMilliseconds => _initialMilliseconds;

    /// <summary>
    /// Material assignment0092EB90 invokes each mapper's virtual copy separately.
    /// Animated phases restart except Mode11 (00935700), which retains current V.
    /// Mode10 (00938C80) draws a fresh triple from the same global random stream.
    /// </summary>
    public EnbW3dMapperRuntimeState CloneForMaterial(uint creationMilliseconds)
    {
        // The immutable program has already passed construction validation.
        if (!TryCreate(_program, creationMilliseconds, _randomSource, out var copy, out var diagnostic))
            throw new InvalidOperationException(diagnostic);
        if (_program.Kind == EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll)
            copy._environmentV = _environmentV;
        return copy;
    }

    public static bool TryCreate(
        EnbW3dMapperRuntimeProgram program,
        uint initialMilliseconds,
        out EnbW3dMapperRuntimeState state,
        out string? diagnostic)
        => TryCreate(program, initialMilliseconds, randomSource: null, out state, out diagnostic);

    public static bool TryCreate(
        EnbW3dMapperRuntimeProgram program,
        uint initialMilliseconds,
        EnbW3dRetailMapperRandomSource? randomSource,
        out EnbW3dMapperRuntimeState state,
        out string? diagnostic)
    {
        diagnostic = null;
        if (program.Kind == EnbW3dMapperRuntimeProgramKind.Disabled)
        {
            state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
            return true;
        }

        switch (program.Kind)
        {
            case EnbW3dMapperRuntimeProgramKind.FixedHalfBias:
                if (!ValidateGeneratedCount2(program, mutable: false, out diagnostic))
                    break;
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.ProjectedLinear:
                if (program.CoordinateSource != EnbW3dMapperCoordinateSource.CameraSpacePosition ||
                    program.TransformShape != EnbW3dMapperTransformShape.ProjectedCount3 ||
                    !program.RequiresViewTransform ||
                    !ValidateLinear(program, requireBump: false, out diagnostic))
                {
                    diagnostic ??= "R6K ProjectedLinear mapper no longer matches the STATIC-CLOSED R4L Mode03 runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.Linear:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !ValidateLinear(program, requireBump: false, out diagnostic))
                    break;
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.StaticScale:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !program.BaseUniforms.HasClosedBaseParameters ||
                    program.UsesElapsedTime ||
                    program.RequiresMutableState ||
                    !IsFinite(program.BaseUniforms.Scale))
                {
                    diagnostic ??= "R6K StaticScale mapper no longer matches the STATIC-CLOSED R4L Mode06 runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.GridAtlas:
                if (!ValidateAtlasSource(program, out diagnostic) ||
                    !TryPrepareAtlas(program, out int direction, out uint millisecondsPerFrame, out uint frameCount,
                        out int log2Width, out float cellSize, out uint currentFrame, out diagnostic))
                    break;
                state = new EnbW3dMapperRuntimeState(
                    program,
                    initialMilliseconds,
                    direction,
                    millisecondsPerFrame,
                    frameCount,
                    log2Width,
                    cellSize,
                    currentFrame);
                return true;

            case EnbW3dMapperRuntimeProgramKind.CenteredRotation:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !program.UsesElapsedTime || !program.RequiresMutableState ||
                    program.Rotation is not EnbW3dMapperRotationParameters rotation ||
                    !IsFinite(rotation.Scale) || !IsFinite(rotation.Center) ||
                    !float.IsFinite(rotation.RadiansPerMillisecond))
                {
                    diagnostic ??= "R6K CenteredRotation mapper no longer matches the STATIC-CLOSED R4L Mode08 runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.SinusoidalOffset:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !program.UsesElapsedTime || !program.RequiresMutableState ||
                    program.Sine is not EnbW3dMapperSineParameters sine ||
                    !IsFinite(sine.Scale) ||
                    !AllFinite(sine.UAmplitude, sine.UFrequency, sine.UPhase, sine.VAmplitude, sine.VFrequency, sine.VPhase))
                {
                    diagnostic ??= "R6K SinusoidalOffset mapper no longer matches the STATIC-CLOSED R4L Mode09 runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.SteppedOffset:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !program.UsesElapsedTime || !program.RequiresMutableState ||
                    program.Step is not EnbW3dMapperStepParameters step ||
                    !IsFinite(step.Scale) || !IsFinite(step.Step) || !float.IsFinite(step.StepsPerMillisecond))
                {
                    diagnostic ??= "R6K SteppedOffset mapper no longer matches the STATIC-CLOSED R4L Mode0A runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.PingPongOffset:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !program.UsesElapsedTime || !program.RequiresMutableState ||
                    program.PingPong is not EnbW3dMapperPingPongParameters pingPong ||
                    !IsFinite(pingPong.Scale) || !IsFinite(pingPong.PerMillisecond) ||
                    !float.IsFinite(pingPong.PeriodMilliseconds))
                {
                    diagnostic ??= "R6K PingPongOffset mapper no longer matches the STATIC-CLOSED R4L Mode0B runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.AxisProjection:
                if (!ValidateGeneratedCount2(program, mutable: false, out diagnostic) ||
                    !Enum.IsDefined(program.Axis))
                {
                    diagnostic ??= "R6K AxisProjection mapper no longer matches the STATIC-CLOSED R4L Mode0C/Mode0D runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.Randomized:
                if (!ValidateRandomized(program, randomSource, out diagnostic))
                    break;
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds, randomSource: randomSource);
                return true;

            case EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll:
                if (!ValidateGeneratedCount2(program, mutable: true, out diagnostic) ||
                    program.EnvironmentScroll is not EnbW3dMapperEnvironmentScrollParameters environment ||
                    !float.IsFinite(environment.VPerSecond) ||
                    !float.IsFinite(environment.VStart) ||
                    environment.UseReflection != (program.CoordinateSource == EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector))
                {
                    diagnostic ??= "R6K VerticalEnvironmentScroll mapper no longer matches the STATIC-CLOSED R4L Mode11 runtime contract.";
                    break;
                }
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.BumpEnvironment:
                if (!ValidatePassThroughCount2(program, out diagnostic) ||
                    !ValidateLinear(program, requireBump: true, out diagnostic))
                    break;
                state = new EnbW3dMapperRuntimeState(program, initialMilliseconds);
                return true;

            case EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection:
                if (!ValidateGeneratedCount2(program, mutable: true, out diagnostic) ||
                    !Enum.IsDefined(program.Axis) ||
                    !TryPrepareAtlas(program, out int axisDirection, out uint axisMillisecondsPerFrame, out uint axisFrameCount,
                        out int axisLog2Width, out float axisCellSize, out uint axisCurrentFrame, out diagnostic))
                    break;
                state = new EnbW3dMapperRuntimeState(
                    program,
                    initialMilliseconds,
                    axisDirection,
                    axisMillisecondsPerFrame,
                    axisFrameCount,
                    axisLog2Width,
                    axisCellSize,
                    axisCurrentFrame);
                return true;

            default:
                diagnostic = $"R6L mapper runtime has not realized {program.Kind}.";
                break;
        }

        state = null!;
        return false;
    }

    /// <summary>
    /// Mirrors each realized selector's vtable +0x18 reset rather than applying a
    /// generic reset. Mode12 resets only its inherited linear state, leaving its
    /// independent bump angle/clock untouched exactly as current retail does.
    /// </summary>
    public void Reset(uint nowMilliseconds)
    {
        StateRevision=unchecked(StateRevision+1);
        switch (_program.Kind)
        {
            case EnbW3dMapperRuntimeProgramKind.Disabled:
            case EnbW3dMapperRuntimeProgramKind.FixedHalfBias:
            case EnbW3dMapperRuntimeProgramKind.StaticScale:
            case EnbW3dMapperRuntimeProgramKind.AxisProjection:
                return;

            case EnbW3dMapperRuntimeProgramKind.ProjectedLinear:
            case EnbW3dMapperRuntimeProgramKind.Linear:
            case EnbW3dMapperRuntimeProgramKind.BumpEnvironment:
                _linearOffset = Vector2.Zero;
                _linearLastMilliseconds = nowMilliseconds;
                return;

            case EnbW3dMapperRuntimeProgramKind.GridAtlas:
            case EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection:
                ResetAtlas(nowMilliseconds);
                return;

            case EnbW3dMapperRuntimeProgramKind.CenteredRotation:
                _rotationAngleRadians = 0f;
                _rotationLastMilliseconds = nowMilliseconds;
                return;

            case EnbW3dMapperRuntimeProgramKind.SinusoidalOffset:
                _sineTimeRadians = 0f;
                _sineLastMilliseconds = nowMilliseconds;
                return;

            case EnbW3dMapperRuntimeProgramKind.SteppedOffset:
                _stepOffset = Vector2.Zero;
                _stepElapsedMilliseconds = 0f;
                _stepLastMilliseconds = nowMilliseconds;
                return;

            case EnbW3dMapperRuntimeProgramKind.PingPongOffset:
                _pingPongElapsedMilliseconds = 0f;
                _pingPongLastMilliseconds = nowMilliseconds;
                return;

            case EnbW3dMapperRuntimeProgramKind.Randomized:
                // Mode10 vtable +0x18 / 00938F10 resets only elapsed time and
                // its unsigned tick. The current random angle/offset seeds survive.
                _randomElapsedMilliseconds = 0f;
                _randomLastMilliseconds = nowMilliseconds;
                return;

            case EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll:
                // Mode11 reset @ 00935FD0 clears the mutable V accumulator rather
                // than restoring authored VStart, then restarts the unsigned clock.
                _environmentV = 0f;
                _environmentLastMilliseconds = nowMilliseconds;
                return;

            default:
                throw new InvalidOperationException($"R6L reset reached unsupported mapper kind {_program.Kind}.");
        }
    }

    // 00980E50 invokes matrix virtual +20 on a wrapped parent. Bump's
    // +20 is linear; angle/state7..A writes belong only to apply +14.
    public EnbW3dMapperRuntimeEvaluation EvaluateMatrix(uint nowMilliseconds)
    {
        StateRevision=unchecked(StateRevision+1);
        return _program.Kind == EnbW3dMapperRuntimeProgramKind.BumpEnvironment
            ? EvaluateLinear(nowMilliseconds, configureBump: false) : EvaluateCore(nowMilliseconds);
    }

    public EnbW3dMapperRuntimeEvaluation Evaluate(uint nowMilliseconds)
    {
        StateRevision=unchecked(StateRevision+1);
        return EvaluateCore(nowMilliseconds);
    }

    private EnbW3dMapperRuntimeEvaluation EvaluateCore(uint nowMilliseconds)
        => _program.Kind switch
        {
            EnbW3dMapperRuntimeProgramKind.Disabled => EnbW3dMapperRuntimeEvaluation.Identity,
            EnbW3dMapperRuntimeProgramKind.FixedHalfBias => Affine(new Vector2(0.5f), Vector2.Zero, new Vector2(0.5f)),
            EnbW3dMapperRuntimeProgramKind.ProjectedLinear => EvaluateLinear(nowMilliseconds, configureBump: false),
            EnbW3dMapperRuntimeProgramKind.Linear => EvaluateLinear(nowMilliseconds, configureBump: false),
            EnbW3dMapperRuntimeProgramKind.StaticScale => ScaleOnly(_program.BaseUniforms.Scale),
            EnbW3dMapperRuntimeProgramKind.GridAtlas => EvaluateAtlas(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.CenteredRotation => EvaluateCenteredRotation(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.SinusoidalOffset => EvaluateSinusoidalOffset(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.SteppedOffset => EvaluateSteppedOffset(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.PingPongOffset => EvaluatePingPongOffset(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.AxisProjection => Affine(new Vector2(0.5f), Vector2.Zero, new Vector2(0.5f)),
            EnbW3dMapperRuntimeProgramKind.Randomized => EvaluateRandomized(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll => EvaluateVerticalEnvironmentScroll(nowMilliseconds),
            EnbW3dMapperRuntimeProgramKind.BumpEnvironment => EvaluateLinear(nowMilliseconds, configureBump: true),
            EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection => EvaluateAtlas(nowMilliseconds),
            _ => throw new InvalidOperationException($"R6L runtime reached unsupported mapper kind {_program.Kind}.")
        };

    private EnbW3dMapperRuntimeEvaluation EvaluateLinear(uint nowMilliseconds, bool configureBump)
    {
        EnbW3dMapperUniforms linear = _program.BaseUniforms;
        var evaluation=EnbCurrentRetailMapperLinearRuntime.Evaluate(linear,nowMilliseconds,
            ref _linearOffset,ref _linearLastMilliseconds);

        if (!configureBump)
            return evaluation;

        EnbW3dMapperBumpParameters bump = _program.Bump
            ?? throw new InvalidOperationException("R6K BumpEnvironment runtime state lost its validated bump parameters.");
        Vector4 bumpMatrix=EnbCurrentRetailMapperBumpRuntime.Evaluate(bump,nowMilliseconds,
            ref _bumpAngleRadians,ref _bumpLastMilliseconds);

        return new EnbW3dMapperRuntimeEvaluation(
            linear.Scale,
            Vector2.Zero,
            evaluation.TextureOffset,
            bumpMatrix,
            ConfiguresBumpEnvironment: true);
    }

    private EnbW3dMapperRuntimeEvaluation EvaluateAtlas(uint nowMilliseconds)
    {
        uint delta = unchecked(nowMilliseconds - _atlasLastMilliseconds);
        uint elapsed = unchecked(_atlasElapsedMilliseconds + delta);
        uint frames = elapsed / _atlasMillisecondsPerFrame;
        int signedFrameDelta = unchecked(_atlasDirection * (int)frames);
        uint frame = unchecked(_atlasCurrentFrame + unchecked((uint)signedFrameDelta)) % _atlasFrameCount;
        // 0093377D/009380DD compare the unsigned DIV remainder as signed.
        // The negative branch adds the authored frame count with DWORD wrap.
        if (unchecked((int)frame) < 0)
            frame = unchecked(frame + _atlasFrameCount);
        _atlasCurrentFrame = frame;
        _atlasElapsedMilliseconds = elapsed % _atlasMillisecondsPerFrame;
        _atlasLastMilliseconds = nowMilliseconds;

        int shift = _atlasLog2Width & 31;
        uint mask = unchecked((1u << shift) - 1u);
        uint column = frame & mask;
        uint row = (frame >> shift) & mask;
        Vector2 cellOrigin = new(column * _atlasCellSize, row * _atlasCellSize);
        bool generatedSource = _program.CoordinateSource != EnbW3dMapperCoordinateSource.PassThrough;
        if (!generatedSource)
            return Affine(Vector2.One, Vector2.Zero, cellOrigin);

        // 00937900/009380A0 and 00939AD0 use half-cell scale/bias because
        // generated normals/reflections occupy the signed [-1,+1] domain.
        float halfCell = _atlasCellSize * 0.5f;
        return Affine(new Vector2(halfCell), Vector2.Zero, cellOrigin + new Vector2(halfCell));
    }

    private EnbW3dMapperRuntimeEvaluation EvaluateRandomized(uint nowMilliseconds)
    {
        EnbW3dMapperRandomizedParameters randomized = _program.Randomized
            ?? throw new InvalidOperationException("R6L Randomized runtime state lost its validated parameters.");
        return EnbCurrentRetailMapperRandomRuntime.Evaluate(randomized,
            _randomSource??throw new InvalidOperationException("Randomized mapper has no retail random source."),
            nowMilliseconds,ref _randomElapsedMilliseconds,ref _randomLastMilliseconds,
            ref _randomAngleRadians,ref _randomFirstOffset,ref _randomSecondOffset);
    }

    private void RefreshRandomizedState()
    {
        EnbW3dRetailMapperRandomSource source = _randomSource
            ?? throw new InvalidOperationException("R6L Randomized mapper has no current-retail random source.");
        EnbW3dRetailMapperRandomState random = source.NextRandomizedState();
        _randomAngleRadians = random.AngleRadians;
        _randomFirstOffset = random.FirstOffset;
        _randomSecondOffset = random.SecondOffset;
    }

    private EnbW3dMapperRuntimeEvaluation EvaluateVerticalEnvironmentScroll(uint nowMilliseconds)
    {
        EnbW3dMapperEnvironmentScrollParameters environment = _program.EnvironmentScroll
            ?? throw new InvalidOperationException("R6K VerticalEnvironmentScroll runtime state lost its validated parameters.");
        return EnbCurrentRetailMapperEnvironmentRuntime.Evaluate(environment.VPerSecond,nowMilliseconds,
            ref _environmentV,ref _environmentLastMilliseconds);
    }
    private EnbW3dMapperRuntimeEvaluation EvaluateCenteredRotation(uint nowMilliseconds)
    {
        EnbW3dMapperRotationParameters rotation = _program.Rotation
            ?? throw new InvalidOperationException("R6J CenteredRotation runtime state lost its validated rotation parameters.");
        return EnbCurrentRetailMapperRotationRuntime.Evaluate(rotation,nowMilliseconds,
            ref _rotationAngleRadians,ref _rotationLastMilliseconds);
    }

    private EnbW3dMapperRuntimeEvaluation EvaluateSinusoidalOffset(uint nowMilliseconds)
    {
        EnbW3dMapperSineParameters sine = _program.Sine
            ?? throw new InvalidOperationException("R6J SinusoidalOffset runtime state lost its validated sine parameters.");
        return EnbCurrentRetailMapperSineRuntime.Evaluate(sine,nowMilliseconds,
            ref _sineTimeRadians,ref _sineLastMilliseconds);
    }

    private EnbW3dMapperRuntimeEvaluation EvaluateSteppedOffset(uint nowMilliseconds)
    {
        EnbW3dMapperStepParameters step = _program.Step
            ?? throw new InvalidOperationException("R6J SteppedOffset runtime state lost its validated step parameters.");
        return EnbCurrentRetailMapperSteppingRuntime.Step(step,nowMilliseconds,
            ref _stepOffset,ref _stepElapsedMilliseconds,ref _stepLastMilliseconds);
    }

    private EnbW3dMapperRuntimeEvaluation EvaluatePingPongOffset(uint nowMilliseconds)
    {
        EnbW3dMapperPingPongParameters pingPong = _program.PingPong
            ?? throw new InvalidOperationException("R6J PingPongOffset runtime state lost its validated ping-pong parameters.");
        return EnbCurrentRetailMapperSteppingRuntime.PingPong(pingPong,nowMilliseconds,
            ref _pingPongElapsedMilliseconds,ref _pingPongLastMilliseconds);
    }
    private void ResetAtlas(uint nowMilliseconds)
    {
        EnbW3dMapperAtlasParameters atlas = _program.Atlas
            ?? throw new InvalidOperationException("R6J GridAtlas runtime state lost its validated atlas parameters.");
        uint normalizedOffset = unchecked((uint)atlas.Offset) % _atlasFrameCount;
        _atlasElapsedMilliseconds = 0;
        _atlasCurrentFrame = _atlasDirection < 0
            ? unchecked(_atlasFrameCount - normalizedOffset - 1u)
            : normalizedOffset;
        _atlasLastMilliseconds = nowMilliseconds;
    }

    private static bool ValidateRandomized(
        EnbW3dMapperRuntimeProgram program,
        EnbW3dRetailMapperRandomSource? randomSource,
        out string? diagnostic)
    {
        diagnostic = null;
        if (program.CoordinateSource != EnbW3dMapperCoordinateSource.PassThrough ||
            program.TransformShape != EnbW3dMapperTransformShape.Count2 ||
            !program.UsesElapsedTime ||
            !program.RequiresMutableState ||
            program.RequiresViewTransform ||
            !program.RequiresRandomSource ||
            program.ConfiguresBumpEnvironment ||
            program.Randomized is not EnbW3dMapperRandomizedParameters randomized ||
            !IsFinite(randomized.Scale) ||
            !float.IsFinite(randomized.EventsPerMillisecond) ||
            !IsFinite(randomized.PerMillisecond))
        {
            diagnostic = "R6L Randomized mapper no longer matches the STATIC-CLOSED R4L Mode10 runtime contract.";
            return false;
        }
        if (randomSource is null)
        {
            diagnostic = "R6L Randomized mapper requires the explicit mapper-owned current-retail random source.";
            return false;
        }
        return true;
    }

    private static bool ValidatePassThroughCount2(EnbW3dMapperRuntimeProgram program, out string? diagnostic)
    {
        diagnostic = null;
        if (program.CoordinateSource != EnbW3dMapperCoordinateSource.PassThrough ||
            program.TransformShape != EnbW3dMapperTransformShape.Count2 ||
            program.RequiresViewTransform ||
            program.RequiresRandomSource)
        {
            diagnostic = $"R6K pass-through COUNT2 mapper contract rejected {program.Kind} source={program.CoordinateSource} shape={program.TransformShape}.";
            return false;
        }
        return true;
    }

    private static bool ValidateGeneratedCount2(EnbW3dMapperRuntimeProgram program, bool mutable, out string? diagnostic)
    {
        diagnostic = null;
        bool generated = program.CoordinateSource is EnbW3dMapperCoordinateSource.CameraSpaceNormal or
            EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector;
        if (!generated ||
            program.TransformShape != EnbW3dMapperTransformShape.Count2 ||
            !program.RequiresViewTransform ||
            program.RequiresRandomSource ||
            program.RequiresMutableState != mutable ||
            program.UsesElapsedTime != mutable)
        {
            diagnostic = $"R6K generated COUNT2 mapper contract rejected {program.Kind} source={program.CoordinateSource} shape={program.TransformShape}.";
            return false;
        }
        return true;
    }

    private static bool ValidateAtlasSource(EnbW3dMapperRuntimeProgram program, out string? diagnostic)
    {
        diagnostic = null;
        if (program.CoordinateSource == EnbW3dMapperCoordinateSource.PassThrough)
            return ValidatePassThroughCount2(program, out diagnostic);
        return ValidateGeneratedCount2(program, mutable: true, out diagnostic);
    }

    private static bool ValidateLinear(EnbW3dMapperRuntimeProgram program, bool requireBump, out string? diagnostic)
    {
        diagnostic = null;
        if (!program.UsesElapsedTime || !program.RequiresMutableState ||
            program.ConfiguresBumpEnvironment != requireBump ||
            !program.BaseUniforms.HasClosedBaseParameters ||
            !IsFinite(program.BaseUniforms.Scale) ||
            !IsFinite(program.BaseUniforms.ScrollPerMillisecond) ||
            !IsFinite(program.BaseUniforms.Offset))
        {
            diagnostic = $"R6J {(requireBump ? "BumpEnvironment" : "Linear")} mapper no longer matches the STATIC-CLOSED R4L runtime contract.";
            return false;
        }

        if (!requireBump)
            return true;

        if (program.Bump is not EnbW3dMapperBumpParameters bump ||
            !float.IsFinite(bump.RadiansPerSecond) ||
            !float.IsFinite(bump.Scale))
        {
            diagnostic = "R6K BumpEnvironment mapper contains invalid bump runtime parameters.";
            return false;
        }
        return true;
    }

    private static bool TryPrepareAtlas(
        EnbW3dMapperRuntimeProgram program,
        out int direction,
        out uint millisecondsPerFrame,
        out uint frameCount,
        out int log2Width,
        out float cellSize,
        out uint currentFrame,
        out string? diagnostic)
    {
        direction = 0;
        millisecondsPerFrame = 1;
        frameCount = 0;
        log2Width = 0;
        cellSize = 1f;
        currentFrame = 0;
        diagnostic = null;

        if (!program.UsesElapsedTime || !program.RequiresMutableState ||
            program.Atlas is not EnbW3dMapperAtlasParameters atlas ||
            !float.IsFinite(atlas.FramesPerSecond))
        {
            diagnostic = "R6K GridAtlas mapper no longer matches the STATIC-CLOSED R4L Mode07 runtime contract.";
            return false;
        }

        log2Width = atlas.Log2Width;
        int shift = log2Width & 31;
        uint gridWidth = 1u << shift;
        frameCount = atlas.Last == 0
            ? unchecked(gridWidth * gridWidth)
            : unchecked((uint)atlas.Last);
        if (frameCount == 0)
        {
            diagnostic = "R6K GridAtlas rejected parameters that make current retail divide by a zero frame count.";
            return false;
        }

        var initialized=EnbCurrentRetailMapperAtlasRuntime.Initialize(atlas.FramesPerSecond,atlas.Log2Width,atlas.Last,atlas.Offset);
        direction=initialized.Direction;millisecondsPerFrame=initialized.MillisecondsPerFrame;
        cellSize=initialized.CellSize;log2Width=initialized.Log2Width;frameCount=initialized.FrameCount;
        currentFrame=initialized.CurrentFrame;
        if (millisecondsPerFrame == 0)
        {
            diagnostic = "R6K GridAtlas rejected FPS whose current-retail millisecond frame divisor truncates to zero.";
            return false;
        }


        return true;
    }

    private static EnbW3dMapperRuntimeEvaluation ScaleOnly(Vector2 scale)
        => Affine(scale, Vector2.Zero, Vector2.Zero);

    private static EnbW3dMapperRuntimeEvaluation Affine(Vector2 scale, Vector2 cross, Vector2 offset)
        => new(
            scale,
            cross,
            offset,
            new Vector4(1f, 0f, 0f, 1f),
            ConfiguresBumpEnvironment: false);

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool AllFinite(params float[] values)
        => values.All(float.IsFinite);
}

/// <summary>
/// Per-apply values consumed by the programmable backend. TextureScale contains
/// current-retail m00/m11, TextureCross contains m01/m10, and TextureOffset
/// contains m02/m12 from the COUNT2 texture matrix. BumpEnvironmentMatrix keeps
/// the/D3D state ordering (MAT00, MAT01, MAT10, MAT11).
/// </summary>
public readonly record struct EnbW3dMapperRuntimeEvaluation(
    Vector2 TextureScale,
    Vector2 TextureCross,
    Vector2 TextureOffset,
    Vector4 BumpEnvironmentMatrix,
    bool ConfiguresBumpEnvironment)
{
    public static EnbW3dMapperRuntimeEvaluation Identity => new(
        Vector2.One,
        Vector2.Zero,
        Vector2.Zero,
        new Vector4(1f, 0f, 0f, 1f),
        ConfiguresBumpEnvironment: false);

    public Vector4 TextureScaleOffset => new(
        TextureScale.X,
        TextureScale.Y,
        TextureOffset.X,
        TextureOffset.Y);

    // m01, m10 are the two off-diagonal terms needed by centered rotation.
    public Vector4 TextureCrossVector => new(TextureCross.X, TextureCross.Y, 0f, 0f);
}
