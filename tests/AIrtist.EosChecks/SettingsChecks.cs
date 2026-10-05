using System.Text.Json;
using AIrtist.Eos;

internal static class SettingsChecks
{
    internal static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"settings-{Guid.NewGuid():N}.json");
        const string sentinel = "TEST_ONLY_SECRET_MUST_NOT_APPEAR";
        try
        {
            var values = new Dictionary<string, object?>
            {
                ["sdkDirectory"] = "sdks/eos", ["productId"] = "test-product", ["sandboxId"] = "test-sandbox",
                ["deploymentId"] = "test-deployment", ["clientId"] = "test-client", ["clientSecret"] = sentinel
            };
            File.WriteAllText(path, JsonSerializer.Serialize(values));
            var loaded = EosSettings.Load(path);
            if (loaded.ClientSecret != sentinel || loaded.ToString()!.Contains(sentinel)) throw new Exception("설정 로딩/문자열 노출 검사 실패");
            foreach (var invalid in new object?[] { null, "", " " + sentinel, sentinel + "\n", 123 })
            {
                values["clientSecret"] = invalid;
                Reject(JsonSerializer.Serialize(values));
            }
            values["clientSecret"] = sentinel;
            values[sentinel] = "unknown-key";
            Reject(JsonSerializer.Serialize(values));
            Reject("{\"clientSecret\":\"" + sentinel + "\",broken}");
            Reject("null");
            Reject(new string('x', 16 * 1024 + 1));
            Console.WriteLine("PASS: 설정 누락/타입/공백/제어문자/알 수 없는 키/깨진 JSON/크기 거절, 오류에 비밀값 미노출");
        }
        finally { File.Delete(path); }

        void Reject(string json)
        {
            File.WriteAllText(path, json);
            try { EosSettings.Load(path); }
            catch (InvalidDataException error)
            {
                if (error.ToString().Contains(sentinel)) throw new Exception("설정 오류에서 비밀값 노출");
                return;
            }
            throw new Exception("잘못된 설정을 허용했습니다.");
        }
    }
}
