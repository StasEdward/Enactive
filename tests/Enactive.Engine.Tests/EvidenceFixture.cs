namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;

internal static class EvidenceFixture
{
    // Existing outcome tests supply actions; render all of them through the real evidence contract.
    public static ProofVerdict Check(ProofClaim claim, IReadOnlyList<ExecutedAction> actions,
        string? workspaceRoot = null)
    {
        var journal = new ExecutionJournal();
        foreach (var action in actions)
            journal.Record(action.Step, action.Tool, action.Arguments, action.Outcome, action.Output);
        return ProofAudit.Check(claim, journal.Describe(maxChars: int.MaxValue), workspaceRoot);
    }
}
