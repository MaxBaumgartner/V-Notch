using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Models;

public enum SpotlightResultKind
{
    Application,
    File,
    Folder,
    Calculation,
    Settings,
    SystemAction,
    AddTodo
}

public sealed record SpotlightSearchItem(
    string Id,
    SpotlightResultKind Kind,
    string Title,
    string Subtitle,
    string Target,
    string? IconPath = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Equals(SpotlightSearchItem? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public double Score { get; init; }
    public bool CanPreviewFile => Kind == SpotlightResultKind.File && QuickLookBridge.IsPreviewPath(Target);
    public bool IsPreviewImage => Path.GetExtension(Target).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp";

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public bool IsRecent { get; init; }
    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }

    public bool IsShortcut => Kind == SpotlightResultKind.Application
        && string.Equals(Path.GetExtension(Target), ".lnk", StringComparison.OrdinalIgnoreCase);
}
