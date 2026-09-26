namespace Enactive.Agents;

using System.Text;
using Enactive.Core.Events;

/// <summary>Retains bounded chunks of adjacent text deltas; action evidence is kept verbatim.</summary>
public sealed class RecordedEventBuffer
{
    public List<WorkEvent> Events { get; } = new();
    private WorkEvent? _first;
    private readonly StringBuilder _text = new();

    public void Add(WorkEvent item)
    {
        if (item.Kind == EventKind.GenerationProgress) return;
        if (item.Kind != EventKind.AssistantDelta)
        { Flush(); Events.Add(item); return; }
        if (_first is not null && (_first.RunId != item.RunId || _first.TaskId != item.TaskId
            || _first.PayloadJson != item.PayloadJson || _text.Length + item.Summary.Length > 4096)) Flush();
        _first ??= item;
        _text.Append(item.Summary);
        if (_text.Length >= 4096) Flush();
    }

    public void Flush()
    {
        if (_first is null) return;
        Events.Add(_first with { Summary = _text.ToString() });
        _text.Clear();
        _first = null;
    }
}
