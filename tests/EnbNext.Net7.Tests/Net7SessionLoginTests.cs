using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // retail/hardware verification, >=5 s; run with --filter Category=Heavy
[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7SessionLoginTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CharacterReturnAuthenticatesFreshConnectionBeforeSelectingAgain(bool accountClosed, bool rejected)
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x2004, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x006F, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 42));
            await finished.Task;
        });
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (accountClosed)
            await (Task)typeof(Net7Session).GetMethod("DisposeAccountConnectionAsync", flags)!.Invoke(session, null)!;
        typeof(Net7Session).GetField("_pendingLogoffDestination", flags)!.SetValue(session, EnbNext.Net7.Gameplay.Net7LogoffDestination.CharacterSelection);
        typeof(Net7Session).GetMethod("HandleLogoffConfirmation", flags)!.Invoke(session, [Packet.ForOpcode(0xBA, Array.Empty<byte>())]);
        Assert.Equal(0u, session.FixedSessionId);
        Task second = harness.RunAccountAsync(async stream =>
        {
            var start = await ReadPacketAsync(stream);
            Assert.Equal(0x3002, start.Header.Opcode);
            Assert.Equal(new byte[4], start.Payload.ToArray());
            Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
            var auth = await ReadPacketAsync(stream);
            Assert.Equal(0x2000, auth.Header.Opcode);
            Assert.Equal(BuildExpectedAccountPayload("pilot", "password"), auth.Payload.ToArray());
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes(rejected ? "Valid=FALSE\n" : "Valid=TRUE\nTicket=fresh-token\n"));
            if (rejected) return;
            var rosterRequest = await ReadPacketAsync(stream);
            Assert.Equal(0x2002, rosterRequest.Header.Opcode);
            Assert.Equal("fresh-token", Encoding.Latin1.GetString(rosterRequest.Payload.Span));
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "FRESH"));
            var selection = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, selection.Header.Opcode);
            Assert.Equal(BuildExpectedAvatarLoginPayload(0, "fresh"), selection.Payload.ToArray());
            await WritePacketAsync(stream, 0x006F, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 99));
            await finished.Task;
        });
        try
        {
            if (rejected)
            {
                await Assert.ThrowsAsync<Net7AuthenticationException>(() => session.RefreshCharacterRosterAsync("pilot", "password"));
                Assert.Null(session.Roster);
                Assert.Null(typeof(Net7Session).GetField("_account", flags)!.GetValue(session));
                Assert.Equal(Net7LoginState.Faulted, session.LoginState);
            }
            else
            {
                var roster = await session.RefreshCharacterRosterAsync("pilot", "password");
                Assert.Equal("FRESH", roster.Slots[0].Avatar.Skeleton);
                Assert.Equal(99u, (await session.SelectCharacterAsync(0)).FixedSessionId);
            }
        }
        finally { finished.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Login01BootstrapFailureRetiresMasterControlTransportBeforeFaulting()
    {
        using var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int masterPort = ((IPEndPoint)master.Client.LocalEndPoint!).Port;
        var options = new Net7ServerOptions
        {
            Host = "127.0.0.1",
            MasterControlUdpPort = masterPort,
            BootstrapAttempts = 1,
            BootstrapAttemptTimeout = TimeSpan.FromMilliseconds(20),
        };

        await using var session = new Net7Session(options);
        await Assert.ThrowsAsync<TimeoutException>(() => session.LoginAsync("pilot", "password"));

        UdpReceiveResult request = await master.ReceiveAsync();
        ServerUdpFrame bootstrap = ServerUdpFrame.Parse(request.Buffer);
        Assert.Equal(0x1000, bootstrap.Opcode);
        Assert.Equal(0u, bootstrap.FixedSessionId);
        Assert.Equal(0u, bootstrap.Sequence);
        Assert.Equal(new byte[] { 0xF6, 0, 0, 0 }, bootstrap.Payload.ToArray());
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);

        FieldInfo masterField = typeof(Net7Session).GetField("_master", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session._master was not found.");
        Assert.Null(masterField.GetValue(session));
    }

    [Theory]
    [InlineData(0x006F)]
    [InlineData(0x2009)]
    public async Task FullEnb02Flow_UsesCertifiedWireAndStopsBeforeMasterHandoff(ushort selectionResponseOpcode)
    {
        await using var harness = await LoginHarness.CreateAsync();
        var sentUdp = new List<(ushort Opcode, string Channel)>();
        await using var session = new Net7Session(harness.Options);
        session.UdpFrameSent += (frame, channel) => sentUdp.Add((frame.Opcode, channel));

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            Packet p3002 = await ReadPacketAsync(stream);
            Assert.Equal(0x3002, p3002.Header.Opcode);
            Assert.Equal(new byte[4], p3002.Payload.ToArray());

            Packet p2010 = await ReadPacketAsync(stream);
            Assert.Equal(0x2010, p2010.Header.Opcode);
            Assert.Equal(new byte[4], p2010.Payload.ToArray());

            Packet p2000 = await ReadPacketAsync(stream);
            Assert.Equal(0x2000, p2000.Header.Opcode);
            Assert.Equal(BuildExpectedAccountPayload("user%20name", "p%2Ba%20ss"), p2000.Payload.ToArray());
            await WritePacketAsync(stream, 0x7FFE, new byte[] { 1, 2, 3 });
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\r\nTicket=pilot-token-rest\r\n"));

            Packet p2002 = await ReadPacketAsync(stream);
            Assert.Equal(0x2002, p2002.Header.Opcode);
            Assert.Equal(Encoding.Latin1.GetBytes("pilot-token-rest"), p2002.Payload.ToArray());
            await WritePacketAsync(stream, 0x7FFE, new byte[] { 1, 2, 3 });
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "HUMAN_MALE"));

            Packet p2004 = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, p2004.Header.Opcode);
            Assert.Equal(BuildExpectedAvatarLoginPayload(0, "pilot"), p2004.Payload.ToArray());

            await WritePacketAsync(stream, 0x7FFF, Array.Empty<byte>());

            // A legal role-10 response owned by another transaction cannot satisfy
            // avatar selection and cannot be retained for a future request.
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=stale\n"));
            await WritePacketAsync(
                stream,
                selectionResponseOpcode,
                TestProtocolFixtures.BuildAvatarSelectionPayload(0x80000000, 0xAABBCCDD));
        });

        Net7AvatarRoster roster = await session.LoginAsync("user name", "p+a ss");
        Assert.Equal("HUMAN_MALE", roster.Slots[0].Avatar.Skeleton);
        Assert.Equal("pilot-token-rest", session.EaTicket);
        Assert.Equal("pilot", session.AvatarLoginUsername);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        Assert.Equal(0u, session.FixedSessionId);

        Net7CharacterSelectionResult selected = await session.SelectCharacterAsync(0);
        Assert.Equal(0xAABBCCDDu, selected.FixedSessionId);
        Assert.Equal(selectionResponseOpcode, selected.ResponseOpcode);
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
        Assert.Null(session.ActiveGameplaySectorPort);

        await server;

        // This emits only the bootstrap UDP request. No Master/routed
        // 0x200F/0x2008/0x2026 traffic is reachable from SelectCharacterAsync.
        Assert.Contains(sentUdp, x => x == (0x1000, "master/control"));
        Assert.DoesNotContain(sentUdp, x => x.Opcode is 0x200F or 0x2008 or 0x2026);
    }

    [Fact]
    public async Task ExplicitSelectionFailureRetainsAssignedIdentityAndRetryReplacesIt()
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);

            Packet first = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, first.Header.Opcode);
            await WritePacketAsync(stream, 0x006F, TestProtocolFixtures.BuildAvatarSelectionPayload(0xFFFFFFFF, 0x11223344));

            Packet second = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, second.Header.Opcode);
            await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0x55667788));
        });

        await session.LoginAsync("pilot", "password");
        await Assert.ThrowsAsync<Net7AvatarSelectionRejectedException>(() => session.SelectCharacterAsync(0));
        Assert.Equal(0x11223344u, session.FixedSessionId);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);

        Net7CharacterSelectionResult result = await session.SelectCharacterAsync(0);
        Assert.Equal(0x55667788u, result.FixedSessionId);
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Role10Opcode0075IsIgnoredAndCannotOwnSelectionResponse()
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Packet selection = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, selection.Header.Opcode);
            await WritePacketAsync(stream, 0x0075, Net7GlobalError.EncodeDirect0075(0, 12, "wrong context"));
            await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0x55667788));
        });

        await session.LoginAsync("pilot", "password");
        Net7CharacterSelectionResult selected = await session.SelectCharacterAsync(0);
        Assert.Equal(0x55667788u, selected.FixedSessionId);
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        await server;
    }

    [Fact]
    public async Task OuterUdp200EDuringSelectionOwnsFailureAndTcpWaitDoesNotSatisfy()
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var releaseServer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Packet selection = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, selection.Header.Opcode);

            byte[] selector = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(selector, 7);
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x200E, 0, 77, selector).ToWireBytes(),
                masterClient);

            // Outer UDP 0x200E is asynchronous and does not
            // satisfy the role-10 TCP response waiter. Keep the fake TCP peer
            // alive until the client has observed the UDP failure so this test
            // does not manufacture a competing EndOfStreamException.
            await releaseServer.Task;
        });

        await session.LoginAsync("pilot", "password");
        try
        {
            Net7GlobalErrorException ex = await Assert.ThrowsAsync<Net7GlobalErrorException>(() => session.SelectCharacterAsync(0));
            Assert.Equal(Net7GlobalErrorSource.OuterUdp200E, ex.Error.Source);
            Assert.Equal(Net7GlobalErrorIngress.MasterControlUdpOuter, ex.Error.Ingress);
            Assert.Equal(Net7GlobalErrorSessionContext.AvatarSelection, ex.Error.SessionContext);
            Assert.Equal(14u, ex.Error.Code);
            Assert.Equal(0u, session.FixedSessionId);
            Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        }
        finally
        {
            releaseServer.TrySetResult(true);
        }

        await server;
    }


    [Fact]
    public async Task DirectOuterUdp0075_IsNormalizedWithIngressAndCharacterSelectionContext()
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var sendError = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseServer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<Net7GlobalError>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GlobalErrorReceived += error => errorEvent.TrySetResult(error);

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            await sendError.Task.WaitAsync(TimeSpan.FromSeconds(2));

            byte[] payload = Net7GlobalError.EncodeDirect0075(0x11223344, 14, "Ticket failed");
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x0075, 0, 78, payload).ToWireBytes(),
                masterClient);

            await releaseServer.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await session.LoginAsync("pilot", "password");
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        sendError.TrySetResult(true);

        try
        {
            Net7GlobalError error = await errorEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Net7GlobalErrorSource.Direct0075, error.Source);
            Assert.Equal(Net7GlobalErrorIngress.MasterControlUdpOuter, error.Ingress);
            Assert.Equal(Net7GlobalErrorSessionContext.CharacterSelection, error.SessionContext);
            Assert.Equal(0x11223344u, error.OpaqueField0);
            Assert.Equal(14u, error.Code);
            Assert.Equal("Ticket failed", error.Message);
            Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        }
        finally
        {
            releaseServer.TrySetResult(true);
        }

        await server;
    }


    [Fact]
    public async Task DecodedInner0075_IsNormalizedWithoutBorrowingOuter200ESemantics()
    {
        await using var session = new Net7Session(new Net7ServerOptions());
        var errorEvent = new TaskCompletionSource<Net7GlobalError>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GlobalErrorReceived += error => errorEvent.TrySetResult(error);

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session.DispatchGameplayPacketAsync was not found.");

        byte[] payload = Net7GlobalError.EncodeDirect0075(0x11223344, 14, "Decoded error");
        Task dispatchTask = (Task)(dispatch.Invoke(
            session,
            [Packet.ForOpcode(0x0075, payload), "decoded-inner", CancellationToken.None])
            ?? throw new InvalidOperationException("Decoded-inner dispatch did not return a Task."));
        await dispatchTask;

        Net7GlobalError error = await errorEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Net7GlobalErrorSource.Direct0075, error.Source);
        Assert.Equal(Net7GlobalErrorIngress.DecodedInner, error.Ingress);
        Assert.Equal(0x11223344u, error.OpaqueField0);
        Assert.Equal(14u, error.Code);
        Assert.Equal("Decoded error", error.Message);
    }

    [Fact]
    public async Task ShortSelectionResponseIsIgnoredAndTheNextCompleteReplyCompletesSelection()
    {
        // Proxy 00414A60 would read +0x14 of a stale mailbox for a short reply;
        // the safety rule ignores the undecodable packet and keeps waiting.
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x2004, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x006F, new byte[0x17]);
            await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0x11223344));
        });

        await session.LoginAsync("pilot", "password");
        Net7CharacterSelectionResult result = await session.SelectCharacterAsync(0);
        Assert.Equal(0x11223344u, result.FixedSessionId);
        Assert.Equal(0x2009, result.ResponseOpcode);
        await server;
    }

    [Fact]
    public async Task SelectionTimeoutFailsSessionClosedAndCannotBeRetriedAgainstLateReply()
    {
        await using var harness = await LoginHarness.CreateAsync(accountTimeout: TimeSpan.FromSeconds(1));
        await using var session = new Net7Session(harness.Options);
        var releaseLateReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x2004, (await ReadPacketAsync(stream)).Header.Opcode);
            await releaseLateReply.Task;
            try
            {
                await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0x11223344));
            }
            catch (IOException)
            {
                // Client teardown after fail-closed timeout is acceptable.
            }
        });

        await session.LoginAsync("pilot", "password");
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => session.SelectCharacterAsync(0));
            Assert.Equal(0u, session.FixedSessionId);
            Assert.Equal(Net7LoginState.Faulted, session.LoginState);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SelectCharacterAsync(0));
        }
        finally { releaseLateReply.TrySetResult(); }
        await server;
    }

    [Fact]
    public async Task MissingUsableRouteFailsBeforeAny2004IsSent()
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        int selectionPackets = 0;
        session.TcpPacketSent += packet =>
        {
            if (packet.Header.Opcode == 0x2004)
                Interlocked.Increment(ref selectionPackets);
        };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            Assert.Equal(0x3002, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2000, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=pilot-token\n"));
            Assert.Equal(0x2002, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(routeGate: 0));
        });

        await session.LoginAsync("pilot", "password");
        await server;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SelectCharacterAsync(0));
        Assert.Equal(0, Volatile.Read(ref selectionPackets));
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        Assert.Equal(0u, session.FixedSessionId);
    }

    [Fact]
    public async Task AccountValidParserRejectsNonExactTrueBeforeRosterRequest()
    {
        await using var harness = await LoginHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            Assert.Equal(0x3002, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2000, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=true\nTicket=must-not-be-used\n"));
        });

        Net7AuthenticationException ex = await Assert.ThrowsAsync<Net7AuthenticationException>(() =>
            session.LoginAsync("pilot", "password"));
        Assert.False(ex.Response.IsValid);
        Assert.Null(session.Roster);
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        await server;
    }

    private static async Task CompleteLoginServerSideAsync(NetworkStream stream)
    {
        Assert.Equal(0x3002, (await ReadPacketAsync(stream)).Header.Opcode);
        Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
        Assert.Equal(0x2000, (await ReadPacketAsync(stream)).Header.Opcode);
        await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=pilot-token\n"));
        Assert.Equal(0x2002, (await ReadPacketAsync(stream)).Header.Opcode);
        await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster());
    }

    private static byte[] BuildExpectedAccountPayload(string escapedUser, string escapedPassword)
    {
        byte[] user = Encoding.ASCII.GetBytes(escapedUser);
        byte[] pass = Encoding.ASCII.GetBytes(escapedPassword);
        byte[] payload = new byte[4 + user.Length + pass.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)user.Length));
        user.CopyTo(payload, 2);
        int passOffset = 2 + user.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(passOffset, 2), checked((ushort)pass.Length));
        pass.CopyTo(payload, passOffset + 2);
        return payload;
    }

    private static byte[] BuildExpectedAvatarLoginPayload(int slot, string identity)
    {
        byte[] user = Encoding.Latin1.GetBytes(identity);
        byte[] payload = new byte[6 + user.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), checked((uint)slot));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)user.Length));
        user.CopyTo(payload, 6);
        return payload;
    }

    private static async Task<Packet> ReadPacketAsync(NetworkStream stream)
    {
        byte[] headerBytes = new byte[PacketHeader.WireSize];
        await ReadExactAsync(stream, headerBytes);
        PacketHeader header = PacketHeader.Read(headerBytes);
        if (header.Size < PacketHeader.WireSize)
            throw new InvalidDataException("Server fixture received invalid packet length.");
        byte[] payload = new byte[header.PayloadLength];
        await ReadExactAsync(stream, payload);
        return new Packet(header, payload);
    }

    private static Task WritePacketAsync(NetworkStream stream, ushort opcode, ReadOnlyMemory<byte> payload)
        => stream.WriteAsync(Packet.ForOpcode(opcode, payload).ToWireBytes()).AsTask();

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..]);
            if (read == 0)
                throw new EndOfStreamException("Loopback account client disconnected unexpectedly.");
            offset += read;
        }
    }

    private sealed class LoginHarness : IAsyncDisposable
    {
        private readonly TcpListener _accountListener;

        private LoginHarness(UdpClient masterUdp, TcpListener accountListener, Net7ServerOptions options)
        {
            MasterUdp = masterUdp;
            _accountListener = accountListener;
            Options = options;
        }

        public UdpClient MasterUdp { get; }
        public Net7ServerOptions Options { get; }

        public static Task<LoginHarness> CreateAsync(TimeSpan? accountTimeout = null)
        {
            var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int masterPort = ((IPEndPoint)master.Client.LocalEndPoint!).Port;
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int accountPort = ((IPEndPoint)tcp.LocalEndpoint).Port;

            var options = new Net7ServerOptions
            {
                Host = "127.0.0.1",
                MasterControlUdpPort = masterPort,
                AccountTcpPort = accountPort,
                MasterHandoffUdpPort = 3808,
                BootstrapAttempts = 2,
                //These scenarios test response ownership, not scheduler latency.
                //Full native-corpus runs can delay the in-process fake peer;
                //timeout-specific tests supply their own short deadline.
                BootstrapAttemptTimeout = TimeSpan.FromSeconds(10),
                AccountResponseTimeout = accountTimeout ?? TimeSpan.FromSeconds(10),
            };
            return Task.FromResult(new LoginHarness(master, tcp, options));
        }

        public async Task RunServerAsync(Func<NetworkStream, IPEndPoint, Task> accountScenario)
        {
            UdpReceiveResult bootstrapRequest = await MasterUdp.ReceiveAsync();
            ServerUdpFrame bootstrap = ServerUdpFrame.Parse(bootstrapRequest.Buffer);
            Assert.Equal(0x1000, bootstrap.Opcode);
            Assert.Equal(0u, bootstrap.FixedSessionId);
            Assert.Equal(0u, bootstrap.Sequence);
            await MasterUdp.SendAsync(
                new ServerUdpFrame(0x1001, 0, 123, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
                bootstrapRequest.RemoteEndPoint);

            using TcpClient client = await _accountListener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            await accountScenario(stream, bootstrapRequest.RemoteEndPoint);
        }

        public async Task RunAccountAsync(Func<NetworkStream, Task> scenario)
        {
            using var client = await _accountListener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await scenario(stream);
        }

        public ValueTask DisposeAsync()
        {
            _accountListener.Stop();
            MasterUdp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
