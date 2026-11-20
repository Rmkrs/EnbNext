using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7Full04DirectAuditTests
{
    [Fact]
    public async Task Outer200E_DuringGameplayIsSurfacedWithoutFaultingSession()
    {
        var session = new Net7Session(new Net7ServerOptions());
        try
        {
            MethodInfo setLoginState = typeof(Net7Session).GetMethod(
                "SetLoginState",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Net7Session.SetLoginState was not found.");
            MethodInfo routeGlobalError = typeof(Net7Session).GetMethod(
                "RouteGlobalError",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Net7Session.RouteGlobalError was not found.");

            setLoginState.Invoke(session, [Net7LoginState.SectorReady, null]);

            Net7GlobalError? observed = null;
            session.GlobalErrorReceived += error => observed = error;

            byte[] payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, 7);
            Net7GlobalError error = Net7GlobalError.DecodeOuterUdp200E(
                payload,
                Net7GlobalErrorIngress.RoutedUdpOuter);

            routeGlobalError.Invoke(session, [error]);

            Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
            Assert.NotNull(observed);
            Assert.Equal(Net7GlobalErrorSource.OuterUdp200E, observed!.Source);
            Assert.Equal(Net7GlobalErrorSessionContext.Gameplay, observed.SessionContext);
            Assert.Equal(14u, observed.Code);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
