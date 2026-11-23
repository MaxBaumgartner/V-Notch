using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private readonly Dictionary<int, PiAssignment> _piAssignments = [];
    private readonly HashSet<int> _piAssignmentsLoading = [];
    private readonly Dictionary<int, DateTime> _piAssignmentRetries = [];
    private readonly Dictionary<int, PiTaskState> _piTasks = [];
    private readonly Dictionary<string, SchoolPushState> _schoolPush = [];
    private string? _schoolArmedPush;
    private DateTime _schoolPushUntil;
    private const long PiMaxUploadBytes = 5L * 1024 * 1024;
    private const int PiMaxPhotoPages = 40;

    private enum PiTaskStep { Idle, Loading, Picked, Uploading, Done, Failed }
    private sealed record PiTaskState(PiTaskStep Step, IReadOnlyList<string>? Pages = null, string? Message = null, string? File = null);
    private sealed record SchoolPushState(string? Message, bool Busy = false, bool Done = false);

    private async void LoadPiAssignment(int cmid)
    {
        if (_pi == null || cmid <= 0 || !IsVisible || _piAssignments.ContainsKey(cmid)
            || _piAssignmentRetries.GetValueOrDefault(cmid) > DateTime.UtcNow || !_piAssignmentsLoading.Add(cmid)) return;
        try
        {
            if (await _pi.GetAssignmentAsync(cmid) is { } assignment && !_disposed) _piAssignments[cmid] = assignment;
            else _piAssignmentRetries[cmid] = DateTime.UtcNow.AddSeconds(30);
        }
        finally { _piAssignmentsLoading.Remove(cmid); }
        RedrawPi();
    }

    private void SetPiTask(int cmid, PiTaskState state)
    {
        if (_disposed) return;
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

    private void AddSchoolTaskActions(PiDeadline deadline, StackPanel actions, Grid dropTarget, bool compact)
    {
        void Add(int glyph, string text, Action action, bool enabled = true)
        {
            var button = SchoolAction(glyph, text, action, iconOnly: compact);
            button.Margin = new Thickness(0, 0, compact ? 0 : 6, 0);
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : .48;
            actions.Children.Add(button);
        }

        void OpenAssignment()
        {
            if (DeadlineLink(deadline) is { } target) _ = OpenAsync(target);

            async Task OpenAsync(string url) => Launch(await SignInToSchoolAsync(url));
        }

        void Message(string text, int glyph = 0xE946)
        {
            if (compact) Add(glyph, text, () => { }, enabled: false);
            else
            {
                var label = SchoolText(text, 12);
                label.MaxWidth = 228;
                label.ToolTip = text;
                actions.Children.Add(label);
            }
        }

        int cmid = deadline.Cmid;
        _piAssignments.TryGetValue(cmid, out var task);
        _piTasks.TryGetValue(cmid, out var state);
        if (task?.Mode is "classroom" or "pose" || deadline.Kind == "classroom")
        {
            string key = AssignmentKey(deadline);
            var push = _schoolPush.GetValueOrDefault(key);
            if (push?.Busy == true) { Message(Say("Push läuft…", "Pushing…")); return; }
            bool armed = _schoolArmedPush == key;
            if (task == null)
            {
                if (DeadlineLink(deadline) != null) Add(0xE8A7, Say("Aufgabe", "Assignment"), OpenAssignment);
                return;
            }
            string text = armed ? Say("Push bestätigen", "Confirm push") : push?.Done == true ? Say("Gepusht", "Pushed") : "Push";
            Add(push?.Done == true ? 0xE73E : 0xE74A, text, () => PushSchoolTask(deadline, task));
            Add(0xE8A7, Say("In IntelliJ öffnen", "Open in IntelliJ"), () => OpenClassroomTask(deadline, task));
            if (push?.Message is { } message)
            {
                if (compact) actions.ToolTip = message;
                else Message(message);
            }
            return;
        }

        if (state?.Step is PiTaskStep.Loading or PiTaskStep.Uploading)
        {
            Message(Loc.Get(state.Step == PiTaskStep.Loading ? "pi.task.loading" : "pi.task.uploading"));
            return;
        }
        if (state?.Step == PiTaskStep.Done)
        {
            Message(Loc.Get("pi.task.submitted"), 0xE73E);
            return;
        }
        if (state?.File is { } file && state.Step is PiTaskStep.Picked or PiTaskStep.Failed)
        {
            Add(0xE898, Say("Abgabe bestätigen", "Confirm submission"), () => SubmitPiFile(deadline, file));
            Add(0xE711, Loc.Get("pi.task.cancel"), () => SetPiTask(cmid, new(PiTaskStep.Idle)));
            if (state.Message is { } error && !compact) Message(error);
            else actions.ToolTip = state.Message ?? Path.GetFileName(file);
            return;
        }
        if (state?.Pages is { Count: > 0 } pages && task != null)
        {
            Add(0xE898, Loc.Get("pi.task.submitPages", pages.Count), () => SubmitPiPhotos(deadline, task, pages));
            Add(0xE711, Loc.Get("pi.task.cancel"), () => SetPiTask(cmid, new(PiTaskStep.Idle)));
            if (state.Message is { } error && !compact) Message(error);
            return;
        }
        if (task?.Attachments.Count > 0)
            Add(0xE896, Say("Aufgabe laden", "Download task"), () => DownloadPiTask(deadline, task));
        else if (DeadlineLink(deadline) != null)
            Add(0xE8A7, Say("Aufgabe öffnen", "Open assignment"), OpenAssignment);

        if (task?.Mode is "upload" or "photo")
        {
            bool photo = task.Mode == "photo";
            Add(0xE898, Loc.Get("pi.task.submit"), () => PickSchoolSubmission(deadline, task), enabled: task.Blocked == null);
            actions.ToolTip = task.Blocked ?? (photo ? Loc.Get("pi.task.dropHint") : Loc.Get("pi.task.dropFileHint"));
            if (task.Blocked == null) HookSubmissionDrop(dropTarget, deadline, task);
        }
        if (state?.Message is { } problem)
        {
            if (compact) actions.ToolTip = problem;
            else Message(problem);
        }
    }

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
        if (_disposed) return;
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "V-Notch", "School", cmid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var paths = await Task.Run(() => SchoolFiles.Save(folder, files));
            if (!_disposed) SchoolFiles.Open(paths);
            SetPiTask(cmid, new(PiTaskStep.Idle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetPiTask(cmid, new(PiTaskStep.Failed, Message: ex.Message));
        }
    }

    private void PickSchoolSubmission(PiDeadline deadline, PiAssignment task)
    {
        bool photo = task.Mode == "photo";
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get(photo ? "pi.task.pickTitle" : "pi.task.pickFile"),
            Multiselect = photo,
            InitialDirectory = Environment.GetFolderPath(photo ? Environment.SpecialFolder.MyPictures : Environment.SpecialFolder.MyDocuments)
        };
        if (photo) dialog.Filter = Loc.Get("pi.task.pickTitle") + "|" + string.Join(";", SchoolFiles.PhotoExtensions.Select(e => "*" + e));
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) StageSchoolSubmission(deadline, task, dialog.FileNames);
    }

    private void HookSubmissionDrop(Grid row, PiDeadline deadline, PiAssignment task)
    {
        row.AllowDrop = true;
        DragEventHandler over = (_, e) =>
        {
            bool canCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;
            bool valid = e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(f => File.Exists(f) && (task.Mode != "photo" || SchoolFiles.IsPhoto(f)));
            e.Effects = canCopy && valid ? DragDropEffects.Copy : DragDropEffects.None;
            row.Background = e.Effects == DragDropEffects.Copy ? SchoolHover : Brushes.Transparent;
            e.Handled = true;
        };
        DragEventHandler leave = (_, _) => row.Background = Brushes.Transparent;
        DragEventHandler drop = (_, e) =>
        {
            over(row, e);
            row.Background = Brushes.Transparent;
            if (e.Effects == DragDropEffects.Copy && e.Data.GetData(DataFormats.FileDrop) is string[] files)
                StageSchoolSubmission(deadline, task, files);
        };
        row.DragOver += over;
        row.DragLeave += leave;
        row.Drop += drop;
        if (ReferenceEquals(row, _schoolHero))
            _removeSchoolHeroDrop = () =>
            {
                row.DragOver -= over;
                row.DragLeave -= leave;
                row.Drop -= drop;
                row.AllowDrop = false;
                row.Background = Brushes.Transparent;
            };
    }

    private Action? _removeSchoolHeroDrop;

    private async void StageSchoolSubmission(PiDeadline deadline, PiAssignment task, IEnumerable<string> paths)
    {
        int cmid = deadline.Cmid;
        if (task.Mode == "upload")
        {
            if (paths.FirstOrDefault(File.Exists) is { } file) SetPiTask(cmid, new(PiTaskStep.Picked, File: file));
            return;
        }
        var photos = SchoolFiles.InPageOrder(paths.Where(File.Exists));
        if (photos.Count is 0 or > PiMaxPhotoPages)
        {
            SetPiTask(cmid, new(PiTaskStep.Failed, Message: photos.Count == 0 ? Loc.Get("pi.task.noPhotos")
                : Say("Höchstens 40 Fotos auswählen", "Select up to 40 photos")));
            return;
        }
        SetPiTask(cmid, new(PiTaskStep.Loading));
        var pages = await Task.Run(() => photos.Select(path => SchoolFiles.ToJpegPage(path)).Where(b => b != null)
            .Select(b => Convert.ToBase64String(b!)).ToList());
        SetPiTask(cmid, pages.Count != photos.Count
            ? new(PiTaskStep.Failed, Message: Say("Mindestens ein Foto konnte nicht gelesen werden", "At least one photo could not be read"))
            : new(PiTaskStep.Picked, pages));
    }

    private async void SubmitPiPhotos(PiDeadline deadline, PiAssignment task, IReadOnlyList<string> pages)
    {
        if (_pi == null || _piTasks.GetValueOrDefault(deadline.Cmid)?.Step == PiTaskStep.Uploading) return;
        SetPiTask(deadline.Cmid, new(PiTaskStep.Uploading));
        var result = await _pi.SubmitPhotosAsync(deadline.Cmid, task.Filename, pages);
        await FinishSchoolSubmission(deadline.Cmid, result, new(PiTaskStep.Failed, pages, result.Error ?? Loc.Get("pi.task.failed")));
    }

    private async void SubmitPiFile(PiDeadline deadline, string file)
    {
        if (_pi == null || _piTasks.GetValueOrDefault(deadline.Cmid)?.Step == PiTaskStep.Uploading) return;
        int cmid = deadline.Cmid;
        SetPiTask(cmid, new(PiTaskStep.Uploading));
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
        var result = await _pi.SubmitFileAsync(cmid, Path.GetFileName(file), bytes);
        await FinishSchoolSubmission(cmid, result, new(PiTaskStep.Failed, Message: result.Error ?? Loc.Get("pi.task.failed"), File: file));
    }

    private async Task FinishSchoolSubmission(int cmid, PiSubmitResult result, PiTaskState failed)
    {
        if (_disposed) return;
        if (!result.Ok) { SetPiTask(cmid, failed); return; }
        _schoolBloom++;
        SetPiTask(cmid, new(PiTaskStep.Done, Message: result.SubmittedName));
        _piSchoolAtUtc = DateTime.MinValue;
        await Task.Delay(1500);
        RefreshPi();
    }

    private async void PushSchoolTask(PiDeadline deadline, PiAssignment task)
    {
        string key = AssignmentKey(deadline);
        if (_schoolPush.GetValueOrDefault(key)?.Busy == true) return;
        if (_schoolArmedPush != key || DateTime.UtcNow >= _schoolPushUntil)
        {
            _schoolArmedPush = key;
            _schoolPushUntil = DateTime.UtcNow.AddSeconds(8);
            _schoolPush.Remove(key);
            RedrawPi();
            return;
        }

        _schoolArmedPush = null;
        _schoolPush[key] = new(null, Busy: true);
        RedrawPi();
        try
        {
            string folder = ClassroomClone(deadline, task);
            await SchoolClassroom.PushAsync(folder, deadline.Name);
            _schoolPush[key] = new(null, Done: true);
            _schoolBloom++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            _schoolPush[key] = new(ex.Message);
        }
        RedrawPi();
    }

    private void OpenClassroomTask(PiDeadline deadline, PiAssignment task)
    {
        try { SchoolClassroom.OpenIntelliJ(ClassroomClone(deadline, task)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _schoolPush[AssignmentKey(deadline)] = new(ex.Message);
            RedrawPi();
        }
    }

    private static string ClassroomClone(PiDeadline deadline, PiAssignment task) =>
        SchoolClassroom.FindClone(DeadlineSubject(deadline), deadline.Course, task.Nr)
        ?? throw new DirectoryNotFoundException(Say("Kein eindeutiger lokaler Clone gefunden", "No unique local clone found"));
}
