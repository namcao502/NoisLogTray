using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace NoisLogTray;

// Receives the HTTP hooks of the app's Claude sessions. Each session runs with a --settings file whose hooks POST to
// <base>/<sessionName>; http.sys answers 404 for any path outside the token prefix, so the token is checked for free.
internal sealed class ClaudeHookServer : IDisposable
{
    private const long MaxBodyBytes = 8L << 20;
    private const int HookTimeoutSeconds = 5;
    private static readonly string[] HookEvents = { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd" };
    private static readonly byte[] EmptyReply = "{}"u8.ToArray();

    private readonly HttpListener _listener;
    private readonly string _baseUrl; // http://localhost:<port>/<token>/

    // Raised on a thread-pool thread; the subscriber marshals to the UI.
    internal event Action<string, ClaudeHookEvent>? HookReceived;

    private ClaudeHookServer(HttpListener listener, string baseUrl)
    {
        _listener = listener;
        _baseUrl = baseUrl;
    }

    // Null when no localhost port could be opened (logged); sessions then start without hooks.
    // A localhost prefix needs no admin URL reservation; the probe port can be taken in between, hence the retries.
    internal static ClaudeHookServer? TryStart()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var listener = new HttpListener();
            string baseUrl;
            try
            {
                baseUrl = $"http://localhost:{FreePort()}/{Guid.NewGuid():N}/";
                listener.Prefixes.Add(baseUrl);
                listener.Start();
            }
            catch (Exception e) when (e is HttpListenerException || e is SocketException)
            {
                listener.Close();
                AppLogger.Error($"[hooks] could not listen on localhost: {e.Message}");
                continue;
            }
            var server = new ClaudeHookServer(listener, baseUrl);
            // Task.Run: TrayApp's ctor has a WinForms sync context, which would resume every await on the UI thread.
            _ = Task.Run(server.ListenAsync);
            AppLogger.Info("[hooks] listening for Claude hooks on localhost");
            return server;
        }
        return null;
    }

    internal string HookUrl(string sessionName) => _baseUrl + sessionName;

    // Writes the session's hook settings to <ClaudeHooksDirectory>\<sessionName>.json and returns its path;
    // null (logged) when the file cannot be written, so the session then starts without hooks.
    internal string? WriteSettingsFile(string sessionName)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ClaudeHooksDirectory);
            var path = Path.Combine(AppPaths.ClaudeHooksDirectory, sessionName + ".json");
            File.WriteAllText(path, BuildSettingsJson(HookUrl(sessionName)));
            return path;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[hooks] could not write the settings file of {sessionName}: {e.Message}");
            return null;
        }
    }

    // Claude merges these with the user's own hooks (all matching hooks run).
    internal static string BuildSettingsJson(string url)
    {
        var hooks = new JsonObject();
        foreach (var eventName in HookEvents)
        {
            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = url, ["timeout"] = HookTimeoutSeconds }),
            });
        }
        return new JsonObject { ["hooks"] = hooks }.ToJsonString();
    }

    // Always 200 "{}": an empty hook output never blocks or changes what Claude does.
    private async Task ListenAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                // Normal after Dispose. While still listening, close too: hooks then fail fast (connection refused)
                // instead of each waiting out its timeout on a queue nobody reads.
                if (_listener.IsListening)
                {
                    AppLogger.Error($"[hooks] stopped listening: {e.Message}");
                    _listener.Close();
                }
                return;
            }
            try
            {
                var request = context.Request;
                var sessionName = request.Url?.Segments.LastOrDefault()?.Trim('/') ?? "";
                ClaudeHookEvent? hookEvent = null;
                if (request.HttpMethod == "POST" && request.ContentLength64 <= MaxBodyBytes)
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    hookEvent = ClaudeAttention.Parse(await reader.ReadToEndAsync());
                }
                if (hookEvent != null) HookReceived?.Invoke(sessionName, hookEvent);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = EmptyReply.Length;
                await context.Response.OutputStream.WriteAsync(EmptyReply);
                context.Response.Close();
            }
            catch (Exception e)
            {
                // Abort never throws, so a client gone mid-request (Restart, closed tab) cannot end this loop,
                // and the connection is dropped at once instead of Claude waiting out the hook timeout.
                AppLogger.Error($"[hooks] handling a hook failed: {e.Message}");
                context.Response.Abort();
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose() => _listener.Close();
}
