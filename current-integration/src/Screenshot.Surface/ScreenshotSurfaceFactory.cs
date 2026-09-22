using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using Screenshot.Surface.ViewModels;
using Screenshot.Surface.Views;

namespace Screenshot.Surface;

public sealed class ScreenshotSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new ScreenshotViewModel(context);
        var view = new ScreenshotView { DataContext = viewModel };
        Dispatcher.UIThread.Post(
            () => _ = viewModel.InitializeAsync(),
            DispatcherPriority.Background);
        return view;
    }
}
