namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Rules;
using Enactive.Server.Accounts;
using Enactive.Workspace;

/// <summary>
/// A workspace's lessons and rules (Plan Phase 5.2), kept from the browser: the window's list (LessonsViewModel) over the
/// stores every run of the service reads, changed here as the person.
///
/// <para><b>Who.</b> A Global rule is given to every run in every workspace the service has, as its settings shape them: an
/// Admin's. A workspace's rules are given to that workspace's runs, as its checks judge them: an Operator's. Anybody signed
/// in reads them. Checked at each call; every change audited, as a check's is.</para>
///
/// <para><b>Where.</b> The Global file is beside the service's settings.json, the one its engine reads (ServerEngine sets
/// RulesFolder to that folder) - never the %APPDATA% of whoever the process resolves to.</para>
///
/// <para><b>When.</b> Any time, unlike a check: a run reads its rules once, when it is composed, and keeps them in its
/// settings, so a change while one works is the next run's and undoes nothing.</para>
/// </summary>
internal sealed class ServerLessons(UserStore users, ServerFolders folders, KnownWorkspaces workspaces, ServerEngine engine)
{
    private const string Ended = "Your session has ended. Sign in again.";
    private const string Unknown = "The service does not know that folder as a workspace.";

    /// <summary>A workspace's list: changed as the person's role allows, read by anybody signed in.</summary>
    public (LessonsViewModel? List, string? Problem) Workspace(ClaimsPrincipal user, string root, string name)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return (null, Ended);
        if (workspaces.Find(root) is not { } known)
            return (null, Unknown);
        // Whether a rule goes against a Global one, asked of the service's engine as it is when the rule is kept (Plan 5.2).
        return (new LessonsViewModel(new AsThePerson(this, user, Book(known.RootPath), known.RootPath), $"Lessons - {name}",
            (text, global, ct) => engine.Current?.RuleConsistency() is { } ask ? ask(text, global, ct) : Task.FromResult<Enactive.Agents.RuleConflict?>(null)), null);
    }

    /// <summary>Why this person may not change <paramref name="scope"/>, or null.</summary>
    public string? WhyNot(ClaimsPrincipal user, RuleScope scope)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return Ended;
        return scope == RuleScope.Global
            ? account.Role >= Role.Admin ? null : "Your role does not include changing the rules every workspace is given."
            : account.Role >= Role.Operator ? null : "Your role does not include changing a workspace's rules.";
    }

    /// <summary>A candidate of the workspace, by its id, for its Checks screen to make into a check - or null.</summary>
    public async Task<RuleEntry?> CandidateAsync(ClaimsPrincipal user, string root, Guid id, CancellationToken ct)
    {
        if (ServerAuth.StillValid(users, user) is null || workspaces.Find(root) is not { } known)
            return null;
        return (await Book(known.RootPath).ListAsync(ct)).FirstOrDefault(r => r.Id == id && r.Status == RuleStatus.Candidate);
    }

    /// <summary>
    /// The candidate became a check, saved as the person on the Checks screen (ServerChecks, audited there): set aside as
    /// the window sets it aside (LessonsViewModel.MadeIntoCheckAsync), and audited here. Null when done, or why not.
    /// </summary>
    public async Task<string?> MadeIntoCheckAsync(ClaimsPrincipal user, string root, RuleEntry entry, string checkId, CancellationToken ct)
    {
        if (WhyNot(user, entry.Scope) is { } why)
            return why;
        if (workspaces.Find(root) is not { } known)
            return Unknown;
        var aside = entry with { Origin = (entry.Origin is { Length: > 0 } o ? o + "; " : "") + $"made into the check '{checkId}'" };
        if (await BookLessons.Said(() => Book(known.RootPath).DismissAsync(aside, ct), "Not set aside") is { } said)
            return said;
        Audited(user, "lesson made into a check", aside, entry.Scope, known.RootPath);
        return null;
    }

    private RuleBook Book(string root)
        => new(RuleStoreFactory.Create(WorkspaceInfo.For(root)), JsonRuleStore.Global(folders.Data));

    private void Audited(ClaimsPrincipal user, string action, RuleEntry entry, RuleScope scope, string root)
        => users.Audit(user.Identity?.Name ?? "?", action,
            $"{entry.Id:N} ({scope}){(scope == RuleScope.Workspace ? " in " + root : "")}: {RuleText.Flat(entry.Text, 200)}");

    /// <summary>The list's changes, as the person whose page it is: each through the role check here and the audit.</summary>
    private sealed class AsThePerson(ServerLessons lessons, ClaimsPrincipal user, RuleBook book, string root) : ILessonChanges
    {
        public Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct) => book.ListAsync(ct);

        public string? WhyNot(RuleScope scope) => lessons.WhyNot(user, scope);

        public async Task<string?> KeepAsync(RuleEntry entry, string? text, RuleScope scope, CancellationToken ct)
        {
            if ((lessons.WhyNot(user, scope) ?? lessons.WhyNot(user, entry.Scope)) is { } why)
                return why;
            RuleEntry? kept = null;
            if (await BookLessons.Said(async () => kept = await book.KeepAsync(entry, text, scope, ct), "Not kept") is { } said)
                return said;
            lessons.Audited(user, "rule kept", kept!, scope, root);
            return null;
        }

        public async Task<string?> DismissAsync(RuleEntry entry, CancellationToken ct)
        {
            if (lessons.WhyNot(user, entry.Scope) is { } why)
                return why;
            if (await BookLessons.Said(() => book.DismissAsync(entry, ct), "Not dismissed") is { } said)
                return said;
            lessons.Audited(user, "lesson dismissed", entry, entry.Scope, root);
            return null;
        }

        public async Task<string?> DeleteAsync(RuleEntry entry, CancellationToken ct)
        {
            if (lessons.WhyNot(user, entry.Scope) is { } why)
                return why;
            if (await BookLessons.Said(() => book.DeleteAsync(entry, ct), "Not deleted") is { } said)
                return said;
            lessons.Audited(user, "rule deleted", entry, entry.Scope, root);
            return null;
        }

        public async Task<string?> AddAsync(string text, RuleScope scope, CancellationToken ct)
        {
            if (lessons.WhyNot(user, scope) is { } why)
                return why;
            RuleEntry? added = null;
            if (await BookLessons.Said(async () => added = await book.AddAsync(text, scope, ct), "Not added") is { } said)
                return said;
            lessons.Audited(user, "rule added", added!, scope, root);
            return null;
        }
    }
}
