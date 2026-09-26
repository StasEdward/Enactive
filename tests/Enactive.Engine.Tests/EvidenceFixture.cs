namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Core.Tools;

internal static class EvidenceFixture
{
    // Existing outcome tests supply actions; render all of them through the real evidence contract.
    public static ProofVerdict Check(ProofClaim claim, IReadOnlyList<ExecutedAction> actions,
        string? workspaceRoot = null)
    {
        var journal = new ExecutionJournal();
        foreach (var action in actions)
        {
            var definition = EngineFixture.ShippedTools().Select(t => t.Definition)
                .FirstOrDefault(d => d.Name == action.Tool);
            var effect = action.WorkspaceEffect != WorkspaceEffect.Unknown ? action.WorkspaceEffect
                : action.Outcome == ActionOutcome.Succeeded ? definition?.WorkspaceEffect ?? WorkspaceEffect.Unknown
                : WorkspaceEffect.Unknown;
            journal.Record(action.Step, action.Tool, action.Arguments, action.Outcome, action.Output,
                effect, action.ChangedPaths ?? (definition is null ? null : ToolEffects.Paths(definition, action.Arguments)));
        }
        return ProofAudit.Check(claim, journal.Describe(maxChars: int.MaxValue), workspaceRoot);
    }
}
