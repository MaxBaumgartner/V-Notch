using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using System.Windows.Threading;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private int _schoolFilePage, _schoolSessionPage;
    private bool _schoolNoteEditing;
    private string? _schoolStudyError;
    private readonly Dictionary<string, string> _schoolNoteDrafts = [];

    private void SelectStudy(PiExam exam)
    {
        string key = ExamKey(exam);
        if (_schoolSelectedExam != key)
        {
            _schoolSelectedExam = key;
            _schoolFilePage = _schoolSessionPage = 0;
            _schoolNoteEditing = false;
            _schoolStudyError = null;
        }
        _schoolTab = SchoolDrawerTab.Tests;
        RedrawPi();
        if (IsVisible && !DrawerOpen) SetDrawerOpen(true);
    }

    private readonly Func<PiExam, string> _studyFolder =
        static exam => SchoolStudy.FolderFor(exam.Subject.Length > 0 ? exam.Subject : exam.Title, exam.Date, ExamTitle(exam));

    /// <summary>Where the study folder lives, as the panel names it: "Schule / 3AHIF / POSE".</summary>
    private string StudyPlace(PiExam exam)
    {
        string? subjectFolder = Path.GetDirectoryName(_studyFolder(exam));
        return subjectFolder == null ? "Schule"
            : "Schule / " + Path.GetRelativePath(SchoolFiles.SchoolRoot, subjectFolder).Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ");
    }

    private void OpenStudyFolder(PiExam exam)
    {
        try
        {
            string folder = _studyFolder(exam);
            Directory.CreateDirectory(folder);
            Launch(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            SelectStudy(exam);
            _schoolStudyError = ex.Message;
            RedrawPi();
        }
    }

    private void ResumeStudy(PiExam exam)
    {
        try { ResumeSchoolSession(_studyFolder(exam)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            SelectStudy(exam);
            _schoolStudyError = ex.Message;
            RedrawPi();
        }
    }

    private static void ResumeSchoolSession(string folder)
    {
        if (SchoolStudy.Sessions(folder).FirstOrDefault() is not { } session)
            throw new InvalidOperationException(Say("Keine frühere Claude-Sitzung", "No previous Claude session"));
        SchoolStudy.OpenClaude(folder, session.Id);
    }

    private void HookStudyDrop(Panel target, PiExam exam)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) => target.Background = PreviewStudyDrop(e) ? SchoolHover : Brushes.Transparent;
        target.DragLeave += (_, _) => target.Background = Brushes.Transparent;
        target.Drop += async (_, e) =>
        {
            target.Background = Brushes.Transparent;
            await DropStudyFiles(exam, e);
        };
    }

    private static bool PreviewStudyDrop(DragEventArgs e)
    {
        bool accepted = (e.AllowedEffects & DragDropEffects.Copy) != 0
            && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(File.Exists);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return accepted;
    }

    private async Task DropStudyFiles(PiExam exam, DragEventArgs e)
    {
        if (!PreviewStudyDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        string? error = null;
        try { await Task.Run(() => SchoolStudy.CopyFiles(_studyFolder(exam), files)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        if (_disposed) return;
        SelectStudy(exam);
        _schoolFilePage = 0;
        _schoolStudyError = error;
        RedrawPi();
    }

    private void BuildStudyPanel(StackPanel body, PiExam exam)
    {
        string key = ExamKey(exam), subject = ExamSubject(exam);
        body.Children.Add(SchoolSourceRow(subject, Say("Test", "Test"), SchoolColor(subject, exam.Color)));
        var heading = SchoolHeading(ExamTitle(exam), 27, 1.24, .91, 550);
        heading.Margin = new Thickness(0, 6, 0, 6);
        heading.ToolTip = heading.Text;
        body.Children.Add(heading);
        string when = ExamWhen(exam);
        body.Children.Add(SchoolText($@"{when}　·　{StudyPlace(exam)}", 13));

        var rows = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(rows);
        HookStudyDrop(rows, exam);
        var feedback = SchoolText(_schoolStudyError ?? "", 13);
        feedback.Visibility = string.IsNullOrEmpty(feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
        var footer = new StackPanel();
        body.Children.Add(footer);
        body.Children.Add(feedback);

        try
        {
            string folder = _studyFolder(exam);
            var files = SchoolStudy.Files(folder);
            var sessions = SchoolStudy.Sessions(folder);
            const int fileSize = 3, sessionSize = 3;
            int filePages = Math.Max(1, (files.Count + fileSize - 1) / fileSize);
            int sessionPages = Math.Max(1, (sessions.Count + sessionSize - 1) / sessionSize);
            _schoolFilePage = Math.Clamp(_schoolFilePage, 0, filePages - 1);
            _schoolSessionPage = Math.Clamp(_schoolSessionPage, 0, sessionPages - 1);
            foreach (string path in files.Skip(_schoolFilePage * fileSize).Take(fileSize))
            {
                string name = Path.GetFileName(path);
                var file = SchoolAsset("file", name, "On this PC", () => Act(() => SchoolFiles.Open([path])), previewRow: true);
                Point? start = null;
                file.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(file);
                file.PreviewMouseLeftButtonUp += (_, _) => start = null;
                file.PreviewMouseMove += (_, e) =>
                {
                    if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
                    Point now = e.GetPosition(file);
                    if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    start = null;
                    DragDrop.DoDragDrop(file, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
                };
                rows.Children.Add(SchoolPreviewRow(file, name, path, () => QuickLookBridge.Shared.ToggleAsync(this, path)));
            }
            if (filePages > 1)
                rows.Children.Add(SchoolPageButtons(_schoolFilePage, filePages, page => { _schoolFilePage = page; RedrawPi(); }));
            rows.Children.Add(SchoolAsset("note", Say("Lernnotiz", "Study note"), Say("Bearbeiten", "Edit"), () =>
            {
                _schoolNoteEditing = !_schoolNoteEditing;
                RedrawPi();
            }));
            foreach (var session in sessions.Skip(_schoolSessionPage * sessionSize).Take(sessionSize))
            {
                var row = SchoolAsset("chat", session.Title, Say("Fortsetzen", "Resume"), () => Act(() => SchoolStudy.OpenClaude(folder, session.Id)));
                row.ToolTip = $"{session.Title} · {session.LastWrite:g}";
                rows.Children.Add(row);
            }
            if (sessionPages > 1)
                rows.Children.Add(SchoolPageButtons(_schoolSessionPage, sessionPages, page => { _schoolSessionPage = page; RedrawPi(); }));
            rows.Children.Add(SchoolAsset("chat", Say("Mit Claude lernen", "Study with Claude"), "", () => Act(() => SchoolStudy.OpenClaude(folder))));

            if (_schoolNoteEditing)
            {
                var note = new TextBox
                {
                    Text = _schoolNoteDrafts.GetValueOrDefault(key) ?? SchoolStudy.ReadNote(folder), FontSize = 15, Height = 60,
                    Background = Frozen("#171719"), Foreground = CompactInk, CaretBrush = CompactInk, BorderBrush = Frozen("#48484C"),
                    Padding = new Thickness(8), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxLength = 4000,
                    Margin = new Thickness(0, 8, 0, 4), FontFamily = SchoolFont
                };
                System.Windows.Automation.AutomationProperties.SetName(note, Say("Lernnotiz", "Study note"));
                note.TextChanged += (_, _) => _schoolNoteDrafts[key] = note.Text;
                note.PreviewMouseLeftButtonDown += (_, _) =>
                {
                    KeyboardInputRequested?.Invoke(true);
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (note.IsVisible) Keyboard.Focus(note); });
                };
                note.LostKeyboardFocus += (_, _) => KeyboardInputRequested?.Invoke(false);
                var save = SchoolTab(Say("Notiz speichern", "Save note"), false, () => Act(() =>
                {
                    SchoolStudy.SaveNote(folder, note.Text);
                    _schoolNoteDrafts.Remove(key);
                    _schoolNoteEditing = false;
                    RedrawPi();
                }));
                save.HorizontalAlignment = HorizontalAlignment.Left;
                note.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
                    e.Handled = true;
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                footer.Children.Add(note);
                footer.Children.Add(save);
            }

            void Act(Action action)
            {
                try { action(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    feedback.Text = ex.Message;
                    feedback.ToolTip = feedback.Text;
                    feedback.Visibility = Visibility.Visible;
                    var height = MeasureDrawer();
                    if (Math.Abs(DrawerHeight - height) > .5) SetDrawerOpen(true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            feedback.Text = ex.Message;
            feedback.ToolTip = feedback.Text;
            feedback.Visibility = Visibility.Visible;
        }
    }

    private Button SchoolAsset(string glyph, string name, string end, Action action, bool endChanged = false, bool previewRow = false)
    {
        var icon = SchoolIcon(glyph, 17, SchoolLabel);
        icon.Margin = new Thickness(0, 0, 10, 0);
        var label = new DockPanel { Children = { icon, SchoolText(name, 14, CompactInk) } };
        var status = SchoolText(end, 12, previewRow ? Frozen("#A6A7AD") : null);
        if (endChanged && !AnimationConfig.ReduceMotion)
        {
            status.Loaded += (_, _) =>
            {
                if (status.IsVisible) Motion.To(status, OpacityProperty, 1, Motion.PressDown, from: .55);
            };
            status.IsVisibleChanged += (_, _) =>
            {
                if (!status.IsVisible) Motion.Stop(status, OpacityProperty, 1);
            };
        }
        var content = SchoolColumns(label, status);
        return SchoolPaintButton(new Border { Height = previewRow ? 33 : 34, Background = Brushes.Transparent, BorderBrush = SchoolRim,
            BorderThickness = new Thickness(0, 0, 0, previewRow ? 0 : 1), Padding = new Thickness(2, 0, 2, 0), Child = content },
            name, action, previewRow ? Brushes.Transparent : SchoolHover);
    }

    private Border SchoolPreviewRow(Button file, string name, object key, Func<Task> preview)
    {
        var row = new Grid();
        var frame = new Border { CornerRadius = new CornerRadius(8), Child = row, BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0, 7, 0), Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(file);
        var eye = new QuickLookEye
        {
            Host = frame, PreviewOwner = this, PreviewKey = key, Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Preview {name}"
        };
        System.Windows.Automation.AutomationProperties.SetName(eye, $"Preview {name}");
        eye.Click += async (_, e) =>
        {
            e.Handled = true;
            await preview();
        };
        Grid.SetColumn(eye, 1);
        row.Children.Add(eye);
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        var active = new DataTrigger { Binding = new Binding(nameof(QuickLookEye.Active)) { Source = eye }, Value = true };
        active.Setters.Add(new Setter(BackgroundProperty, Frozen("#13FFFFFF")));
        active.Setters.Add(new Setter(BorderBrushProperty, Frozen("#0CFFFFFF")));
        style.Triggers.Add(active);
        frame.Style = style;
        return frame;
    }

    internal bool CloseSchoolPreview()
    {
        if (Page != ControlCenterPage.School || !QuickLookBridge.Shared.IsPreviewing(this)) return false;
        EndSchoolPreview();
        return true;
    }

    private void EndSchoolPreview()
    {
        if (Page != ControlCenterPage.School) return;
        _piPreviewRequest++;
        _ = QuickLookBridge.Shared.CloseAsync(this);
    }
}
