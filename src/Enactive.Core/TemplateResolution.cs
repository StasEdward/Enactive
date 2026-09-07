namespace Enactive.Core.Templates;

using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Context;
using Enactive.Core.Permissions;

/// <summary>
/// A template, a workspace and the user's answers, frozen into the one thing a run is started from.
///
/// <para>The execution engine is meant to see THIS and never the template it came from. A template
/// is editable; a run that already happened is not, and reading a finished run against a template
/// that has since changed answers the wrong question - "what would this do today" rather than "what
/// did it do".</para>
/// </summary>
public sealed record ResolvedTaskSpec(
    string TemplateId,
    int TemplateVersion,
    string TemplateName,
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceRoot,
    string Goal,
    IReadOnlyDictionary<string, string> Parameters,
    PermissionPolicy Permissions,
    IReadOnlyList<SuccessCriterionDefinition> SuccessCriteria,
    ExecutionLimits Limits,
    string? WorkerId,
    bool ReviewRequired)
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = false,
        // Enums by name: a snapshot read back in two years should not depend on nobody having
        // inserted a value into the middle of PermissionLevel.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// The canonical form stored with a run. Parameters are ordered so that the same inputs produce
    /// the same bytes - a snapshot that differs only by dictionary order cannot be compared between
    /// two runs, which is most of what a snapshot is for.
    /// </summary>
    public string Snapshot()
    {
        var ordered = this with
        {
            Parameters = Parameters
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
        };
        return JsonSerializer.Serialize(ordered, SnapshotJson);
    }

    /// <summary>
    /// Reads a snapshot back. Returns null for anything it cannot make sense of - a run recorded
    /// before specifications existed, a truncated column, a shape from a future version - because a
    /// history view must render a run it cannot fully understand rather than refuse to open it.
    /// </summary>
    public static ResolvedTaskSpec? Parse(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResolvedTaskSpec>(snapshot!, SnapshotJson);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The outcome of resolving. Either a specification or the reasons there is none - never both, and
/// never a half-filled specification with the problems attached, because something downstream would
/// eventually run it.
/// </summary>
public sealed record TemplateResolutionResult(ResolvedTaskSpec? Spec, IReadOnlyList<TemplateProblem> Problems)
{
    public bool Ok => Spec is not null;

    public static TemplateResolutionResult Failed(IReadOnlyList<TemplateProblem> problems)
        => new(null, problems);

    public static TemplateResolutionResult Succeeded(ResolvedTaskSpec spec)
        => new(spec, Array.Empty<TemplateProblem>());
}

/// <summary>Turns a template plus a workspace plus answers into a <see cref="ResolvedTaskSpec"/>.</summary>
public static class TemplateResolution
{
    public static TemplateResolutionResult Resolve(
        TaskTemplate template,
        WorkspaceInfo workspace,
        PermissionPolicy workspacePolicy,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var problems = new List<TemplateProblem>(TemplateValidator.Validate(template));

        var supplied = values ?? new Dictionary<string, string>();
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in template.ParameterList)
        {
            supplied.TryGetValue(parameter.Id, out var given);
            var value = string.IsNullOrEmpty(given) ? parameter.Default : given;

            if (string.IsNullOrEmpty(value))
            {
                if (parameter.Required)
                    problems.Add(new TemplateProblem($"Parameter '{parameter.Id}'",
                        $"'{parameter.Name}' is required and was not supplied."));
                continue;
            }

            if (!TemplateValidator.Accepts(parameter, value))
            {
                problems.Add(new TemplateProblem($"Parameter '{parameter.Id}'",
                    $"'{value}' is not a valid {parameter.Type} value."));
                continue;
            }

            resolved[parameter.Id] = value;
        }

        if (problems.Count > 0)
            return TemplateResolutionResult.Failed(problems);

        return TemplateResolutionResult.Succeeded(new ResolvedTaskSpec(
            TemplateId: template.Id,
            TemplateVersion: template.Version,
            TemplateName: template.Name,
            WorkspaceId: workspace.Id,
            WorkspaceName: workspace.Name,
            WorkspaceRoot: workspace.RootPath,
            Goal: Substitute(template.Goal, resolved),
            Parameters: resolved,
            Permissions: Narrow(workspacePolicy, template.Ceiling),
            SuccessCriteria: template.CriteriaList,
            Limits: template.LimitsOrNone,
            WorkerId: template.WorkerId,
            ReviewRequired: template.ReviewRequired));
    }

    /// <summary>
    /// The intersection of what the workspace permits and what the template will accept.
    ///
    /// <para>Only ever narrower than <paramref name="policy"/>. That is not enforced by a check
    /// here - a <see cref="PermissionCeiling"/> has no Allow list, so widening cannot be expressed
    /// in the first place. The Allow list below is passed through untouched for the same reason:
    /// there is nothing in the ceiling that could add to it.</para>
    /// </summary>
    public static PermissionPolicy Narrow(PermissionPolicy policy, PermissionCeiling ceiling)
    {
        var level = ceiling.MaxLevel is { } max && (int)max < (int)policy.Level
            ? max
            : policy.Level;

        return policy with
        {
            Level = level,
            AskBefore = Union(policy.AskBefore, ceiling.AskBefore),
            Deny = Union(policy.Deny, ceiling.Deny)
        };
    }

    /// <summary>
    /// Fills the goal in. Only placeholders naming a DECLARED parameter are replaced; everything
    /// else - <c>{0}</c>, <c>{}</c>, a brace in a code fence or a JSON body pasted into the task
    /// text - is left exactly as written. That is what makes substitution safe to run over prose
    /// nobody wrote with a template engine in mind.
    /// </summary>
    public static string Substitute(string goal, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(goal) || values.Count == 0)
            return goal;

        return TemplateValidator.Placeholder.Replace(goal, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    private static IReadOnlyList<string> Union(IReadOnlyList<string> left, IReadOnlyList<string>? right)
    {
        if (right is null || right.Count == 0)
            return left;

        var merged = new List<string>(left);
        foreach (var item in right)
            if (!merged.Contains(item, StringComparer.OrdinalIgnoreCase))
                merged.Add(item);
        return merged;
    }
}
