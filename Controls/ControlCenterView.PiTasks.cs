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

    private void AddSchoolTaskActions(PiDeadline deadline, StackPanel actions, Grid dropTarget, bool compact = false)
    {
        int cmid = deadline.Cmid;
        var task = _piAssignments.GetValueOrDefault(cmid);
        var state = _piTasks.GetValueOrDefault(cmid);
        var push = _schoolPush.GetValueOrDefault(AssignmentKey(deadline));
        bool classroom = IsClassroomTask(deadline);
        bool canSubmit = task?.Mode is "upload" or "photo";
        string primary = classroom ? "Push" : Say("Abgeben", "Submit");
        Action act = classroom ? () => BeginSchoolPush(deadline) : () => PickSchoolSubmission(deadline, task!);
        var main = compact ? SchoolIconButton(classroom ? "push" : "up", primary, act, 24, 28, 15)
            : SchoolPrimary(classroom ? "push" : "up", primary, act);
        main.IsEnabled = task != null && (classroom || canSubmit && task.Blocked == null)
            && state?.Step is not (PiTaskStep.Loading or PiTaskStep.Uploading) && push?.Busy != true;
        main.ToolTip = task?.Blocked ?? push?.Message ?? state?.Message ?? primary;
        if (classroom || canSubmit || task == null && cmid > 0) actions.Children.Add(main);
        if (!main.IsEnabled && !compact) main.Opacity = .52;

        if (task?.Attachments.Count > 0 || DeadlineLink(deadline) != null)
        {
            var label = classroom ? Say("Angabe öffnen", "Open assignment brief") :
                task?.Attachments.Count > 0 ? Say("Aufgabe laden", "Download task") : Say("Aufgabe öffnen", "Open assignment");
            AddIcon("down", label, () =>
            {
                if (classroom && DeadlineLink(deadline) is { } brief) _ = OpenAsync(brief);
                else if (task?.Attachments.Count > 0) DownloadPiTask(deadline, task);
                else if (DeadlineLink(deadline) is { } target) _ = OpenAsync(target);
            });
        }
        AddIcon("folder", DeadlineSubject(deadline) == "POSE" ? Say("In IntelliJ öffnen", "Open in IntelliJ")
            : Say("Projektordner öffnen", "Open project folder"), () => OpenTaskFolder(deadline, task));
        AddIcon("chat", Say("Claude-Sitzung fortsetzen", "Resume Claude session"), () => ResumeTask(deadline, task));
        if (canSubmit && task!.Blocked == null && state?.Step is not (PiTaskStep.Loading or PiTaskStep.Uploading))
            HookSubmissionDrop(dropTarget, deadline, task);

        void AddIcon(string glyph, string label, Action click) => actions.Children.Add(compact
            ? SchoolIconButton(glyph, label, click, 24, 28, 15) : SchoolTool(glyph, label, click));

        async Task OpenAsync(string url) => Launch(await SignInToSchoolAsync(url));
    }

    private void OpenTaskPanel(PiDeadline deadline, bool confirm = false)
    {
        string key = AssignmentKey(deadline);
        _schoolTab = SchoolDrawerTab.Work;
        _schoolSelectedTask = key;
        _schoolConfirmTask = confirm ? key : null;
        RedrawPi();
        if (IsVisible && !DrawerOpen) SetDrawerOpen(true);
    }

    private void BeginSchoolPush(PiDeadline deadline)
    {
        string key = AssignmentKey(deadline);
        if (_schoolPush.GetValueOrDefault(key)?.Busy == true) return;
        _schoolArmedPush = key;
        _schoolPushUntil = DateTime.UtcNow.AddSeconds(8);
        _schoolPush.Remove(key);
        OpenTaskPanel(deadline, confirm: true);
    }

    private void BuildTaskPanel(StackPanel body, PiDeadline deadline)
    {
        string key = AssignmentKey(deadline);
        int cmid = deadline.Cmid;
        var task = _piAssignments.GetValueOrDefault(cmid);
        var state = _piTasks.GetValueOrDefault(cmid);
        var push = _schoolPush.GetValueOrDefault(key);
        string subject = DeadlineSubject(deadline);
        body.Children.Add(SchoolSourceRow(subject, TaskSource(deadline), SchoolColor(subject)));
        var title = SchoolHeading(deadline.Name, 27, 1.24, .91, 550);
        title.Margin = new Thickness(0, 6, 0, 6);
        body.Children.Add(title);
        body.Children.Add(SchoolText(TaskWhen(deadline.Due), 13));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 19, 0, 19) };
        bool armed = _schoolArmedPush == key;
        bool staged = _schoolConfirmTask == key && (armed || state?.File != null || state?.Pages?.Count > 0);
        if (staged)
        {
            string selection = armed ? Say("Lokaler Commit　·　Push zu origin", "Local commit　·　Push to origin")
                : state!.File is { } file ? Say($"Ausgewählte Datei　·　{Path.GetFileName(file)}", $"Selected file　·　{Path.GetFileName(file)}")
                : Loc.Get("pi.task.submitPages", state.Pages!.Count);
            var details = SchoolText(selection, 13);
            details.Margin = new Thickness(0, 12, 0, 0);
            details.ToolTip = selection;
            body.Children.Add(details);
            actions.Children.Add(SchoolPrimary(null, armed ? Say("Push bestätigen", "Confirm push") : Say("Upload bestätigen", "Confirm upload"), () =>
            {
                if (armed && task != null) PushSchoolTask(deadline, task);
                else if (state?.File is { } upload) SubmitPiFile(deadline, upload);
                else if (state?.Pages is { } pages && task != null) SubmitPiPhotos(deadline, task, pages);
            }));
            var cancel = SchoolTab(Say("Abbrechen", "Cancel"), false, () =>
            {
                _schoolArmedPush = _schoolConfirmTask = null;
                SetPiTask(cmid, new(PiTaskStep.Idle));
            });
            cancel.Margin = new Thickness(6, 0, 0, 0);
            actions.Children.Add(cancel);
        }
        else
        {
            var drop = new Grid { Background = Brushes.Transparent, Children = { actions } };
            AddSchoolTaskActions(deadline, actions, drop);
            if (state?.Step == PiTaskStep.Picked || state?.File != null || state?.Pages?.Count > 0)
                actions.Children.Add(SchoolTab(Say("Auswahl prüfen", "Review selection"), false, () => OpenTaskPanel(deadline, confirm: true)));
            body.Children.Add(drop);
        }
        string? status = state?.Step switch
        {
            PiTaskStep.Loading => Loc.Get("pi.task.loading"),
            PiTaskStep.Uploading => Loc.Get("pi.task.uploading"),
            _ => state?.Message
        };
        status ??= push?.Busy == true ? Say("Push läuft…", "Pushing…") : push?.Message;
        if (!string.IsNullOrEmpty(status))
        {
            var message = SchoolText(status, 13);
            message.Margin = new Thickness(0, 8, 0, 0);
            message.ToolTip = status;
            body.Children.Add(message);
        }
        if (staged) body.Children.Add(actions);
    }

    private void OpenTaskFolder(PiDeadline deadline, PiAssignment? task)
    {
        try
        {
            string folder = TaskFolder(deadline, task);
            if (DeadlineSubject(deadline) == "POSE") SchoolClassroom.OpenIntelliJ(folder);
            else Launch(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _schoolPush[AssignmentKey(deadline)] = new(ex.Message);
            OpenTaskPanel(deadline);
        }
    }

    private void ResumeTask(PiDeadline deadline, PiAssignment? task)
    {
        try { ResumeSchoolSession(TaskFolder(deadline, task)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _schoolPush[AssignmentKey(deadline)] = new(ex.Message);
            OpenTaskPanel(deadline);
        }
    }

    private static string TaskFolder(PiDeadline deadline, PiAssignment? task)
    {
        string subject = DeadlineSubject(deadline);
        string exercise = task?.Nr ?? "";
        string folder = SchoolClassroom.FindClone(subject, deadline.Course, exercise)
            ?? SchoolFiles.FolderFor($"{subject} {deadline.Course}", exercise);
        return Directory.Exists(folder) ? folder : throw new DirectoryNotFoundException(Say("Projektordner nicht gefunden", "Project folder not found"));
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
        if (_piTasks.GetValueOrDefault(cmid)?.Step is PiTaskStep.Loading or PiTaskStep.Uploading or PiTaskStep.Done) return;
        if (task.Mode == "upload")
        {
            if (paths.FirstOrDefault(File.Exists) is { } file)
            {
                SetPiTask(cmid, new(PiTaskStep.Picked, File: file));
                OpenTaskPanel(deadline, confirm: true);
            }
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
        OpenTaskPanel(deadline, confirm: true);
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
        if (DrawerOpen) SetDrawerOpen(false);
        _schoolSelectedTask = _schoolConfirmTask = null;
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
        if (_schoolArmedPush != key || DateTime.UtcNow >= _schoolPushUntil) return;

        _schoolArmedPush = null;
        _schoolPush[key] = new(null, Busy: true);
        RedrawPi();
        try
        {
            string folder = ClassroomClone(deadline, task);
            await SchoolClassroom.PushAsync(folder, deadline.Name);
            _schoolPush[key] = new(null, Done: true);
            if (DrawerOpen) SetDrawerOpen(false);
            _schoolSelectedTask = _schoolConfirmTask = null;
            _schoolBloom++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            _schoolPush[key] = new(ex.Message);
        }
        RedrawPi();
    }

    private static string ClassroomClone(PiDeadline deadline, PiAssignment task) =>
        SchoolClassroom.FindClone(DeadlineSubject(deadline), deadline.Course, task.Nr)
        ?? throw new DirectoryNotFoundException(Say("Kein eindeutiger lokaler Clone gefunden", "No unique local clone found"));
}
