using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Eos;

namespace AIrtist.App;

// Per-Windows-user discovery only. All sockets bind/connect to IPv4 loopback.
internal sealed record LocalRoomAddress(string Code, int Port, int ProcessId, long ProcessStarted, string Token, string Hash)
{
    private static string Root => Path.Combine(Path.GetTempPath(), "AIrtist", "local-rooms-v1");
    private static string FilePath(string code) => Path.Combine(Root, RoomCode.Normalize(code) + ".json");
    internal static LocalRoomAddress Register(string code, int port, string hash)
    {
        using var process = Process.GetCurrentProcess();
        var address = new LocalRoomAddress(code, port, process.Id, process.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"), hash);
        Directory.CreateDirectory(Root);
        using var file = new FileStream(FilePath(code), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, address); return address;
    }
    internal static LocalRoomAddress? Find(string code)
    {
        try
        {
            var path = FilePath(code); if (new FileInfo(path).Length > 2048) return null;
            var address = JsonSerializer.Deserialize<LocalRoomAddress>(File.ReadAllText(path));
            if (address is null || address.Code != RoomCode.Normalize(code) || address.Port is < 1024 or > 65535 ||
                address.Token is not { Length: 32 } || address.Hash is not { Length: 64 }) return null;
            using var process = Process.GetProcessById(address.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == address.ProcessStarted ? address : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    internal void Remove()
    {
        // Never remove another process's replacement registration.
        try { if (Find(Code)?.Token == Token) File.Delete(FilePath(Code)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
