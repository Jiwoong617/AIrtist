using System.Security.Cryptography;

namespace AIrtist.Eos;

public static class RoomCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string Prefix = "airtist-";
    public const int Length = 10;
    public static string Normalize(string value)
    {
        if (value is null || value.Length > 32) throw new ArgumentException("방 코드 10자리를 입력해 주세요.");
        string code = value.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (code.Length != Length || code.Any(c => !Alphabet.Contains(c))) throw new ArgumentException("영문과 숫자로 된 방 코드 10자리를 확인해 주세요.");
        return code;
    }
    public static string CreateLobbyId() => Prefix + string.Concat(Enumerable.Range(0, Length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
    public static string ToLobbyId(string code) => Prefix + Normalize(code);
    public static string? FromLobbyId(string? id)
    {
        if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { string code = Normalize(id[Prefix.Length..]); return ToLobbyId(code) == id ? code : null; }
        catch (ArgumentException) { return null; }
    }
    public static string Display(string code) { code = Normalize(code); return code[..5] + "-" + code[5..]; }
}
