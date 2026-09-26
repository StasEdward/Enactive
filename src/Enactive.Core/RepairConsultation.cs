namespace Enactive.Core.Chat;

using Enactive.Core.Providers;

/// <summary>Opt-in advice, never a worker replacement. Threshold requires local measurement/configuration.</summary>
public sealed record RepairConsultation(ModelRef? Model = null, int FailedRepairs = 0, int OutputTokens = 1024)
{
    public bool Enabled => Model is { ProviderId.Length: > 0, Model.Length: > 0 } && FailedRepairs >= 2;
}
