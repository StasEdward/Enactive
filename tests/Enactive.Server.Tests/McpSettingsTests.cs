namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Tools.Mcp;

/// <summary>
/// MCP servers on the settings page (server plan phase 3): their tokens never on the page, kept when nothing new is
/// typed, replaced or removed only on purpose - and a connection tried only by an Admin.
/// </summary>
public sealed class McpSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-mcp-settings", Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly ServerSettings _settings;

    public McpSettingsTests()
    {
        _users = new UserStore(_root);
        _users.Add("ada", "correct horse battery", Role.Admin);
        _users.Add("alice", "correct horse battery", Role.Operator);
        _engine = new ServerEngine(_root, new Enactive.Workspace.FileLogSink(Path.Combine(_root, "logs")));
        _settings = new ServerSettings(_users, _engine, new WorkspaceRoots([]));

        var settings = AppSettings.Load(_engine.SettingsFile);
        settings.McpServers.Add(new McpServerConfig
        {
            Id = "tracker", Enabled = true, Transport = McpTransportKind.Http, Url = "https://mcp.example.com/",
            Headers = new() { ["Authorization"] = "Bearer tk-very-secret" }
        });
        Assert.True(settings.Save(_engine.SettingsFile, replaceUnreadable: true));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    private McpServerConfig Saved() => AppSettings.Load(_engine.SettingsFile).McpServers.Single();

    /// <summary>Open, change something else, save: a token nobody could see to type back must still be there.</summary>
    [Fact]
    public void Saved_with_nothing_typed_its_credentials_are_kept()
    {
        var opened = _settings.Open(As("ada")).Opened!;
        var server = opened.Copy.McpServers.Single();
        var form = McpServerForm.Of(server);
        form.Timeout = "30";

        Assert.Null(form.ApplyTo(server));
        Assert.True(_settings.Save(As("ada"), opened).Saved);

        Assert.Equal(30, Saved().TimeoutSeconds);
        Assert.Equal("Bearer tk-very-secret", Saved().Headers["Authorization"]);
        Assert.DoesNotContain("tk-very-secret", File.ReadAllText(_engine.SettingsFile));
    }

    [Fact]
    public void The_page_is_told_the_names_never_the_values()
    {
        var copy = _settings.Open(As("ada")).Opened!.Copy.McpServers.Single();
        var said = McpServerForm.NamesSet(copy);

        Assert.Contains("Authorization", said);
        Assert.DoesNotContain("tk-very-secret", said);
        // Nor does the copy the page edits hold them, to be drawn by a box bound to the wrong field.
        Assert.Equal("", copy.Headers["Authorization"]);
    }

    /// <summary>
    /// "Remove its environment and headers" on the page's copy removes them: the copy holds a handle for the credentials
    /// it never saw, and a save put back whatever a handle named - the box removed nothing.
    /// </summary>
    [Fact]
    public void The_box_removes_them_from_the_page_s_copy_too()
    {
        var opened = _settings.Open(As("ada")).Opened!;
        var server = opened.Copy.McpServers.Single();

        Assert.Null(new McpServerForm { ClearCredentials = true, Timeout = "60" }.ApplyTo(server));
        Assert.True(_settings.Save(As("ada"), opened).Saved);

        Assert.Empty(Saved().Headers);
        Assert.Equal("", Saved().SecretsProtected);
    }

    [Fact]
    public void New_lines_replace_them_and_the_box_removes_them()
    {
        var replaced = Saved();
        Assert.Null(new McpServerForm { NewHeaders = "Authorization: Bearer tk-new\nX-Team: lab", Timeout = "60" }.ApplyTo(replaced));
        Assert.Equal(new Dictionary<string, string> { ["Authorization"] = "Bearer tk-new", ["X-Team"] = "lab" }, replaced.Headers);

        var removed = Saved();
        Assert.Null(new McpServerForm { ClearCredentials = true, Timeout = "60" }.ApplyTo(removed));
        Assert.Empty(removed.Headers);
    }

    [Fact]
    public void A_line_that_is_not_a_pair_is_refused_and_says_so()
    {
        var server = new McpServerConfig { Id = "local", Command = "server.exe" };

        Assert.Contains("NAME=value", new McpServerForm { NewEnvironment = "TOKEN", Timeout = "60" }.ApplyTo(server));
    }

    /// <summary>One argument per line: a path with a space in it stays one argument.</summary>
    [Fact]
    public void Arguments_are_one_per_line()
    {
        var server = new McpServerConfig { Id = "local", Command = "server.exe" };

        Assert.Null(new McpServerForm { Arguments = "--root\nC:\\Program Files\\Lab\n", Timeout = "60" }.ApplyTo(server));
        Assert.Equal(["--root", "C:\\Program Files\\Lab"], server.Arguments);
    }

    /// <summary>A stdio server is a program the service starts as its own account: an Admin's to try.</summary>
    [Fact]
    public async Task Only_an_admin_tries_a_connection_and_a_failure_says_why_without_the_details()
    {
        var server = new McpServerConfig { Id = "local", Command = "not-a-real-executable-" + Guid.NewGuid().ToString("N"), TimeoutSeconds = 10 };

        Assert.Contains("role does not include", await _settings.TestMcpAsync(As("alice"), server, CancellationToken.None));
        Assert.StartsWith("Could not connect", await _settings.TestMcpAsync(As("ada"), server, CancellationToken.None));
        // Typed in full, nothing kept was used: nothing to record.
        Assert.DoesNotContain(_users.AuditTrail(), e => e.Action == "MCP server tested");
    }

    /// <summary>
    /// The credentials kept for a server go wherever an Admin points it now - an Admin may do that - and the audit trail
    /// says where they went, and who sent them, never what they were.
    /// </summary>
    [Fact]
    public async Task A_test_with_the_credentials_kept_for_a_server_is_audited_with_where_they_went()
    {
        var server = _settings.Open(As("ada")).Opened!.Copy.McpServers.Single();
        // With a token of its own in the address (user info is refused by the server's own check): the trail says where,
        // never what the query carried (ServerSettings.Address).
        server.Url = "http://127.0.0.1:1/elsewhere?token=tok-in-url#part";
        server.TimeoutSeconds = 5;

        await _settings.TestMcpAsync(As("ada"), server, CancellationToken.None);

        var entry = Assert.Single(_users.AuditTrail(), e => e.Action == "MCP server tested");
        Assert.Equal("ada", entry.Account);
        Assert.Equal("tracker: http://127.0.0.1:1/elsewhere (with the credentials kept for it)", entry.Detail);
        Assert.DoesNotContain("tk-very-secret", entry.Detail);
        Assert.DoesNotContain("in-url", entry.Detail);
    }
}
