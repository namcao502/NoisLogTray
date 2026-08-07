using System.Text.Json;
using ModelContextProtocol.Client;

// HRM MCP smoke test: prove the C# SDK can connect to api-hrm.nois.vn/mcp over
// Streamable HTTP with Authorization: Bearer HRM_API_KEY and enumerate tools.
// Reads the key from the HRM_API_KEY environment variable, else the legacy project .env.

const string HrmMcpUrl = "https://api-hrm.nois.vn/mcp";
const string EnvPath = @"C:\Project\NoisLogTray\.env";

static string? ReadEnvValue(string path, string key)
{
    if (!File.Exists(path)) return null;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        if (!line[..eq].Trim().Equals(key, StringComparison.Ordinal)) continue;
        var value = line[(eq + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        return value;
    }
    return null;
}

var apiKey = Environment.GetEnvironmentVariable("HRM_API_KEY") ?? ReadEnvValue(EnvPath, "HRM_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine($"FAIL: HRM_API_KEY not found in {EnvPath}");
    return 1;
}
Console.WriteLine($"Loaded HRM_API_KEY (len={apiKey.Length}, prefix={apiKey[..Math.Min(4, apiKey.Length)]}...)");

try
{
    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(HrmMcpUrl),
        TransportMode = HttpTransportMode.StreamableHttp,
        ConnectionTimeout = TimeSpan.FromSeconds(30),
        AdditionalHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}",
        },
    });

    Console.WriteLine($"Connecting to {HrmMcpUrl} ...");
    await using var client = await McpClient.CreateAsync(transport);
    Console.WriteLine("Connected. Listing tools...");

    var tools = await client.ListToolsAsync();
    Console.WriteLine($"OK: {tools.Count} tool(s) advertised:");
    foreach (var tool in tools)
        Console.WriteLine($"  - {tool.Name}: {tool.Description}");

    var logTool = tools.FirstOrDefault(t => t.Name == "log_timesheet");
    if (logTool is null)
    {
        Console.WriteLine("FAIL: log_timesheet tool not found.");
        return 2;
    }

    foreach (var name in new[] { "log_timesheet", "get_my_day_logs", "get_my_timesheet_tasks" })
    {
        var t = tools.FirstOrDefault(x => x.Name == name);
        if (t is null) { Console.WriteLine($"\n[{name}] NOT FOUND"); continue; }
        Console.WriteLine($"\n{name} input schema:");
        Console.WriteLine(JsonSerializer.Serialize(t.JsonSchema, new JsonSerializerOptions { WriteIndented = true }));
    }

    Console.WriteLine("\nSMOKE TEST PASSED: C# MCP client connects, authenticates, and sees log_timesheet.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
        Console.WriteLine($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    return 3;
}
