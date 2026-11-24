using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;
using Xunit;
using static VNotch.Tests.HomeSchoolTabTests;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SchoolDeadlineDialTests
{
    [Fact]
    public void FacePagerAndDayUseTheDeadlineDialLayout() => SharedStaTestRunner.Run(() =>
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        try
        {
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            SchoolV3Tests.Seed(view);
            Arrange(view);

            var dial = (Grid)Field(view, "_schoolDial");
            Assert.Equal(140, dial.ActualWidth);
            Assert.Equal(140, dial.ActualHeight);
            Assert.Equal(46, Descendants<Line>(dial).Count());
            Assert.Equal(44, ((TextBlock)Field(view, "_schoolTitle")).FontSize);
            Assert.Equal("1 / 6", ((TextBlock)Field(view, "_schoolCount")).Text);
            Assert.Equal("2", ((System.Windows.Documents.Run)Field(view, "_schoolDigits")).Text);
            Assert.Equal("d", ((System.Windows.Documents.Run)Field(view, "_schoolUnit")).Text);
            for (int index = 0; index < 3; index++)
                ((Button)Field(view, "_schoolNext")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("4 / 6", ((TextBlock)Field(view, "_schoolCount")).Text);
            Assert.Equal("SQL Joins & Subqueries", ((TextBlock)Field(view, "_schoolTitle")).Text);
            var actions = (StackPanel)Field(view, "_schoolActions");
            Assert.Contains(actions.Children.OfType<Button>(), button => Equals(button.ToolTip, "Study"));

            view.SetDrawerOpen(true);
            Arrange(view);
            var drawer = (StackPanel)Field(view, "_drawerBody");
            Assert.Contains(Descendants<TextBlock>(drawer), label => label.Text == "Task 3 – REST client");
            Assert.Contains(Descendants<TextBlock>(drawer), label => label.Text == "SQL Joins & Subqueries");
            Assert.Equal(Visibility.Collapsed, ((Border)Field(view, "_schoolFaceSlot")).Visibility);

            var day = Descendants<Button>(drawer).Single(button => (string)button.ToolTip == "Day");
            day.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Arrange(view);
            var columns = (Grid[])Field(view, "_schoolDayColumns");
            Assert.Equal(3, columns[0].Children.OfType<Grid>().Count(element => element.Height == 52));
            Assert.Equal(2, columns[1].Children.OfType<Grid>().Count(element => element.Height == 52));
            Assert.Equal("10:38", ((TextBlock)Field(view, "_schoolDayClock")).Text);
            Assert.InRange(view.MeasureContentHeight(600) + 30, 240, 580);
        }
        finally { Loc.SetLanguage(language); }
    });

    [Fact]
    public void CrossingTheLastDayAnimatesOnceAndHidingStopsTheClockAndDial() => SharedStaTestRunner.RunAsync(async ct =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        bool reduced = AnimationConfig.ReduceMotion;
        bool? animationOverride = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        var window = new Window { Content = view, Width = 600, Height = 360, Top = -20000, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            SchoolV3Tests.Seed(view);
            var school = SchoolV3Tests.Sample();
            var now = SchoolV3Tests.MockNow;
            var ledgerNow = DateTime.UtcNow;
            SetField(view, "_seen", new TabLedger(() => view.IsVisible, () => ledgerNow));
            SetField(view, "_schoolNow", (Func<DateTimeOffset>)(() => now));
            window.Show();
            var turn = (RotateTransform)Field(view, "_schoolRingTurn");
            var scale = (ScaleTransform)Field(view, "_schoolDialScale");
            var timer = (System.Windows.Threading.DispatcherTimer)Field(view, "_schoolCountdownTimer");
            Assert.True(timer.IsEnabled);
            Assert.False(IsTurning());

            now = school.Deadlines[0].Due.AddHours(-24);
            Invoke(view, "UpdateSchoolClock", now);
            Assert.True(IsTurning());
            Assert.True(Motion.IsRunning(scale, ScaleTransform.ScaleXProperty));
            await WpfFrameWaiter.UntilAsync(() => !IsTurning(), "school dial settles", ct);
            Invoke(view, "UpdateSchoolClock", now.AddMinutes(1));
            Assert.False(IsTurning());

            view.Visibility = Visibility.Collapsed;
            now = school.Deadlines[0].Due.AddHours(1);
            Invoke(view, "UpdateSchoolClock", now);
            Assert.False(timer.IsEnabled);
            Assert.False(scale.HasAnimatedProperties);
            Assert.False(turn.HasAnimatedProperties);
            Assert.False(((WeatherTween)Field(view, "_schoolLight")).HasAnimatedProperties);

            ledgerNow += TabLedger.Away;
            now = school.Deadlines[0].Due.AddHours(-25);
            view.Visibility = Visibility.Visible;
            Invoke(view, "UpdateSchoolClock", now);
            Assert.False(IsTurning());
            AnimationConfig.SetReduceMotion(true);
            Invoke(view, "UpdateSchoolClock", now.AddHours(2));
            Assert.False(IsTurning());
            Assert.False(scale.HasAnimatedProperties);

            bool IsTurning() => Motion.IsRunning(turn, RotateTransform.AngleProperty);
        }
        finally
        {
            window.Close();
            AnimationConfig.ClientAreaAnimationOverride = animationOverride;
            AnimationConfig.SetReduceMotion(reduced);
        }
    });

    [Fact]
    public void AStagedUploadGetsAConfirmPanelAndSuccessAdvancesTheFace() => SharedStaTestRunner.RunAsync(async _ =>
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        using var client = new PiDashboardClient(() => "http://127.0.0.1:1");
        using var view = new ControlCenterView { Page = ControlCenterPage.School };
        string file = System.IO.Path.GetTempFileName();
        try
        {
            view.AttachPi(client, () => false, () => false, () => "", () => "");
            SchoolV3Tests.Seed(view);
            var assignment = ((Dictionary<int, PiAssignment>)Field(view, "_piAssignments"))[71] with { Mode = "upload" };
            var deadline = SchoolV3Tests.Sample().Deadlines[0];
            Invoke(view, "StageSchoolSubmission", deadline, assignment, new[] { file });
            view.SetDrawerOpen(true);
            Arrange(view);
            var drawer = (StackPanel)Field(view, "_drawerBody");
            Assert.Contains(Descendants<Button>(drawer), button => Equals(button.ToolTip, "Confirm upload"));
            Assert.Contains(Descendants<TextBlock>(drawer), label => label.Text.EndsWith(System.IO.Path.GetFileName(file), StringComparison.Ordinal));

            await (Task)Invoke(view, "FinishSchoolSubmission", 71, new PiSubmitResult(true, "Submitted.sql", null), null!)!;
            Arrange(view);
            Assert.False(view.DrawerOpen);
            Assert.Equal("Task 3 – REST client", ((TextBlock)Field(view, "_schoolTitle")).Text);
            Assert.Equal("1 / 5", ((TextBlock)Field(view, "_schoolCount")).Text);
        }
        finally
        {
            System.IO.File.Delete(file);
            Loc.SetLanguage(language);
        }
    });


    private static void Arrange(ControlCenterView view)
    {
        var size = new Size(600, view.MeasureContentHeight(600));
        view.Arrange(new Rect(size));
        view.UpdateLayout();
    }
}
