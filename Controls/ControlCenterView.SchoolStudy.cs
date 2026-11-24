using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private static string StudyFolder(PiExam exam) => SchoolStudy.FolderFor(ExamSubject(exam), exam.Date, ExamTitle(exam));

    private void OpenStudyFolder(PiExam exam)
    {
        try
        {
            string folder = StudyFolder(exam);
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
        try { ResumeSchoolSession(StudyFolder(exam)); }
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
        try { await Task.Run(() => SchoolStudy.CopyFiles(StudyFolder(exam), files)); }
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
        body.Children.Add(SchoolText($@"{when}　·　Lernen / {subject}", 13));

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
            string folder = StudyFolder(exam);
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
                var file = SchoolAsset("file", name, Say("Datei", "File"), () => Act(() => SchoolFiles.Open([path])));
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
                rows.Children.Add(file);
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

    private Button SchoolAsset(string glyph, string name, string end, Action action)
    {
        var icon = SchoolIcon(glyph, 17, SchoolLabel);
        icon.Margin = new Thickness(0, 0, 10, 0);
        var label = new DockPanel { Children = { icon, SchoolText(name, 14, CompactInk) } };
        var content = SchoolColumns(label, SchoolText(end, 12));
        return SchoolPaintButton(new Border { Height = 34, Background = Brushes.Transparent, BorderBrush = SchoolRim,
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(2, 0, 2, 0), Child = content }, name, action, SchoolHover);
    }
}
