using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace CloudRedirect.Services;

/// <summary>
/// Helper to generate high-quality WPF BitmapSource QR codes.
/// </summary>
public static class QrCodeHelper
{
    public static BitmapSource GenerateQrCode(string payload, int pixelsPerModule = 6)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(data);
        byte[] png = qrCode.GetGraphic(pixelsPerModule);

        var bitmap = new BitmapImage();
        using var ms = new MemoryStream(png);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
