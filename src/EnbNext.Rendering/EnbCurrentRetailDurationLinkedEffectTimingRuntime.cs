using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail pre-construction timing program used by wire effect family
/// 0x000E when DurationLinkedEffectDescID is non-negative. The selected
/// effect.ini definitions are timing references only; they are not instantiated
/// as generic runtime effect nodes by this traversal.
/// </summary>
public static class EnbCurrentRetailDurationLinkedEffectTimingRuntime
{
    public const int DefaultTraversalLimit = 256;

    public static EnbCurrentRetailDurationLinkedEffectTimingResolution Resolve(
        EnbRetailPresentationCatalog catalog,
        int selector,
        float packetSpeedup,
        int traversalLimit = DefaultTraversalLimit)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (selector < 0)
            return Failure(selector, EnbCurrentRetailDurationLinkedEffectTimingFailure.NegativeSelector);
        if (traversalLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(traversalLimit));
        if (!float.IsFinite(packetSpeedup))
            return Failure(selector, EnbCurrentRetailDurationLinkedEffectTimingFailure.NonFiniteSpeedup);

        // CreateEffectFromDefinition asserts on speedup <= 0.0f and then stores
        // 1.0f. It also stores 1.0f / speedup in a float local. LineEffect timing
        // paths use that reciprocal, so a larger packet Speedup shortens time.
        float normalizedSpeedup = packetSpeedup <= 0f ? 1f : packetSpeedup;
        float reciprocalSpeedup = 1f / normalizedSpeedup;
        if (!float.IsFinite(reciprocalSpeedup) || reciprocalSpeedup <= 0f)
            return Failure(selector, EnbCurrentRetailDurationLinkedEffectTimingFailure.NonFiniteSpeedup);

        var visited = new HashSet<int>();
        var path = new List<int>();
        float offsetSeconds = 0f;
        int currentSelector = selector;

        for (int step = 0; step < traversalLimit; step++)
        {
            if (!visited.Add(currentSelector))
                return Failure(
                    selector,
                    EnbCurrentRetailDurationLinkedEffectTimingFailure.CycleDetected,
                    path,
                    currentSelector,
                    reciprocalSpeedup);

            EnbEffectDefinition? definition = catalog.FindEffect(currentSelector);
            path.Add(currentSelector);

            // The retail selector loop performs keyed INI reads directly and does
            // not first require that EFFECT-N exists. A missing section therefore
            // contributes the getter defaults: zero StartDelay, both link flags
            // clear, unknown EffectClass, and zero Duration. The terminal zero
            // Duration then takes retail's diagnostic 1.0-second fallback below.

            // Unlike the actual root's StartDelay, the linked-selector loop does
            // not clamp this value before adding it to its float accumulator.
            if (!TryAddSeconds(ref offsetSeconds, definition?.StartDelay ?? 0f))
                return Failure(
                    selector,
                    EnbCurrentRetailDurationLinkedEffectTimingFailure.NonFiniteTiming,
                    path,
                    currentSelector,
                    reciprocalSpeedup);

            // Retail checks StartLink first and jumps back immediately when it is
            // enabled and has a non-negative selector. NextLink is only considered
            // when that StartLink branch is not taken.
            if (definition?.DurationLinkedEffectUseStartLink == true &&
                definition.StartLinkId is int startLink && startLink >= 0)
            {
                if (!TryAddSeconds(ref offsetSeconds, definition.StartLinkDelay ?? 0f))
                    return Failure(
                        selector,
                        EnbCurrentRetailDurationLinkedEffectTimingFailure.NonFiniteTiming,
                        path,
                        currentSelector,
                        reciprocalSpeedup);
                currentSelector = startLink;
                continue;
            }

            if (definition?.DurationLinkedEffectUseNextLink == true &&
                definition.NextLinkId is int nextLink && nextLink >= 0)
            {
                float durationSeconds = definition.DurationValue ?? 0f;
                if (definition.EffectClassKind == EnbEffectClassKind.LineEffect)
                    durationSeconds *= reciprocalSpeedup;
                if (!TryAddSeconds(ref offsetSeconds, durationSeconds))
                    return Failure(
                        selector,
                        EnbCurrentRetailDurationLinkedEffectTimingFailure.NonFiniteTiming,
                        path,
                        currentSelector,
                        reciprocalSpeedup);
                currentSelector = nextLink;
                continue;
            }

            float terminalDurationSeconds = definition?.DurationValue ?? 0f;
            bool usedRetailFallbackDuration = false;
            if (!float.IsFinite(terminalDurationSeconds))
                return Failure(
                    selector,
                    EnbCurrentRetailDurationLinkedEffectTimingFailure.NonFiniteTiming,
                    path,
                    currentSelector,
                    reciprocalSpeedup);
            if (terminalDurationSeconds <= 0f)
            {
                // Current retail reports/asserts this malformed authored case and
                // then loads its exact 1.0f fallback constant before conversion.
                terminalDurationSeconds = 1.0f;
                usedRetailFallbackDuration = true;
            }

            if (!TryTruncateMilliseconds(terminalDurationSeconds, out int durationMilliseconds))
            {
                return Failure(
                    selector,
                    EnbCurrentRetailDurationLinkedEffectTimingFailure.DurationOutOfRange,
                    path,
                    currentSelector,
                    reciprocalSpeedup);
            }

            // Retail first truncates terminal Duration * 1000.0 to an integer.
            // Only then, for LineEffect, it converts that integer back through
            // x87, multiplies by reciprocal speedup, and truncates again.
            if (definition?.EffectClassKind == EnbEffectClassKind.LineEffect)
            {
                if (!TryTruncateDouble((double)durationMilliseconds * reciprocalSpeedup, out durationMilliseconds))
                {
                    return Failure(
                        selector,
                        EnbCurrentRetailDurationLinkedEffectTimingFailure.DurationOutOfRange,
                        path,
                        currentSelector,
                        reciprocalSpeedup);
                }
            }

            if (!TryTruncateMilliseconds(offsetSeconds, out int startOffsetMilliseconds))
            {
                return Failure(
                    selector,
                    EnbCurrentRetailDurationLinkedEffectTimingFailure.StartOffsetOutOfRange,
                    path,
                    currentSelector,
                    reciprocalSpeedup);
            }

            return new EnbCurrentRetailDurationLinkedEffectTimingResolution(
                Success: true,
                InitialSelector: selector,
                TerminalSelector: currentSelector,
                AdditionalStartOffsetMilliseconds: startOffsetMilliseconds,
                DurationMilliseconds: durationMilliseconds,
                ReciprocalSpeedup: reciprocalSpeedup,
                TraversedDefinitionIds: path.ToArray(),
                UsedRetailFallbackDuration: usedRetailFallbackDuration,
                Failure: EnbCurrentRetailDurationLinkedEffectTimingFailure.None,
                FailureSelector: null);
        }

