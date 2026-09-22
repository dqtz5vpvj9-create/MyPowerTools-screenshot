using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Screenshot.Surface.Views;

public sealed partial class ScreenshotView : UserControl
{
    public ScreenshotView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
