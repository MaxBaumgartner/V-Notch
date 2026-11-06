using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// One click on a deadline, like the Pi dashboard's hand-in panel: a POSE exercise opens as an IntelliJ project,
/// the task's files download and open, and maths photos go back to Moodle as one PDF. Handing in is final, so
/// picked photos wait for a second, explicit click.
/// </summary>
public partial class ControlCenterView
{
    #region Deadline actions

    private readonly Dictionary<int, PiAssignment> _piAssignments = new();
    private readonly HashSet<int> _piAssignmentsLoading = new();
    private readonly Dictionary<int, PiTaskState> _piTasks = new();

    private const long PiMaxUploadBytes = 5L * 1024 * 1024;

    private enum PiTaskStep { Idle, Loading, Picked, Uploading, Done, Failed }

    /// <summary>Pages: picked maths photos (base64 JPEG); File: the one file picked for an NW hand-in.</summary>
    private sealed record PiTaskState(PiTaskStep Step, IReadOnlyList<string>? Pages = null, string? Message = null, string? File = null);

    private string PiTaskKey()
    {
        var sb = new StringBuilder();
        foreach (var (cmid, a) in _piAssignments) sb.Append(cmid).Append(a.Mode).Append(a.Attachments.Count).Append(';');
        foreach (var (cmid, t) in _piTasks) sb.Append(cmid).Append(t.Step).Append(t.Pages?.Count).Append(t.Message).Append(t.File).Append(';');
        return sb.ToString();
    }

    /// <summary>What the Pi knows about the assignment is fetched once; the row gets its buttons when it arrives.</summary>
    private async void LoadPiAssignment(int cmid)
    {
        if (_pi == null || cmid <= 0 || _piAssignments.ContainsKey(cmid) || !_piAssignmentsLoading.Add(cmid)) return;
        try
        {
            if (await _pi.GetAssignmentAsync(cmid) is { } assignment && !_disposed) _piAssignments[cmid] = assignment;
        }
        finally
        {
            _piAssignmentsLoading.Remove(cmid);
        }
        RedrawPi();
    }

    private void SetPiTask(int cmid, PiTaskState state)
    {
        if (state.Step == PiTaskStep.Idle) _piTasks.Remove(cmid);
        else _piTasks[cmid] = state;
        RedrawPi();
    }

    private void RedrawPi()
    {
        if (_disposed) return;
        _piKey = "";
        BuildPiPage();
    }

