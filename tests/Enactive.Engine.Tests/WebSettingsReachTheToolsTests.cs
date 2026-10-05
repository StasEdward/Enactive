namespace Enactive.Engine.Tests;

using Enactive.Core.Mail;
using Enactive.Core.Web;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Tools.Web;
using Xunit;

/// <summary>What a person sets under Settings → Web, as the web tools receive it - and survives a save.</summary>
public sealed class WebSettingsReachTheToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("web-settings").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_settings_file_without_the_section_has_the_web_off()
    {
        var access = EngineComposition.Web(new AppSettings());

        Assert.False(access.Enabled);
        Assert.False(access.CanSearch);
    }

    [Fact]
    public void A_search_server_set_while_the_web_is_off_gives_no_search()
    {
        var settings = new AppSettings { Web = new WebSettings { Enabled = false, SearchUrl = "http://localhost:8888" } };

        Assert.False(EngineComposition.Web(settings).CanSearch);
    }

    [Theory]
    [InlineData("localhost:8888")]
    [InlineData("searx")]
    [InlineData("ftp://localhost:8888")]
    public void An_address_that_is_not_http_gives_no_search_rather_than_one_that_fails_every_call(string address)
        => Assert.False(new WebAccess(true, address).CanSearch);

    [Fact]
    public void What_was_set_is_what_the_tools_get_after_a_save_and_a_load()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new AppSettings { Web = new WebSettings { Enabled = true, SearchUrl = " http://localhost:8888/ ", UseWithoutAsking = true } };
        Assert.True(settings.Save(path), settings.LastSaveError);

        var access = EngineComposition.Web(AppSettings.Load(path));
        var names = BuiltInTools.Create(MailAccount.None, access).Select(t => t.Definition.Name).ToArray();

        Assert.True(access.CanSearch);
        Assert.True(access.UseWithoutAsking);
        Assert.Contains(FetchUrlTool.Name, names);
        Assert.Contains(WebSearchTool.Name, names);
    }

    [Fact]
    public void The_settings_window_keeps_the_section_through_its_copy()
    {
        var settings = new AppSettings { Web = new WebSettings { Enabled = true, SearchUrl = "http://localhost:8888", UseWithoutAsking = true } };

        var copy = settings.Clone();

        Assert.NotSame(settings.Web, copy.Web);
        Assert.Equal((true, "http://localhost:8888", true), (copy.Web.Enabled, copy.Web.SearchUrl, copy.Web.UseWithoutAsking));
    }
}
