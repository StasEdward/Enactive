namespace Enactive.Core.Execution;

/// <summary>A new transport attempt was refused by the run's token/time budget.</summary>
public sealed class RetryBudgetExceededException(string reason, Exception cause) : Exception(reason, cause);
