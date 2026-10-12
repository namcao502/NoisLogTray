namespace NoisLogTray;

// Owns one Claude session (the general page's, or a ticket's) for the life of the tray, so rebuilding the
// window (a Settings save) does not kill it. Keeps recent output so a new view can replay the screen on attach.
internal sealed class ClaudeSessionHost : IDisposable
{
    private const int ReplayLimitBytes = 1 << 20;

    private readonly object _lock = new();
    private readonly Func<string> _workingDirectory;
    private readonly Func<string?>? _argument;
    private readonly Func<string?>? _settingsFile;
    private readonly bool _fallBackToProfile;
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

    internal ClaudeSessionHost(Func<string> workingDirectory, Func<string?>? argument = null, bool fallBackToProfile = true,
        Func<string?>? settingsFile = null)
    {
        _workingDirectory = workingDirectory;
        _argument = argument;
        _settingsFile = settingsFile;
        _fallBackToProfile = fallBackToProfile;
    }

    internal bool IsRunning
    {
        get { lock (_lock) return _session != null; }
    }

    internal string? CurrentDirectory { get; private set; }

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

    // A flag ("--continue") goes bare; a prompt is quoted so the CLI gets it as one argument (so it must hold no quote).
    // The settings file (the app's hooks) is a path, so it is always quoted.
    internal static string BuildCommandLine(string executable, string? settingsFile, string? argument)
    {
        var command = $"\"{executable}\"";
        if (!string.IsNullOrEmpty(settingsFile)) command += $" --settings \"{settingsFile}\"";
        if (string.IsNullOrEmpty(argument)) return command;
        return argument.StartsWith("--", StringComparison.Ordinal) ? $"{command} {argument}" : $"{command} \"{argument}\"";
    }

    // Starts claude in the working directory (the user profile if it is missing and fallBackToProfile).
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
        // The native installer puts claude.exe here; otherwise CreateProcess searches PATH.
        var localClaude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        var commandLine = BuildCommandLine(File.Exists(localClaude) ? localClaude : "claude.exe", _settingsFile?.Invoke(), _argument?.Invoke());

        var session = new PseudoConsoleSession(commandLine, directory, columns, rows);
        lock (_lock)
        {
            _replay.Clear();
            _replayBytes = 0;
            _session = session;
            CurrentDirectory = directory;
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
