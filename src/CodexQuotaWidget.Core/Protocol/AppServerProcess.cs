using System.Diagnostics;
using System.Text;

namespace CodexQuotaWidget.Core;

internal interface IAppServerConnection : IAsyncDisposable
{
    TextWriter StandardInput { get; }

    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    bool HasExited { get; }

    bool TryTrimWorkingSet();
}

internal interface IAppServerProcessFactory
{
    IAppServerConnection Start(CodexAppServerOptions options);
}

internal sealed class AppServerProcessFactory : IAppServerProcessFactory
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public IAppServerConnection Start(CodexAppServerOptions options)
    {
        var startInfo = CreateStartInfo(options);
        var process = Process.Start(startInfo)
            ?? throw new CodexAppServerException("Unable to start the bundled Codex app server.");
        return new ProcessAppServerConnection(process);
    }

    internal static ProcessStartInfo CreateStartInfo(CodexAppServerOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        // Keep transport and command immutable: the quota provider is intentionally
        // restricted to the local JSONL stdio surface and never opens a socket.
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        return startInfo;
    }
}

internal sealed class ProcessAppServerConnection(Process process) : IAppServerConnection
{
    private int _disposed;

    public TextWriter StandardInput => process.StandardInput;

    public TextReader StandardOutput => process.StandardOutput;

    public TextReader StandardError => process.StandardError;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public bool TryTrimWorkingSet()
    {
        try
        {
            return !process.HasExited && EmptyWorkingSet(process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint processHandle);
}
