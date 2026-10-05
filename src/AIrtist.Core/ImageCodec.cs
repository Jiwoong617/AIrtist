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

}
