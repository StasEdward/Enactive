namespace AIClient.Agents;

using AIClient.Core.Workers;

/// <summary>Exposes a single, pre-built default worker. Multiple roles arrive with the Workers stage.</summary>
public sealed class StaticWorkerProvider : IWorkerProvider
{
    public StaticWorkerProvider(Worker worker) => Default = worker;

    public Worker Default { get; }
}
