using System.IO;
using AIrtist.Eos;

namespace AIrtist.App;

internal static class EmbeddedOnlineSettings
{
    internal static EosSettings Load()
    {
        using var stream = typeof(EmbeddedOnlineSettings).Assembly.GetManifestResourceStream("AIrtist.OnlineConfig")
            ?? throw new InvalidDataException("온라인 연결 설정이 없는 빌드입니다. 최신 게임 패키지를 사용해 주세요.");
        return EosSettings.Read(stream);
    }
}
