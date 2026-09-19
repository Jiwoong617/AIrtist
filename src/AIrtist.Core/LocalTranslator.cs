using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AIrtist.Core;

public sealed class LocalTranslator(RuntimeAssets assets)
{
    public long[] Tokenize(string text)
    {
        using var stream = System.IO.File.OpenRead(assets.File("translation/source.spm"));
        var tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        var vocab = assets.Read<Dictionary<string, int>>("translation/vocab.json");
        var pieces = tokenizer.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false);
        var ids = pieces.Select(piece => (long)(vocab.TryGetValue(piece.Value, out int id) ? id : vocab["<unk>"])).ToList();
        ids.Add(vocab["</s>"]);
        if (ids.Count > 128) throw new ArgumentException("번역할 묘사가 너무 깁니다.");
        return ids.ToArray();
    }

    public string Translate(string description, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Regex.IsMatch(description, @"[\p{IsHangulSyllables}\p{IsHangulJamo}\p{IsHangulCompatibilityJamo}]")) return description;
        long[] source = Tokenize(description);
        var vocab = assets.Read<Dictionary<string, int>>("translation/vocab.json");
        var inverse = vocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        int pad = vocab["<pad>"], eos = vocab["</s>"];
        var result = new List<long> { pad };
        using var session = OrtSessions.Open(assets.File("translation/step-fp16-storage.onnx"), false);
        for (int step = 0; step < 64; step++)
        {
            float[] logits = OrtSessions.Run(session, [
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(source, [1, source.Length])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, source.Length).ToArray(), [1, source.Length])),
                NamedOnnxValue.CreateFromTensor("decoder_input_ids", new DenseTensor<long>(result.ToArray(), [1, result.Count]))
            ], token);
            int best = 0;
            for (int i = 1; i < logits.Length; i++)
                if (i != pad && logits[i] > logits[best]) best = i;
            if (best == eos)
            {
                string translated = string.Concat(result.Skip(1).Select(id => inverse[(int)id])).Replace('▁', ' ').Trim();
                if (string.IsNullOrWhiteSpace(translated) || translated.Contains("<unk>") || Regex.IsMatch(translated, @"[\p{IsHangulSyllables}]"))
                    throw new InvalidDataException("묘사를 번역하지 못했습니다. 문장을 바꿔 주세요.");
                return translated;
            }
            result.Add(best);
        }
        throw new InvalidDataException("번역이 제한 길이 안에 끝나지 않았습니다.");
    }
}
