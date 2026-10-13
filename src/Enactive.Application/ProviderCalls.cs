namespace Enactive.Application;

using Enactive.Providers;
using Enactive.Settings;

/// <summary>
/// How the settings ask a provider whether it is there (Test, and the lights on the Providers list) and which models it
/// serves (Fetch).
///
/// <para>A seam because of the key. The window holds the person's keys and asks with the one the editor shows. The
/// service's settings page is never handed a stored key (AppSettings.WithoutSecrets): asked as the window asks, every
/// provider whose box was left as it was would be asked with no key at all, and a cloud provider that is fine would
/// light up red. The service asks with the key it keeps instead.</para>
/// </summary>
internal interface IProviderCalls
{
    /// <param name="provider">
    /// The provider as typed. Its <see cref="ProviderConfig.ApiKey"/> is what the box holds; where that is empty, its
    /// <see cref="ProviderConfig.ApiKeyProtected"/> says which key is kept for it, or is empty when it has none.
    /// </param>
    /// <param name="model">The model to look for among the ones it serves, or null to ask only whether it answers.</param>
    Task<ProviderStatus> CheckAsync(ProviderConfig provider, string? model);

    /// <summary>The models the provider serves. Throws with what went wrong, which the editor says.</summary>
    Task<List<string>> ModelsAsync(ProviderConfig provider);
}

/// <summary>The window's: the provider is asked with the key the editor holds.</summary>
internal sealed class DirectProviderCalls : IProviderCalls
{
    public static DirectProviderCalls Instance { get; } = new();

    public Task<ProviderStatus> CheckAsync(ProviderConfig provider, string? model)
        => ProviderProbe.CheckAsync(ProviderEditViewModel.Http, provider.Kind, provider.Id, provider.BaseUrl, provider.ApiKey,
            provider.Headers, model);

    public Task<List<string>> ModelsAsync(ProviderConfig provider)
        => ModelFetch.ForAsync(ProviderEditViewModel.Http, provider.Kind, provider.BaseUrl, provider.ApiKey, provider.Headers);
}
