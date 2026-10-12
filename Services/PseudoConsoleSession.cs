using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NoisLogTray;

// One child process attached to a Windows pseudo console (ConPTY): what the in-app terminal shows.
// Output arrives as raw UTF-8/VT bytes on a background thread; the host marshals it to the UI.
internal sealed class PseudoConsoleSession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;

    private IntPtr _pseudoConsole;
    private IntPtr _attributeList;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _inputStream;
    private ProcessInformation _process;
    private Thread? _readThread;
    private bool _disposed;

    internal event Action<byte[]>? Output;
    internal event Action? Exited;

    internal PseudoConsoleSession(string commandLine, string workingDirectory, short columns, short rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        _inputWrite = inputWrite;
        _outputRead = outputRead;

        var result = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
        // The console holds its own copies; closing ours lets the read loop see EOF when it exits.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (result != 0) throw new Win32Exception(result);

        var startupInfo = new StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(_attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, _pseudoConsole,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();
        startupInfo.lpAttributeList = _attributeList;

        var environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(Environment.GetEnvironmentVariables()));
        try
        {
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environment, workingDirectory, ref startupInfo, out _process))
                throw new Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }

        _inputStream = new FileStream(_inputWrite, FileAccess.Write, 1);
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
        _readThread.Start();
    }

    // The tray's environment minus the variables that would make claude think it runs inside another Claude Code
    // session (the tray may have been started from one): "name=value\0" entries ended by one more "\0".
    internal static string BuildEnvironmentBlock(IDictionary variables)
    {
        var entries = variables.Cast<DictionaryEntry>()
            .Select(de => (Name: (string)de.Key, Value: de.Value as string ?? ""))
            .Where(ev => !TicketLauncher.IsClaudeSessionVariable(ev.Name))
            .OrderBy(ev => ev.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ev => $"{ev.Name}={ev.Value}\0");
        return string.Concat(entries) + "\0";
    }

    internal void Write(byte[] data)
    {
        if (_disposed || _inputStream == null) return;
        try
        {
            _inputStream.Write(data, 0, data.Length);
            _inputStream.Flush();
        }
        catch (IOException ex)
        {
            AppLogger.Error($"[pty] write failed: {ex.Message}");
        }
    }

    internal void Resize(short columns, short rows)
    {
        if (_disposed || columns <= 0 || rows <= 0) return;
        ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows));
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        try
        {
            using var stream = new FileStream(_outputRead!, FileAccess.Read, 1);
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                Output?.Invoke(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (IOException)
        {
            // The pipe breaks when the pseudo console closes: a normal end.
        }
        catch (ObjectDisposedException)
        {
        }
        Exited?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pseudoConsole != IntPtr.Zero)
        {
            // Closing the console ends the child's console session and breaks the output pipe.
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
        if (_process.hProcess != IntPtr.Zero)
        {
            TerminateProcess(_process.hProcess, 0);
            CloseHandle(_process.hProcess);
            CloseHandle(_process.hThread);
        }
        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
        _inputStream?.Dispose();
        _inputWrite?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
