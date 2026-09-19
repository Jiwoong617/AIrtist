using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIrtist.Core;

public static class ImageCodec
{
    public static byte[] Png(float[] decoded)
    {
        const int pixels = 512 * 512;
        if (decoded.Length != 3 * pixels) throw new InvalidDataException("잘못된 이미지 크기입니다.");
        var rgb = new byte[decoded.Length];
        for (int i = 0; i < pixels; i++)
            for (int c = 0; c < 3; c++)
                rgb[i * 3 + c] = (byte)Math.Round(Math.Clamp(decoded[c * pixels + i] / 2f + .5f, 0, 1) * 255f, MidpointRounding.ToEven);
        if (rgb.Min() == rgb.Max()) throw new InvalidDataException("단색 이미지가 생성되었습니다.");
        var source = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Rgb24, null, rgb, 512 * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public static BitmapSource Load(byte[] png)
    {
        if (png.Length > 10_000_000) throw new InvalidDataException("이미지 파일이 너무 큽니다.");
        using var input = new MemoryStream(png, writable: false);
        var bitmap = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (bitmap.PixelWidth is <= 0 or > 2048 || bitmap.PixelHeight is <= 0 or > 2048)
            throw new InvalidDataException("지원하지 않는 이미지 해상도입니다.");
        bitmap.Freeze();
        return bitmap;
    }

    // TinyCLIP uses Pillow bicubic antialiasing and the checkpoint's CLIP normalization.
    public static float[] Preprocess(byte[] png)
    {
        var source = Load(png);
        var rgb = new FormatConvertedBitmap(source, PixelFormats.Rgb24, null, 0);
        int width = rgb.PixelWidth, height = rgb.PixelHeight;
        var pixels = new byte[width * height * 3];
        rgb.CopyPixels(pixels, width * 3, 0);
        int scaledWidth = width <= height ? 224 : 224 * width / height;
        int scaledHeight = height <= width ? 224 : 224 * height / width;
        var horizontal = ResizeAxis(pixels, width, height, scaledWidth, true);
        var resized = ResizeAxis(horizontal, scaledWidth, height, scaledHeight, false);
        int left = (int)Math.Round((scaledWidth - 224) / 2d, MidpointRounding.ToEven);
        int top = (int)Math.Round((scaledHeight - 224) / 2d, MidpointRounding.ToEven);
        float[] mean = [.48145466f, .4578275f, .40821073f], std = [.26862954f, .26130258f, .27577711f];
        var output = new float[3 * 224 * 224];
        for (int y = 0; y < 224; y++)
            for (int x = 0; x < 224; x++)
                for (int c = 0; c < 3; c++)
                    output[c * 224 * 224 + y * 224 + x] = (resized[((y + top) * scaledWidth + x + left) * 3 + c] / 255f - mean[c]) / std[c];
        return output;
    }

    private static byte[] ResizeAxis(byte[] source, int width, int height, int target, bool horizontal)
    {
        int original = horizontal ? width : height;
        if (original == target) return source;
        int outWidth = horizontal ? target : width, outHeight = horizontal ? height : target;
        var output = new byte[outWidth * outHeight * 3];
        double scale = (double)original / target, filterScale = Math.Max(scale, 1), support = 2 * filterScale;
        for (int p = 0; p < target; p++)
        {
            double center = (p + .5) * scale;
            int begin = Math.Max(0, (int)Math.Floor(center - support + .5));
            int end = Math.Min(original, (int)Math.Floor(center + support + .5));
            double[] weights = Enumerable.Range(begin, end - begin).Select(q => Cubic((q + .5 - center) / filterScale)).ToArray();
            double total = weights.Sum();
            for (int line = 0; line < (horizontal ? height : width); line++)
                for (int c = 0; c < 3; c++)
                {
                    double value = 0;
                    for (int q = begin; q < end; q++)
                    {
                        int offset = horizontal ? (line * width + q) * 3 + c : (q * width + line) * 3 + c;
                        value += source[offset] * weights[q - begin] / total;
                    }
                    int index = horizontal ? (line * outWidth + p) * 3 + c : (p * outWidth + line) * 3 + c;
                    output[index] = (byte)Math.Clamp((int)Math.Floor(value + .5), 0, 255);
                }
        }
        return output;
    }

    private static double Cubic(double distance)
    {
        double x = Math.Abs(distance);
        return x < 1 ? ((1.5 * x - 2.5) * x) * x + 1 : x < 2 ? ((-.5 * x + 2.5) * x - 4) * x + 2 : 0;
    }
}
