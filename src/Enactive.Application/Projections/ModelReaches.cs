namespace Enactive.Application.Projections;

using Enactive.Settings;

/// <summary>
/// Where a provider runs, by its id - this machine (or the network) or bought from a cloud. The engine records WHICH
/// provider produced each turn and stops there; only the settings know a provider's address, so the rule is here, for
/// every host that splits a run's work by where it ran (ModelWorkSplit).
/// </summary>
internal static class ModelReaches
{
    /// <summary>
    /// A provider the settings no longer contain is Unknown, not Cloud: a deleted or renamed provider is a gap in what
    /// is known, and filing it under "cloud" would put made-up numbers next to real ones.
    /// </summary>
    public static ModelReach Of(AppSettings settings, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return ModelReach.Unknown;
        var provider = settings.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return ModelReach.Unknown;
        return ProviderReach.Local(provider.BaseUrl) ? ModelReach.Local : ModelReach.Cloud;
    }
}
