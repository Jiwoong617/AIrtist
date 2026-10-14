using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using AIrtist.Core;

namespace AIrtist.App;

internal sealed record LocalMessage(string Kind)
{
    public string? Code { get; init; }
    public string? Token { get; init; }
    public string? Hash { get; init; }
    public string? Id { get; init; }
    public string? Host { get; init; }
    public string? Target { get; init; }
    public Guid Scope { get; init; }
    public long Revision { get; init; }
    public int Capacity { get; init; }
    public string? Stage { get; init; }
    public ConnectionMember[]? Members { get; init; }
    public string[]? Prepared { get; init; }
    public RoundPacketKind PacketKind { get; init; }
    public byte[]? Bytes { get; init; }
}

// Bounded framing and queues; socket I/O never blocks the WPF or game worker threads.
internal sealed class LocalWire : IDisposable
{
    internal const int MaxFrame = 8192, MaxQueue = 256;
    private readonly TcpClient socket;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<LocalMessage> incoming = Channel.CreateBounded<LocalMessage>(MaxQueue);
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(MaxQueue);
    private readonly Task read, write;
    private long lastRead = Stopwatch.GetTimestamp(), lastPing = Stopwatch.GetTimestamp();
    private int closed;
    internal bool Closed => Volatile.Read(ref closed) != 0;
    internal LocalWire(TcpClient socket)
    {
        this.socket = socket; socket.NoDelay = true;
        read = ReadLoop(); write = WriteLoop();
    }
    internal bool Send(LocalMessage message)
    {
        if (Closed) return false;
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        return data.Length <= MaxFrame && outgoing.Writer.TryWrite(data);
    }
    internal bool Receive(out LocalMessage message) => incoming.Reader.TryRead(out message!);
    internal void Tick()
    {
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead)) > TimeSpan.FromSeconds(15)) Close();
        if (!Closed && Stopwatch.GetElapsedTime(lastPing) > TimeSpan.FromSeconds(2))
        { lastPing = Stopwatch.GetTimestamp(); if (!Send(new("ping"))) Close(); }
    }
    private async Task ReadLoop()
    {
        try
        {
            var stream = socket.GetStream(); var header = new byte[4];
            while (!stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, stop.Token);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size is < 1 or > MaxFrame) break;
                var body = new byte[size]; await stream.ReadExactlyAsync(body, stop.Token);
                var message = JsonSerializer.Deserialize<LocalMessage>(body);
                if (message is null || message.Kind is null || !incoming.Writer.TryWrite(message)) break;
                Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception) { /* Only the closed state crosses the socket boundary. */ }
        finally { Close(); }
    }
    private async Task WriteLoop()
    {
        try
        {
            var stream = socket.GetStream(); var header = new byte[4];
            await foreach (var body in outgoing.Reader.ReadAllAsync(stop.Token))
            {
                BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
                await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(body, stop.Token);
            }
        }
        catch (Exception) { }
        finally { Close(); }
    }
    private void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel(); socket.Dispose(); incoming.Writer.TryComplete(); outgoing.Writer.TryComplete();
    }
    public void Dispose()
    {
        Close();
        // The loops handle their failures and finish after cancellation closes the socket.
        _ = Task.WhenAll(read, write).ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
