namespace NoisLogTray;

// Owns one terminal (the Terminal page's, or a ticket's) for the life of the tray, so rebuilding the window (a Settings
// save) does not kill it. Keeps recent output so a new view can replay the screen on attach.
internal sealed class ClaudeSessionHost : IDisposable
{
    private const int ReplayLimitBytes = 1 << 20;

    private readonly object _lock = new();
    private readonly Func<string> _workingDirectory;
    private readonly Func<string?>? _argument;
    private readonly Func<string?>? _settingsFile;
    private readonly bool _fallBackToProfile;
    private readonly string? _claudeExecutable;
    private readonly LinkedList<byte[]> _replay = new();
    private int _replayBytes;
    private PseudoConsoleSession? _session;
    private Action<byte[]>? _output;

    // Raised on the pty read thread; the subscriber marshals to the UI.
    internal event Action? Exited;

    // Raised on the caller's thread after a successful Start.
    internal event Action? Started;

    // Raised on the caller's thread when Start begins, before the old session stops; Start may still throw after it.
    internal event Action? Starting;

    // claudeExecutable: null runs the installed claude (tests pass a stand-in).
    internal ClaudeSessionHost(Func<string> workingDirectory, Func<string?>? argument = null, bool fallBackToProfile = true,
        Func<string?>? settingsFile = null, string? claudeExecutable = null)
    {
        _workingDirectory = workingDirectory;
        _argument = argument;
        _settingsFile = settingsFile;
        _fallBackToProfile = fallBackToProfile;
        _claudeExecutable = claudeExecutable;
    }

    internal bool IsRunning
    {
        get { lock (_lock) return _session != null; }
    }

    internal string? CurrentDirectory { get; private set; }

    // Whether the last Start passed hook settings, so claude's hooks will tell when it really runs.
    internal bool HasHooks { get; private set; }

    // Returns the output so far and subscribes in one step, so no chunk is lost or shown twice.
    internal byte[] Attach(Action<byte[]> onOutput)
    {
        lock (_lock)
        {
            _output = onOutput;
            var buffer = new byte[_replayBytes];
            var offset = 0;
            foreach (var chunk in _replay)
            {
                chunk.CopyTo(buffer, offset);
                offset += chunk.Length;
            }
            return buffer;
        }
    }

    internal void Detach(Action<byte[]> onOutput)
    {
        lock (_lock)
        {
            if (_output == onOutput) _output = null;
        }
    }

    // A PowerShell that runs claude at once and stays at its prompt after /exit. Its `claude` function adds the hook
    // settings, so a claude typed there later reports to the app too. A flag ("--continue") goes bare; a prompt is
    // quoted as one argument. Nothing here may hold a double quote, which would end the -Command string.
    internal static string BuildCommandLine(string? powerShell7Path, string claudeExecutable, string? settingsFile, string? argument)
    {
        var shell = powerShell7Path != null ? $"\"{powerShell7Path}\"" : "powershell.exe";
        var settings = string.IsNullOrEmpty(settingsFile) ? "" : $" --settings {PowerShellQuote(settingsFile)}";
        var script = $"function global:claude {{ & {PowerShellQuote(claudeExecutable)}{settings} @args }}; claude";
        if (!string.IsNullOrEmpty(argument))
        {
            script += argument.StartsWith("--", StringComparison.Ordinal) ? $" {argument}" : $" {PowerShellQuote(argument)}";
        }
        return $"{shell} -NoLogo -NoExit -Command \"{script}\"";
    }

    // The native installer puts claude.exe here; otherwise it is looked up on PATH.
    internal static string DefaultClaudeExecutable()
    {
        var localClaude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(localClaude) ? localClaude : "claude.exe";
    }

    private static string PowerShellQuote(string text)
    {
        return "'" + text.Replace("'", "''") + "'";
    }

    // Starts the terminal in the working directory (the user profile if it is missing and fallBackToProfile).
    // Throws Win32Exception, or DirectoryNotFoundException when the directory is missing and there is no fallback.
    internal void Start(short columns, short rows)
    {
        Starting?.Invoke();
        Stop();
        var directory = _workingDirectory();
        if (!Directory.Exists(directory))
        {
            if (!_fallBackToProfile) throw new DirectoryNotFoundException($"Directory not found: {directory}");
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        // Always the .exe, since a bare `claude` inside the function would call the function itself.
        var claudeExecutable = _claudeExecutable ?? DefaultClaudeExecutable();
        var powerShell7 = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "pwsh.exe"))
            .FirstOrDefault(File.Exists);
        var settingsFile = _settingsFile?.Invoke();
        var commandLine = BuildCommandLine(powerShell7, claudeExecutable, settingsFile, _argument?.Invoke());

        var session = new PseudoConsoleSession(commandLine, directory, columns, rows);
        lock (_lock)
        {
            _replay.Clear();
            _replayBytes = 0;
            _session = session;
            CurrentDirectory = directory;
            HasHooks = !string.IsNullOrEmpty(settingsFile);
        }
        session.Output += bytes => OnOutput(session, bytes);
        session.Exited += () => OnExited(session);
        AppLogger.Info($"[claude] session started in {directory}");
        Started?.Invoke();
    }

    internal void Write(byte[] data)
    {
        PseudoConsoleSession? session;
        lock (_lock) session = _session;
        session?.Write(data);
    }

    internal void Resize(short columns, short rows)
    {
        PseudoConsoleSession? session;
        lock (_lock) session = _session;
        session?.Resize(columns, rows);
    }

    // Ends the running session without raising Exited (the caller already knows).
    internal void Stop()
    {
        PseudoConsoleSession? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
        }
        session?.Dispose();
    }

    private void OnOutput(PseudoConsoleSession session, byte[] bytes)
    {
        lock (_lock)
        {
            if (_session != session) return;
            _replay.AddLast(bytes);
            _replayBytes += bytes.Length;
            while (_replayBytes > ReplayLimitBytes && _replay.First != null)
            {
                _replayBytes -= _replay.First.Value.Length;
                _replay.RemoveFirst();
            }
            _output?.Invoke(bytes);
        }
    }

    private void OnExited(PseudoConsoleSession session)
    {
        lock (_lock)
        {
            if (_session != session) return;
            _session = null;
        }
        session.Dispose();
        AppLogger.Info("[claude] session exited");
        Exited?.Invoke();
    }

    public void Dispose()
    {
        Stop();
    }
}
