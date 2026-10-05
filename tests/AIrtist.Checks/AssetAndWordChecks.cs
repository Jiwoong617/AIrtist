using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIrtist.Core;

internal static class AssetAndWordChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException) { return; }
        throw new InvalidOperationException("Invalid asset or word input was accepted");
    }

    public static void Run(string root)
    {
        var words = WordCatalog.Load(Path.Combine(root, "data/words.json"));
        var apple = words.Single(word => word.Id == "apple");
        foreach (string invalid in new[] { "사 과", "ＡＰＰＬＥ", "사\u200b과", "apple!", "", "<|endoftext|>" })
            Reject(() => InputRules.Validate(invalid, apple));
        Require(InputRules.ValidateDescription("사과") == "사과" && InputRules.ValidateTranslation("apple") == "apple", "Drawing inherited multiplayer aliases");
        Reject(() => InputRules.ValidateTranslation("an apple", apple));
        Require(InputRules.ValidateTranslation("a red fruit", apple) == "a red fruit", "Valid indirect description rejected");

        string checkRoot = Path.GetFullPath(Path.Combine(root, "outputs/scoring-removal"));
        string temporary = Path.Combine(checkRoot, "asset-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string catalogue = Path.Combine(temporary, "words.json");
            void BadWords(object data) { File.WriteAllText(catalogue, JsonSerializer.Serialize(data)); Reject(() => WordCatalog.Load(catalogue)); }
            BadWords(Array.Empty<Word>());
            BadWords(new[] { apple, apple with { Id = "APPLE" } });
            BadWords(new[] { apple with { Aliases = ["---"] } });
            BadWords(new[] { apple with { Meaning = "" } });

            var layout = JsonSerializer.Deserialize<AssetLayout>(File.ReadAllText(Path.Combine(root, "data/runtime-assets.json")), RuntimeAssets.Json)!;
            var entries = layout.RequiredFiles.Select(relative =>
            {
                string path = Path.Combine(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] content = System.Text.Encoding.UTF8.GetBytes(relative);
                File.WriteAllBytes(path, content);
                return new AssetEntry(relative, content.Length, Convert.ToHexString(SHA256.HashData(content)));
            }).ToArray();
            var valid = new AssetManifest(layout.Version, layout.Profile, entries);
            string manifest = Path.Combine(temporary, "manifest.json");
            void Write(AssetManifest value) => File.WriteAllText(manifest, JsonSerializer.Serialize(value, RuntimeAssets.Json));
            var assets = new RuntimeAssets(temporary);
            Write(valid);
            assets.Verify(CancellationToken.None);
            Require(!Directory.Exists(Path.Combine(temporary, "scoring")), "Asset fixture unexpectedly needed scoring");
            Write(valid with { Version = 1 }); Reject(() => assets.Verify(CancellationToken.None));
            Write(valid with { Profile = "legacy" }); Reject(() => assets.Verify(CancellationToken.None));
            Write(valid with { Files = entries.Skip(1).ToArray() }); Reject(() => assets.Verify(CancellationToken.None));
            Write(valid with { Files = [.. entries, new("scoring/vectors.json", 0, "")] }); Reject(() => assets.Verify(CancellationToken.None));
            Write(valid with { Files = [entries[1], .. entries.Skip(1)] }); Reject(() => assets.Verify(CancellationToken.None));
            Write(valid with { Files = [entries[0] with { Path = "../escape" }, .. entries.Skip(1)] }); Reject(() => assets.Verify(CancellationToken.None));
            Write(valid);
            File.WriteAllText(Path.Combine(temporary, entries[0].Path), "corrupt");
            Reject(() => assets.Verify(CancellationToken.None));
        }
        finally
        {
            if (!Path.GetFullPath(temporary).StartsWith(checkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unsafe fixture cleanup path");
            Directory.Delete(temporary, recursive: true);
        }
        Console.WriteLine("Assets/words: independent catalogue, drawing vs multiplayer aliases, scoring-free manifest, missing/extra/duplicate/traversal/corrupt assets passed");
    }
}
