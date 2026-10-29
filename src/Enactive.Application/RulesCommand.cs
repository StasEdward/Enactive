namespace Enactive.Application;

using Enactive.Core.Rules;
using Enactive.Workspace;

/// <summary>
/// The console's "rules" command (Plan Phase 5.2): the lessons and rules of a workspace, and a person's decision on one.
/// Here rather than in the console's top-level code, so what each word does is tested and not only typed.
/// </summary>
public static class RulesCommand
{
    /// <param name="args">What follows "rules" and the workspace: nothing to list, or a verb and its id or text.</param>
    /// <returns>The exit code: 0 done, 2 a request it could not act on.</returns>
    public static async Task<int> RunAsync(RuleBook book, string[] args, TextWriter output, CancellationToken ct = default)
    {
        try
        {
            switch (args)
            {
                case []:
                    await ListAsync(book, output, ct);
                    return 0;
                case ["add", var text, ..]:
                    var added = await book.AddAsync(text, ScopeIn(args) ?? RuleScope.Workspace, ct);
                    output.WriteLine($"Added {Short(added)} ({added.Scope}): {added.Text}");
                    return 0;
                case [var verb, var id, ..] when verb is "keep" or "dismiss" or "delete":
                    if (await book.FindAsync(id, ct) is not { } entry)
                    {
                        output.WriteLine($"No rule or candidate has an id starting '{id}'.");
                        return 2;
                    }
                    switch (verb)
                    {
                        case "keep":
                            var kept = await book.KeepAsync(entry, Option(args, "--text"), ScopeIn(args), ct);
                            output.WriteLine($"Kept {Short(kept)} ({kept.Scope}); new runs are given it: {kept.Text}");
                            break;
                        case "dismiss":
                            await book.DismissAsync(entry, ct);
                            output.WriteLine($"Dismissed {Short(entry)}; it will not be proposed again.");
                            break;
                        default:
                            await book.DeleteAsync(entry, ct);
                            output.WriteLine($"Deleted {Short(entry)}.");
                            break;
                    }
                    return 0;
                default:
                    output.WriteLine("Usage: rules \"workspace\" [keep ID [--text \"...\"] [--global] | dismiss ID | delete ID | add \"text\" [--global]]");
                    return 2;
            }
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task ListAsync(RuleBook book, TextWriter output, CancellationToken ct)
    {
        var all = await book.ListAsync(ct);
        if (all.Count == 0)
        {
            output.WriteLine("(no rules and no candidates)");
            return;
        }
        // Candidates first: they are what waits on the person.
        foreach (var status in new[] { RuleStatus.Candidate, RuleStatus.Confirmed, RuleStatus.Dismissed })
        {
            var those = all.Where(r => r.Status == status).ToArray();
            if (those.Length == 0)
                continue;
            output.WriteLine(status switch
            {
                RuleStatus.Candidate => "CANDIDATES - given to no run until kept",
                RuleStatus.Confirmed => "RULES - given to every new run",
                _ => "DISMISSED"
            });
            foreach (var rule in those)
            {
                output.WriteLine($"  {Short(rule)}  [{rule.Scope}] {rule.Text}");
                if (rule.Origin is { Length: > 0 } origin)
                    output.WriteLine($"            from {origin}");
            }
        }
        var applied = AppliedRules.Compose(all.Where(r => r.Scope == RuleScope.Global), all.Where(r => r.Scope == RuleScope.Workspace));
        if (applied.Left > 0)
            output.WriteLine($"{applied.Left} confirmed rule(s) do not fit the {AppliedRules.MaxChars}-character limit and are given to no run.");
    }

    private static string Short(RuleEntry rule) => rule.Id.ToString("N")[..8];

    private static RuleScope? ScopeIn(string[] args)
        => args.Contains("--global", StringComparer.OrdinalIgnoreCase) ? RuleScope.Global
            : args.Contains("--workspace", StringComparer.OrdinalIgnoreCase) ? RuleScope.Workspace
            : null;

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