    /// <summary>The buttons at the end of a deadline row, by kind of assignment and where a hand-in stands.</summary>
    private FrameworkElement PiTaskActions(PiDeadline deadline, Grid row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        int cmid = deadline.Cmid;
        if (!_piAssignments.TryGetValue(cmid, out var task))
        {
            LoadPiAssignment(cmid);
            return panel;
        }
        _accent = Pink;
        _piTasks.TryGetValue(cmid, out var state);
        switch (state?.Step)
        {
            case PiTaskStep.Loading:
                panel.Children.Add(Note(Loc.Get("pi.task.loading"), Muted));
                return panel;
            case PiTaskStep.Uploading:
                panel.Children.Add(Note(Loc.Get("pi.task.uploading"), Muted));
                return panel;
            case PiTaskStep.Done:
                panel.Children.Add(Note("✓ " + Loc.Get("pi.task.submitted"), Mint));
                return panel;
            case PiTaskStep.Picked when state.File is { } file:
                panel.Children.Add(TextButton(Loc.Get("pi.task.submitFile", Path.GetFileName(file)), accent: true, () => SubmitPiFile(deadline, file)));
                panel.Children.Add(IconButton("\uE711", Loc.Get("pi.task.cancel"), () => SetPiTask(cmid, new(PiTaskStep.Idle))));
                return panel;
            case PiTaskStep.Picked when state.Pages is { Count: > 0 } pages:
                panel.Children.Add(TextButton(Loc.Get("pi.task.submitPages", pages.Count), accent: true, () => SubmitPiPhotos(deadline, task, pages)));
                panel.Children.Add(IconButton("", Loc.Get("pi.task.cancel"), () => SetPiTask(cmid, new(PiTaskStep.Idle))));
                return panel;
            case PiTaskStep.Failed:
                var failed = Note(state.Message ?? Loc.Get("pi.task.failed"), DueToday);
                failed.MaxWidth = 150;
                failed.ToolTip = state.Message;
                panel.Children.Add(failed);
                if (state.Pages is { Count: > 0 } kept)
                {
                    panel.Children.Add(TextButton(Loc.Get("pi.task.submitPages", kept.Count), accent: true, () => SubmitPiPhotos(deadline, task, kept)));
                    panel.Children.Add(IconButton("", Loc.Get("pi.task.cancel"), () => SetPiTask(cmid, new(PiTaskStep.Idle))));
                    return panel;
                }
                if (state.File is { } keptFile)
                {
                    panel.Children.Add(TextButton(Loc.Get("pi.task.submitFile", Path.GetFileName(keptFile)), accent: true, () => SubmitPiFile(deadline, keptFile)));
                    panel.Children.Add(IconButton("\uE711", Loc.Get("pi.task.cancel"), () => SetPiTask(cmid, new(PiTaskStep.Idle))));
                    return panel;
                }
                break;
        }

        // NW-Abgaben (NSCS): one file, picked or dropped on the row, handed in only after a second click.
        if (task.Mode == "upload")
        {
            var hand = TextButton(Loc.Get("pi.task.submit"), accent: true, () => PickPiFile(deadline, task));
            hand.ToolTip = Loc.Get("pi.task.dropFileHint");
            panel.Children.Add(hand);
            AcceptFileDrop(row, cmid);
            return panel;
        }

        if (task.Mode == "pose")
            panel.Children.Add(TextButton(Loc.Get("pi.task.project", task.Nr), accent: true, () =>
            {
                if (!SchoolFiles.OpenPoseProject(cmid)) SetPiTask(cmid, new(PiTaskStep.Failed, Message: Loc.Get("pi.task.poseMissing")));
            }));
        // WMC on Classroom 50: accept, clone into WMC\HÜn and open VS Code, all through the dashboard's pi-wmc:// tool.
        else if (task.Mode == "classroom")
            panel.Children.Add(TextButton(Loc.Get("pi.task.repo", task.Nr), accent: true, () =>
            {
                if (!SchoolFiles.OpenClassroomRepo(cmid)) SetPiTask(cmid, new(PiTaskStep.Failed, Message: Loc.Get("pi.task.wmcMissing")));
            }));
        else if (task.Attachments.Count > 0)
            panel.Children.Add(TextButton(Loc.Get("pi.task.files"), accent: false, () => DownloadPiTask(deadline, task)));

        if (task.Mode == "photo")
        {
            var submit = TextButton(Loc.Get("pi.task.submit"), accent: task.Blocked == null, () =>
            {
                if (task.Blocked == null) PickPiPhotos(deadline, task);
            });
            submit.ToolTip = task.Blocked ?? Loc.Get("pi.task.dropHint");
            if (task.Blocked != null) submit.Opacity = 0.45;
            panel.Children.Add(submit);
            AcceptPhotoDrops(row, deadline, task);
        }
        return panel;
    }

    private TextBlock Note(string text, Brush color)
    {
        var note = MakeLabel(text, 11.5, color);
        note.Margin = new Thickness(0, 0, 8, 0);
        return note;
    }

    /// <summary>The task's files into the school folder (Schule\3AHIF\DBI\01), then open (one document) or show (several).</summary>
    private async void DownloadPiTask(PiDeadline deadline, PiAssignment task)
    {
        if (_pi == null) return;
        int cmid = deadline.Cmid;
        SetPiTask(cmid, new(PiTaskStep.Loading));
        var files = new List<(string, byte[])>();
        for (int i = 0; i < task.Attachments.Count; i++)
        {
            if (await _pi.DownloadAttachmentAsync(cmid, i) is not { } bytes)
            {
                SetPiTask(cmid, new(PiTaskStep.Failed, Message: Loc.Get("pi.task.failed")));
                return;
            }
            files.Add((task.Attachments[i].Name, bytes));
        }
        try
        {
            string folder = SchoolFiles.FolderFor(deadline.Course, task.Nr);
            var paths = await Task.Run(() => SchoolFiles.Save(folder, files));
            SchoolFiles.Open(paths);
            SetPiTask(cmid, new(PiTaskStep.Idle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetPiTask(cmid, new(PiTaskStep.Failed, Message: ex.Message));
        }
    }

    private void PickPiPhotos(PiDeadline deadline, PiAssignment task)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("pi.task.pickTitle"),
            Multiselect = true,
            Filter = Loc.Get("pi.task.pickTitle") + "|" + string.Join(";", SchoolFiles.PhotoExtensions.Select(e => "*" + e)),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) PreparePiPages(deadline, task, dialog.FileNames);
    }

