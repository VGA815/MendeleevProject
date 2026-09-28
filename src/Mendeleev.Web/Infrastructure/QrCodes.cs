using QRCoder;

namespace Mendeleev.Web.Infrastructure
{
    /// <summary>QR code of the subscription link, drawn on the server: nothing third-party on the page.</summary>
    internal static class QrCodes
    {
        public static byte[] Png(string text)
        {
            using var generator = new QRCodeGenerator();
            using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            return new PngByteQRCode(data).GetGraphic(10);
        }

        /// <summary>For an <c>img</c> tag: the CSP allows <c>data:</c> images only.</summary>
        public static string PngDataUri(string text) => "data:image/png;base64," + Convert.ToBase64String(Png(text));
    }
}
