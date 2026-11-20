using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailSpeechAdmissionTests
{
    [Theory]
    // 0043F2D0: no active speech starts; the same file is ignored; a strictly higher
    // priority interrupts; otherwise speech with under 2000 ms left queues unless an
    // equal-or-higher request is already queued; anything else is dropped.
    [InlineData("a", 1f, false, null, 0f, 0u, 0u, null, EnbRetailSpeechDecision.Start)]
    [InlineData("a", 9f, true, "a", 1f, 5000u, 0u, null, EnbRetailSpeechDecision.Reject)]
    [InlineData("b", 2f, true, "a", 1f, 5000u, 0u, null, EnbRetailSpeechDecision.Interrupt)]
    [InlineData("b", 1f, true, "a", 1f, 5000u, 3001u, null, EnbRetailSpeechDecision.Queue)]
    [InlineData("b", 1f, true, "a", 1f, 5000u, 3000u, null, EnbRetailSpeechDecision.Reject)]
    [InlineData("b", 1f, true, "a", 1f, 5000u, 4000u, 1f, EnbRetailSpeechDecision.Reject)]
    [InlineData("b", 2f, true, "a", 3f, 5000u, 4000u, 1f, EnbRetailSpeechDecision.Queue)]
    public void AdmissionFollowsRetailSpeechRequest(string name, float priority, bool active, string? current,
        float currentPriority, uint duration, uint elapsed, float? queued, EnbRetailSpeechDecision expected)
        => Assert.Equal(expected, EnbRetailSpeechAdmission.Decide(name, priority, active, current, currentPriority, duration, elapsed, queued));
}
