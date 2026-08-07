namespace NoisLogTray;

// In-memory config lookup with a process-environment fallback. Built from a key/value
// map (settings.json's Config section); the process environment is the last-resort
// fallback for any key not present in the map. ParseFile still reads the legacy .env
// format, used only for the one-time migration into settings.json.
internal sealed class Env
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    internal Env(IReadOnlyDictionary<string, string> values)
    {
        foreach (var kv in values)
            _values[kv.Key] = kv.Value;
    }

    // Parse one legacy .env file into key/value pairs (no process-environment fallback).
    // A missing file yields an empty map. Used for one-time migration into settings.json.
    internal static Dictionary<string, string> ParseFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
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
            values[key] = value;
        }
        return values;
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
