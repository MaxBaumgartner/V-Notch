using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Handing maths photos in through a planner dashboard: the Moodle id from the assignment's address, what may be
/// handed in from the notch, and the PDF built here the way the dashboard builds it in the browser.
/// </summary>
public sealed class PlannerHandInTests
{
    /// <summary>The headers of a baseline JPEG: enough for the PDF, which embeds the bytes as they are.</summary>
    private static byte[] FakeJpeg(int width, int height, int components = 3)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, (byte)(8 + components * 3), 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components });
        for (int c = 1; c <= components; c++) bytes.AddRange(new byte[] { (byte)c, 0x11, 0 });
        bytes.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 0x3F, 0, 0x12, 0x34, 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    [Fact]
    public void TheJpegSizeComesFromItsFrameHeader()
    {
        Assert.Equal((3000, 2000, 3), PdfPages.JpegSize(FakeJpeg(3000, 2000)));
        Assert.Equal((800, 1200, 1), PdfPages.JpegSize(FakeJpeg(800, 1200, 1)));
        Assert.Null(PdfPages.JpegSize("not a jpeg"u8.ToArray()));
    }

    [Fact]
    public void EveryPhotoBecomesAnA4PageAndTheIndexPointsAtEveryObject()
    {
        byte[] pdf = PdfPages.FromJpegs([FakeJpeg(1500, 2000), FakeJpeg(2000, 1500)])!;
        string text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/Type /Pages /Count 2 /Kids [3 0 R 6 0 R]", text);
        // Portrait photo on a portrait page, landscape photo on a landscape page.
        Assert.Contains("/MediaBox [0 0 595.28 841.89]", text);
        Assert.Contains("/MediaBox [0 0 841.89 595.28]", text);
        Assert.Contains("/Width 1500 /Height 2000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", text);

        int xref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var offsets = Regex.Matches(text[xref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(8, offsets.Count);
        for (int i = 0; i < offsets.Count; i++) Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
    }

    [Fact]
    public void APageThatIsNoJpegMakesNoPdf() => Assert.Null(PdfPages.FromJpegs([FakeJpeg(10, 10), "png?"u8.ToArray()]));

    [Theory]
    [InlineData("https://moodle.school.example/moodle/mod/assign/view.php?id=123456", 123456)]
    [InlineData("https://moodle.example/mod/assign/view.php?foo=1&id=77", 77)]
    [InlineData("https://moodle.example/calendar/view.php", 0)]
    [InlineData("", 0)]
    public void TheMoodleIdComesFromTheAssignmentsAddress(string url, int cmid) => Assert.Equal(cmid, PlannerDashboard.CmidFromUrl(url));

    [Theory]
    [InlineData("Abgabe: Übung 7 bis 4.11.2026", "Übung 7")]
    [InlineData("Übung 3: Matrizen / Vektoren?", "Übung 3 Matrizen  Vektoren")]
    [InlineData("Abgabe: ", "Abgabe")]
    public void ThePdfIsNamedLikeTheDashboardNamesIt(string title, string name) => Assert.Equal(name, PlannerDashboard.DefaultPdfName(title));

    /// <summary>An open assignment with files allowed, and <paramref name="extra"/> (", \"key\": value") laid over it.</summary>
    private static JsonObject Info(string extra)
    {
        var info = (JsonObject)JsonNode.Parse(
            """{ "name": "Übung 3", "status": "new", "fileSubmissions": true, "fileTypes": "", "existingFiles": [] }""")!;
        foreach (var (key, value) in (JsonObject)JsonNode.Parse("{" + extra.TrimStart(',', ' ') + "}")!)
            info[key] = value?.DeepClone();
        return info;
    }

    [Fact]
    public void PhotosGoInWhereFilesAndPdfsAreAllowed()
    {
        var open = PlannerDashboard.Assignment(42, Info(""));
        Assert.Equal((42, "photo", (string?)null, "Übung 3"), (open.Cmid, open.Mode, open.Blocked, open.Filename));
        Assert.Empty(open.Attachments);

        Assert.Equal("photo", PlannerDashboard.Assignment(1, Info(", \"fileTypes\": \".pdf,.docx\"")).Mode);
        Assert.Equal("link", PlannerDashboard.Assignment(1, Info(", \"fileTypes\": \".java\"")).Mode);
        Assert.Equal("link", PlannerDashboard.Assignment(1, Info(", \"fileSubmissions\": false")).Mode);
    }

    [Theory]
    [InlineData(", \"locked\": true")]
    [InlineData(", \"status\": \"submitted\"")]
    [InlineData(", \"existingFiles\": [\"alt.pdf\"]")]
    [InlineData(", \"needsSubmitButton\": true, \"needsStatement\": true")]
    public void WhatTheDashboardShouldConfirmIsNotOfferedHere(string extra) =>
        Assert.NotNull(PlannerDashboard.Assignment(42, Info(extra)).Blocked);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhotosGoToThePlannerAsOnePdfOnlyAsked(bool piAccepts)
    {
        string due = DateTime.Now.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var pi = new FakePlanner(due, piAccepts);
        using var client = new PiDashboardClient(() => pi.Address);

        var school = await client.GetSchoolAsync();
        var deadline = Assert.Single(school!.Deadlines);
        Assert.Equal(4242, deadline.Cmid);

        var task = await client.GetAssignmentAsync(4242);
        Assert.Equal(("photo", "Übung 3"), (task!.Mode, task.Filename));

        var result = await client.SubmitPhotosAsync(4242, task.Filename, [Convert.ToBase64String(FakeJpeg(1500, 2000))]);

        Assert.Equal(piAccepts, result.Ok);
        Assert.Equal(piAccepts ? "Übung 3.pdf" : null, result.SubmittedName);
        if (!piAccepts) Assert.Equal("PDF ist zu groß", result.Error);
        Assert.Equal("/api/moodle/assign/moodle-abc/submit", pi.SubmitPath);
        Assert.Equal("application/pdf", pi.SubmitType);
        Assert.Contains("final=1", pi.SubmitQuery);
        Assert.Contains("accept=0", pi.SubmitQuery);
        Assert.Contains("name=%C3%9Cbung%203", pi.SubmitQuery);
        Assert.StartsWith("%PDF-1.4", pi.SubmitStart);
    }

    /// <summary>A planner dashboard with one Moodle deadline, its assignment and the hand-in route.</summary>
    private sealed class FakePlanner : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly string _due;
        private readonly bool _accepts;

        public string Address { get; }
        public volatile string SubmitPath = "", SubmitQuery = "", SubmitType = "", SubmitStart = "";

        public FakePlanner(string due, bool accepts)
        {
            _due = due;
            _accepts = accepts;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                var request = context.Request;
                string path = request.Url!.AbsolutePath;
                int status = 200;
                string? body = path switch
                {
                    "/api/lesson-timer" => """{ "enabled": true, "now": { "state": "off" } }""",
                    "/api/timetable" => "[]",
                    "/api/events" => "[{ \"id\": \"moodle-abc\", \"title\": \"Abgabe: Übung 3\", \"sub\": \"AM 1AHIF\", \"date\": \"" + _due +
                                     "\", \"start\": \"23:00\", \"end\": \"23:59\", \"kind\": \"hausuebung\", \"repeat\": \"none\" }]",
                    "/api/moodle/assign/moodle-abc" => """
                        { "name": "Übung 3", "url": "https://moodle.example/mod/assign/view.php?id=4242", "status": "new",
                          "fileSubmissions": true, "fileTypes": "", "existingFiles": [], "canEdit": true, "locked": false }
                        """,
                    _ => null
                };
                if (path == "/api/moodle/assign/moodle-abc/submit" && request.HttpMethod == "POST")
                {
                    SubmitPath = path;
                    SubmitQuery = request.Url.Query;
                    SubmitType = request.ContentType ?? "";
                    var start = new byte[8];
                    int read = await request.InputStream.ReadAsync(start).ConfigureAwait(false);
                    SubmitStart = Encoding.Latin1.GetString(start, 0, read);
                    status = _accepts ? 200 : 400;
                    body = _accepts ? """{ "name": "Übung 3", "status": "submitted" }""" : """{ "error": "PDF ist zu groß" }""";
                }
                context.Response.StatusCode = body == null ? 404 : status;
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
