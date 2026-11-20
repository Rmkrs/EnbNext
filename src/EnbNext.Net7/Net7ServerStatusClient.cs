using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace EnbNext.Net7;

public readonly record struct Net7ServerStatus(bool Online, short? Players = null, bool Starting = false);

// LaunchNet7 FormMain status/count workers; independent of account establishment.
public static class Net7ServerStatusClient
{
    public static async Task<Net7ServerStatus> CheckAsync(string host, CancellationToken cancellationToken = default)
    {
        try
        {
            using var dnsDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            var addresses=await Dns.GetHostAddressesAsync(host,dnsDeadline.Token).ConfigureAwait(false);
            var address=addresses.FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork);
            if(address is null)return default;
            return await CheckEndpointAsync(new(address,3806),cancellationToken).ConfigureAwait(false);
        }
        catch(SocketException){return default;}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return default;}
    }

    internal static async Task<Net7ServerStatus> CheckEndpointAsync(IPEndPoint endpoint,CancellationToken cancellationToken)
    {
        byte[]? reply=await ExchangeAsync(endpoint,false,cancellationToken).ConfigureAwait(false);
        if(reply is null || reply.Length<4)return default;
        ushort opcode=BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(2));
        if(opcode==0x100b)return new(false,Starting:true);
        if(opcode!=0x1001)return default;
        reply=await ExchangeAsync(endpoint,true,cancellationToken).ConfigureAwait(false);
        short? count=reply is {Length:>=14}?BinaryPrimitives.ReadInt16LittleEndian(reply.AsSpan(12)):null;
        return new(true,count==-1?null:count);
    }

    private static async Task<byte[]?> ExchangeAsync(IPEndPoint endpoint,bool count,CancellationToken cancellationToken)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket=new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Connect(endpoint);
            byte[] packet=new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(packet,16);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2),count?(ushort)0x5000:(ushort)0x1000);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12),count?300:0x40000000);
            await socket.SendAsync(packet,deadline.Token).ConfigureAwait(false);
            return (await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false)).Buffer;
        }
        catch(SocketException){return null;}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return null;}
    }
}
