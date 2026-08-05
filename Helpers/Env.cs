namespace NoisLogTray;

// Minimal .env loader with a process-environment fallback. Files are applied in
// order, so later files override earlier ones; the process environment is the
// last-resort fallback for any key not present in a file.
internal sealed class Env
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    internal Env(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
            LoadFile(path);
    }

    private void LoadFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            _values[key] = value;
        }
    }

    internal string? Get(string key)
    {
        if (_values.TryGetValue(key, out var v) && v.Length > 0) return v;
        var env = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(env) ? null : env;
    }

    internal string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException($"Missing required config: {key}");
}
