using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;
using static VNotch.Tests.HomeSchoolTabTests;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SchoolV3Tests
{
    internal static readonly DateTimeOffset MockNow = new(2026, 11, 24, 10, 38, 0, TimeSpan.FromHours(1));

    internal static PiSchool Sample()
    {
        var day = MockNow.Date;
        return new PiSchool("2026-11-24", true,
            [
                new("08:00", "08:50", "D", "E58-2", false, "", 480, 530, "0D"),
                new("08:55", "09:45", "POSE", "EDV 2", false, "", 535, 585, "0POSE"),
                new("09:50", "10:40", "AM", "E58-2", false, "", 590, 640, "0AM"),
                new("10:50", "11:40", "DBI", "EDV 3", false, "anderer Raum", 650, 700, "0DBI"),
                new("11:45", "12:35", "NSCS", "N17", false, "", 705, 755, "0NSCS")
            ],
            [
                new("Übung 7 – Integrale", "AM 3AHIF", MockNow.AddDays(2), false, 71),
                new("Task 3 – REST client", "WMC 3AHIF", MockNow.AddDays(3), false, 72, "classroom"),
                new("Task 4 – Tool class", "POSE 3AHIF", MockNow.AddDays(6), false, 73)
            ],
            [
                new(day.AddDays(6), "10:50", "Test", "SQL Joins & Subqueries", "0DBI"),
                new(day.AddDays(8), "10:50", "Test", "Scrum & Kanban", "0SYP"),
                new(day.AddDays(10), "10:50", "Test", "VLANs", "0NSCS")
            ], null);
    }

    internal static void Seed(ControlCenterView view)
    {
        SetField(view, "_piRefreshing", true);
        SetField(view, "_schoolNow", (Func<DateTimeOffset>)(() => MockNow));
        SetField(view, "_piSchool", Sample());
        var assignments = (Dictionary<int, PiAssignment>)Field(view, "_piAssignments");
        assignments[71] = new(71, "Übung 7 – Integrale", "photo", "07", [new("Integrale.pdf", 1024)], "", null, "Integrale.pdf");
        assignments[72] = new(72, "Task 3 – REST client", "classroom", "03", [], "", null, "");
        assignments[73] = new(73, "Task 4 – Tool class", "pose", "04", [], "", null, "");
        Invoke(view, "BuildPiPage");
    }

    [Fact]
    public void FaceAndDrawerFitWithEveryPriorityInOrder() => SharedStaTestRunner.Run(() =>
    {
        var previous = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        try
        {
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            Seed(view);
            view.SetDrawerOpen(true);
            var size = new Size(552, view.MeasureContentHeight(552));
            view.Arrange(new Rect(size));
            view.UpdateLayout();
            Assert.InRange(size.Height + 30, 300, 580);
            var face = (Grid)Field(view, "_schoolFace");
            Assert.Equal(52, ((TextBlock)Field(view, "_schoolSubject")).FontSize);
            Assert.Equal("Übung 7 – Integrale", ((TextBlock)Field(view, "_schoolTitle")).Text);
            Assert.Contains(Descendants<TextBlock>(face), x => x.Text == "Download task");
            var ordered = Descendants<TextBlock>((StackPanel)Field(view, "_drawerBody")).Select(x => x.Text).ToList();
            Assert.True(ordered.Contains("Hand-ins") && ordered.IndexOf("Hand-ins") < ordered.IndexOf("Tests"));
            Assert.True(ordered.Contains("Today") && ordered.IndexOf("Tests") < ordered.IndexOf("Today"));
            Assert.Contains("SQL Joins & Subqueries", ordered);
            Assert.Contains("10:38", ordered);
        }
        finally { Loc.SetLanguage(previous); }
    });

    [Fact]
    public void DeadlineChangesPulseOnceAndHideStopsTheClockAndMotion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        bool reduced = AnimationConfig.ReduceMotion;
        bool? overrideAnimation = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        view.AttachPi(client, () => false, () => false, () => "", () => "");
        Seed(view);
        System.Windows.Threading.DispatcherTimer? timer = null;
        var now = DateTime.UtcNow;
        SetField(view, "_seen", new TabLedger(() => view.IsVisible, () => now));
        var window = new Window { Content = view, Width = 552, Height = 550, Top = -20000, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var hero = (Grid)Field(view, "_schoolHero");
            timer = (System.Windows.Threading.DispatcherTimer)Field(view, "_schoolCountdownTimer");
            Assert.True(timer.IsEnabled);
            Assert.False(Motion.IsRunning(hero, UIElement.OpacityProperty));
            view.Visibility = Visibility.Collapsed;

            var data = Sample();
            SetField(view, "_piSchool", data with { Deadlines = [data.Deadlines[0] with { Due = MockNow.AddHours(4) }, .. data.Deadlines.Skip(1)] });
            Invoke(view, "BuildPiPage");
            Assert.False(timer.IsEnabled);
            Assert.False(hero.HasAnimatedProperties);

            now += TabLedger.Away;
            view.Visibility = Visibility.Visible;
            await WpfFrameWaiter.UntilAsync(() => Motion.IsRunning(hero, UIElement.OpacityProperty), "school hero starts", ct);
            await WpfFrameWaiter.UntilAsync(() => !Motion.IsRunning(hero, UIElement.OpacityProperty), "school hero settles", ct);
            Invoke(view, "BuildPiPage");
            Assert.False(Motion.IsRunning(hero, UIElement.OpacityProperty));

            var countdown = (TextBlock)Field(view, "_schoolCountdown");
            Invoke(view, "UpdateSchoolClock", MockNow.AddMinutes(1));
            Assert.Same(countdown, Field(view, "_schoolCountdown"));
            Assert.Equal("3:59", countdown.Text);
            Assert.False(countdown.HasAnimatedProperties);

            SetField(view, "_schoolBloom", 1L);
            Invoke(view, "BuildPiPage");
            var light = (WeatherTween)Field(view, "_schoolLight");
            Assert.True(Motion.IsRunning(light, WeatherTween.ValueProperty));
            view.Visibility = Visibility.Collapsed;
            Assert.False(timer.IsEnabled);
            Assert.False(light.HasAnimatedProperties);
        }
        finally
        {
            timer?.Stop();
            window.Close();
            AnimationConfig.ClientAreaAnimationOverride = overrideAnimation;
            AnimationConfig.SetReduceMotion(reduced);
        }
    });

    [Fact]
    public void WebUntisColoursOverrideTheExactFallbacks()
    {
        var data = (JsonObject)JsonNode.Parse("""
            {"subjectColors":{"0POSETHI":"#123456","0DBI":"broken"},"days":{"2026-11-24":[{"subjectShort":"0DBI","color":"0aBCeF"}]},
             "exams":[{"date":"2026-11-25","subjectShort":"0NSCS","color":"#026aff"}]}
            """)!;
        var school = PiDashboardClient.ParseSchool(data);
        Assert.Equal(Color.FromRgb(18, 52, 86), school.SubjectColors["POSE"]);
        Assert.Equal(Color.FromRgb(10, 188, 239), school.SubjectColors["DBI"]);
        Assert.Equal(Color.FromRgb(2, 106, 255), school.SubjectColors["NSCS"]);
        Assert.Equal(Color.FromRgb(255, 0, 0), ControlCenterView.SubjectColor("AM"));
        Assert.Equal(Color.FromRgb(221, 254, 175), ControlCenterView.SubjectColor("Religion"));
    }

    [Fact]
    public void StudyFilesAndSessionsStayInTheTestFolder()
    {
        using var temporary = new TestTempDirectory();
        string folder = SchoolStudy.FolderFor("DBI", MockNow.Date, "SQL Joins & Subqueries", temporary.Path);
        Assert.EndsWith(@"Lernen\DBI\2026-11-24 SQL Joins & Subqueries", folder);
        string file = Path.Combine(temporary.Path, "example.sql");
        File.WriteAllText(file, "select 1");
        SchoolStudy.CopyFiles(folder, [file, file]);
        SchoolStudy.SaveNote(folder, "Kap. 5–7");
        Assert.Equal(["example (2).sql", "example.sql"], SchoolStudy.Files(folder).Select(Path.GetFileName));
        Assert.Equal("Kap. 5–7", SchoolStudy.ReadNote(folder));

        string project = Path.Combine(temporary.Path, SchoolStudy.ProjectKey(folder));
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(project, $"{id}.jsonl"), """{"type":"custom-title","customTitle":"Joins üben"}""");
        Assert.Equal(id, Assert.Single(SchoolStudy.Sessions(folder, temporary.Path)).Id);
        Assert.Empty(SchoolStudy.Sessions(folder + " other", temporary.Path));
    }

    [Fact]
    public void ClassroomFinderAcceptsOneExistingExerciseClone()
    {
        using var temporary = new TestTempDirectory();
        string task = Path.Combine(temporary.Path, "3AHIF", "WMC", "HÜ3");
        string clone = Path.Combine(task, "festival-lineup");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        Assert.Equal(clone, SchoolClassroom.FindClone("WMC", "WMC 3AHIF", "03", temporary.Path));
        Assert.Null(SchoolClassroom.FindClone("WMC", "WMC 3AHIF", "04", temporary.Path));
        Directory.CreateDirectory(Path.Combine(task, "second-clone", ".git"));
        Assert.Null(SchoolClassroom.FindClone("WMC", "WMC 3AHIF", "03", temporary.Path));
    }

    private sealed class TestTempDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("vnotch-school-v3-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}