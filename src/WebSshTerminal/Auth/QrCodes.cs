using System.Text;
using QRCoder;

namespace WebSshTerminal.Auth;

/// <summary>QR en SVG generat al servidor: res no surt cap a serveis externs i encaixa amb la CSP (img-src data:).</summary>
public static class QrCodes
{
    public static string SvgDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(6);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
