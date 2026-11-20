using System.Net.Sockets;
using EnbNext.Net7.Protocol;

namespace EnbNext.App;

// Player-facing outcomes of a login attempt, not a blanket exception filter.
internal readonly record struct LoginFailureFeedback(string Message, string Cue, bool Expected)
{
    internal static LoginFailureFeedback Report(Exception error, Action<string> transcript, Action<string> issue)
    {
        var feedback = error switch
        {
            EnbNext.Net7.LocalData.Net7LocalDataException => new LoginFailureFeedback(error.Message, "Problem", true),
            Net7AuthenticationException => new LoginFailureFeedback("Your login name or password was not accepted. Please check them and try again.", "RejectedOnce", true),
            TimeoutException => new("The server is not responding. It may be offline or busy. Please try again shortly.", "Problem", true),
            SocketException => new("Unable to reach the server. Please check your connection and try again shortly.", "Problem", true),
            IOException { InnerException: SocketException } or EndOfStreamException => new("The connection to the server was lost. Please try logging in again.", "Problem", true),
            Net7ServerStartingException => new("The server is starting up. Please try again shortly.", "Problem", true),
            Net7ServerShutdownException => new("The server has closed the connection. Please try again later.", "Problem", true),
            Net7IncompatibleVersionException => new("The server requires a different client version. Please check for an EnbNext update.", "Problem", true),
            _ => new("Something unexpected prevented login. Details have been recorded in Session Issues.", "Problem", false)
        };
        if (feedback.Expected) transcript($"Login did not complete: {error}");
        else issue($"LOGIN ERROR: {error}");
        return feedback;
    }
}
