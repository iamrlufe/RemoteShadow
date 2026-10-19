using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteShadow.App.Shell;

/// <summary>Значок приложения из ресурса <c>Resources/app.ico</c> (тот же, что вшит в exe).</summary>
internal static class AppIcon
{
    private static readonly Lazy<ImageSource?> Source = new(Load);

    public static ImageSource? Image => Source.Value;

    private static ImageSource? Load()
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute));
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null; // Без значка окна просто остаются с системным — это не повод падать.
        }
    }
}
