using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AIrtist.Core;

public sealed class ImageScorer(RuntimeAssets assets)
{
    public ScoringData Data { get; } = assets.Read<ScoringData>("scoring/vectors.json");

    public double[] Similarities(byte[] png, CancellationToken token)
    {
        float[] input = ImageCodec.Preprocess(png);
        using var session = OrtSessions.Open(assets.File("scoring/tinyclip-image.onnx"), false);
        float[] embedding = OrtSessions.Run(session, [NamedOnnxValue.CreateFromTensor("pixels", new DenseTensor<float>(input, [1, 3, 224, 224]))], token);
        double norm = Math.Sqrt(embedding.Sum(value => (double)value * value));
        if (norm <= 1e-12) throw new InvalidDataException("이미지를 평가할 수 없습니다.");
        return Data.Vectors.Select(vector => vector.Select((value, i) => value * embedding[i] / norm).Sum()).ToArray();
    }

    public (double Cosine, double Score) Score(byte[] png, string wordId, CancellationToken token)
    {
        int index = Array.FindIndex(Data.Words, word => word.Id == wordId);
        if (index < 0) throw new ArgumentException("평가할 제시어를 찾을 수 없습니다.");
        double cosine = Similarities(png, token)[index];
        return (cosine, Math.Clamp((cosine - Data.Score_Floor) / (Data.Score_Ceiling - Data.Score_Floor) * 100, 0, 100));
    }
}
