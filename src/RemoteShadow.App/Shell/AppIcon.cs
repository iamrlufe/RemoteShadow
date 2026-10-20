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
            // BitmapFrame.Create берёт первый кадр ico (16×16) — значок в шапке выглядел размытым. Берём самый крупный.
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null; // Без значка окна просто остаются с системным — это не повод падать.
        }
    }
}
