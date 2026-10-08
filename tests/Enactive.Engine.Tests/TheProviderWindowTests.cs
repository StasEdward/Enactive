namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Settings;
using Xunit;

/// <summary>
/// The provider window in two columns - where the provider is and how to reach it, then how its models are used - with
/// no display name asked for. One column of twenty fields needed a scroll to reach the models list.
/// </summary>
public sealed class TheProviderWindowTests
{
    private static string Window()
        => File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "ProviderEditWindow.axaml"));

    private static string Column(string window, string name, string? next)
    {
        var start = window.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        var end = next is null ? window.Length : window.IndexOf($"x:Name=\"{next}\"", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"no column {name} before {next}");
        return window[start..end];
    }

    /// <summary>Every field in its column: connection on the left, the model's use on the right.</summary>
    [Fact]
    public void Each_field_is_in_its_column()
    {
        var window = Window();
        var connection = Column(window, "ConnectionColumn", "ModelColumn");
        var model = Column(window, "ModelColumn", null);

        foreach (var field in new[] { "Id", "Kind", "BaseUrl", "ApiKey", "HeadersText", "ModelsText", "FetchCommand", "TestCommand" })
            Assert.Contains("{Binding " + field + "}", connection, StringComparison.Ordinal);
        foreach (var field in new[] { "ContextWindowText", "HandoverText", "AnswerReserveText", "MaxTokensText", "ReasoningAllowanceText",
                     "TemperatureText", "EffortText", "OllamaKeepAliveText", "OpenAiReasoningProfile", "SendReasoningBack",
                     "StreamIdleTimeoutText", "CompletionTimeoutText" })
            Assert.Contains("{Binding " + field + "}", model, StringComparison.Ordinal);
    }

    /// <summary>The window asks for no display name - the id is the name in every model reference.</summary>
    [Fact]
    public void The_window_asks_for_no_display_name()
        => Assert.DoesNotContain("DisplayName", Window(), StringComparison.Ordinal);

    /// <summary>A provider with no display name is called by its id wherever it is named, and the file gains no field for it.</summary>
    [Fact]
    public void A_provider_with_no_display_name_is_called_by_its_id()
    {
        Assert.Equal("local", new ProviderConfig { Id = "local" }.Name);
        Assert.Equal("Anthropic (work)", new ProviderConfig { Id = "anthropic", DisplayName = "Anthropic (work)" }.Name);
        Assert.DoesNotContain("\"Name\"", JsonSerializer.Serialize(new ProviderConfig { Id = "local" }), StringComparison.Ordinal);
    }
}
