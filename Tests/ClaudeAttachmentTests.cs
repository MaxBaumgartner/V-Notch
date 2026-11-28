using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeAttachmentTests
{
    [Fact]
    public void ImageAttachmentRetainsItsRawPngPathAlongsideItsMention() => SharedStaTestRunner.Run(() =>
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        var attachment = ClaudeAttachments.FromImage(bitmap);
        string path = Assert.IsType<string>(attachment.ImagePath);
        try
        {
            Assert.Equal(ClaudeAttachmentKind.Image, attachment.Kind);
            Assert.Equal(".png", Path.GetExtension(path));
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(path.Contains(' ') ? path : "@" + path, attachment.Mention);
            using var stream = File.OpenRead(path);
            var image = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(1, Assert.Single(image.Frames).PixelWidth);
        }
        finally { File.Delete(path); }
    });
}
