using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The OpenCode Go and MiniMax quota checks through their seams: a fake HTTP handler, a test's own OpenCode database
/// and configuration, so nothing here reads the real key or reaches opencode.ai or MiniMax. The Go sign-in lives in
/// OpenCode's database; the MiniMax key in the environment or OpenCode's configuration — both are only ever read.
/// </summary>
public sealed class OpenCodeMinimaxQuotaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-opencode-minimax-").FullName;
    private readonly FakeApi _api = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private QuotaMonitor Agent()
    {
        Environment.SetEnvironmentVariable("MINIMAX_API_KEY", null);
        return new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
        {
            OpenCodeDbPath = Path.Combine(_dir, "opencode.db"),
            MiniMaxConfigPaths = [Path.Combine(_dir, "opencode.json")],
            HttpHandler = _api,
        };
    }

    // ------------------------------------------------------------------ OpenCode Go

    private void OpenCodeDatabase(params string[] values)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(_dir, "opencode.db"));
        db.Open();
        using var create = db.CreateCommand();
        create.CommandText = "CREATE TABLE credential (id TEXT PRIMARY KEY, integration_id TEXT, value TEXT, time_updated INTEGER)";
        create.ExecuteNonQuery();
        for (var i = 0; i < values.Length; i++)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO credential (id, integration_id, value, time_updated) VALUES ($id, 'opencode-go', $value, $time)";
            insert.Parameters.AddWithValue("$id", "cred-" + i);
            insert.Parameters.AddWithValue("$value", values[i]);
            insert.Parameters.AddWithValue("$time", i);
            insert.ExecuteNonQuery();
        }
    }

    [Theory]
    [InlineData("""{"type":"key","key":"go-key-1"}""", "go-key-1", null, null, null)]
    [InlineData("""{"type":"oauth","access":"token-2","metadata":{"server":"https://console.opencode.ai/console","orgID":"org_1","orgName":"Team X"}}""",
        "token-2", "https://console.opencode.ai/console", "org_1", "Team X")]
    public void ParsesTheSavedGoIntegration(string json, string key, string? server, string? org, string? orgName)
    {
        var credential = QuotaMonitor.OpenCodeCredentialFromJson(json);
        Assert.NotNull(credential);
        Assert.Equal(key, credential.Key);
        Assert.Equal(server, credential.Server);
        Assert.Equal(org, credential.OrgId);
        Assert.Equal(orgName, credential.OrgName);
    }

    [Theory]
    [InlineData("""{"type":"key"}""")]
    [InlineData("""{"type":"key","key":42}""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public void AValueWithoutAKeyIsNoSignIn(string json) => Assert.Null(QuotaMonitor.OpenCodeCredentialFromJson(json));

    [Fact]
    public void TheUsageEndpointSitsOnTheConsoleTheSignInBelongsTo()
    {
        Assert.Equal("https://opencode.ai/zen/go/v1/usage",
            QuotaMonitor.OpenCodeUsageUrl(new QuotaMonitor.OpenCodeCredential("k", null, null, null)));
        Assert.Equal("https://opencode.ai/zen/go/v1/usage",
            QuotaMonitor.OpenCodeUsageUrl(new QuotaMonitor.OpenCodeCredential("k", "https://console.opencode.ai/console", null, null)));
        Assert.Equal("https://self.hosted/zen/go/v1/usage",
            QuotaMonitor.OpenCodeUsageUrl(new QuotaMonitor.OpenCodeCredential("k", "https://self.hosted/console", null, null)));
    }

    [Fact]
    public void ReadsTheNewestGoKeyFromTheDatabase()
    {
        OpenCodeDatabase("""{"type":"key","key":"older"}""", """{"type":"key","key":"newest"}""");
        var agent = Agent();
        Assert.Equal("newest", QuotaMonitor.ReadOpenCodeCredential(agent.OpenCodeDbPath)!.Key);
    }

    [Fact]
    public void AMissingDatabaseIsNoOpenCodeInstallation()
    {
        var agent = Agent();
        Assert.Null(QuotaMonitor.ReadOpenCodeCredential(agent.OpenCodeDbPath));
    }

    private const string GoUsage =
        """{"usage":{"rolling":{"status":"ok","percent":3,"resetsAt":"2099-01-01T07:06:58.000Z"},"weekly":{"status":"ok","percent":1,"resetsAt":"2099-01-05T00:00:00.000Z"},"monthly":{"status":"ok","percent":14,"resetsAt":"2099-01-24T09:30:36.000Z"}}}""";

    [Fact]
    public async Task TheGoWindowsArriveAsThreeLimits()
    {
        OpenCodeDatabase("""{"type":"key","key":"go-key-1"}""");
        _api.Respond(HttpStatusCode.OK, GoUsage);
        var agent = Agent();
        Assert.True(await agent.RefreshOpenCodeAsync(default));
        Assert.Equal(1, _api.Calls);
        Assert.Equal("https://opencode.ai/zen/go/v1/usage", _api.LastUrl);
        Assert.Equal("go-key-1", _api.LastKey);
        var go = Assert.Single(agent.Snapshot(), p => p.Id == "opencode");
        Assert.Equal("OpenCode", go.Name);
        Assert.Equal("Go", go.Plan);
        Assert.Null(go.Error);
        Assert.Collection(go.Limits,
            rolling =>
            {
                Assert.Equal("5-hour session", rolling.Label);
                Assert.Equal(3, rolling.Percent);
                Assert.Equal("normal", rolling.Severity);
                Assert.Equal(18000, rolling.WindowSec);
                Assert.Equal(new DateTimeOffset(2099, 1, 1, 7, 6, 58, TimeSpan.Zero).ToUnixTimeMilliseconds(), rolling.ResetsAt);
            },
            weekly => { Assert.Equal("Weekly", weekly.Label); Assert.Equal(604800, weekly.WindowSec); },
            monthly => { Assert.Equal("Monthly", monthly.Label); Assert.Null(monthly.WindowSec); Assert.Equal(14, monthly.Percent); });
    }

    [Fact]
    public async Task ARateLimitedGoWindowIsCritical()
    {
        OpenCodeDatabase("""{"type":"key","key":"go-key-1"}""");
        _api.Respond(HttpStatusCode.OK,
            """{"usage":{"rolling":{"status":"rate-limited","percent":100,"resetsAt":"2099-01-01T07:06:58.000Z"},"weekly":{"status":"ok","percent":96,"resetsAt":null},"monthly":{"status":"ok","percent":60,"resetsAt":null}}}""");
        var agent = Agent();
        Assert.True(await agent.RefreshOpenCodeAsync(default));
        var go = Assert.Single(agent.Snapshot(), p => p.Id == "opencode");
        Assert.Equal("critical", go.Limits[0].Severity);
        Assert.Equal("critical", go.Limits[1].Severity); // ≥ 95 % used, even while the server still says ok
        Assert.Equal("normal", go.Limits[2].Severity);
        Assert.Null(go.Limits[1].ResetsAt);
    }

    [Fact]
    public async Task ARefusedGoSignInIsReported()
    {
        OpenCodeDatabase("""{"type":"key","key":"go-key-1"}""");
        _api.Respond(HttpStatusCode.Forbidden, "error code: 1010");
        var agent = Agent();
        Assert.False(await agent.RefreshOpenCodeAsync(default));
        var go = Assert.Single(agent.Snapshot(), p => p.Id == "opencode");
        Assert.Empty(go.Limits);
        Assert.Equal("OpenCode did not accept the OpenCode Go sign-in", go.Error);
    }

    [Fact]
    public async Task ADatabaseWithoutAGoSignInSaysSo()
    {
        OpenCodeDatabase();
        var agent = Agent();
        Assert.False(await agent.RefreshOpenCodeAsync(default));
        Assert.Equal(0, _api.Calls);
        Assert.Equal(QuotaMonitor.NoOpenCodeGo, Assert.Single(agent.Snapshot(), p => p.Id == "opencode").Error);
    }

    [Fact]
    public async Task WithoutOpenCodeThereIsNoCard()
    {
        var agent = Agent();
        Assert.False(await agent.RefreshOpenCodeAsync(default));
        Assert.Equal(0, _api.Calls);
        Assert.DoesNotContain(agent.Snapshot(), p => p.Id == "opencode");
    }

    // ------------------------------------------------------------------ MiniMax

    [Fact]
    public void ReadsTheKeyFromTheOpenCodeConfiguration()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """
            {
              "$schema": "https://opencode.ai/config.json",
              "providers": {
                "other": { "settings": { "apiKey": "not-minimax" } },
                "minimax": {
                  "name": "MiniMax",
                  "settings": { "baseURL": "https://api.minimaxi.com/v1", "apiKey": "sk-cp-test" }
                }
              }
            }
            """);
        var agent = Agent();
        Assert.Equal("sk-cp-test", agent.ReadMiniMaxKey());
        Assert.Equal("https://api.minimaxi.com/v1", agent.ReadMiniMaxBaseUrl());
    }

    [Fact]
    public void ReadsTheKeyFromTheSingularSpellingAndSkipsComments()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """
            {
              // the pay-as-you-go key is elsewhere
              "provider": { "minimax": { "apiKey": "sk-cp-singular" } }
            }
            """);
        var agent = Agent();
        Assert.Equal("sk-cp-singular", agent.ReadMiniMaxKey());
    }

    [Fact]
    public void AnEnvironmentVariableBeatsTheConfiguration()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"minimax":{"apiKey":"sk-cp-file"}}}""");
        var previous = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("MINIMAX_API_KEY", "sk-cp-env");
            var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
            {
                OpenCodeDbPath = Path.Combine(_dir, "unused.db"),
                MiniMaxConfigPaths = [path],
                HttpHandler = _api,
            };
            Assert.Equal("sk-cp-env", agent.ReadMiniMaxKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINIMAX_API_KEY", previous);
        }
    }

    [Fact]
    public void AConfigurationWithoutMiniMaxYieldsNoKey()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"other":{"apiKey":"sk-other"}}}""");
        Assert.Null(Agent().ReadMiniMaxKey());
        Assert.Null(QuotaMonitor.MiniMaxKeyFromJson("no json here"));
    }

    [Fact]
    public void TheRemainsReplyBecomesTheTwoUsageWindows()
    {
        var (limits, refused) = QuotaMonitor.ParseMiniMax("""
            {"model_remains":[
              {"model_name":"video","current_interval_remaining_percent":100,"end_time":1795503600000,
               "current_weekly_remaining_percent":50,"weekly_end_time":1795968000000},
              {"model_name":"general","current_interval_remaining_percent":40,"end_time":1795503600000,
               "current_weekly_remaining_percent":100,"weekly_end_time":1795968000000}],
             "base_resp":{"status_code":0,"status_msg":"success"}}
            """)!.Value;
        Assert.Null(refused);
        Assert.Collection(limits,
            fiveHour =>
            {
                Assert.Equal("5-hour session", fiveHour.Label);
                Assert.Equal(60, fiveHour.Percent); // 40 % left means 60 % used
                Assert.Equal("normal", fiveHour.Severity);
                Assert.Equal(18000, fiveHour.WindowSec);
                Assert.Equal(1795503600000, fiveHour.ResetsAt);
            },
            weekly => { Assert.Equal("Weekly", weekly.Label); Assert.Equal(0, weekly.Percent); Assert.Equal(604800, weekly.WindowSec); });
    }

    [Fact]
    public void AGeneralBucketIsPreferredOverVideo()
    {
        var (limits, _) = QuotaMonitor.ParseMiniMax("""
            {"model_remains":[
              {"model_name":"video","current_interval_remaining_percent":0,"end_time":1,"current_weekly_remaining_percent":0,"weekly_end_time":2},
              {"model_name":"general","current_interval_remaining_percent":100,"end_time":3,"current_weekly_remaining_percent":100,"weekly_end_time":4}],
             "base_resp":{"status_code":0,"status_msg":"success"}}
            """)!.Value;
        Assert.Equal(0, limits[0].Percent); // the video bucket's empty 5-hour window is not shown
        Assert.Equal(3, limits[0].ResetsAt);
    }

    [Fact]
    public void NearlyFullWindowsTurnTheRingsAmberAndRed()
    {
        var (limits, _) = QuotaMonitor.ParseMiniMax("""
            {"model_remains":[{"model_name":"general","current_interval_remaining_percent":2,"end_time":1,
             "current_weekly_remaining_percent":15,"weekly_end_time":2}],"base_resp":{"status_code":0,"status_msg":"success"}}
            """)!.Value;
        Assert.Equal("critical", limits[0].Severity); // 98 % used
        Assert.Equal("warning", limits[1].Severity);  // 85 % used
    }

    [Fact]
    public void AKeyTheServiceDoesNotKnowIsReported()
    {
        var parsed = QuotaMonitor.ParseMiniMax("""{"base_resp":{"status_code":2049,"status_msg":"invalid api key"}}""")!.Value;
        Assert.Empty(parsed.Limits);
        Assert.Equal("MiniMax said: invalid api key", parsed.Refused);
        Assert.Null(QuotaMonitor.ParseMiniMax("no json here"));
        Assert.Null(QuotaMonitor.ParseMiniMax("""{"base_resp":{}}"""));
    }

    [Fact]
    public async Task TheConfiguredRegionsEndpointIsTriedFirst()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"minimax":{"settings":{"baseURL":"https://self.hosted/v1","apiKey":"sk-cp-test"}}}}""");
        _api.Respond(HttpStatusCode.OK, GeneralRemains("https://self.hosted"));
        var agent = Agent();
        Assert.True(await agent.RefreshMiniMaxAsync(default));
        var _ = Assert.Single(agent.Snapshot(), p => p.Id == "minimax");
        Assert.Equal("https://self.hosted/v1/token_plan/remains", _api.LastUrl);
        Assert.Equal("sk-cp-test", _api.LastKey);
    }

    private static string GeneralRemains(string resetSentinel = "") =>
        """
        {"model_remains":[{"model_name":"general","current_interval_remaining_percent":40,"end_time":1795503600000,"current_weekly_remaining_percent":100,"weekly_end_time":1795968000000}],"base_resp":{"status_code":0,"status_msg":"success"}}
        """;

    [Fact]
    public async Task AKeyOfTheOtherRegionFallsThroughToTheNextOrigin()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"minimax":{"settings":{"apiKey":"sk-cp-test"}}}}""");
        _api.Respond(HttpStatusCode.Unauthorized, "{}", thenStatus: HttpStatusCode.OK, GeneralRemains());
        var agent = Agent();
        Assert.True(await agent.RefreshMiniMaxAsync(default));
        Assert.Equal(2, _api.Calls);
        Assert.Equal("https://www.minimax.io/v1/token_plan/remains", _api.LastUrl); // api.minimaxi.com refused it, the next origin knows the key
        // The working origin is remembered: the next pass asks it alone.
        _api.Respond(HttpStatusCode.OK, GeneralRemains());
        Assert.True(await agent.RefreshMiniMaxAsync(default));
        Assert.Equal(3, _api.Calls);
        Assert.Equal("https://www.minimax.io/v1/token_plan/remains", _api.LastUrl);
    }

    [Fact]
    public async Task NoOriginKnowsTheKey()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"minimax":{"apiKey":"sk-cp-test"}}}""");
        var refused = """{"base_resp":{"status_code":2049,"status_msg":"invalid api key"}}""";
        _api.Respond(HttpStatusCode.OK, refused, HttpStatusCode.OK, refused); // both regions refuse it
        var agent = Agent();
        Assert.False(await agent.RefreshMiniMaxAsync(default));
        Assert.Equal(2, _api.Calls); // api.minimaxi.com and www.minimax.io both refused it
        Assert.Equal("MiniMax said: invalid api key", Assert.Single(agent.Snapshot(), p => p.Id == "minimax").Error);
    }

    [Fact]
    public async Task WithoutAnyKeyOrConfigurationThereIsNoCard()
    {
        var agent = Agent();
        Assert.False(await agent.RefreshMiniMaxAsync(default));
        Assert.Equal(0, _api.Calls);
        Assert.DoesNotContain(agent.Snapshot(), p => p.Id == "minimax");
    }

    [Fact]
    public async Task AConfigurationWithoutAMiniMaxKeySaysSo()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"other":{"apiKey":"sk-other"}}}""");
        var agent = Agent();
        Assert.False(await agent.RefreshMiniMaxAsync(default));
        Assert.Equal(0, _api.Calls);
        Assert.Equal(QuotaMonitor.NoMiniMaxKey, Assert.Single(agent.Snapshot(), p => p.Id == "minimax").Error);
    }

    // ------------------------------------------------------------------ snapshot

    [Fact]
    public async Task BothProvidersAppearInTheSnapshotAndLeaveExternalsAlone()
    {
        OpenCodeDatabase("""{"type":"key","key":"go-key-1"}""");
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """{"providers":{"minimax":{"apiKey":"sk-cp-test"}}}""");
        _api.Respond(HttpStatusCode.OK, GoUsage, thenStatus: HttpStatusCode.OK, GeneralRemains());
        var agent = Agent();
        Assert.True(await agent.RefreshOpenCodeAsync(default));
        Assert.True(await agent.RefreshMiniMaxAsync(default));
        var ids = agent.Snapshot().Select(p => p.Id).ToArray();
        Assert.Contains("opencode", ids);
        Assert.Contains("minimax", ids);
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _next = new();
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastKey { get; private set; }

        public void Respond(HttpStatusCode status, string body, HttpStatusCode thenStatus = HttpStatusCode.OK, string thenBody = "")
        {
            _next.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            if (thenBody.Length > 0)
                _next.Enqueue(() => new HttpResponseMessage(thenStatus) { Content = new StringContent(thenBody, Encoding.UTF8, "application/json") });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            LastKey = request.Headers.Authorization?.Parameter;
            return Task.FromResult(_next.Count > 0 ? _next.Dequeue()() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
