using System.IO;
using System.Windows;
using System.Windows.Controls;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class QuickLookTests
{
    [Fact]
    public void PipeMessagesPreservePathsAndEmptyFields()
    {
        const string path = @"C:\Files\Seeufer – Route.pdf";
        Assert.Equal(@"QuickLook.App.PipeMessages.Toggle|C:\Files\Seeufer – Route.pdf|",
            QuickLookBridge.FormatMessage(QuickLookCommand.Toggle, path));
        Assert.Equal(@"QuickLook.App.PipeMessages.Switch|C:\Files\Seeufer – Route.pdf|",
            QuickLookBridge.FormatMessage(QuickLookCommand.Switch, path));
        Assert.Equal("QuickLook.App.PipeMessages.Close||", QuickLookBridge.FormatMessage(QuickLookCommand.Close));
        Assert.False(QuickLookBridge.IsPreviewPath(@"C:\Files\name|injected.pdf"));
        Assert.False(QuickLookBridge.IsPreviewPath("relative.pdf"));
    }

    [Fact]
    public async Task DiscoveryCachesMissingInstallationAndCanFindPortablePipeLater()
    {
        var now = DateTime.UtcNow;
        bool pipeExists = false;
        int probes = 0;
        var bridge = new QuickLookBridge(() => null, () => { probes++; return pipeExists; },
            (_, _) => Task.FromResult(true), _ => throw new InvalidOperationException(), () => now);
        Assert.False(await bridge.RefreshAvailabilityAsync());
        Assert.False(await bridge.ToggleAsync(this, @"C:\missing.pdf"));
        pipeExists = true;
        Assert.False(await bridge.RefreshAvailabilityAsync());
        Assert.Equal(1, probes);
        now += TimeSpan.FromMinutes(4);
        Assert.True(await bridge.RefreshAvailabilityAsync());
        Assert.True(bridge.IsAvailable);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task MissingPipeStartsExecutableOnceAndWaitsForIt()
    {
        string file = Path.GetTempFileName();
        try
        {
            var attempts = new List<TimeSpan>();
            var starts = new List<string>();
            var bridge = new QuickLookBridge(() => @"C:\Portable\QuickLook.exe", () => false,
                (_, timeout) => { attempts.Add(timeout); return Task.FromResult(false); }, starts.Add, () => DateTime.UtcNow);
            Assert.False(await bridge.ToggleAsync(this, file));
            Assert.False(await bridge.ToggleAsync(this, file));
            Assert.Equal(@"C:\Portable\QuickLook.exe", Assert.Single(starts));
            Assert.Equal([300d, 3000d, 300d, 3000d], attempts.Select(timeout => timeout.TotalMilliseconds));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task StartupRetrySendsTheOriginalToggleWhenThePipeArrives()
    {
        string file = Path.GetTempFileName();
        try
        {
            bool running = false;
            var attempts = new List<(string Message, TimeSpan Timeout)>();
            var bridge = new QuickLookBridge(() => @"C:\Portable\QuickLook.exe", () => running,
                (message, timeout) => { attempts.Add((message, timeout)); return Task.FromResult(running); },
                _ => running = true, () => DateTime.UtcNow);
            Assert.True(await bridge.ToggleAsync(this, file));
            Assert.True(bridge.IsPreviewing(this));
            Assert.Equal(2, attempts.Count);
            Assert.Equal(QuickLookBridge.FormatMessage(QuickLookCommand.Toggle, file), attempts[0].Message);
            Assert.Equal(attempts[0].Message, attempts[1].Message);
            Assert.Equal(TimeSpan.FromSeconds(3), attempts[1].Timeout);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task SwitchesOwnedPreviewAndReleasesItsExportWhenReplaced()
    {
        string first = Path.GetTempFileName(), second = Path.GetTempFileName();
        try
        {
            int released = 0;
            var sent = new List<string>();
            var bridge = new QuickLookBridge(() => null, () => true,
                (message, _) => { sent.Add(message); return Task.FromResult(true); },
                _ => throw new InvalidOperationException(), () => DateTime.UtcNow);
            Assert.True(await bridge.ToggleAsync(this, "entry", () => Task.FromResult<QuickLookBridge.PreviewFile?>(
                new(first, () => { released++; return Task.CompletedTask; }))));
            await bridge.CloseAsync(new object());
            Assert.Equal(0, released);
            Assert.True(await bridge.SwitchAsync(this, second));
            Assert.Equal(1, released);
            await bridge.CloseAsync(this);
            Assert.False(bridge.IsPreviewing(this));
            Assert.Equal([
                QuickLookBridge.FormatMessage(QuickLookCommand.Toggle, first),
                QuickLookBridge.FormatMessage(QuickLookCommand.Switch, second),
                QuickLookBridge.FormatMessage(QuickLookCommand.Close)], sent);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void EyeFitsRowAndDoesNotAnimateWhileHidden() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var bridge = new QuickLookBridge(() => null, () => true,
            (_, _) => Task.FromResult(true), _ => { }, () => DateTime.UtcNow);
        await bridge.RefreshAvailabilityAsync();
        var row = new Grid { Width = 530, Height = 33 };
        row.Children.Add(new TextBlock { Text = "Subnetze.pdf", VerticalAlignment = VerticalAlignment.Center });
        var eye = new QuickLookEye(bridge) { Host = row, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(eye);
        var window = new BackgroundWindow { Content = row, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => eye.IsVisible && eye.ActualWidth == 29, "file preview eye arranged", ct);
            Assert.Equal(new Size(29, 27), eye.RenderSize);
            Assert.Equal(new Size(17, 17), ((Viewbox)eye.Content).DesiredSize);
            eye.Selected = true;
            row.Visibility = Visibility.Collapsed;
            eye.Selected = false;
            eye.Selected = true;
            Assert.Equal(0, eye.Opacity);
            Assert.False(Motion.IsRunning(eye, UIElement.OpacityProperty));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AttachmentStatusAnimatesOnceAfterReturningToSchool()
    {
        bool visible = true;
        var now = DateTime.UtcNow;
        var seen = new TabLedger(() => visible, () => now);
        const string attachment = "school.attachment.17.0.Angabe.pdf";
        Assert.False(seen.Changed(attachment, "Fetching…"));
        seen.Hidden();
        visible = false;
        Assert.False(seen.Changed(attachment, "Cached"));
        now += TabLedger.Away;
        visible = true;
        seen.Shown();
        Assert.True(seen.Changed(attachment, "Cached"));
        Assert.False(seen.Changed(attachment, "Cached"));
    }
}
