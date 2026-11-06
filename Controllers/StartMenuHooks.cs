using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>
/// Lets Spotlight stand in for the Windows Start menu on the keyboard: a lone tap of a Windows key is taken
/// away from Windows and reported instead. Win+key combinations, Ctrl+Esc and the taskbar's Start button
/// stay with Windows. A mouse click while Win is held makes the press a combination, not a tap. Both
/// low-level hooks live on one dedicated thread so a busy UI thread can never stall system input.
/// </summary>
internal sealed unsafe partial class StartMenuHooks : IDisposable
{
    private const string LogTag = "START-REPLACEMENT";
    // Marks the keys we inject so our own hook lets them through untouched.
    private static readonly nint InjectionMarker = 0x564E5354;
    private const ushort MaskKey = 0xE8; // unassigned: Windows sees Win+<nothing useful> and keeps Start closed
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmXButtonDown = 0x020B;

    private static StartMenuHooks? s_mouseOwner;

    private readonly Action _winTap;
    private readonly WinKeyTapDetector _detector = new();
    private readonly Thread _thread;
    private readonly LowLevelKeyboardHook _keyboard;
    private Dispatcher? _dispatcher;
    private nint _mouseHook;
    private int _disposed;

    internal bool IsInstalled { get; private set; }

    /// <param name="winTap">Called on the hook thread after a lone Win tap was taken from Windows.</param>
    internal StartMenuHooks(Action winTap)
    {
        _winTap = winTap;
        _keyboard = new LowLevelKeyboardHook(OnKey);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Start menu replacement hooks" };
        _thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    private void Run(TaskCompletionSource ready)
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            bool keyboard = _keyboard.Install();
            if (Interlocked.CompareExchange(ref s_mouseOwner, this, null) == null)
            {
                delegate* unmanaged[Stdcall]<int, nint, nint, nint> proc = &OnMouse;
                _mouseHook = SetWindowsHookExW(WhMouseLl, (nint)proc, GetModuleHandleW(null), 0);
                if (_mouseHook == 0) Interlocked.CompareExchange(ref s_mouseOwner, null, this);
            }
            IsInstalled = keyboard;
            if (!keyboard) RuntimeLog.Warn(LogTag, $"Keyboard hook failed (Win32={_keyboard.InstallationError})");
            if (_mouseHook == 0) RuntimeLog.Warn(LogTag, "Mouse hook for Win+click could not be installed");
            ready.TrySetResult();
            if (keyboard || _mouseHook != 0) Dispatcher.Run();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "Start menu replacement hooks failed");
        }
        finally
        {
            ready.TrySetResult();
            RemoveHooks();
        }
    }

    private void RemoveHooks()
    {
        _keyboard.Dispose();
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        Interlocked.CompareExchange(ref s_mouseOwner, null, this);
    }

    private bool OnKey(KBDLLHOOKSTRUCT key, int message)
    {
        if (Volatile.Read(ref _disposed) != 0 || key.dwExtraInfo == InjectionMarker) return false;
        bool down = message is WmKeyDown or WmSysKeyDown;
        bool up = message is WmKeyUp or WmSysKeyUp;
        if (!down && !up) return false;
        if (_detector.Process(key.vkCode, down) != WinKeyTapAction.Tap) return false;

        // Swallow the real Win-up and replay it behind an unassigned key, so Windows treats the
        // press as a combination and never opens Start.
        SendMaskedWinRelease((ushort)key.vkCode);
        _winTap();
        return true;
    }

    private static void SendMaskedWinRelease(ushort winVk)
    {
        const uint keyUp = 0x0002, extended = 0x0001;
        var inputs = stackalloc INPUT[3];
        inputs[0] = KeyInput(MaskKey, 0);
        inputs[1] = KeyInput(MaskKey, keyUp);
        inputs[2] = KeyInput(winVk, keyUp | extended);
        SendInput(3, inputs, sizeof(INPUT));
    }

    private static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = InjectionMarker },
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnMouse(int code, nint wParam, nint lParam)
    {
        try
        {
            var owner = Volatile.Read(ref s_mouseOwner);
            if (code >= 0 && owner != null && Volatile.Read(ref owner._disposed) == 0 &&
                (int)wParam is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
                owner._detector.NoteOtherInput();
        }
        catch
        {
            // Never let an exception unwind into user32's input chain.
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(() =>
        {
            RemoveHooks();
            dispatcher.InvokeShutdown();
        }, DispatcherPriority.Send);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    // The union in INPUT is as large as MOUSEINPUT; KEYBDINPUT shares its start.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);
}
