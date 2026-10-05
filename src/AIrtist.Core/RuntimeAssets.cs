using System.Security.Cryptography;
using System.Text.Json;

namespace AIrtist.Core;

public sealed class RuntimeAssets(string root)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly AssetLayout Layout = ReadLayout();
    public string Root { get; } = Path.GetFullPath(root);
    public string File(string relative) => Path.Combine(Root, relative);
    public T Read<T>(string relative) => JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(File(relative)), Json)
        ?? throw new InvalidDataException($"파일을 읽을 수 없습니다: {relative}");

    public void Verify(CancellationToken token)
    {
        var manifest = Read<AssetManifest>("manifest.json");
        if (manifest.Version != Layout.Version || manifest.Profile != Layout.Profile || manifest.Files is null)
            throw new InvalidDataException("지원하지 않는 모델 패키지입니다.");
        var required = new HashSet<string>(Layout.RequiredFiles, StringComparer.OrdinalIgnoreCase);
        if (manifest.Files.Length != required.Count)
            throw new InvalidDataException("필수 모델·고지 파일 목록이 일치하지 않습니다.");
        foreach (var entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!required.Remove(entry.Path)) throw new InvalidDataException("알 수 없거나 중복된 모델 경로입니다.");
            string path = Path.GetFullPath(File(entry.Path));
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("모델 경로가 잘못되었습니다.");
            using var stream = System.IO.File.OpenRead(path);
            if (stream.Length != entry.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"모델 파일이 없거나 손상되었습니다: {entry.Path}");
        }
    }

    private static AssetLayout ReadLayout()
    {
        using var stream = typeof(RuntimeAssets).Assembly.GetManifestResourceStream("AIrtist.RuntimeAssetLayout.json")
            ?? throw new InvalidDataException("모델 구성 정보가 없습니다.");
        return JsonSerializer.Deserialize<AssetLayout>(stream, Json) ?? throw new InvalidDataException("잘못된 모델 구성입니다.");
    }
}

public sealed record AssetLayout(int Version, string Profile, string[] RequiredFiles);
public sealed record AssetManifest(int Version, string Profile, AssetEntry[] Files);
public sealed record AssetEntry(string Path, long Bytes, string Sha256);
