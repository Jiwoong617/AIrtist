using System.Text.Json;

namespace AIrtist.Core;

// Multiplayer content only. Free drawing does not load a catalogue or a hidden answer.
public sealed record Word(string Id, string Korean, string Meaning, string English, string[] Aliases);

public static class WordCatalog
{
    public static Word[] Load(string path)
    {
        var words = JsonSerializer.Deserialize<Word[]>(System.IO.File.ReadAllText(path), RuntimeAssets.Json)
            ?? throw new InvalidDataException("단어 목록을 읽을 수 없습니다.");
        if (words.Length == 0) throw new InvalidDataException("단어 목록이 비어 있습니다.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Id) || !ids.Add(word.Id)
                || word.Id != word.Id.Trim() || string.IsNullOrWhiteSpace(word.Korean)
                || string.IsNullOrWhiteSpace(word.Meaning) || string.IsNullOrWhiteSpace(word.English)
                || word.Aliases is null || word.Aliases.Length == 0
                || word.Aliases.Any(alias => string.IsNullOrWhiteSpace(alias) || InputRules.Canonical(alias).Length == 0))
                throw new InvalidDataException("단어 ID·뜻·금지 별칭을 확인해 주세요.");
        }
        return words;
    }
}
