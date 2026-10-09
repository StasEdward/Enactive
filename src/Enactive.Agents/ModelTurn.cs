namespace Enactive.Agents;

using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using static ToolCallParsing;

/// <summary>Per-response assembly and progress signals. Never executes tools or changes the transcript/run budget.</summary>
internal sealed class ModelTurn
{
    private const int ProgressEveryChars = 2000;
    private readonly Dictionary<int, ToolCallBuilder> _tools = new();
    private readonly RunawayReply _runaway = new();
    private readonly RunawayReply _reasoningLoop = new();
    private int _argumentChars, _saidAtArguments, _saidAtReasoning, _saidAtText;
    public StringBuilder Content { get; } = new();
    public StringBuilder Reasoning { get; } = new();
    public string? FinishReason { get; private set; }
    public RunawayReply.Stop? Stopped { get; private set; }
    public List<ToolCall>? BuildCalls() => BuildToolCalls(_tools);

    public Observation Observe(ChatStreamEvent delta)
    {
        Signal? visible = null, progress = null;
        switch (delta)
        {
            case TextDelta text:
                Content.Append(text.Text);
                visible = new(EventKind.AssistantDelta, text.Text);
                if (Content.Length - _saidAtText >= ProgressEveryChars)
                {
                    _saidAtText = Content.Length;
                    progress = new(EventKind.GenerationProgress,
                        $"Writing a reply: {Content.Length:N0} characters so far… {RunawayReply.LastLine(Content)}");
                }
                if (_tools.Count == 0) Stopped = _runaway.After(Content);
                break;

            case ToolCallDelta call:
                var builder = _tools.TryGetValue(call.Index, out var existing)
                    ? existing : _tools[call.Index] = new ToolCallBuilder();
                if (call.Id is not null) builder.Id = call.Id;
                if (call.Name is not null) builder.Name = call.Name;
                if (call.ArgumentsJson is not null) builder.Arguments.Append(call.ArgumentsJson);
                _argumentChars += call.ArgumentsJson?.Length ?? 0;
                if (_argumentChars - _saidAtArguments >= ProgressEveryChars)
                {
                    _saidAtArguments = _argumentChars;
                    progress = new(EventKind.GenerationProgress,
                        $"Writing {builder.Name ?? "a tool call"}: {_argumentChars:N0} characters so far…");
                }
                break;

            // Reasoning stays diagnostic; it never becomes assistant text or tool arguments.
            case ReasoningDelta reasoning:
                Reasoning.Append(reasoning.Text);
                if (Reasoning.Length - _saidAtReasoning >= ProgressEveryChars)
                {
                    _saidAtReasoning = Reasoning.Length;
                    progress = new(EventKind.GenerationProgress,
                        $"Reasoning: {Reasoning.Length:N0} characters so far…");
                }
                // A reasoning that goes round, word for word, is stopped as a reply that does (RunawayReply.LoopIn).
                if (_tools.Count == 0 && Stopped is null) Stopped = _reasoningLoop.LoopIn(Reasoning);
                break;

            case FinishDelta finish:
                FinishReason = finish.Reason;
                break;
        }
        return new(visible, progress);
    }

    // Value types avoid adding an iterator/list/event allocation for each streamed token.
    internal readonly record struct Signal(EventKind Kind, string Text);
    internal readonly record struct Observation(Signal? Visible, Signal? Progress);
}