    /// <summary>Photos dropped on a maths deadline are taken like picked ones.</summary>
    private void AcceptPhotoDrops(Grid row, PiDeadline deadline, PiAssignment task)
    {
        if (task.Blocked != null) return;
        row.AllowDrop = true;
        if (row.Background == null) row.Background = Brushes.Transparent;
        row.DragOver += (_, e) =>
        {
            bool photos = e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(SchoolFiles.IsPhoto);
            e.Effects = photos ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        row.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) PreparePiPages(deadline, task, files);
        };
    }

    private async void PreparePiPages(PiDeadline deadline, PiAssignment task, IEnumerable<string> files)
    {
        int cmid = deadline.Cmid;
        var photos = SchoolFiles.InPageOrder(files);
        if (photos.Count == 0)
        {
            SetPiTask(cmid, new(PiTaskStep.Failed, Message: Loc.Get("pi.task.noPhotos")));
            return;
        }
        SetPiTask(cmid, new(PiTaskStep.Loading));
        var pages = await Task.Run(() => photos.Select(p => SchoolFiles.ToJpegPage(p)).Where(b => b != null)
            .Select(b => Convert.ToBase64String(b!)).ToList());
        SetPiTask(cmid, pages.Count == 0
            ? new(PiTaskStep.Failed, Message: Loc.Get("pi.task.noPhotos"))
            : new(PiTaskStep.Picked, pages));
    }

    private async void SubmitPiPhotos(PiDeadline deadline, PiAssignment task, IReadOnlyList<string> pages)
    {
        if (_pi == null) return;
        int cmid = deadline.Cmid;
        SetPiTask(cmid, new(PiTaskStep.Uploading));
        var result = await _pi.SubmitPhotosAsync(cmid, task.Filename, pages);
        if (!result.Ok)
        {
            // The pages stay: once whatever the Pi complained about is sorted, one more click hands them in.
            SetPiTask(cmid, new(PiTaskStep.Failed, pages, result.Error ?? Loc.Get("pi.task.failed")));
            return;
        }
        _piAssignments.Remove(cmid);
        SetPiTask(cmid, new(PiTaskStep.Done, Message: result.SubmittedName));
        // The deadline list drops it once Moodle shows the hand-in.
        _piSchoolAtUtc = DateTime.MinValue;
        await Task.Delay(1500);
        RefreshPi();
    }

    /// <summary>A file for an NW hand-in, starting in the task's school folder (where the script usually is).</summary>
    private void PickPiFile(PiDeadline deadline, PiAssignment task)
    {
        string folder = SchoolFiles.FolderFor(deadline.Course, task.Nr);
        string start = Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder) is { } parent && Directory.Exists(parent) ? parent : SchoolFiles.SchoolRoot;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("pi.task.pickFile"),
            InitialDirectory = Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SetPiTask(deadline.Cmid, new(PiTaskStep.Picked, File: dialog.FileName));
    }

    /// <summary>A file dropped on an NW task is taken like a picked one (the first, if several).</summary>
    private void AcceptFileDrop(Grid row, int cmid)
    {
        row.AllowDrop = true;
        if (row.Background == null) row.Background = Brushes.Transparent;
        row.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        row.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(File.Exists) is { } file)
                SetPiTask(cmid, new(PiTaskStep.Picked, File: file));
        };
    }

    private async void SubmitPiFile(PiDeadline deadline, string file)
    {
        if (_pi == null) return;
        int cmid = deadline.Cmid;
        byte[] bytes;
        try
        {
            if (new FileInfo(file).Length > PiMaxUploadBytes)
            {
                SetPiTask(cmid, new(PiTaskStep.Failed, Message: Loc.Get("pi.task.tooBig"), File: file));
                return;
            }
            bytes = await File.ReadAllBytesAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetPiTask(cmid, new(PiTaskStep.Failed, Message: ex.Message, File: file));
            return;
        }
        SetPiTask(cmid, new(PiTaskStep.Uploading));
        var result = await _pi.SubmitFileAsync(cmid, Path.GetFileName(file), bytes);
        if (!result.Ok)
        {
            // The file stays picked: one more click hands it in once whatever the site complained about is sorted.
            SetPiTask(cmid, new(PiTaskStep.Failed, Message: result.Error ?? Loc.Get("pi.task.failed"), File: file));
            return;
        }
        _piAssignments.Remove(cmid);
        SetPiTask(cmid, new(PiTaskStep.Done, Message: result.SubmittedName));
        _piSchoolAtUtc = DateTime.MinValue;
        await Task.Delay(1500);
        RefreshPi();
    }

    #endregion
}
