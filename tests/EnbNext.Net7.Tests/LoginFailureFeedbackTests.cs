using System.Net.Sockets;
using EnbNext.App;
using EnbNext.Engine;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class LoginFailureFeedbackTests
{
    public static IEnumerable<object[]> ExpectedFailures()
    {
        yield return [new Net7AuthenticationException(Net7AuthLoginResponse.Decode("Valid=FALSE\n"u8)), "RejectedOnce", "login name or password"];
        yield return [new Net7ServerStartingException(), "Problem", "starting up"];
        yield return [new Net7ServerShutdownException(), "Problem", "closed the connection"];
        yield return [Net7IncompatibleVersionException.FromPayload(new byte[]{1,0,0,0}), "Problem", "different client version"];
        // 0040BE70 reads a short frame's version from stale bytes; it stays terminal.
        yield return [Net7IncompatibleVersionException.FromPayload(new byte[]{1}), "Problem", "different client version"];
        yield return [new TimeoutException("bootstrap timeout"), "Problem", "not responding"];
        yield return [new SocketException((int)SocketError.HostNotFound), "Problem", "reach the server"];
        yield return [new SocketException((int)SocketError.ConnectionRefused), "Problem", "reach the server"];
        yield return [new IOException("network read",new SocketException((int)SocketError.ConnectionReset)), "Problem", "connection to the server was lost"];
        yield return [new EndOfStreamException("TCP closed"), "Problem", "connection to the server was lost"];
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public void ExpectedOutcomesKeepDetailsInTranscriptAndDoNotCreateIssue(Exception exception,string cue,string message)
    {
        var issues=new LiveIssueLog();var transcript=new List<string>();
        var feedback=LoginFailureFeedback.Report(exception,transcript.Add,issues.ObserveDiagnostic);
        Assert.True(feedback.Expected);Assert.Equal(cue,feedback.Cue);
        Assert.Contains(message,feedback.Message,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(exception.GetType().Name,feedback.Message);
        Assert.Contains(exception.ToString(),Assert.Single(transcript));
        Assert.Equal(0,issues.Count);Assert.Null(issues.ReportPath);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("asset")]
    [InlineData("bug")]
    public void UnexpectedFailuresStillCreateIssues(string kind)
    {
        Exception exception=kind switch
        {
            "protocol"=>new InvalidDataException("Malformed account response"),
            "asset"=>new IOException("Cannot read installed asset"),
            _=>new NullReferenceException("broken owner")
        };
        var issues=new LiveIssueLog();var transcript=new List<string>();
        var feedback=LoginFailureFeedback.Report(exception,transcript.Add,issues.ObserveDiagnostic);
        Assert.False(feedback.Expected);Assert.Empty(transcript);Assert.Equal(1,issues.Count);
        Assert.Contains(exception.ToString(),issues.Describe());
        Assert.Contains("Session Issues",feedback.Message);
    }

    [Theory]
    [InlineData("login/control fatal: Net7IncompatibleVersionException: Net7 rejected the client/proxy compatibility version") ]
    [InlineData("bootstrap send failed: SocketException: Network is unreachable")]
    [InlineData("master/control UDP receive failed; retrying: Connection reset")]
    public void ExpectedTransportRetriesDoNotCreateIssues(string message)
    {
        var issues=new LiveIssueLog();issues.ObserveDiagnostic(message);Assert.Equal(0,issues.Count);
        issues.ObserveDiagnostic("protocol error: malformed frame");Assert.Equal(1,issues.Count);
        issues.ObserveDiagnostic("bootstrap send failed: InvalidDataException: broken packet");Assert.Equal(2,issues.Count);
    }
}
