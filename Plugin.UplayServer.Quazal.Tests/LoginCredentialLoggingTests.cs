using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Serializables;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// LoginEx carries the credential the game's launcher handed it, which with
/// Ubisoft's genuine launcher is the player's Ubisoft one.
/// </summary>
public sealed class LoginCredentialLoggingTests
{
    private const int Fc3Port = 6570;
    private const string LocalAccount = "lanplayer";
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.168.1.179"), 3074);
    private static readonly byte[] Credential = Encoding.ASCII.GetBytes("UBISOFT-TICKET-0123456789abcdef");

    [Theory]
    [InlineData(LocalAccount)]
    [InlineData("a-ubisoft-name-nobody-here-knows")]
    public void NoLogLineCarriesTheLoginExCredential(string username)
    {
        var sink = new CapturingSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var settings = new QuazalSettings
            {
                Accounts = [new QuazalAccount { Username = LocalAccount, Password = "lan-password", Pid = 1234 }],
            };
            var fc3 = QuazalTitle.ForTests("farcry3", Fc3Port, 6571, QuazalProtocolFamily.FarCry3);
            var responder = new QuazalResponder(settings, fc3.Ports.ToDictionary(port => port, _ => fc3));

            SendLoginEx(responder, username);
        }
        finally
        {
            Log.Logger = previous;
        }

        string credentialHex = Convert.ToHexString(Credential);
        // The login was read: "Login OK for <name>" or "Login for unknown user <name>".
        Assert.Contains(sink.Lines, line => line.Contains(username));
        Assert.DoesNotContain(sink.Lines, line => line.Contains(credentialHex, StringComparison.OrdinalIgnoreCase));
    }

    private static void SendLoginEx(QuazalResponder responder, string username)
    {
        uint signature = Open(responder);
        byte[] framed = [0, .. Rmc(TicketGranting.ProtocolId, TicketGranting.MethodLoginEx, LoginExParameters(username))];
        PrudpPacketV0 data = Packet(PacketType.Data, PacketFlags.NeedAck | PacketFlags.Reliable, 2, signature);
        data.Payload = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(framed);
        responder.Handle(data, Client, Fc3Port, data.ToArray());
    }

    /// <summary>The username as a Quazal string (u16 length with its NUL), then the auth holder.</summary>
    private static byte[] LoginExParameters(string username)
    {
        byte[] name = Encoding.UTF8.GetBytes(username + "\0");
        byte[] parameters = new byte[2 + name.Length + Credential.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(parameters, (ushort)name.Length);
        name.CopyTo(parameters, 2);
        Credential.CopyTo(parameters, 2 + name.Length);
        return parameters;
    }

    /// <summary>u32 size, protocol|0x80, callId, methodId, then the parameters.</summary>
    private static byte[] Rmc(byte protocol, uint method, byte[] parameters)
    {
        const uint callId = 7;
        byte[] message = new byte[4 + 1 + 4 + 4 + parameters.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(message.Length - 4));
        message[4] = (byte)(protocol | 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(5), callId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(9), method);
        parameters.CopyTo(message, 13);
        return message;
    }

    private static PrudpPacketV0 Packet(PacketType type, PacketFlags flags, ushort sequence, uint signature) => new()
    {
        Source = new VirtualPort { StreamType = StreamType.RV, Port = 15 },
        Destination = new VirtualPort { StreamType = StreamType.RV, Port = 1 },
        PacketType = type,
        Flags = flags,
        SequenceId = sequence,
        Signature = signature,
        ConnectionSignature = type == PacketType.Connect ? 0x11223344u : 0,
        ChecksumWidth = PrudpPacketV0.NexChecksumWidth,
        ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold,
    };

    /// <summary>SYN and CONNECT, returning the server's signature.</summary>
    private static uint Open(QuazalResponder responder)
    {
        PrudpPacketV0 syn = Packet(PacketType.Syn, PacketFlags.NeedAck, 0, 0);
        var synAck = responder.Handle(syn, Client, Fc3Port, syn.ToArray());
        uint signature = PrudpPacketV0.TryDeserialize(Assert.Single(synAck), out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold)
            ? reply.ConnectionSignature
            : throw new InvalidOperationException("no SYN-ACK");
        PrudpPacketV0 connect = Packet(PacketType.Connect, PacketFlags.NeedAck | PacketFlags.Reliable, 1, signature);
        responder.Handle(connect, Client, Fc3Port, connect.ToArray());
        return signature;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Emit(LogEvent logEvent) => Lines.Enqueue(logEvent.RenderMessage());
    }
}
