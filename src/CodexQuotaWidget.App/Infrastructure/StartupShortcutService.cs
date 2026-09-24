using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace CodexQuotaWidget.App.Infrastructure;

public sealed class StartupShortcutService
{
    private const string ShortcutFileName = "Codex Quota Widget.lnk";
    private readonly AppLogger _logger;

    public StartupShortcutService(AppLogger logger)
    {
        _logger = logger;
    }

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutFileName);

    public bool IsEnabled => File.Exists(ShortcutPath);

    public void SetEnabled(bool enabled)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("The executable path is unavailable.");
        }

        AssertShortcutOwnedByCurrentExecutable(executablePath);
        if (!enabled)
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)(object)new ShellLinkCom();
            shellLink.SetPath(executablePath);
            shellLink.SetWorkingDirectory(AppContext.BaseDirectory);
            shellLink.SetDescription("在桌面显示 Codex 额度");
            shellLink.SetIconLocation(executablePath, 0);
            ((IPersistFile)shellLink).Save(ShortcutPath, true);
        }
        catch (Exception ex)
        {
            _logger.Write("startup_shortcut_create_failed", ex);
            throw;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    private static void AssertShortcutOwnedByCurrentExecutable(string executablePath)
    {
        if (!File.Exists(ShortcutPath))
        {
            return;
        }

        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)(object)new ShellLinkCom();
            ((IPersistFile)shellLink).Load(ShortcutPath, 0);
            var targetBuffer = new StringBuilder(32_768);
            shellLink.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0);
            var targetPath = targetBuffer.ToString();
            if (string.IsNullOrWhiteSpace(targetPath)
                || !Path.GetFullPath(targetPath).Equals(
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to replace an unrelated startup shortcut: '{ShortcutPath}'.");
            }
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCom
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
