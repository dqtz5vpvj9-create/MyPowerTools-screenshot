using SkiaSharp;

namespace Screenshot.Surface.Services;

public sealed record ImageCompressionOptions(string Format = "webp", int Quality = 85, int MaximumEdge = 0);
public sealed record ImageCompressionResult(string Source, string? Output, long BeforeBytes, long AfterBytes, int Width, int Height);

public static class ScreenshotImageCompressor
{
    public static ImageCompressionResult Compress(string source, ImageCompressionOptions options, CancellationToken cancellationToken)
    {
        if (options.Format is not ("webp" or "jpeg" or "png") || options.Quality is < 1 or > 100 || options.MaximumEdge is < 0 or > 16384)
            throw new ArgumentException("格式、质量或最长边设置无效。");
        cancellationToken.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var before = input.Length;
        if (before is <= 0 or > 128 * 1024 * 1024) throw new InvalidDataException("图片须在 128 MB 以内。");
        using var stream = new SKManagedStream(input, disposeManagedStream: false);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("无法读取图片。");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Bmp))
            throw new InvalidDataException("支持 PNG、JPEG、WebP 和 BMP 图片。");
        if (codec.FrameCount > 1) throw new InvalidDataException("动画图片暂不支持压缩，以免丢失帧。");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64_000_000)
            throw new InvalidDataException("图片尺寸超过 6400 万像素限制。");
        using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("图片数据不完整或已损坏。");
        cancellationToken.ThrowIfCancellationRequested();
        var rotated = (int)codec.EncodedOrigin >= 5;
        var width = rotated ? info.Height : info.Width;
        var height = rotated ? info.Width : info.Height;
        var scale = options.MaximumEdge == 0 ? 1d : Math.Min(1d, (double)options.MaximumEdge / Math.Max(width, height));
        var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(options.Format == "jpeg" ? SKColors.White : SKColors.Transparent);
            canvas.Scale((float)outputWidth / width, (float)outputHeight / height);
            var orientation = Orientation(codec.EncodedOrigin, info.Width, info.Height);
            canvas.Concat(in orientation);
            using var image = SKImage.FromBitmap(decoded);
            canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var outputImage = SKImage.FromBitmap(bitmap);
        var format = options.Format switch { "jpeg" => SKEncodedImageFormat.Jpeg, "png" => SKEncodedImageFormat.Png, _ => SKEncodedImageFormat.Webp };
        using var encoded = outputImage.Encode(format, options.Quality) ?? throw new InvalidDataException("图片编码失败。");
        cancellationToken.ThrowIfCancellationRequested();
        if (encoded.Size >= before) return new(source, null, before, before, width, height);

        var extension = options.Format == "jpeg" ? "jpg" : options.Format;
        var stem = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "-compressed");
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"{stem}{(suffix == 0 ? "" : $"-{suffix}")}.{extension}";
            FileStream output;
            try { output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }
            try
            {
                using (output) { encoded.SaveTo(output); output.Flush(); cancellationToken.ThrowIfCancellationRequested(); }
                return new(source, path, before, encoded.Size, outputWidth, outputHeight);
            }
            catch { output.Dispose(); File.Delete(path); throw; }
        }
    }

    private static SKMatrix Orientation(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity
    };
}