        return Failure(
            selector,
            EnbCurrentRetailDurationLinkedEffectTimingFailure.TraversalLimitReached,
            path,
            currentSelector,
            reciprocalSpeedup);
    }

    private static bool TryAddSeconds(ref float accumulator, float value)
    {
        if (!float.IsFinite(value))
            return false;
        accumulator += value;
        return float.IsFinite(accumulator);
    }

    private static bool TryTruncateMilliseconds(float seconds, out int milliseconds)
        => TryTruncateDouble((double)seconds * 1000.0, out milliseconds);

    private static bool TryTruncateDouble(double value, out int result)
    {
        result = 0;
        if (!double.IsFinite(value) || value > int.MaxValue || value < int.MinValue)
            return false;
        result = (int)value;
        return true;
    }

    private static EnbCurrentRetailDurationLinkedEffectTimingResolution Failure(
        int initialSelector,
        EnbCurrentRetailDurationLinkedEffectTimingFailure failure,
        IReadOnlyList<int>? path = null,
        int? failureSelector = null,
        float reciprocalSpeedup = 1f)
        => new(
            Success: false,
            InitialSelector: initialSelector,
            TerminalSelector: null,
            AdditionalStartOffsetMilliseconds: 0,
            DurationMilliseconds: 0,
            ReciprocalSpeedup: reciprocalSpeedup,
            TraversedDefinitionIds: path?.ToArray() ?? [],
            UsedRetailFallbackDuration: false,
            Failure: failure,
            FailureSelector: failureSelector);
}

public enum EnbCurrentRetailDurationLinkedEffectTimingFailure
{
    None,
    NegativeSelector,
    NonFiniteSpeedup,
    CycleDetected,
    TraversalLimitReached,
    NonFiniteTiming,
    StartOffsetOutOfRange,
    DurationOutOfRange,
}

public sealed record EnbCurrentRetailDurationLinkedEffectTimingResolution(
    bool Success,
    int InitialSelector,
    int? TerminalSelector,
    int AdditionalStartOffsetMilliseconds,
    int DurationMilliseconds,
    float ReciprocalSpeedup,
    IReadOnlyList<int> TraversedDefinitionIds,
    bool UsedRetailFallbackDuration,
    EnbCurrentRetailDurationLinkedEffectTimingFailure Failure,
    int? FailureSelector);
