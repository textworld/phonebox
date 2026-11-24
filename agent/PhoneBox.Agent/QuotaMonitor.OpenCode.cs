using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PhoneBox.Agent;

public sealed partial class QuotaMonitor
{
    // ------------------------------------------------------------------ OpenCode Go
    // The Go subscription's usage lives at opencode.ai/zen/go/v1/usage and is read with the key OpenCode itself
    // stores in its local database (opencode.db, table `credential`, integration "opencode-go"). The database is
    // only ever read (one SELECT), never written, and the key is never logged.

    /// <summary>OpenCode's local database with the saved sign-ins. Tests point it at a file of their own.</summary>
    internal string OpenCodeDbPath { get; init; } = DefaultOpenCodeDbPath();

    internal const long OpenCodeIntervalMs = 300_000;
    internal const string NoOpenCodeGo =
        "No OpenCode Go sign-in in the local OpenCode data; connect it in OpenCode (/connect) and keep it signed in";

    internal static string DefaultOpenCodeDbPath() => Path.Combine(OpenCodeDataDir(), "opencode.db");

    private static string OpenCodeConfigDir() => Path.Combine(OpenCodeHomeDir("XDG_CONFIG_HOME", ".config"), "opencode");

    private static string OpenCodeDataDir() => Path.Combine(OpenCodeHomeDir("XDG_DATA_HOME", Path.Combine(".local", "share")), "opencode");

