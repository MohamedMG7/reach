using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Reach.Agent.Terminal;

/// <summary>A process running inside a Windows pseudo console (ConPTY).</summary>
public sealed class PseudoConsole : IDisposable
{
    private readonly nint _console;
    private readonly SafeWaitHandle _process;
    private readonly Lock _lock = new();
    private bool _consoleClosed;

    private PseudoConsole(nint console, SafeWaitHandle process, FileStream input, FileStream output)
    {
        _console = console;
        _process = process;
        Input = input;
        Output = output;
    }

    /// <summary>Keystrokes for the shell (UTF-8, VT sequences).</summary>
    public FileStream Input { get; }

    /// <summary>What the shell prints (UTF-8, VT sequences). Ends once the console is closed.</summary>
    public FileStream Output { get; }

    public static PseudoConsole Start(string commandLine, string workingDirectory, int cols, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var outputWrite, 0, 0)) throw new Win32Exception();

        var hr = CreatePseudoConsole(Size(cols, rows), inputRead, outputWrite, 0, out var console);
        // The console holds its own duplicates; ours must close so the output pipe can reach EOF.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        nint attributes = 0;
        try
        {
            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, nint.Size, 0, 0))
                throw new Win32Exception();

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            // STARTF_USESTDHANDLES with null handles: otherwise a child of a process whose own
            // stdio is redirected (e.g. under a test runner) inherits that stdio instead of the console.
            startup.StartupInfo.Flags = StartfUseStdHandles;

            if (!CreateProcess(null, commandLine, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    0, workingDirectory, ref startup, out var info))
                throw new Win32Exception();
            CloseHandle(info.Thread);
            return new PseudoConsole(console, new SafeWaitHandle(info.Process, ownsHandle: true),
                new FileStream(inputWrite, FileAccess.Write, 1), new FileStream(outputRead, FileAccess.Read, 1));
        }
        catch
        {
            ClosePseudoConsole(console);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            if (!_consoleClosed) ResizePseudoConsole(_console, Size(cols, rows));
        }
    }

    /// <summary>Completes when the process exits; returns its exit code.</summary>
    public async Task<int> WaitForExitAsync()
    {
        var exited = new TaskCompletionSource();
        using var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_process.DangerousGetHandle(), ownsHandle: false) };
        var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        await exited.Task;
        registration.Unregister(null);
        return GetExitCodeProcess(_process, out var code) ? (int)code : -1;
    }

    public void Kill() => TerminateProcess(_process, 1);

    /// <summary>Closes the pseudo console; <see cref="Output"/> then reaches end of stream.</summary>
    public void CloseConsole()
    {
        lock (_lock)
        {
            if (_consoleClosed) return;
            _consoleClosed = true;
        }
        ClosePseudoConsole(_console);
    }

    public void Dispose()
    {
        CloseConsole();
        Input.Dispose();
        Output.Dispose();
        _process.Dispose();
    }

    private static Coord Size(int cols, int rows) => new() { X = (short)cols, Y = (short)rows };

    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly nint ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(nint console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, string commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
