using System.Runtime.InteropServices;

namespace AIrtist.App;

// WinMM is part of Windows. All native buffer operations belong to this thread.
internal sealed class WaveAudioOutput : IDisposable
{
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private int disposed;
    internal bool Opened { get; private set; }
    internal bool Finished { get; private set; }
    internal string? Failure { get; private set; }
    internal long SubmittedBuffers { get; private set; }
    internal WaveAudioOutput(AudioMixer mixer, string directory)
    {
        thread = new(() => Run(mixer, directory)) { IsBackground = true, Name = "AIrtist audio" };
        thread.Start();
    }
    private void Run(AudioMixer mixer, string directory)
    {
        nint device = 0;
        var buffers = new List<Buffer>();
        using var ready = new AutoResetEvent(false);
        try
        {
            mixer.Load(AudioBank.Load(directory));
            if (stop.WaitOne(0)) return;
            var format = new Format { Tag = 1, Channels = 1, Rate = AudioBank.Rate, BytesPerSecond = AudioBank.Rate * 2, BlockAlign = 2, Bits = 16 };
            Check(waveOutOpen(out device, uint.MaxValue, ref format, ready.SafeWaitHandle.DangerousGetHandle(), 0, 0x50000));
            Opened = true;
            for (int i = 0; i < 4; i++)
            {
                var buffer = new Buffer(); buffers.Add(buffer);
                Check(waveOutPrepareHeader(device, buffer.Header, HeaderSize)); buffer.Prepared = true;
            }
            while (!stop.WaitOne(0))
            {
                foreach (var buffer in buffers)
                {
                    var header = Marshal.PtrToStructure<Header>(buffer.Header);
                    if (buffer.Queued && (header.Flags & 1) == 0) continue;
                    mixer.Render(buffer.Pcm); Marshal.Copy(buffer.Pcm, 0, buffer.Data, buffer.Pcm.Length);
                    Check(waveOutWrite(device, buffer.Header, HeaderSize)); buffer.Queued = true; SubmittedBuffers++;
                }
                if (WaitHandle.WaitAny([stop, ready], 500) == 0) break;
            }
        }
        catch (Exception ex) { Failure = ex.GetType().Name; }
        finally
        {
            if (device != 0) waveOutReset(device);
            foreach (var buffer in buffers)
            {
                // Never free a buffer still owned by the driver, even on a device error.
                if (!buffer.Prepared || waveOutUnprepareHeader(device, buffer.Header, HeaderSize) == 0) buffer.Dispose();
            }
            if (device != 0) waveOutClose(device);
            Finished = true;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        if (thread.Join(2000)) stop.Dispose();
    }
    private static void Check(uint result) { if (result != 0) throw new InvalidOperationException("Audio device result " + result); }
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    private sealed class Buffer : IDisposable
    {
        public readonly short[] Pcm = new short[441]; // 20 ms, four queued buffers.
        public readonly nint Data = Marshal.AllocHGlobal(882);
        public readonly nint Header = Marshal.AllocHGlobal((int)HeaderSize);
        public bool Prepared, Queued;
        public Buffer() => Marshal.StructureToPtr(new Header { Data = Data, Length = 882 }, Header, false);
        public void Dispose() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Format { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public nint Data; public uint Length, Recorded; public nuint User; public uint Flags, Loops; public nint Next; public nuint Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint device, uint id, ref Format format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
}
