using System.Security.Cryptography;
using System.Text.Json;

namespace AIrtist.Core;

public sealed class RuntimeAssets(string root)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string Root { get; } = Path.GetFullPath(root);
    public string File(string relative) => Path.Combine(Root, relative);
    public T Read<T>(string relative) => JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(File(relative)), Json)
        ?? throw new InvalidDataException($"파일을 읽을 수 없습니다: {relative}");

    public void Verify(CancellationToken token)
    {
        var manifest = Read<AssetManifest>("manifest.json");
        if (manifest.Version != 1 || manifest.Files.Length == 0)
            throw new InvalidDataException("지원하지 않는 모델 패키지입니다.");
        foreach (var entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(File(entry.Path));
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("모델 경로가 잘못되었습니다.");
            using var stream = System.IO.File.OpenRead(path);
            if (stream.Length != entry.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"모델 파일이 없거나 손상되었습니다: {entry.Path}");
        }
    }
}

public sealed record AssetManifest(int Version, AssetEntry[] Files);
public sealed record AssetEntry(string Path, long Bytes, string Sha256);
public sealed record Word(string Id, string Korean, string Meaning, string English, string[] Aliases);
public sealed record ScoringData(string Model, Word[] Words, float[][] Vectors, double Score_Floor, double Score_Ceiling);
public sealed record WorkflowResult(string Translation, byte[] Png, double Cosine, double Score, double TranslationSeconds, double GenerationSeconds, double ScoringSeconds);
