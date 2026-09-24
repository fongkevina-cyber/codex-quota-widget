using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Owns a native notification-area icon while using a lightweight WPF context menu.
/// This avoids loading the full Windows Forms stack into the always-on widget.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WindowMessageApp + 0x51;
    private const uint WindowMessageApp = 0x8000;
    private const uint WindowMessageLeftButtonDoubleClick = 0x0203;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const int ErrorClassAlreadyExists = 1410;
    private const string WindowClassName = "CodexQuotaWidget.NativeTrayWindow.v1";

    private static readonly ConcurrentDictionary<nint, TrayIconService> Instances = new();
    private static readonly WindowProcedureCallback WindowProcedureDelegate = WindowProcedure;
    private static readonly object WindowClassLock = new();

    private readonly Action _toggleWindow;
    private readonly Func<bool> _toggleTopmost;
    private readonly Func<Task> _refresh;
    private readonly Func<bool, bool> _setStartup;
    private readonly Func<Task> _exit;
    private readonly Func<bool> _isWindowVisible;
    private readonly Func<bool> _isTopmost;
    private readonly Func<bool> _isStartupEnabled;
    private readonly Func<bool>? _toggleTaskbarHost;
    private readonly Func<bool>? _isTaskbarHosted;
    private readonly AppLogger _logger;
    private readonly ContextMenu _contextMenu;
    private readonly MenuItem _visibilityItem;
    private readonly MenuItem _topmostItem;
    private readonly MenuItem _startupItem;
    private readonly MenuItem? _hostModeItem;
    private readonly nint _instanceHandle;
    private readonly uint _taskbarCreatedMessage;
    private nint _windowHandle;
    private nint _iconHandle;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconService(
        Action toggleWindow,
        Func<bool> toggleTopmost,
        Func<Task> refresh,
        Func<bool, bool> setStartup,
        Func<Task> exit,
        Func<bool> isWindowVisible,
        Func<bool> isTopmost,
        Func<bool> isStartupEnabled,
        AppLogger logger,
        Func<bool>? toggleTaskbarHost = null,
        Func<bool>? isTaskbarHosted = null)
    {
        _toggleWindow = toggleWindow;
        _toggleTopmost = toggleTopmost;
        _refresh = refresh;
        _setStartup = setStartup;
        _exit = exit;
        _isWindowVisible = isWindowVisible;
        _isTopmost = isTopmost;
        _isStartupEnabled = isStartupEnabled;
        _toggleTaskbarHost = toggleTaskbarHost;
        _isTaskbarHosted = isTaskbarHosted;
        _logger = logger;

        _visibilityItem = new MenuItem { FontWeight = FontWeights.SemiBold };
        _visibilityItem.Click += (_, _) => _toggleWindow();

        _topmostItem = new MenuItem
        {
            Header = "始终置顶",
            IsCheckable = true,
        };
        _topmostItem.Click += (_, _) => _topmostItem.IsChecked = _toggleTopmost();

        if (_toggleTaskbarHost is not null && _isTaskbarHosted is not null)
        {
            _hostModeItem = new MenuItem
            {
                Header = "嵌入任务栏",
                IsCheckable = true,
            };
            _hostModeItem.Click += (_, _) => _hostModeItem.IsChecked = _toggleTaskbarHost();
        }

        var refreshItem = new MenuItem { Header = "立即刷新" };
        refreshItem.Click += async (_, _) => await InvokeSafelyAsync(_refresh, "tray_refresh_failed");

        _startupItem = new MenuItem
        {
            Header = "开机启动",
            IsCheckable = true,
        };
        _startupItem.Click += (_, _) =>
            _startupItem.IsChecked = _setStartup(!_isStartupEnabled());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += async (_, _) => await InvokeSafelyAsync(_exit, "tray_exit_failed");

        _contextMenu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };
        _contextMenu.Items.Add(_visibilityItem);
        _contextMenu.Items.Add(_topmostItem);
        if (_hostModeItem is not null)
        {
            _contextMenu.Items.Add(_hostModeItem);
        }

        _contextMenu.Items.Add(refreshItem);
        _contextMenu.Items.Add(_startupItem);
        _contextMenu.Items.Add(new Separator());
        _contextMenu.Items.Add(exitItem);
        _contextMenu.Opened += (_, _) => RefreshMenuState();

        _instanceHandle = GetModuleHandle(moduleName: null);
        EnsureWindowClassRegistered(_instanceHandle);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _windowHandle = CreateWindowEx(
            extendedStyle: 0x00000080, // Hidden tool window, excluded from Alt+Tab.
            className: WindowClassName,
            windowName: string.Empty,
            style: 0,
            x: 0,
            y: 0,
            width: 0,
            height: 0,
            // Message-only windows do not receive TaskbarCreated broadcasts.
            parent: nint.Zero,
            menu: nint.Zero,
            instance: _instanceHandle,
            parameter: nint.Zero);
        if (_windowHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the tray message window.");
        }

        Instances[_windowHandle] = this;
        _iconHandle = CreateQuotaIcon();
        if (_iconHandle == nint.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the tray icon.");
            Dispose();
            throw error;
        }

        AddIcon();
        RefreshMenuState();
    }

    /// <summary>
    /// Raised after Windows broadcasts "TaskbarCreated", which happens when Explorer
    /// restarts and the taskbar handle changes.
    /// </summary>
    public event EventHandler? TaskbarRecreated;

    public void RefreshMenuState()
    {
        if (_disposed)
        {
            return;
        }

        _visibilityItem.Header = _isWindowVisible() ? "隐藏额度组件" : "显示额度组件";
        _topmostItem.IsChecked = _isTopmost();
        if (_hostModeItem is not null && _isTaskbarHosted is not null)
        {
            _hostModeItem.IsChecked = _isTaskbarHosted();
        }

        _startupItem.IsChecked = _isStartupEnabled();
    }

    private static void EnsureWindowClassRegistered(nint instanceHandle)
    {
        lock (WindowClassLock)
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Instance = instanceHandle,
                Procedure = WindowProcedureDelegate,
                ClassName = WindowClassName,
            };
            if (RegisterClassEx(ref windowClass) != 0)
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
            {
                throw new Win32Exception(error, "Unable to register the tray message window.");
            }
        }
    }

    private void AddIcon()
    {
        if (_disposed || _windowHandle == nint.Zero || _iconHandle == nint.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        _iconAdded = ShellNotifyIcon(NotifyIconAdd, ref data);
        if (!_iconAdded)
        {
            _logger.Write(
                "tray_icon_add_failed",
                new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not add the notification-area icon."));
        }
    }

    private NotifyIconData CreateNotifyIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = NotifyIconMessage | NotifyIconIcon | NotifyIconTip,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = "GPT 额度",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void HandleWindowMessage(uint message, nint parameter)
    {
        if (_disposed)
        {
            return;
        }

        if (message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            AddIcon();
            TaskbarRecreated?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (message != CallbackMessage)
        {
            return;
        }

        var notification = unchecked((uint)parameter.ToInt64());
        switch (notification)
        {
            case WindowMessageLeftButtonDoubleClick:
                _toggleWindow();
                break;
            case WindowMessageRightButtonUp:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        RefreshMenuState();
        SetForegroundWindow(_windowHandle);
        _contextMenu.IsOpen = true;
    }

    private async Task InvokeSafelyAsync(Func<Task> action, string eventName)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.Write(eventName, ex);
        }
    }

    private static nint CreateQuotaIcon()
    {
        const int size = 32;
        var andMask = Enumerable.Repeat((byte)0xFF, size * size / 8).ToArray();
        var xorMask = new byte[size * size * 4];
        var center = (size - 1) / 2d;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x - center, 2) + Math.Pow(y - center, 2));
                if (distance > 14)
                {
                    continue;
                }

                var maskOffset = y * (size / 8) + (x / 8);
                andMask[maskOffset] &= (byte)~(0x80 >> (x % 8));

                var isTrack = distance is >= 8 and <= 12;
                var isData = isTrack && Math.Atan2(y - center, x - center) <= Math.PI / 2;
                var (red, green, blue) = isData
                    ? ((byte)79, (byte)110, (byte)247)
                    : isTrack
                        ? ((byte)92, (byte)101, (byte)111)
                        : ((byte)23, (byte)26, (byte)31);

                var pixelOffset = ((size - 1 - y) * size + x) * 4;
                xorMask[pixelOffset] = blue;
                xorMask[pixelOffset + 1] = green;
                xorMask[pixelOffset + 2] = red;
                xorMask[pixelOffset + 3] = 0xFF;
            }
        }

        return CreateIcon(GetModuleHandle(moduleName: null), size, size, 1, 32, andMask, xorMask);
    }

    private static nint WindowProcedure(nint windowHandle, uint message, nint wordParameter, nint longParameter)
    {
        if (Instances.TryGetValue(windowHandle, out var instance))
        {
            instance.HandleWindowMessage(message, longParameter);
        }

        return DefWindowProc(windowHandle, message, wordParameter, longParameter);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _contextMenu.IsOpen = false;

        if (_iconAdded && _windowHandle != nint.Zero)
        {
            var data = CreateNotifyIconData();
            ShellNotifyIcon(NotifyIconDelete, ref data);
            _iconAdded = false;
        }

        if (_windowHandle != nint.Zero)
        {
            Instances.TryRemove(_windowHandle, out _);
            DestroyWindow(_windowHandle);
            _windowHandle = nint.Zero;
        }

        if (_iconHandle != nint.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WindowProcedureCallback Procedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedureCallback(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIcon(
        nint instance,
        int width,
        int height,
        byte planes,
        byte bitsPerPixel,
        byte[] andBits,
        byte[] xorBits);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
