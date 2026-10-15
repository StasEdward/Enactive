namespace Enactive.Application;

/// <summary>
/// What is checked before a run is started at all, by any host - said where the request was made, not discovered
/// later as an error from somewhere deeper.
/// </summary>
internal static class RunLauncher
{
    /// <summary>Why a run in this folder cannot start, or null when it can.</summary>
    /// <param name="engineProblem">Why the host has no engine to run on, when it has none.</param>
    public static string? Refusal(string workspacePath, string? engineProblem)
    {
        // The app used to create whatever path was in the box. That is how an empty workspace appeared beside
        // the .exe on first start: a typo, or a default nobody chose, became a folder. A run happens in a folder
        // that already exists, or it does not happen.
        if (!Directory.Exists(workspacePath))
            return $"No such folder: {workspacePath}. Pick another workspace, or create the folder yourself first.";

        // No model, no run - said here rather than discovered as a 404 from a provider. Before the model names
        // came out of AppSettings this could not happen: an unconfigured machine was configured for whatever
        // string was compiled in, and it ran.
        if (engineProblem is { Length: > 0 })
            return engineProblem;

        return null;
    }

    /// <summary>A one-line title from a request that may be a paragraph, until the planner names the run.</summary>
    public static string Title(string request)
    {
        var line = request.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 70 ? line : line[..70] + "…";
    }
}