    /// <summary>OpenCode follows the XDG layout outside Windows (as OpenCode's own debug paths command reports).</summary>
    private static string OpenCodeHomeDir(string variable, string fallback) =>
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v
                ? v
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), fallback);

    /// <summary>
    /// The Go sign-in (a Bearer key, never logged), or null when OpenCode has no Go integration saved here.
    /// OpenCode keeps renewing nothing here: keys do not expire, so the database is simply read again each poll.
    /// </summary>
    internal static OpenCodeCredential? ReadOpenCodeCredential(string dbPath)
    {
        if (!File.Exists(dbPath)) return null;
        try
        {
            // Only a SELECT on the shared database: SQLite lets a reader in while OpenCode writes (WAL mode).
            using var db = new SqliteConnection("Data Source=" + dbPath);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM credential WHERE integration_id = 'opencode-go' ORDER BY time_updated DESC LIMIT 1";
            var raw = cmd.ExecuteScalar() as string;
            return raw is { Length: > 0 } ? OpenCodeCredentialFromJson(raw) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException
            or System.Security.SecurityException)
        {
            return null; // OpenCode owns the file and may be busy writing; the next pass reads again
        }
    }

    /// <summary>Parses the saved integration: {"type":"key"|"oauth","key"|"access":…,"metadata":{server,orgID,orgName}}.</summary>
    internal static OpenCodeCredential? OpenCodeCredentialFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = Str(root, "type");
            var key = type == "oauth" ? Str(root, "access") : Str(root, "key");
            if (string.IsNullOrEmpty(key)) return null;
            string? server = null, org = null, orgName = null;
            if (root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                server = Str(meta, "server");
                org = Str(meta, "orgID");
                orgName = Str(meta, "orgName");
            }
            return new OpenCodeCredential(key, server, org, orgName);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A saved OpenCode Go integration. <paramref name="Server"/> and org fields come from its metadata when present.</summary>
    internal sealed record OpenCodeCredential(string Key, string? Server, string? OrgId, string? OrgName);

    /// <summary>The usage endpoint of the console the sign-in belongs to: its own server URL, else opencode.ai.</summary>
    internal static string OpenCodeUsageUrl(OpenCodeCredential credential)
    {
        // The same rewrite OpenCode applies: a console address is served at the site root.
        var server = credential.Server is { Length: > 0 } s
            ? s.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s : "https://" + s
            : "https://opencode.ai";
        if (server.Contains("console.opencode.ai", StringComparison.OrdinalIgnoreCase))
            server = server.Replace("console.opencode.ai", "opencode.ai", StringComparison.OrdinalIgnoreCase);
        return new Uri(server).GetLeftPart(UriPartial.Authority) + "/zen/go/v1/usage";
    }

    internal async Task<bool> RefreshOpenCodeAsync(CancellationToken ct)
    {
        var credential = ReadOpenCodeCredential(OpenCodeDbPath);
        if (credential is null)
        {
            // A database but no Go sign-in is a state worth naming on a card; no OpenCode installation shows nothing.
            _opencode = File.Exists(OpenCodeDbPath)
                ? _opencode is { Limits.Count: > 0 } prev ? prev with { Error = NoOpenCodeGo } : Fail("opencode", "OpenCode", NoOpenCodeGo)
                : null;
            return false;
        }
        var (ok, error) = await FetchOpenCodeAsync(credential, ct);
        if (ok) return true;
        _opencode = _opencode is { Limits.Count: > 0 } cached ? cached with { Error = error } : Fail("opencode", "OpenCode", error ?? "Could not fetch quota");
        return false;
    }

    /// <summary>Sends the one request and sets <see cref="_opencode"/> from its answer.</summary>
    private async Task<(bool Ok, string? Error)> FetchOpenCodeAsync(OpenCodeCredential credential, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OpenCodeUsageUrl(credential));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Key);
            if (credential.OrgId is { Length: > 0 } org) req.Headers.Add("x-opencode-org-id", org);
            using var res = await Http.SendAsync(req, ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, "OpenCode did not accept the OpenCode Go sign-in");
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return (false, "OpenCode is limiting quota checks");
            if (!res.IsSuccessStatusCode)
                return (false, $"Could not fetch quota (HTTP {(int)res.StatusCode})");
            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                log.LogDebug("opencode quota: unrecognized reply ({Length} bytes)", body.Length);
                return (false, "OpenCode sent a quota reply this version doesn't recognize");
            }
            var limits = new List<QuotaLimit>();
            AddOpenCodeWindow(limits, usage, "rolling", "5-hour session", 5 * 3600L);
            AddOpenCodeWindow(limits, usage, "weekly", "Weekly", 7 * 86400L);
            AddOpenCodeWindow(limits, usage, "monthly", "Monthly", null);
            if (limits.Count == 0)
            {
                log.LogDebug("opencode quota: no usage windows in reply ({Length} bytes)", body.Length);
                return (false, "OpenCode sent a quota reply this version doesn't recognize");
            }
            var plan = credential.OrgName is { Length: > 0 } name ? $"Go · {name}" : "Go";
            _opencode = new QuotaProvider("opencode", "OpenCode", plan, limits, null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            // the request never reached the server: keep the last reading (its age is shown), the error only without one
            log.LogDebug("opencode quota: {Msg}", ex.Message);
            return (false, _opencode is { Limits.Count: > 0 } ? null : "Could not reach OpenCode: " + ex.Message);
        }
        catch (Exception ex)
        {
            log.LogDebug("opencode quota: {Msg}", ex.Message);
            return (false, "Could not fetch quota: " + ex.Message);
        }
    }

    /// <summary>One Go usage window: percent used, or rate-limited with what is left of the window.</summary>
    private static void AddOpenCodeWindow(List<QuotaLimit> limits, JsonElement usage, string key, string label, long? windowSec)
    {
        if (!usage.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return;
        var percent = Num(w, "percent") ?? 0;
        var limited = Str(w, "status") == "rate-limited";
        var severity = limited || percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal";
        limits.Add(new QuotaLimit(label, percent, Iso(Str(w, "resetsAt")), windowSec, severity));
    }

    // ------------------------------------------------------------------ MiniMax
    // The M Plan subscription counts a 5-hour window and a weekly window; both are read from the endpoint MiniMax
    // documents for the plan-usage page (GET /v1/token_plan/remains with the M Plan API key). The key is found the
    // same way MiniMax tools find it: the MINIMAX_API_KEY environment variable, or the OpenCode configuration.

    /// <summary>Where MiniMax API keys are looked up, in order. Tests replace these paths.</summary>
    internal string[] MiniMaxConfigPaths { get; init; } = [Path.Combine(OpenCodeConfigDir(), "opencode.json"), Path.Combine(OpenCodeConfigDir(), "opencode.jsonc")];

    internal const long MiniMaxIntervalMs = 300_000;
    internal const string NoMiniMaxKey =
        "No MiniMax M Plan API key found; set MINIMAX_API_KEY or connect MiniMax in OpenCode (/connect)";

    private volatile QuotaProvider? _opencode;
    private volatile QuotaProvider? _minimax;
    private long _nextOpenCode;
    private long _nextMiniMax;
    private string? _minimaxOrigin; // the region whose endpoint answered, reused so one wrong guess stays one guess
    private string? _minimaxKey;

    internal async Task<bool> RefreshMiniMaxAsync(CancellationToken ct)
    {
        var key = ReadMiniMaxKey();
        if (string.IsNullOrEmpty(key))
        {
            _minimaxKey = null;
            _minimaxOrigin = null;
            // A configuration but no MiniMax key is a state worth naming on a card; neither installed shows nothing.
            _minimax = MiniMaxConfigPaths.Any(File.Exists)
                ? _minimax is { Limits.Count: > 0 } prev ? prev with { Error = NoMiniMaxKey } : Fail("minimax", "MiniMax", NoMiniMaxKey)
                : null;
            return false;
        }
        var reusedKey = key == _minimaxKey;
        _minimaxKey = key;
        var (ok, error) = await FetchMiniMaxAsync(key, reusedKey, ct);
        if (ok) return true;
        _minimax = _minimax is { Limits.Count: > 0 } cached ? cached with { Error = error } : Fail("minimax", "MiniMax", error ?? "Could not fetch quota");
        return false;
    }

    /// <summary>The M Plan API key from the environment, then the OpenCode configuration; null when neither has one.</summary>
    internal string? ReadMiniMaxKey()
    {
        var env = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        foreach (var path in MiniMaxConfigPaths)
            if (ReadConfigText(path) is { } text && MiniMaxProperty(text, FindApiKey) is { Length: > 0 } key) return key;
        return null;
    }

    /// <summary>The MiniMax base URL from the OpenCode configuration, so the region the key belongs to is tried first.</summary>
    internal string? ReadMiniMaxBaseUrl()
    {
        foreach (var path in MiniMaxConfigPaths)
            if (ReadConfigText(path) is { } text && MiniMaxProperty(text, FindBaseUrl) is { Length: > 0 } url) return url;
        return null;
    }

    private static string? ReadConfigText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Runs a MiniMax lookup over the OpenCode configuration's providers → "minimax" entry.</summary>
    private static string? MiniMaxProperty(string text, Func<JsonElement, string?> find)
    {
        if (text.IndexOf("minimax", StringComparison.OrdinalIgnoreCase) < 0) return null;
        try { return find(JsonDocument.Parse(text).RootElement); }
        catch (JsonException)
        {
            try { return find(JsonDocument.Parse(StripComments(text)).RootElement); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>The M Plan key in the OpenCode configuration, or null.</summary>
    internal static string? MiniMaxKeyFromJson(string text) => MiniMaxProperty(text, FindApiKey);

    private static string? FindApiKey(JsonElement root)
    {
        foreach (var entry in MiniMaxEntries(root))
            if (FindStringKey(entry, "apiKey") is { Length: > 0 } key) return key;
        return null;
    }

    private static string? FindBaseUrl(JsonElement root)
    {
        foreach (var entry in MiniMaxEntries(root))
            if (FindStringKey(entry, "baseURL") is { Length: > 0 } url) return url;
        return null;
    }

    /// <summary>The "minimax" entries under both possible OpenCode configuration spellings (providers / provider).</summary>
    private static IEnumerable<JsonElement> MiniMaxEntries(JsonElement root)
    {
        foreach (var section in new[] { "providers", "provider" })
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(section, out var providers)
                || providers.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in providers.EnumerateObject())
                if (name.NameEquals("minimax") && name.Value.ValueKind == JsonValueKind.Object) yield return name.Value;
        }
    }

    /// <summary>The named string property here or in the nested settings, the places OpenCode's configuration uses.</summary>
    private static string? FindStringKey(JsonElement entry, string key)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in entry.EnumerateObject())
        {
            if (prop.NameEquals(key) && prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.NameEquals("settings")
                && FindStringKey(prop.Value, key) is { Length: > 0 } nested) return nested;
        }
        return null;
    }

    /// <summary>Removes // comments while keeping them inside strings, so a commented JSONC configuration still parses.</summary>
    internal static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                if (i < text.Length) sb.Append('\n');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>MiniMax endpoint origins to try, the configured base URL's first, until one answers for the key.</summary>
    internal static IReadOnlyList<string> MiniMaxOrigins(string? configuredBase)
    {
        var list = new List<string>();
        if (configuredBase is { Length: > 0 } baseUrl && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            list.Add(uri.GetLeftPart(UriPartial.Authority));
        list.Add("https://api.minimaxi.com"); // the mainland service, where an OpenCode-configured key is drawn from too
        list.Add("https://www.minimax.io");   // the global service, as documented for the plan-usage page
        return list;
    }

    /// <summary>Sends the one request and sets <see cref="_minimax"/> from its answer.</summary>
    private async Task<(bool Ok, string? Error)> FetchMiniMaxAsync(string key, bool reusedKey, CancellationToken ct)
    {
        // The region is remembered from the last success; a key change starts the search again.
        var origins = reusedKey && _minimaxOrigin is { } cached ? [cached] : MiniMaxOrigins(ReadMiniMaxBaseUrl());
        string? refusal = null;
        foreach (var origin in origins)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, origin + "/v1/token_plan/remains");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var res = await Http.SendAsync(req, ct);
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    continue; // a key of the other region is refused here; the next origin knows it
                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                    return (false, "MiniMax is limiting quota checks");
                if (!res.IsSuccessStatusCode)
                    return (false, $"Could not fetch quota (HTTP {(int)res.StatusCode})");
                var body = await res.Content.ReadAsStringAsync(ct);
                var parsed = ParseMiniMax(body);
                if (parsed is null)
                {
                    log.LogDebug("minimax quota: unrecognized reply ({Length} bytes)", body.Length);
                    return (false, "MiniMax sent a quota reply this version doesn't recognize");
                }
                var (limits, message) = parsed.Value;
                if (message is not null) { refusal = message; continue; } // refused here: the next region may know the key
                if (limits.Count == 0)
                    return (false, "MiniMax sent a quota reply this version doesn't recognize");
                _minimaxOrigin = origin;
                _minimax = new QuotaProvider("minimax", "MiniMax", "M Plan", limits, null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
                return (true, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
            {
                // the request never reached the server: keep the last reading (its age is shown), the error only without one
                log.LogDebug("minimax quota: {Msg}", ex.Message);
                return (false, _minimax is { Limits.Count: > 0 } ? null : "Could not reach MiniMax: " + ex.Message);
            }
            catch (Exception ex)
            {
                log.LogDebug("minimax quota: {Msg}", ex.Message);
                return (false, "Could not fetch quota: " + ex.Message);
            }
        }
        return (false, refusal ?? (_minimax is { Limits.Count: > 0 } ? null : "MiniMax did not accept the API key"));
    }

    /// <summary>
    /// Parses the plan-usage reply. The text (and speech) models share one bucket named "general", which carries the
    /// 5-hour window the plan shows; image and video models only count against the weekly window and are skipped.
    /// Returns the limits, and when the key was refused for this region, the service's message instead of them.
    /// </summary>
    internal static (List<QuotaLimit> Limits, string? Refused)? ParseMiniMax(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("base_resp", out var resp)) return null;
            var status = resp.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt64(out var code) ? code : 0;
            if (status != 0)
                return (new List<QuotaLimit>(), "MiniMax said: " + (Str(resp, "status_msg") ?? "error " + status));
            if (!root.TryGetProperty("model_remains", out var remains) || remains.ValueKind != JsonValueKind.Array) return null;

            JsonElement bucket = default;
            var hasBucket = false;
            foreach (var entry in remains.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                hasBucket = true;
                if (Str(entry, "model_name") is "general" or "text" or "abab") { bucket = entry; break; }
                if (!hasBucket || bucket.ValueKind != JsonValueKind.Object) bucket = entry; // the first entry, when no text bucket exists
            }
            if (!hasBucket) return null;

            var used5h = 100 - (Num(bucket, "current_interval_remaining_percent") ?? 100);
            var usedWeekly = 100 - (Num(bucket, "current_weekly_remaining_percent") ?? 100);
            var limits = new List<QuotaLimit>
            {
                new("5-hour session", ClampPercent(used5h), Long(bucket, "end_time"), 5 * 3600L,
                    used5h >= 95 ? "critical" : used5h >= 80 ? "warning" : "normal"),
                new("Weekly", ClampPercent(usedWeekly), Long(bucket, "weekly_end_time"), 7 * 86400L,
                    usedWeekly >= 95 ? "critical" : usedWeekly >= 80 ? "warning" : "normal"),
            };
            return (limits, null);
        }
        catch (JsonException) { return null; }
    }

    private static double ClampPercent(double percent) => Math.Clamp(percent, 0, 100);
}
