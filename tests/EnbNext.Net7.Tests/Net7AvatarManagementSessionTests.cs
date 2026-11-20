using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7AvatarManagementSessionTests
{
    [Fact]
    public async Task Create_SendsOnceRoutesOnly0070AndPublishesRosterAtomically()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var rosterUpdated = new TaskCompletionSource<Net7AvatarRoster>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RosterUpdated += roster => rosterUpdated.TrySetResult(roster);

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Packet create = await ReadPacketAsync(stream);
            Assert.Equal(0x200B, create.Header.Opcode);
            Assert.Equal(0x21B, create.Payload.Length);

            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x1001, 0, 76, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
                masterClient);
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=stale\n"));
            await WritePacketAsync(stream, 0x006F, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 1));
            await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 2));
            await WritePacketFragmentedAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "REFRESHED"));
        });

        await session.LoginAsync("typed-name", "password");
        Net7AvatarCreateResult result = await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));

        Assert.Equal(1, result.TargetSlot);
        Assert.Equal("REFRESHED", result.Roster.Slots[0].Avatar.Skeleton);
        Assert.Same(result.Roster, session.Roster);
        Assert.Same(result.Roster, await rosterUpdated.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Create_UsesTicketDerivedAccountIdentityInFixedUsernameField()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream, "ticketName-suffix");
            Packet create = await ReadPacketAsync(stream);
            Assert.Equal(0x200B, create.Header.Opcode);
            Assert.Equal("ticketName", Encoding.Latin1.GetString(create.Payload.Span.Slice(0x00C, 10)));
            Assert.Equal(0, create.Payload.Span[0x016]);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster());
        });

        await session.LoginAsync("typedName", "password");
        await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
        await server;
    }

    [Fact]
    public async Task Create_TimeoutIsOneSendCleanFailureAndFailsClosedAgainstLateRoster()
    {
        await using var harness = await ManagementHarness.CreateAsync(TimeSpan.FromMilliseconds(80));
        await using var session = new Net7Session(harness.Options);
        int sends = 0;
        session.TcpPacketSent += p => { if (p.Header.Opcode == 0x200B) Interlocked.Increment(ref sends); };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            await Task.Delay(160);
            try { await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "LATE")); }
            catch (IOException) { }
        });

        await session.LoginAsync("pilot", "password");
        await Assert.ThrowsAsync<TimeoutException>(() => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
        await server;
    }

    [Fact]
    public async Task Create_DisconnectAfterOneSendIsCleanLocalFailure()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        int sends = 0;
        session.TcpPacketSent += p => { if (p.Header.Opcode == 0x200B) Interlocked.Increment(ref sends); };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            // Returning closes the loopback account stream without a response.
        });

        await session.LoginAsync("pilot", "password");
        await Assert.ThrowsAsync<EndOfStreamException>(() => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_RosterIgnoresUnusedTailAndConsumesAnIncompleteRosterWithoutEffect(bool oversize)
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            byte[] malformed = oversize
                ? [.. TestProtocolFixtures.BuildRoster(), 0xFF]
                : new byte[17];
            await WritePacketAsync(stream, 0x0070, malformed);
        });

        Net7AvatarRoster original = await session.LoginAsync("pilot", "password");
        if (oversize)
        {
            Net7AvatarCreateResult result = await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
            Assert.Same(result.Roster, session.Roster);
            Assert.NotSame(original, session.Roster);
            Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        }
        else
        {
            // The undecodable roster is consumed without effect (safety rule);
            // the create then ends only because the server closes the link.
            await Assert.ThrowsAnyAsync<IOException>(() => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
            Assert.Same(original, session.Roster);
            Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        }
        await server;
    }

    [Fact]
    public async Task Create_GlobalErrorFailsTransactionIndependentlyThenLateRosterResynchronizes()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var rosterResync = new TaskCompletionSource<Net7AvatarRoster>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RosterUpdated += roster =>
        {
            if (roster.Slots[0].Avatar.Skeleton == "AFTER_ERROR")
                rosterResync.TrySetResult(roster);
        };

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);

            // Generic direct framing is distinct from the exact fixed-message
            // account-TCP compatibility form and does not own this rejection.
            await WritePacketAsync(stream, 0x0075,
                Net7GlobalError.EncodeDirect0075(0, 12, "non-account response"));

            byte[] selector = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(selector, 7);
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x200E, 0, 77, selector).ToWireBytes(), masterClient);

            await allowResync.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "AFTER_ERROR"));

            Packet delete = await ReadPacketAsync(stream);
            Assert.Equal(0x200D, delete.Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "AFTER_DELETE"));
        });

        await session.LoginAsync("pilot", "password");
        Net7GlobalErrorException error = await Assert.ThrowsAsync<Net7GlobalErrorException>(
            () => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
        Assert.Equal(Net7GlobalErrorIngress.MasterControlUdpOuter, error.Error.Ingress);
        Assert.Equal(Net7GlobalErrorSessionContext.AvatarManagement, error.Error.SessionContext);
        Assert.Equal(14u, error.Error.Code);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);

        allowResync.TrySetResult(true);
        await rosterResync.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Net7AvatarDeleteResult deletion = await session.DeleteCharacterAsync(0);
        Assert.True(deletion.ServerRequestSent);
        Assert.Equal("AFTER_DELETE", deletion.Roster.Slots[0].Avatar.Skeleton);
        await server;
    }

    [Theory]
    [InlineData(1u, 8u)]
    [InlineData(5u, 12u)]
    [InlineData(10u, 17u)]
    public async Task Create_RecoverableOuterErrorAllowsCorrectedRetryWithoutRoster(uint selector, uint code)
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0075,
                Net7GlobalError.EncodeDirect0075(0, code, "role-10 non-response"));
            byte[] selectorPayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(selectorPayload, selector);
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x200E, 0, 77, selectorPayload).ToWireBytes(), masterClient);

            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070,
                TestProtocolFixtures.BuildRoster(slot0Skeleton: "RETRIED_CREATE"));
        });

        await session.LoginAsync("pilot", "password");
        Net7GlobalErrorException rejected = await Assert.ThrowsAsync<Net7GlobalErrorException>(
            () => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
        Assert.Equal(Net7GlobalErrorIngress.MasterControlUdpOuter, rejected.Error.Ingress);
        Assert.Equal(code, rejected.Error.Code);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);

        Net7AvatarCreateResult retried = await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
        Assert.Equal("RETRIED_CREATE", retried.Roster.Slots[0].Avatar.Skeleton);
        await server;
    }

    [Theory]
    [InlineData(0u, 7u, false)]
    [InlineData(1u, 8u, true)]
    [InlineData(2u, 9u, true)]
    [InlineData(3u, 10u, true)]
    [InlineData(4u, 11u, true)]
    [InlineData(5u, 12u, true)]
    [InlineData(6u, 13u, true)]
    [InlineData(7u, 14u, false)]
    [InlineData(8u, 15u, false)]
    [InlineData(9u, 16u, false)]
    [InlineData(10u, 17u, true)]
    [InlineData(11u, 18u, false)]
    public async Task Create_ExactAccountTcp0075FixedMessagesFollowRetailRecoveryClasses(
        uint selector, uint code, bool recoverable)
    {
        byte[] selectorPayload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(selectorPayload, selector);
        string message = Net7GlobalError.DecodeOuterUdp200E(selectorPayload).Message;
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var allowServerFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            byte[] text = Encoding.Latin1.GetBytes(message);
            byte[] payload = new byte[text.Length + 8];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)text.Length);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), code);
            text.CopyTo(payload, 8);
            await WritePacketAsync(stream, 0x0075, payload);

            if (recoverable)
            {
                Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
                await WritePacketAsync(stream, 0x0070,
                    TestProtocolFixtures.BuildRoster(slot0Skeleton: "RETRIED_TCP_ERROR"));
            }
            else
                await allowServerFinish.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await session.LoginAsync("pilot", "password");
        Net7GlobalErrorException rejected = await Assert.ThrowsAsync<Net7GlobalErrorException>(
            () => session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1)));
        Assert.Equal(Net7GlobalErrorSource.AccountTcp0075, rejected.Error.Source);
        Assert.Equal(Net7GlobalErrorIngress.ServerTcpRole10, rejected.Error.Ingress);
        Assert.Equal(Net7GlobalErrorSessionContext.AvatarManagement, rejected.Error.SessionContext);
        Assert.Equal(code, rejected.Error.Code);
        Assert.Equal(recoverable, rejected.Error.IsLoginTaskRecoverable);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);

        if (recoverable)
        {
            Net7AvatarCreateResult retried = await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
            Assert.Equal("RETRIED_TCP_ERROR", retried.Roster.Slots[0].Avatar.Skeleton);
        }
        allowServerFinish.TrySetResult(true);
        await server;
    }


    [Fact]
    public async Task Create_Direct0075WrongContextDoesNotCompleteOrPoisonRosterWaiter()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var errorEvent = new TaskCompletionSource<Net7GlobalError>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GlobalErrorReceived += error => errorEvent.TrySetResult(error);

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);

            byte[] direct = Net7GlobalError.EncodeDirect0075(0x01020304, 8, "wrong gameplay-side context");
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x0075, 0, 79, direct).ToWireBytes(),
                masterClient);

            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "CREATE_AFTER_DIRECT_ERROR"));
        });

        await session.LoginAsync("pilot", "password");
        Net7AvatarCreateResult created = await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
        Net7GlobalError observed = await errorEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("CREATE_AFTER_DIRECT_ERROR", created.Roster.Slots[0].Avatar.Skeleton);
        Assert.Equal(Net7GlobalErrorSource.Direct0075, observed.Source);
        Assert.Equal(Net7GlobalErrorIngress.MasterControlUdpOuter, observed.Ingress);
        Assert.Equal(Net7GlobalErrorSessionContext.CharacterSelection, observed.SessionContext);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Create_RosterThenLateGlobalErrorIsDeterministicAndNonFatal()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var errorEvent = new TaskCompletionSource<Net7GlobalError>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GlobalErrorReceived += error => errorEvent.TrySetResult(error);

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "CREATE_DONE"));
            await Task.Delay(20);

            byte[] selector = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(selector, 2);
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x200E, 0, 78, selector).ToWireBytes(), masterClient);
        });

        await session.LoginAsync("pilot", "password");
        Net7AvatarCreateResult created = await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
        Assert.Equal("CREATE_DONE", created.Roster.Slots[0].Avatar.Skeleton);

        Net7GlobalError late = await errorEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(9u, late.Code);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Delete_EmptySlotSuppressesServerRequest()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        int deleteSends = 0;
        session.TcpPacketSent += p => { if (p.Header.Opcode == 0x200D) Interlocked.Increment(ref deleteSends); };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream, roster: TestProtocolFixtures.BuildRoster(slot0Occupied: false));
            await Task.Delay(60);
        });

        Net7AvatarRoster roster = await session.LoginAsync("pilot", "password");
        Net7AvatarDeleteResult result = await session.DeleteCharacterAsync(0);
        Assert.False(result.ServerRequestSent);
        Assert.Same(roster, result.Roster);
        Assert.Equal(0, Volatile.Read(ref deleteSends));
        await server;
    }

    [Fact]
    public async Task Delete_NegativeShardSlotIsClearedLocallyWithoutSend()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        int deleteSends = 0;
        session.TcpPacketSent += p => { if (p.Header.Opcode == 0x200D) Interlocked.Increment(ref deleteSends); };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream, roster: TestProtocolFixtures.BuildRoster(slot0Shard: 0xFFFFFFFF));
            await Task.Delay(60);
        });

        await session.LoginAsync("pilot", "password");
        // Retail 007683F0 resets a negative-shard slot locally and sends no 0x0071.
        Net7AvatarDeleteResult result = await session.DeleteCharacterAsync(0);
        Assert.False(result.ServerRequestSent);
        Assert.True(result.Roster.Slots[0].IsEmpty);
        Assert.Same(result.Roster, session.Roster);
        Assert.Equal(0, Volatile.Read(ref deleteSends));
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Delete_RefreshedRosterAuthoritativelyRemovesTargetWhenServerSaysEmpty()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200D, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Occupied: false));
        });

        await session.LoginAsync("pilot", "password");
        Net7AvatarDeleteResult result = await session.DeleteCharacterAsync(0);

        Assert.True(result.ServerRequestSent);
        Assert.True(result.Roster.Slots[0].IsEmpty);
        Assert.Same(result.Roster, session.Roster);
        await server;
    }

    [Fact]
    public async Task Delete_UsesStableSlotTicketIdentityAndAcceptsUnchangedAuthoritativeRoster()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        byte[] unchanged = TestProtocolFixtures.BuildRoster(slot0Skeleton: "UNCHANGED");

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream, "ticketName-suffix", unchanged);
            Packet delete = await ReadPacketAsync(stream);
            Assert.Equal(0x200D, delete.Header.Opcode);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, delete.Payload.Span.Slice(0, 4).ToArray());
            Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(delete.Payload.Span.Slice(4, 2)));
            Assert.Equal("ticketName", Encoding.Latin1.GetString(delete.Payload.Span.Slice(6)));
            await WritePacketAsync(stream, 0x0070, unchanged);
        });

        await session.LoginAsync("typedName", "password");
        Net7AvatarDeleteResult result = await session.DeleteCharacterAsync(0);
        Assert.True(result.ServerRequestSent);
        Assert.False(result.Roster.Slots[0].IsEmpty);
        Assert.Equal("UNCHANGED", result.Roster.Slots[0].Avatar.Skeleton);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Delete_TimeoutSendsOnceAndDoesNotFabricateRosterSuccess()
    {
        await using var harness = await ManagementHarness.CreateAsync(TimeSpan.FromMilliseconds(80));
        await using var session = new Net7Session(harness.Options);
        int sends = 0;
        session.TcpPacketSent += p => { if (p.Header.Opcode == 0x200D) Interlocked.Increment(ref sends); };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200D, (await ReadPacketAsync(stream)).Header.Opcode);
            await Task.Delay(160);
        });

        Net7AvatarRoster original = await session.LoginAsync("pilot", "password");
        await Assert.ThrowsAsync<TimeoutException>(() => session.DeleteCharacterAsync(0));
        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.Same(original, session.Roster);
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        await server;
    }

    [Fact]
    public async Task Delete_GlobalErrorIsIndependentAndLateRosterResynchronizesAuthority()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        var rosterResync = new TaskCompletionSource<Net7AvatarRoster>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RosterUpdated += roster =>
        {
            if (roster.Slots[0].Avatar.Skeleton == "DELETE_RESYNC")
                rosterResync.TrySetResult(roster);
        };

        Task server = harness.RunServerAsync(async (stream, masterClient) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200D, (await ReadPacketAsync(stream)).Header.Opcode);

            byte[] selector = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(selector, 1);
            await harness.MasterUdp.SendAsync(
                new ServerUdpFrame(0x200E, 0, 79, selector).ToWireBytes(),
                masterClient);

            await Task.Delay(20);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "DELETE_RESYNC"));
        });

        Net7AvatarRoster original = await session.LoginAsync("pilot", "password");
        await Assert.ThrowsAsync<Net7GlobalErrorException>(() => session.DeleteCharacterAsync(0));
        Assert.Same(original, session.Roster);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);

        Net7AvatarRoster refreshed = await rosterResync.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(refreshed, session.Roster);
        await server;
    }

    [Fact]
    public async Task Delete_UnknownAndUnrelatedRole10PacketsCannotSatisfyRosterWaiter()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200D, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x7FFE, new byte[] { 1, 2, 3 });
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=nope\n"));
            await WritePacketAsync(stream, 0x006F, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 1));
            await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 2));
            await WritePacketAsync(stream, 0x0075,
                Net7GlobalError.EncodeDirect0075(0, 12, "unrelated name rejection"));
            await Task.Delay(20);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Occupied: false));
        });

        await session.LoginAsync("pilot", "password");
        Net7AvatarDeleteResult result = await session.DeleteCharacterAsync(0);
        Assert.True(result.ServerRequestSent);
        Assert.True(result.Roster.Slots[0].IsEmpty);
        await server;
    }

    [Fact]
    public async Task DuplicateRosterIsConsumedByPumpAndDoesNotDuplicatePriorRequest()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        int rosterEvents = 0;
        var duplicateObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RosterUpdated += _ =>
        {
            if (Interlocked.Increment(ref rosterEvents) >= 2)
                duplicateObserved.TrySetResult(true);
        };
        int createSends = 0;
        int deleteSends = 0;
        session.TcpPacketSent += p =>
        {
            if (p.Header.Opcode == 0x200B) Interlocked.Increment(ref createSends);
            if (p.Header.Opcode == 0x200D) Interlocked.Increment(ref deleteSends);
        };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200B, (await ReadPacketAsync(stream)).Header.Opcode);
            byte[] roster = TestProtocolFixtures.BuildRoster(slot0Skeleton: "DUP");
            await WritePacketAsync(stream, 0x0070, roster);
            await WritePacketAsync(stream, 0x0070, roster);

            Packet delete = await ReadPacketAsync(stream);
            Assert.Equal(0x200D, delete.Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Occupied: false));
        });

        await session.LoginAsync("pilot", "password");
        await session.CreateCharacterAsync(BuildCreateRequest(targetSlot: 1));
        await duplicateObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Net7AvatarDeleteResult deletion = await session.DeleteCharacterAsync(0);

        Assert.Equal(1, Volatile.Read(ref createSends));
        Assert.Equal(1, Volatile.Read(ref deleteSends));
        Assert.True(deletion.Roster.Slots[0].IsEmpty);
        await server;
    }

    [Fact]
    public async Task MalformedTicketIdentityCannotFallBackToTypedUsernameForDelete()
    {
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        int deleteSends = 0;
        session.TcpPacketSent += p => { if (p.Header.Opcode == 0x200D) Interlocked.Increment(ref deleteSends); };

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream, "---");
            await Task.Delay(40);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => session.LoginAsync("typedName", "password"));
        Assert.Null(session.AvatarLoginUsername);
        Assert.Null(session.Roster);
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteCharacterAsync(0));
        Assert.Equal(0, Volatile.Read(ref deleteSends));
        await server;
    }

    [Fact]
    public async Task MalformedRosterDuringDeleteIsConsumedAndTheNextRosterCompletesTheDelete()
    {
        // Safety rule: an undecodable 0x0070 is consumed without effect and the
        // transaction keeps waiting; the session is not faulted.
        await using var harness = await ManagementHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        Task server = harness.RunServerAsync(async (stream, _) =>
        {
            await CompleteLoginServerSideAsync(stream);
            Assert.Equal(0x200D, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070, new byte[17]);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster(slot0Skeleton: "AFTER_DELETE"));
        });

        Net7AvatarRoster original = await session.LoginAsync("pilot", "password");
        Net7AvatarDeleteResult result = await session.DeleteCharacterAsync(0);
        Assert.NotSame(original, session.Roster);
        Assert.Equal("AFTER_DELETE", result.Roster.Slots[0].Avatar.Skeleton);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        await server;
    }

    private static Net7AvatarCreateRequest BuildCreateRequest(int targetSlot)
    {
        Net7AvatarData avatar = Net7AvatarRosterCodec.Decode(
            TestProtocolFixtures.BuildRoster(slot0Skeleton: new string('S', 27))).Slots[0].Avatar;
        return new Net7AvatarCreateRequest(
            TargetShardId: 7,
            TargetSlot: targetSlot,
            Control: 1,
            Avatar: avatar,
            ShipAppearance: new Net7ShipAppearanceData(new byte[Net7ShipAppearanceData.SerializedSize]));
    }

    private static async Task CompleteLoginServerSideAsync(
        NetworkStream stream,
        string ticket = "pilot-token",
        byte[]? roster = null)
    {
        Assert.Equal(0x3002, (await ReadPacketAsync(stream)).Header.Opcode);
        Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
        Assert.Equal(0x2000, (await ReadPacketAsync(stream)).Header.Opcode);
        await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes($"Valid=TRUE\nTicket={ticket}\n"));
        Assert.Equal(0x2002, (await ReadPacketAsync(stream)).Header.Opcode);
        await WritePacketAsync(stream, 0x0070, roster ?? TestProtocolFixtures.BuildRoster());
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

    private static async Task WritePacketFragmentedAsync(NetworkStream stream, ushort opcode, ReadOnlyMemory<byte> payload)
    {
        byte[] wire = Packet.ForOpcode(opcode, payload).ToWireBytes();
        int[] chunks = [1, 2, 5, 11, 17];
        int offset = 0;
        foreach (int chunk in chunks)
        {
            if (offset == wire.Length)
                break;
            int take = Math.Min(chunk, wire.Length - offset);
            await stream.WriteAsync(wire.AsMemory(offset, take));
            offset += take;
        }
        if (offset < wire.Length)
            await stream.WriteAsync(wire.AsMemory(offset));
    }

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

    private sealed class ManagementHarness : IAsyncDisposable
    {
        private readonly TcpListener _accountListener;

        private ManagementHarness(UdpClient masterUdp, TcpListener accountListener, Net7ServerOptions options)
        {
            MasterUdp = masterUdp;
            _accountListener = accountListener;
            Options = options;
        }

        public UdpClient MasterUdp { get; }
        public Net7ServerOptions Options { get; }

        public static Task<ManagementHarness> CreateAsync(TimeSpan? accountTimeout = null)
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
                BootstrapAttemptTimeout = TimeSpan.FromMilliseconds(250),
                AccountResponseTimeout = accountTimeout ?? TimeSpan.FromMilliseconds(750),
            };
            return Task.FromResult(new ManagementHarness(master, tcp, options));
        }

        public async Task RunServerAsync(Func<NetworkStream, IPEndPoint, Task> accountScenario)
        {
            UdpReceiveResult bootstrapRequest = await MasterUdp.ReceiveAsync();
            ServerUdpFrame bootstrap = ServerUdpFrame.Parse(bootstrapRequest.Buffer);
            Assert.Equal(0x1000, bootstrap.Opcode);
            await MasterUdp.SendAsync(
                new ServerUdpFrame(0x1001, 0, 123, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
                bootstrapRequest.RemoteEndPoint);

            using TcpClient client = await _accountListener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            await accountScenario(stream, bootstrapRequest.RemoteEndPoint);
        }

        public ValueTask DisposeAsync()
        {
            _accountListener.Stop();
            MasterUdp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
