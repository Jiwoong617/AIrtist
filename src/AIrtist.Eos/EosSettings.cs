using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIrtist.Eos;

// Deliberately not a record: default ToString must never include credentials.
public sealed class EosSettings
{
    public string SdkDirectory { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string SandboxId { get; init; } = "";
    public string DeploymentId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";

    public static EosSettings Load(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("EOS 설정 파일이 너무 큽니다.");
            using var input = File.OpenRead(path);
            var value = JsonSerializer.Deserialize<EosSettings>(input, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("EOS 설정 객체가 없습니다.");
            value.Validate();
            return value;
        }
        // Do not include parser exceptions: they can contain user supplied JSON or property names.
        catch (JsonException) { throw new InvalidDataException("EOS 설정 JSON 형식을 확인하세요."); }
        catch (IOException) { throw new InvalidDataException("EOS 설정 파일을 읽을 수 없습니다. 경로·크기·값을 확인하세요."); }
        catch (UnauthorizedAccessException) { throw new InvalidDataException("EOS 설정 파일 읽기 권한이 없습니다."); }
    }

    public void Validate()
    {
        Check(SdkDirectory, nameof(SdkDirectory), 1024);
        Check(ProductId, nameof(ProductId), 64);
        Check(SandboxId, nameof(SandboxId), 64);
        Check(DeploymentId, nameof(DeploymentId), 64);
        Check(ClientId, nameof(ClientId), 256);
        Check(ClientSecret, nameof(ClientSecret), 1024);
    }

    private static void Check(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException($"EOS 설정 누락/형식 오류: {field}");
    }
}
