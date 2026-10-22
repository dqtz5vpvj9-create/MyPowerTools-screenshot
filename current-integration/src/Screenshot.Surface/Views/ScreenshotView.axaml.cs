using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Screenshot.Surface.ViewModels;

namespace Screenshot.Surface.Views;

public sealed partial class ScreenshotView : UserControl
{
    public ScreenshotView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void Compress_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreenshotViewModel model || model.IsCompressing) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || !storage.CanOpen) { model.ReportCompressionError("当前窗口无法打开文件选择器。"); return; }
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要压缩的图片", AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }]
            });
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths.Length != files.Count) { model.ReportCompressionError("请先将图片保存到本机，再进行压缩。"); return; }
            await model.CompressFilesAsync(paths);
        }
        catch (Exception exception) { model.ReportCompressionError(exception.Message); }
    }

    private void CancelCompression_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScreenshotViewModel model) model.CancelCompression();
    }

    private async void KuaikaDownload_Click(object? sender, RoutedEventArgs e)
    {
        try { if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri("https://www.kuaika.app/")); }
        catch (Exception exception) { if (DataContext is ScreenshotViewModel model) model.ReportCompressionError(exception.Message); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is ScreenshotViewModel model) model.CancelCompression();
        base.OnDetachedFromVisualTree(e);
    }
}
