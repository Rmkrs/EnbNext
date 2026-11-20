using System.Buffers.Binary;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7;

public sealed partial class Net7Session
{
    // Current proxy 00404490 suppresses static durations 32100..32200.
    // 0040461E..65 stores (duration-32100)*250 as the receipt delay and
    // adds it to the server stamp. 004049F0 -> 00402C60 replays mask3,
    // without a duration or scalar; optional shorts reconstruct at 1/8.
    internal static (Packet Packet, int Delay)? StaticEffectReplay(Packet packet)
    {
        if(packet.Header.Opcode!=9)return null;
        var p=packet.Payload.Span;
        if(p.Length<13 || (p[0]&4)==0)return null;
        int cursor=11;
        uint stamp=0;
        if((p[0]&2)!=0){stamp=BinaryPrimitives.ReadUInt32LittleEndian(p[cursor..]);cursor+=4;}
        short duration=BinaryPrimitives.ReadInt16LittleEndian(p[cursor..]);cursor+=2;
        if(duration is <32100 or >32200)return null;
        int delay=(duration-32100)*250;
        if((p[0]&8)!=0)cursor+=4;
        short first=0,second=0;
        if((p[0]&16)!=0){first=unchecked((short)(long)Math.Truncate(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p[cursor..]))));cursor+=4;}
        if((p[0]&32)!=0)second=unchecked((short)(long)Math.Truncate(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p[cursor..]))));
        byte[] replay=new byte[15+(first!=0?4:0)+(second!=0?4:0)];
        replay[0]=(byte)(3|(first!=0?16:0)|(second!=0?32:0));
        p.Slice(1,10).CopyTo(replay.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(replay.AsSpan(11),unchecked(stamp+(uint)delay));
        cursor=15;
        if(first!=0){BinaryPrimitives.WriteInt32LittleEndian(replay.AsSpan(cursor),BitConverter.SingleToInt32Bits(first*.125f));cursor+=4;}
        if(second!=0)BinaryPrimitives.WriteInt32LittleEndian(replay.AsSpan(cursor),BitConverter.SingleToInt32Bits(second*.125f));
        return (Packet.ForOpcode(9,replay),delay);
    }

    private bool ScheduleStaticEffectReplay(Packet packet,CancellationToken cancellationToken)
    {
        if(StaticEffectReplay(packet) is not {} replay)return false;
        int generation=Volatile.Read(ref _gameplayGeneration);
        StartOwnedOneShot(async()=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(_gameplayLifetimeCts.Token,cancellationToken);
            try
            {
                // Native due comparison is strict (00404A4E/51); first integer
                // receipt tick after the delay is eligible, even for delay0.
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(replay.Delay+1),linked.Token).ConfigureAwait(false);
                // 00404A5D/64 retains the slot while no client target exists.
                await _transportState.WaitForGameplayForwardingAsync(linked.Token).ConfigureAwait(false);
                if(generation!=Volatile.Read(ref _gameplayGeneration))return;
                // SendStaticEffectPacket 00402CD1..E7 fills a zero stamp with
                // the proxy clock, also used by existing proxy extensions.
                if(BinaryPrimitives.ReadUInt32LittleEndian(replay.Packet.Payload.Span[11..])==0)
                {
                    byte[] payload=replay.Packet.Payload.ToArray();
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(11),_clock.GetMilliseconds());
                    replay=(Packet.ForOpcode(9,payload),replay.Delay);
                }
                await DispatchGameplayPacketAsync(replay.Packet,"proxy static replay",linked.Token).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(linked.IsCancellationRequested) { }
        });
        return true;
    }

    // Current proxy 00404490 / 004046B0 / 004049F0: finite ordinary
    // effects also receive a synthetic RemoveEffect. Native phase expiry
    // alone does not retire infinite StartLink/NextLink descendants.
    private void ScheduleOrdinaryEffectRemoval(Packet packet, CancellationToken cancellationToken)
    {
        if (packet.Header.Opcode is not (0x0009 or 0x000B)) return;
        var payload = packet.Payload.Span;
        // The proxy reads these bytes from its receive buffer without a length
        // check; bytes past this packet are undefined there, so a packet too short
        // for the effect id schedules nothing (safety rule: stop at undefined reads).
        int required = packet.Header.Opcode == 0x0009 ? 11 : 17;
        if (payload.Length < required) return;
        int idOffset;
        if (packet.Header.Opcode == 0x0009)
        {
            if ((payload[0] & 1) == 0) return;
            idOffset = 7;
        }
        else
        {
            // The proxy deliberately ignores named binary effects.
            if (payload[12] != 0 || (payload[0] & 1) == 0) return;
            idOffset = 13;
        }
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[idOffset..]);
        lock (_worldStateSync)
        {
            if (id == 0 || !Gameplay.Effects.TryGetValue(id, out var effect) ||
                OrdinaryEffectRemovalDelay(effect) is not int delay) return;
            int sectorGeneration = Volatile.Read(ref _gameplayGeneration);
            StartOwnedOneShot(() => RemoveOrdinaryEffectAsync(effect, delay, sectorGeneration, cancellationToken));
        }
    }

    internal static int? OrdinaryEffectRemovalDelay(Net7EffectState effect)
    {
        if (effect.DurationMilliseconds <= 0) return null;
        if (!effect.IsObjectToObject)
        {
            // Delayed sentinel replay is a separate proxy translation, not an
            // ordinary 32-second visual lifetime.
            if (effect.DurationMilliseconds is >= 32100 and <= 32200) return null;
            return effect.DurationMilliseconds + 250;
        }
        if (effect.BoneName.Length != 0 || effect.EffectOffset.Z == -1 ||
            effect.EffectOffset.Z > 0 || !float.IsFinite(effect.EffectOffset.Z) ||
            ((effect.Mask & 0x0800) != 0 && effect.EffectScalar == 0)) return null;
        return effect.DurationMilliseconds;
    }

    private async Task RemoveOrdinaryEffectAsync(Net7EffectState effect, int delay, int sectorGeneration,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_gameplayLifetimeCts.Token, cancellationToken);
        try
        {
            await _clock.DelayAsync(TimeSpan.FromMilliseconds(delay), linked.Token).ConfigureAwait(false);
            lock (_worldStateSync)
            {
                // A stale timer must never cancel a replacement instance or a new sector.
                if (sectorGeneration != Volatile.Read(ref _gameplayGeneration) ||
                    !Gameplay.Effects.TryGetValue(effect.EffectId, out var current) ||
                    current.InstanceGeneration != effect.InstanceGeneration) return;
                Gameplay.RemoveEffect(effect.EffectId);
                _trace?.Invoke($"COMBAT-04 proxy lifetime removed effect 0x{effect.EffectId:X}/g{effect.InstanceGeneration} after {delay}ms");
                NotifyPresentationStateChanged();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }
}
