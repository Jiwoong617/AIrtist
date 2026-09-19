using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AIrtist.Core;

// Byte-level CLIP BPE. Checked against the pinned Transformers tokenizer.
public sealed class ClipTokenizer
{
    private readonly Dictionary<string, int> vocabulary;
    private readonly Dictionary<(string, string), int> ranks = [];
    private readonly string[] bytes = new string[256];
    private readonly Regex words = new(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ClipTokenizer(RuntimeAssets assets)
    {
        vocabulary = assets.Read<Dictionary<string, int>>("generation/tokenizer/vocab.json");
        var direct = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToHashSet();
        int extra = 0;
        for (int b = 0; b < 256; b++) bytes[b] = char.ConvertFromUtf32(direct.Contains(b) ? b : 256 + extra++);
        int rank = 0;
        foreach (string line in System.IO.File.ReadLines(assets.File("generation/tokenizer/merges.txt")).Skip(1))
        {
            string[] pair = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length == 2) ranks[(pair[0], pair[1])] = rank++;
        }
    }

    public long[] Encode(string input)
    {
        string text = Regex.Replace(WebUtility.HtmlDecode(WebUtility.HtmlDecode(input)).Normalize(NormalizationForm.FormC), @"\s+", " ").Trim().ToLowerInvariant();
        var ids = new List<long> { vocabulary["<|startoftext|>"] };
        foreach (Match match in words.Matches(text))
        {
            if (match.Value is "<|startoftext|>" or "<|endoftext|>")
                throw new ArgumentException("특수 토큰은 입력할 수 없습니다.");
            var parts = Encoding.UTF8.GetBytes(match.Value).Select(b => bytes[b]).ToList();
            parts[^1] += "</w>";
            while (parts.Count > 1)
            {
                int best = int.MaxValue;
                (string, string) chosen = default;
                for (int i = 0; i < parts.Count - 1; i++)
                    if (ranks.TryGetValue((parts[i], parts[i + 1]), out int value) && value < best)
                    { best = value; chosen = (parts[i], parts[i + 1]); }
                if (best == int.MaxValue) break;
                var merged = new List<string>();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i + 1 < parts.Count && (parts[i], parts[i + 1]) == chosen)
                        merged.Add(parts[i] + parts[++i]);
                    else merged.Add(parts[i]);
                }
                parts = merged;
            }
            ids.AddRange(parts.Select(part => (long)vocabulary[part]));
        }
        ids.Add(vocabulary["<|endoftext|>"]);
        if (ids.Count > 77) throw new ArgumentException("묘사가 너무 깁니다. 내용을 줄여 주세요.");
        while (ids.Count < 77) ids.Add(vocabulary["<|endoftext|>"]);
        return ids.ToArray();
    }
}
