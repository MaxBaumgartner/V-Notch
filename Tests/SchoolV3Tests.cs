using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using VNotch.Controls;
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

    [Theory]
    [InlineData("1POSEOO", "POSE")]
    [InlineData("0POSETHI", "POSE")]
    [InlineData("0BWMRW", "REWE")]
    [InlineData("0BWMBO", "BO")]
    [InlineData("0GGPGW", "GW")]
    [InlineData("0RK", "REL")]
    [InlineData("RELIGION ROEM.KATH", "REL")]
    [InlineData("0E", "E")]
    [InlineData("DBI TEST", "DBI")]
    public void StudySubjectsUseTheSchoolFoldersAlreadyThere(string untis, string folder) =>
        Assert.Equal(folder, SchoolStudy.SubjectFolder(untis));

    [Fact]
    public void ExternalEventTestTakesTheSubjectOfTheLessonItReplaces()
    {
        var data = (JsonObject)JsonNode.Parse("""
            {"days":{"2026-11-27":[{"startTime":"10:00","subjectShort":"1WMC","subject":"Webprogrammierung"},
                                   {"startTime":"10:55","subjectShort":"1POSEOO","subject":"OO Programmieren Übung","color":"#ff8080"}]},
             "exams":[{"date":"2026-11-27","startTime":"10:55","kind":"Test","title":"Externe Veranst.","subjectShort":""},
                      {"date":"2027-01-19","startTime":"10:00","kind":"Test","title":"DBI TEST","subjectShort":""}]}
            """)!;
        var exams = PiDashboardClient.ParseSchool(data).Exams;
        Assert.Equal(("1POSEOO", "OO Programmieren Übung"), (exams[0].Subject, exams[0].Title));
        Assert.Equal("POSE", ControlCenterView.ExamSubject(exams[0]));
        Assert.Equal(Color.FromRgb(255, 128, 128), exams[0].Color);
        // Outside the known days the title still names the subject.
        Assert.Equal(("", "DBI TEST"), (exams[1].Subject, exams[1].Title));
        Assert.Equal("DBI", ControlCenterView.ExamSubject(exams[1]));
    }

    [Fact]
    public void StudyFilesAndSessionsStayInTheTestFolder()
    {
        using var temporary = new TestTempDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "2AHIF"));
        Directory.CreateDirectory(Path.Combine(temporary.Path, "3AHIF"));
        string folder = SchoolStudy.FolderFor("0DBI", MockNow.Date, "SQL Joins & Subqueries", temporary.Path);
        Assert.Equal(Path.Combine(temporary.Path, "3AHIF", "DBI", "2026-11-24 SQL Joins & Subqueries"), folder);
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
