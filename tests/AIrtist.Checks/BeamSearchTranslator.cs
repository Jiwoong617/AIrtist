using System.IO;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AIrtist.Core;

internal sealed class BeamSearchTranslator(RuntimeAssets assets)
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

    public string Translate(string description, CancellationToken token, int beamCount = 6)
    {
        token.ThrowIfCancellationRequested();
        if (beamCount is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(beamCount));
        if (!Regex.IsMatch(description, @"[\p{IsHangulSyllables}\p{IsHangulJamo}\p{IsHangulCompatibilityJamo}]")) return description;
        long[] source = Tokenize(description);
        var vocab = assets.Read<Dictionary<string, int>>("translation/vocab.json");
        var inverse = vocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        int pad = vocab["<pad>"], eos = vocab["</s>"];
        var active = new List<Hypothesis> { new([pad], 0) };
        var finished = new List<Hypothesis>();
        using var session = OrtSessions.Open(assets.File("translation/step-fp16-storage.onnx"), false);
        var sourceInput = NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(source, [1, source.Length]));
        var maskInput = NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, source.Length).ToArray(), [1, source.Length]));
        for (int step = 0; step < 64; step++)
        {
            var candidates = new List<Hypothesis>();
            foreach (var hypothesis in active)
            {
                token.ThrowIfCancellationRequested();
                float[] logits = OrtSessions.Run(session, [sourceInput, maskInput,
                    NamedOnnxValue.CreateFromTensor("decoder_input_ids", new DenseTensor<long>(hypothesis.Ids, [1, hypothesis.Ids.Length]))
                ], token);
                // The published Marian configuration suppresses PAD and renormalizes logits.
                logits[pad] = float.NegativeInfinity;
                double maximum = logits.Max();
                double logNormalizer = maximum + Math.Log(logits.Sum(value => Math.Exp(value - maximum)));
                var top = new PriorityQueue<int, double>();
                for (int i = 0; i < logits.Length; i++)
                {
                    if (i == pad) continue;
                    if (top.Count < 2 * beamCount) top.Enqueue(i, logits[i]);
                    else top.EnqueueDequeue(i, logits[i]);
                }
                foreach (var item in top.UnorderedItems)
                    candidates.Add(new([.. hypothesis.Ids, item.Element], hypothesis.LogProbability + logits[item.Element] - logNormalizer));
            }
            var ordered = candidates.OrderByDescending(item => item.LogProbability).ThenBy(item => item.Ids[^1]).ToArray();
            active.Clear();
            for (int rank = 0; rank < ordered.Length && active.Count < beamCount; rank++)
            {
                var hypothesis = ordered[rank];
                if (hypothesis.Ids[^1] == eos)
                {
                    if (rank < beamCount) finished.Add(hypothesis);
                }
                else active.Add(hypothesis);
            }
            finished = finished.OrderByDescending(item => item.NormalizedScore).Take(beamCount).ToList();
            // beamCount=1 is retained for comparison with the original greedy export reference.
            if (beamCount == 1 && ordered[0].Ids[^1] == eos) break;
            if (active.Count == 0 || (finished.Count == beamCount && finished[^1].NormalizedScore >= active[0].LogProbability / (step + 1))) break;
        }
        if (finished.Count == 0) throw new InvalidDataException("번역이 제한 길이 안에 끝나지 않았습니다.");
        string translated = string.Concat(finished[0].Ids.Skip(1).SkipLast(1).Select(id => inverse[(int)id])).Replace('▁', ' ').Trim();
        if (string.IsNullOrWhiteSpace(translated) || translated.Contains("<unk>") || Regex.IsMatch(translated, @"[\p{IsHangulSyllables}]"))
            throw new InvalidDataException("묘사를 번역하지 못했습니다. 문장을 바꿔 주세요.");
        return translated;
    }

    private sealed record Hypothesis(long[] Ids, double LogProbability)
    {
        public double NormalizedScore => LogProbability / (Ids.Length - 1);
    }
}
