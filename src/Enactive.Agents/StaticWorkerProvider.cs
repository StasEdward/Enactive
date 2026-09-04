namespace Enactive.Agents;

using Enactive.Core.Workers;

/// <summary>Exposes a fixed set of worker roles and resolves one by id.</summary>
public sealed class StaticWorkerProvider : IWorkerProvider
{
    private readonly IReadOnlyDictionary<string, Worker> _byId;

    public StaticWorkerProvider(Worker worker) : this(new[] { worker }, worker.Id) { }

    public StaticWorkerProvider(IReadOnlyList<Worker> workers, string defaultId)
    {
        if (workers.Count == 0)
            throw new ArgumentException("At least one worker is required.", nameof(workers));
        _byId = workers.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        All = workers;
        Default = _byId.TryGetValue(defaultId, out var d) ? d : workers[0];
    }

    public Worker Default { get; }
    public IReadOnlyList<Worker> All { get; }

    public Worker Get(string? id)
        => id is not null && _byId.TryGetValue(id, out var w) ? w : Default;
}
