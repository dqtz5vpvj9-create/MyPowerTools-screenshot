using Screenshot.Surface.Services;

namespace Screenshot.Surface.ViewModels;

public sealed partial class ScreenshotViewModel
{
    private CancellationTokenSource? _compressionCancellation;
    private bool _isCompressing;
    private string _compressionResults = "选择图片后，在原目录生成 -compressed 副本；如果体积没有减小，则跳过保存。";
    private int _compressionFormatIndex;
    private decimal _compressionQuality = 85;
    private decimal _compressionMaximumEdge;
    public IReadOnlyList<string> CompressionFormats { get; } = ["WebP（推荐）", "JPEG（白底）", "PNG（无损）"];
    public int CompressionFormatIndex { get => _compressionFormatIndex; set => SetProperty(ref _compressionFormatIndex, value); }
    public decimal CompressionQuality { get => _compressionQuality; set => SetProperty(ref _compressionQuality, value); }
    public decimal CompressionMaximumEdge { get => _compressionMaximumEdge; set => SetProperty(ref _compressionMaximumEdge, value); }
    public bool IsCompressing { get => _isCompressing; private set => SetProperty(ref _isCompressing, value); }
    public string CompressionResults { get => _compressionResults; private set => SetProperty(ref _compressionResults, value); }
    public void CancelCompression() => _compressionCancellation?.Cancel();
    public void ReportCompressionError(string message) => CompressionResults = message;

    public async Task CompressFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsCompressing || paths.Count == 0) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _compressionCancellation = cancellation;
        IsCompressing = true;
        var results = new List<string>();
        try
        {
            var format = CompressionFormatIndex switch { 0 => "webp", 1 => "jpeg", 2 => "png", _ => throw new ArgumentException("请选择压缩格式。") };
            var options = new ImageCompressionOptions(format, decimal.ToInt32(CompressionQuality), decimal.ToInt32(CompressionMaximumEdge));
            foreach (var path in paths)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                CompressionResults = string.Join(Environment.NewLine, results.Append($"正在处理 {results.Count + 1}/{paths.Count}：{Path.GetFileName(path)}"));
                try
                {
                    var result = await Task.Run(() => ScreenshotImageCompressor.Compress(path, options, cancellation.Token), cancellation.Token);
                    results.Add(result.Output is null
                        ? $"{Path.GetFileName(path)}：{Size(result.BeforeBytes)}，已足够小，跳过。"
                        : $"{Path.GetFileName(path)}：{Size(result.BeforeBytes)} → {Size(result.AfterBytes)}（减少 {100d * (result.BeforeBytes - result.AfterBytes) / result.BeforeBytes:F1}%）\n{result.Output}");
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    results.Add($"{Path.GetFileName(path)}：{exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) { results.Add("已取消，已完成的副本保留。"); }
        catch (Exception exception) { results.Add(exception.Message); }
        finally
        {
            CompressionResults = string.Join(Environment.NewLine, results);
            _compressionCancellation = null;
            IsCompressing = false;
        }
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:F2} MB" : $"{bytes / 1024d:F1} KB";
}
