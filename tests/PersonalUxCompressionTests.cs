using Screenshot.Surface.Services;
using SkiaSharp;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Screenshot.Surface.ViewModels;
using Screenshot.Surface.Views;

namespace PersonalUx.Tests;

public sealed class ScreenshotCompressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-compression-" + Guid.NewGuid().ToString("N"));
    public ScreenshotCompressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [AvaloniaFact]
    public async Task Screenshot_page_exposes_controls_and_batch_continues_after_bad_image()
    {
        var model = new ScreenshotViewModel(null!);
        var view = new ScreenshotView { DataContext = model };
        var window = new Window { Content = new ScrollViewer { Content = view }, Width = 1000, Height = 800 };
        window.Show();
        try
        {
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "选择图片并压缩"));
            Assert.Contains("快咔", model.BackendChoices);
            var bad = Path.Combine(_root, "bad.png");
            File.WriteAllText(bad, "invalid");
            await model.CompressFilesAsync([bad, CreateBmp()]);
            Assert.False(model.IsCompressing);
            Assert.Contains("bad.png", model.CompressionResults);
            Assert.Contains("input-compressed.webp", model.CompressionResults);
            Assert.Single(Directory.GetFiles(_root, "input-compressed.webp"));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Exif_orientation_is_applied_before_encoding()
    {
        using var bitmap = SKBitmap.Decode(CreateBmp());
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var original = jpeg.ToArray();
        // APP1: Exif, little-endian TIFF, one orientation SHORT with value 6 (90 degrees).
        byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0,
            73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        var source = Path.Combine(_root, "rotated.jpg");
        File.WriteAllBytes(source, original[..2].Concat(exif).Concat(original[2..]).ToArray());
        var result = ScreenshotImageCompressor.Compress(source, new("webp", 85, 64), default);
        using var decoded = SKBitmap.Decode(result.Output!);
        Assert.Equal(32, decoded.Width);
        Assert.Equal(64, decoded.Height);
    }

    [Fact]
    public void Compression_preserves_original_and_existing_output()
    {
        var source = CreateBmp();
        var original = File.ReadAllBytes(source);
        var first = ScreenshotImageCompressor.Compress(source, new(), default);
        var firstBytes = File.ReadAllBytes(first.Output!);
        var second = ScreenshotImageCompressor.Compress(source, new(), default);
        Assert.True(first.AfterBytes < first.BeforeBytes);
        Assert.NotEqual(first.Output, second.Output);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(firstBytes, File.ReadAllBytes(first.Output!));
        using var decoded = SKBitmap.Decode(second.Output!);
        Assert.Equal(256, decoded.Width);
        Assert.Equal(128, decoded.Height);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("jpeg")]
    [InlineData("png")]
    public void Longest_edge_limits_dimensions_without_upscaling(string format)
    {
        var source = CreateBmp();
        var smaller = ScreenshotImageCompressor.Compress(source, new(format, 85, 64), default);
        using var decoded = SKBitmap.Decode(smaller.Output!);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(32, decoded.Height);
        var largerLimit = ScreenshotImageCompressor.Compress(source, new(format, 85, 1024), default);
        Assert.Equal(256, largerLimit.Width);
    }

    [Theory]
    [InlineData("webp", true)]
    [InlineData("png", true)]
    [InlineData("jpeg", false)]
    public void Transparency_is_preserved_or_flattened_on_white(string format, bool transparent)
    {
        var source = Path.Combine(_root, "transparent.png");
        using (var bitmap = new SKBitmap(1600, 800))
        {
            bitmap.Erase(SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(source);
            data.SaveTo(file);
        }
        var result = ScreenshotImageCompressor.Compress(source, new(format, 85, 64), default);
        using var decoded = SKBitmap.Decode(result.Output!);
        var pixel = decoded.GetPixel(0, 0);
        if (transparent) Assert.Equal(0, pixel.Alpha);
        else { Assert.Equal(255, pixel.Alpha); Assert.True(pixel.Red > 245 && pixel.Green > 245 && pixel.Blue > 245); }
    }

    [Fact]
    public void Already_small_image_is_skipped_and_invalid_input_leaves_no_output()
    {
        var compressed = ScreenshotImageCompressor.Compress(CreateBmp(), new("png"), default);
        var again = ScreenshotImageCompressor.Compress(compressed.Output!, new("png"), default);
        Assert.Null(again.Output);
        var bad = Path.Combine(_root, "invalid.png");
        File.WriteAllText(bad, "invalid image");
        Assert.Throws<InvalidDataException>(() => ScreenshotImageCompressor.Compress(bad, new(), default));
        Assert.Empty(Directory.GetFiles(_root, "invalid-compressed*"));
    }

    [Fact]
    public void Cancelled_work_and_invalid_options_never_write()
    {
        var source = CreateBmp();
        Assert.ThrowsAny<OperationCanceledException>(() => ScreenshotImageCompressor.Compress(source, new(), new CancellationToken(true)));
        Assert.Throws<ArgumentException>(() => ScreenshotImageCompressor.Compress(source, new("jpeg", 101), default));
        Assert.Single(Directory.GetFiles(_root));
    }

    private string CreateBmp()
    {
        var path = Path.Combine(_root, "input.bmp");
        using var writer = new BinaryWriter(File.Create(path));
        const int width = 256, height = 128, pixels = width * height * 3;
        writer.Write((ushort)0x4d42); writer.Write(54 + pixels); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(pixels); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) { writer.Write((byte)x); writer.Write((byte)y); writer.Write((byte)100); }
        return path;
    }
}
