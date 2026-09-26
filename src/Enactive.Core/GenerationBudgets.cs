namespace Enactive.Core.Chat;

public enum GenerationPurpose { Action, FileWrite, FinalAnswer, Handover, Review, Planning }

/// <summary>Per-request output ceilings, independent of context capacity and provider preferences.</summary>
public sealed record GenerationBudgets(
    int Action = 4096, int FileWrite = 8192, int FinalAnswer = 2048, int Handover = 2048, int Planner = 4096)
{
    public int For(GenerationPurpose purpose) => Math.Max(1, purpose switch
    {
        GenerationPurpose.FileWrite => FileWrite,
        GenerationPurpose.FinalAnswer => FinalAnswer,
        GenerationPurpose.Handover => Handover,
        GenerationPurpose.Planning => Planner,
        _ => Action
    });
}
