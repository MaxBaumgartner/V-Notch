using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

public sealed class SchoolFilesTests
{
    [Fact]
    public void FilesLandInAFolderPerSubject()
    {
        string folder = SchoolFiles.FolderFor("POSE 3AHIF Aitenbichler, Kern 26/27");
        Assert.EndsWith(Path.Combine("Downloads", "Schule", "POSE"), folder);
        Assert.EndsWith(Path.Combine("Schule", "Moodle"), SchoolFiles.FolderFor(""));
    }

    [Theory]
    [InlineData("Angabe.pdf", "Angabe.pdf")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("a/b/c:d?.pdf", "c_d_.pdf")]
    [InlineData("  ", "Angabe")]
    public void MoodleNamesAreMadeSafe(string name, string expected) => Assert.Equal(expected, SchoolFiles.SafeName(name));

    [Fact]
    public void AnUnchangedFileIsKeptAndANewVersionIsKeptBeside()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vnotch-school-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = SchoolFiles.Save(folder, [("Angabe.pdf", new byte[] { 1, 2, 3 })]);
            var again = SchoolFiles.Save(folder, [("Angabe.pdf", new byte[] { 1, 2, 3 })]);
            var newer = SchoolFiles.Save(folder, [("Angabe.pdf", new byte[] { 1, 2, 3, 4 })]);
            Assert.Equal(first, again);
            Assert.EndsWith("Angabe (2).pdf", newer[0]);
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PhotosGoInPageOrderAndOthersAreLeftOut()
    {
        var order = SchoolFiles.InPageOrder(["IMG_10.jpg", "notes.txt", "IMG_2.JPG", "IMG_1.png"]);
        Assert.Equal(["IMG_1.png", "IMG_2.JPG", "IMG_10.jpg"], order);
    }

    [Theory]
    [InlineData(4000, 3000, 2000, 1500)]
    [InlineData(3000, 4000, 1500, 2000)]
    [InlineData(800, 600, 800, 600)]
    public void PagesShrinkToTheLongEdgeButNeverGrow(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), SchoolFiles.Fit(w, h, 2000));

    [Fact]
    public void APhotoBecomesAJpegPageOnWhite()
    {
        string path = Path.Combine(Path.GetTempPath(), "vnotch-page-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // 3000 x 1000, fully transparent except a black square: transparency must come out white, not black.
            var pixels = new byte[3000 * 1000 * 4];
            for (int y = 0; y < 100; y++)
                for (int x = 0; x < 100; x++)
                    pixels[(y * 3000 + x) * 4 + 3] = 255;
            var source = BitmapSource.Create(3000, 1000, 96, 96, PixelFormats.Bgra32, null, pixels, 3000 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var file = File.Create(path)) encoder.Save(file);

            byte[] jpeg = SchoolFiles.ToJpegPage(path)!;
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
            var page = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal((2000, 667), (page.PixelWidth, page.PixelHeight));
            var corner = new byte[4];
            new FormatConvertedBitmap(page, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1999, 666, 1, 1), corner, 4, 0);
            Assert.True(corner[0] > 240 && corner[1] > 240 && corner[2] > 240, "transparent parts are white");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANonImageIsNoPage()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a photo");
            Assert.Null(SchoolFiles.ToJpegPage(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheHandInPanelAnswerBecomesAnAssignment()
    {
        var json = JsonNode.Parse("""
            {"cmid":238422,"name":"06 Notification","mode":"pose","nr":"06","attachments":[{"name":"Notification.zip","size":43371}],
             "status":"new","filename":"06_Notification_Leitner_David.pdf","blocked":"Diese Aufgabe nimmt keine PDFs an (.zip)."}
            """)!.AsObject();
        var a = PiDashboardClient.ParseAssignment(json)!;
        Assert.Equal((238422, "pose", "06"), (a.Cmid, a.Mode, a.Nr));
        Assert.Equal(new PiAttachment("Notification.zip", 43371), Assert.Single(a.Attachments));
        Assert.NotNull(a.Blocked);
        Assert.Null(PiDashboardClient.ParseAssignment(JsonNode.Parse("""{"error":"x"}""")!.AsObject()));
    }
}
