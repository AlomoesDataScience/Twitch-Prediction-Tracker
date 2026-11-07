using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace PredictionBot;

record OutcomeEvent(string Id, string Title, string? Color, long Points, long Users);

record PredictionEvent(string Id, string? ChannelId, string Title, string Status, string? CreatedAt,
                       string? LockedAt, string? EndedAt, string? WinningOutcomeId, List<OutcomeEvent> Outcomes);

/// Pulls prediction events out of whatever the Twitch page received. The browser hook forwards raw
/// text (websocket frames and GraphQL responses), so this accepts both shapes: snake_case from the
/// realtime socket and camelCase from GraphQL, at any nesting depth, including JSON-inside-a-string.
static class Parser
{
    public static List<PredictionEvent> Parse(string raw)
    {
        var found = new List<PredictionEvent>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            Walk(doc.RootElement, found, 0);
        }
        catch (JsonException) { }
        return found;
    }

    static void Walk(JsonElement el, List<PredictionEvent> found, int depth)
    {
        if (depth > 32) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryEvent(el) is { } e) { found.Add(e); return; }
                foreach (var p in el.EnumerateObject()) Walk(p.Value, found, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Walk(item, found, depth + 1);
                break;
            case JsonValueKind.String:
                var s = el.GetString()!;
                if (s.Length > 20 && (s[0] == '{' || s[0] == '[') && s.Contains("outcomes"))
                {
                    try
                    {
                        using var inner = JsonDocument.Parse(s);
                        Walk(inner.RootElement, found, depth + 1);
                    }
                    catch (JsonException) { }
                }
                break;
        }
    }

    static PredictionEvent? TryEvent(JsonElement el)
    {
        if (Str(el, "id") is not { } id || Str(el, "status") is not { } status) return null;
        if (!el.TryGetProperty("outcomes", out var outs) || outs.ValueKind != JsonValueKind.Array) return null;

        var outcomes = new List<OutcomeEvent>();
        foreach (var o in outs.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object || Str(o, "id") is not { } oid) return null;
            outcomes.Add(new OutcomeEvent(oid, Str(o, "title") ?? "", Str(o, "color"),
                Num(o, "total_points", "totalPoints"), Num(o, "total_users", "totalUsers")));
        }
        if (outcomes.Count == 0) return null;

        var winner = Str(el, "winning_outcome_id", "winningOutcomeID");
        if (winner is null && el.TryGetProperty("winningOutcome", out var w) && w.ValueKind == JsonValueKind.Object)
            winner = Str(w, "id");

        var channelId = Str(el, "channel_id", "channelID");
        if (channelId is null && el.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.Object)
            channelId = Str(c, "id");

        return new PredictionEvent(id, channelId, Str(el, "title") ?? "", status.ToUpperInvariant(),
            Str(el, "created_at", "createdAt"), Str(el, "locked_at", "lockedAt"),
            Str(el, "ended_at", "endedAt"), winner, outcomes);
    }

    static string? Str(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    static long Num(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var l)) return l;
        }
        return 0;
    }
}

/// Local endpoint the browser extension posts to. Loopback only.
class IngestServer(Db db)
{
    // PREDICTIONBOT_PORT is for testing; the extension always talks to 47800.
    public static readonly int Port =
        int.TryParse(Environment.GetEnvironmentVariable("PREDICTIONBOT_PORT"), out var port) ? port : 47800;

    /// Every message from the extension is recorded here, so a dropped hook can be diagnosed.
    public static string LogPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Program.DbPath))!, "hook.log");

    readonly HttpListener _listener = new();
    readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();
    readonly object _logGate = new();

    /// Raised (on a background thread) whenever stored data or hook status changed.
    public event Action? Changed;

    /// Channels with a hooked Twitch tab that has been heard from recently. The extension checks
    /// in at least every 30s; the window allows for missed beats before showing it as unhooked.
    public List<string> HookedChannels() =>
        _lastSeen.Where(p => DateTime.UtcNow - p.Value < TimeSpan.FromSeconds(100))
                 .Select(p => p.Key).Order().ToList();

    /// Returns an error message if the port could not be opened.
    public string? Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }
        catch (Exception ex)
        {
            return $"Could not listen on port {Port}: {ex.Message}";
        }

        var log = new FileInfo(LogPath);
        if (log.Exists && log.Length > 5_000_000) log.Delete();
        Log("---- app started ----");
        _ = Task.Run(Loop);
        return null;
    }

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            // Nothing that goes wrong with one request may stop the loop: if it did, the app
            // would silently stop hearing the extension for good.
            try
            {
                var ctx = await _listener.GetContextAsync();
                try
                {
                    ctx.Response.StatusCode = Handle(ctx.Request) ? 204 : 404;
                }
                catch (Exception ex)
                {
                    Log($"error  {ctx.Request.Url?.AbsolutePath} {ex.GetType().Name}: {ex.Message}");
                    ctx.Response.StatusCode = 400;
                }
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Log($"error  listener {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    bool Handle(HttpListenerRequest request)
    {
        if (request.HttpMethod != "POST") return false;
        using var reader = new StreamReader(request.InputStream);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        var root = doc.RootElement;
        var channel = Field(root, "channel").ToLowerInvariant();
        var origin = $"via={Field(root, "via"),-8} ext={Field(root, "v")}";

        switch (request.Url?.AbsolutePath)
        {
            case "/ping":
                Log($"ping   {channel,-20} {origin} {Field(root, "detail")}");
                Seen(channel);
                return true;

            case "/ingest":
                var events = Parser.Parse(Field(root, "raw"));
                foreach (var e in events) db.Upsert(e, channel);
                Log($"ingest {channel,-20} {origin} events={events.Count} {string.Join(",", events.Select(e => e.Status))}");
                Seen(channel); // data arriving is proof of a live hook too
                if (events.Count > 0) Changed?.Invoke();
                return true;

            default:
                return false;
        }
    }

    void Seen(string channel)
    {
        if (channel == "") return;
        var isNew = !HookedChannels().Contains(channel);
        _lastSeen[channel] = DateTime.UtcNow;
        if (isNew) Changed?.Invoke();
    }

    static string Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    void Log(string line)
    {
        try
        {
            lock (_logGate) File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch (IOException) { }
    }
}
