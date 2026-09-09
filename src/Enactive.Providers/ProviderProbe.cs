namespace Enactive.Providers;

using Enactive.Core.Providers;

/// <summary>What a check learned about a provider. Ordered from worst to best is deliberate.</summary>
public enum ProviderHealth
{
    /// <summary>Nobody has asked yet. Not a claim about the provider.</summary>
    Unknown,

    /// <summary>Asking now.</summary>
    Checking,

    /// <summary>It could not be reached, or refused the credential.</summary>
    Unreachable,

    /// <summary>Reached, and it does not offer the model this is configured to use.</summary>
    ModelMissing,

    /// <summary>Reached, credential accepted, and the configured model is among the ones it offers.</summary>
    Ready
}

/// <summary>
/// The result of one check, with the time it was learned.
///
/// <para>The time is part of the answer, not decoration. A green light with nothing behind it is
/// the same lie as a card that says "Waiting" after the answer was accepted: what it tells you is
/// how things were at some unstated moment, and the reader supplies "now" for free.</para>
/// </summary>
public sealed record ProviderStatus(
    ProviderHealth Health,
    string Summary,
    IReadOnlyList<string> Models,
    DateTimeOffset At)
{
    public static ProviderStatus Unknown { get; } =
        new(ProviderHealth.Unknown, "Not checked", [], default);
}

/// <summary>
/// Asks a provider whether it is there, in the cheapest way that can answer honestly.
///
/// <para><b>Why not simply a status endpoint.</b> Reaching one proves the server is running and
/// that the credential was accepted. It does not prove the configured MODEL is there, and a model
/// that is not installed is the single commonest way this fails after a working setup - somebody
/// changes the model name, or moves to a machine where it was never pulled. A green light that
/// means "the server answered" would go green in exactly that case.</para>
///
/// <para><b>Why not a real completion either.</b> That is the only thing that proves generation
/// works, and it costs money on a metered provider and can take half a minute on a local model
/// loading for the first time - for a button somebody presses while configuring. So the catalogue
/// is the default, and the answer says what it actually established rather than "OK".</para>
///
/// <para>The catalogue call is the same one the Refresh button makes, which is the point: this
/// exercises the path the app really uses, not a second one written for the check.</para>
/// </summary>
public static class ProviderProbe
{
    /// <param name="model">
    /// The model this provider is configured to use, when there is one. Null asks only whether the
    /// provider answers - which is the honest question when nothing has chosen a model yet.
    /// </param>
    public static async Task<ProviderStatus> CheckAsync(
        HttpClient http, ProviderKind kind, string providerId, string baseUrl, string? apiKey,
        IReadOnlyDictionary<string, string>? headers, string? model,
        TimeProvider? clock = null, CancellationToken ct = default)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();

        List<string> models;

        try
        {
            models = await ModelFetch.ForAsync(http, kind, baseUrl, apiKey, headers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The same sentence the run would have shown, from the same place. Two explanations of
            // one failure drift apart, and the one nobody is looking at is the one that rots.
            var said = ProviderTrouble.Explain(providerId, model ?? "the model", baseUrl, ex);

            return new ProviderStatus(ProviderHealth.Unreachable, said ?? ex.Message, [], now);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ProviderStatus(
                ProviderHealth.Ready, $"Answered · {models.Count} model(s)", models, now);
        }

        // Ollama names a model "qwen2.5-coder:14b" and is asked for "qwen2.5-coder", which it
        // resolves to the :latest tag. Treating that as missing would light a warning on the
        // commonest local setup there is.
        var present = models.Any(m =>
            string.Equals(m, model, StringComparison.OrdinalIgnoreCase)
            || m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));

        return present
            ? new ProviderStatus(
                ProviderHealth.Ready, $"Answered · '{model}' is there · {models.Count} model(s)", models, now)

            // Reached, and wrong: a distinct state rather than a failure, because the fix is a
            // different one. Nothing is broken; something is not installed or is misspelled.
            : new ProviderStatus(
                ProviderHealth.ModelMissing,
                $"Answered, but '{model}' is not among the {models.Count} it offers.", models, now);
    }
}
