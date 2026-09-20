namespace Enactive.Engine.Tests;

using System.Reflection;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Providers;
using Enactive.Settings;
using Xunit;

/// <summary>
/// The context window a provider serves, which nothing in this application can find out.
///
/// <para><c>IChatProvider.ContextWindow</c> returns null by default and only the Ollama adapter
/// implements it — by echoing back the <c>num_ctx</c> it was handed, so it mirrors the request
/// rather than reporting a fact. Every cloud provider answers "I do not know", and the callers
/// that need a number guessed one: <c>LogAnalyst</c> assumed 16,000 tokens, so a log analysed by a
/// model with a 200,000-token window was sent about a seventh of what would have fitted — and the
/// number it did get came from <c>NumCtx</c>, an OLLAMA setting, whichever provider was working.
/// </para>
/// </summary>
public sealed class ProviderContextWindowTests
{
    /// <summary>
    /// A census rather than a check of one field. Every property added to <c>ProviderConfig</c>
    /// from now on has to be copied, and forgetting one is silent: the editor saves, the clone the
    /// editor works on drops it, and the setting is gone with nothing going red. <c>MaxTokens</c>
    /// and the headers were both lost that way once, on the way to the wire.
    /// </summary>
    [Fact]
    public void Clone_copies_every_property_of_a_provider()
    {
        var source = new ProviderConfig
        {
            Id = "anthropic", DisplayName = "Anthropic", Kind = ProviderKind.Anthropic,
            BaseUrl = "https://api.anthropic.com", ApiKeyProtected = "dpapi:xxxx", ApiKey = "sk-test",
            Headers = { ["X-Thing"] = "1" }, Models = { "claude-opus-5" },
            MaxTokens = 8_192, ContextWindowTokens = 200_000
        };

        var clone = source.Clone();

        foreach (var property in typeof(ProviderConfig).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            var original = property.GetValue(source);
            var copied = property.GetValue(clone);

            if (original is System.Collections.IEnumerable and not string)
                continue;   // the collections are copied by value; checked below

            Assert.True(Equals(original, copied),
                $"ProviderConfig.{property.Name} was not carried by Clone(): "
                + $"{original ?? "null"} became {copied ?? "null"}");
        }

        Assert.Equal(source.Headers, clone.Headers);
        Assert.Equal(source.Models, clone.Models);
        Assert.NotSame(source.Headers, clone.Headers);
        Assert.NotSame(source.Models, clone.Models);
    }

    /// <summary>It has to survive the disk, or it is a setting that lasts until the app closes.</summary>
    [Fact]
    public void The_declared_window_survives_a_round_trip_through_settings_json()
    {
        var json = JsonSerializer.Serialize(new ProviderConfig { Id = "x", ContextWindowTokens = 128_000 });
        var back = JsonSerializer.Deserialize<ProviderConfig>(json)!;

        Assert.Equal(128_000, back.ContextWindowTokens);
    }

    /// <summary>Blank keeps the old guess, so a provider nobody has filled in behaves as before.</summary>
    [Fact]
    public void A_provider_that_declares_nothing_keeps_the_old_assumption()
    {
        Assert.Null(new ProviderConfig { Id = "x" }.ContextWindowTokens);
        Assert.Equal(LogAnalyst.BudgetChars(null), LogAnalyst.BudgetChars(16_000));
    }

    /// <summary>
    /// And the point of the whole thing: a declared window is spent on the log. A 200,000-token
    /// model gets an order of magnitude more of it than the 16,000 that was assumed.
    /// </summary>
    [Fact]
    public void A_declared_window_buys_proportionally_more_of_the_log()
    {
        var assumed = LogAnalyst.BudgetChars(null);
        var declared = LogAnalyst.BudgetChars(200_000);

        Assert.True(declared > assumed * 10,
            $"a 200,000-token window bought {declared:N0} characters against the assumed {assumed:N0}");
    }
}
