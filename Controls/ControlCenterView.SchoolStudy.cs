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
    private string? _schoolStudyError;
    private readonly Dictionary<string, string> _schoolNoteDrafts = [];

    private void SelectStudy(PiExam? exam)
    {
        string? key = exam is null ? null : ExamKey(exam);
        if (_schoolSelectedExam == key) return;
        _schoolSelectedExam = key;
        _schoolFilePage = _schoolSessionPage = 0;
        _schoolStudyError = null;
        RedrawPi();
    }

    private static string StudyFolder(PiExam exam) => SchoolStudy.FolderFor(ExamSubject(exam), exam.Date, ExamTitle(exam));

    private void HookStudyDrop(Border target, PiExam exam)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            bool accepted = (e.AllowedEffects & DragDropEffects.Copy) != 0
                && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(File.Exists);
            target.Background = accepted ? SchoolHover : Brushes.Transparent;
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        target.DragLeave += (_, _) => target.Background = Brushes.Transparent;
        target.Drop += async (_, e) =>
        {
            target.Background = Brushes.Transparent;
            if ((e.AllowedEffects & DragDropEffects.Copy) == 0 || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            try
            {
                await Task.Run(() => SchoolStudy.CopyFiles(StudyFolder(exam), files));
                _schoolStudyError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _schoolStudyError = ex.Message;
            }
            if (_disposed) return;
            _schoolSelectedExam = ExamKey(exam);
            _schoolFilePage = 0;
            RedrawPi();
            if (IsVisible && !DrawerOpen) SetDrawerOpen(true);
        };
    }

    private void BuildStudyPanel(StackPanel body, PiExam exam)
    {
        string key = ExamKey(exam);
        var back = SchoolAction(0xE72B, Say("Übersicht", "Overview"), () => SelectStudy(null), iconOnly: false);
        var heading = SchoolText(ExamTitle(exam), 21, CompactInk, FontWeights.SemiBold);
        heading.ToolTip = heading.Text;
        body.Children.Add(SchoolColumns(heading, back));
        var details = SchoolColumns(SchoolMark(ExamSubject(exam)), SchoolText(exam.Date.ToString("ddd, d MMM", VNotch.Services.Loc.GetCulture()), 12));
        details.Height = 26;
        body.Children.Add(details);

        var papers = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var claude = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var material = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        material.ColumnDefinitions.Add(new ColumnDefinition());
        material.ColumnDefinitions.Add(new ColumnDefinition());
        material.Children.Add(papers);
        Grid.SetColumn(claude, 1);
        material.Children.Add(claude);
        var drop = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Child = material };
        var feedback = SchoolText(_schoolStudyError ?? "", 12);
        feedback.ToolTip = feedback.Text;
        body.Children.Add(drop);
        body.Children.Add(feedback);
        HookStudyDrop(drop, exam);

        try
        {
            string folder = StudyFolder(exam);
            var files = SchoolStudy.Files(folder);
            var sessions = SchoolStudy.Sessions(folder);
            int filePages = Math.Max(1, (files.Count + 2) / 3);
            int sessionPages = Math.Max(1, (sessions.Count + 3) / 4);
            _schoolFilePage = Math.Clamp(_schoolFilePage, 0, filePages - 1);
            _schoolSessionPage = Math.Clamp(_schoolSessionPage, 0, sessionPages - 1);
            papers.Children.Add(SchoolSection(Say("Material", "Materials"), _schoolFilePage, filePages, page => { _schoolFilePage = page; RedrawPi(); }));
            foreach (string path in files.Skip(_schoolFilePage * 3).Take(3))
            {
                string name = System.IO.Path.GetFileName(path);
                var label = SchoolText(name, 12, CompactInk);
                var file = SchoolButton(new Border { Height = 25, Background = Brushes.Transparent, Child = label }, name, () => SchoolFiles.Open([path]));
                file.Cursor = Cursors.Hand;
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
                papers.Children.Add(file);
            }

            string saved = SchoolStudy.ReadNote(folder);
            var noteLabel = SchoolText(Say("Notiz", "Note"), 16, CompactInk, FontWeights.SemiBold);
            var note = new TextBox
            {
                Text = _schoolNoteDrafts.GetValueOrDefault(key) ?? saved, FontSize = 12, Height = 56,
                Background = SchoolTile, Foreground = CompactInk, CaretBrush = CompactInk, BorderBrush = SchoolRim,
                Padding = new Thickness(6, 3, 6, 3), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxLength = 4000
            };
            System.Windows.Automation.AutomationProperties.SetName(note, noteLabel.Text);
            note.TextChanged += (_, _) => _schoolNoteDrafts[key] = note.Text;
            note.PreviewMouseLeftButtonDown += (_, _) =>
            {
                KeyboardInputRequested?.Invoke(true);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (note.IsVisible) Keyboard.Focus(note); });
            };
            note.LostKeyboardFocus += (_, _) => KeyboardInputRequested?.Invoke(false);
            var save = SchoolAction(0xE74E, Say("Notiz speichern", "Save note"), () => Act(() =>
            {
                SchoolStudy.SaveNote(folder, note.Text);
                _schoolNoteDrafts.Remove(key);
                feedback.Text = Say("Notiz gespeichert", "Note saved");
            }));
            note.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
                e.Handled = true;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            papers.Children.Add(SchoolColumns(noteLabel, save));
            papers.Children.Add(note);

            var launch = SchoolAction(0xE756, Say("Mit Claude lernen", "Study with Claude"), () => Act(() => SchoolStudy.OpenClaude(folder)), iconOnly: false);
            launch.HorizontalAlignment = HorizontalAlignment.Left;
            claude.Children.Add(launch);
            if (sessions.Count > 0)
                claude.Children.Add(SchoolSection(Say("Sitzungen", "Sessions"), _schoolSessionPage, sessionPages, page => { _schoolSessionPage = page; RedrawPi(); }));
            foreach (var session in sessions.Skip(_schoolSessionPage * 4).Take(4))
            {
                var name = SchoolText(session.Title, 12, CompactInk);
                name.Margin = new Thickness(0, 0, 4, 0);
                var resume = SchoolAction(0xE768, Say("Sitzung fortsetzen", "Resume session"), () => Act(() => SchoolStudy.OpenClaude(folder, session.Id)));
                var row = SchoolColumns(name, resume);
                row.Height = 25;
                row.ToolTip = $"{session.Title} · {session.LastWrite:g}";
                claude.Children.Add(row);
            }

            void Act(Action action)
            {
                try { action(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    feedback.Text = ex.Message;
                }
                feedback.ToolTip = feedback.Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            feedback.Text = ex.Message;
            feedback.ToolTip = ex.Message;
        }
    }
}
