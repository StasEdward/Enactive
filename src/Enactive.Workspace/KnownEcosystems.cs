namespace Enactive.Workspace;

using Enactive.Core.Builds;

/// <summary>
/// The kinds of project this engine knows how to build and test. One list, for every place that needs it - the run's own
/// build and test checks and the run_tests tool - so a new kind is added here once and reaches both.
/// </summary>
public static class KnownEcosystems
{
    public static IReadOnlyList<IEcosystem> All { get; } = [new DotnetEcosystem()];
}
