namespace Enactive.Settings;

using Enactive.Agents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Mail;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Workspace;

/// <summary>
/// The engine's pieces, built from what a person configured. Everything a host needs before it can
/// submit anything, and nothing that belongs to any one run.
/// </summary>
/// <param name="DefaultModel">
/// The default worker's preferred model, for a host that wants to show or record what it is about
/// to run on. A convenience: it is already inside <paramref name="Workers"/>.
/// </param>
public sealed record ComposedEngine(
    ChatProviderFactory Providers,
    IWorkerProvider Workers,
    IModelRouter Router,
    ModelResolver Models,
    string DefaultModel);

/// <summary>
/// Settings in, an engine out — for every host, from one piece of code.
///
/// <para><b>Why this exists.</b> The desktop built this inline in its main window and the console
/// built its own: one hard-coded Ollama descriptor and a model name from an environment variable.
/// On 2026-09-10 the first scheduled runs went out on <c>OpenAiCompatible</c> against an
/// <c>OllamaNative</c> endpoint, on a model that was not installed, with no phase bindings — so
/// planning, which the person had bound to Anthropic, ran on a local model instead. None of that
/// was chosen by anybody; the two hosts had simply never been built by the same code.</para>
///
/// <para>It is also the first time any of this is testable. The composition lived in
/// <c>MainWindow.axaml.cs</c>, tangled up with the role picker and the model label, in a WinExe no
/// test project references — which is why a table of "what the window runs versus what the runner
/// runs" had never been checked by anything.</para>
/// </summary>
public static class EngineComposition
{
    /// <summary>
    /// The model to fall back to when a worker names none, or names one that cannot be parsed.
    ///
    /// <para>The first model of the first configured provider, because that is the one thing on
    /// this machine known to exist. NULL when nothing is configured — there is no honest answer
    /// then, and the one that used to be here was <c>ollama/qwen2.5-coder</c>: a guess about
    /// somebody else's machine, and the literal text of the error the first scheduled runs died
    /// on.</para>
    /// </summary>
    public static ModelRef? FallbackModel(AppSettings settings)
        => settings.Providers.FirstOrDefault(p => p.Models.Count > 0) is { } provider
            ? new ModelRef(provider.Id, provider.Models[0])
            : null;

    /// <summary>
    /// What stops these settings from producing an engine, in words for a person. Empty when they
    /// are runnable.
    ///
    /// <para>Asked BEFORE <see cref="Build"/>, by every host. A configuration that names no model is
    /// not a failure to report at the moment a provider returns 404 — by then a scheduled run has
    /// woken at three in the morning, opened a workspace and written a run record whose reason is an
    /// HTTP status. It is a thing to say up front, in the words of the thing that is missing.</para>
    /// </summary>
    public static IReadOnlyList<string> Missing(AppSettings settings)
    {
        var problems = new List<string>();

        if (settings.Providers.Count == 0)
            problems.Add("No provider is configured. Open Settings and add one.");
        else if (FallbackModel(settings) is null)
            problems.Add(
                "No model is chosen. "
                + $"{string.Join(", ", settings.Providers.Select(p => p.DisplayName ?? p.Id))} "
                + "has no model list — open Settings, edit the provider and fetch its models.");

        return problems;
    }

    /// <summary>
    /// Providers, team, router and default model, from what a person configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When <see cref="Missing"/> is not empty. A precondition, not a runtime path: there is nothing
    /// to compose an engine out of, and the alternative — inventing a model name so the call can
    /// return something — is the defect this whole class was written for.
    /// </exception>
    public static ComposedEngine Build(AppSettings settings, HttpClient http, LogHub log)
    {
        if (Missing(settings) is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(" ", problems));

        var providers = new ChatProviderFactory(Descriptors(settings), http, log)
        {
            PromptBodies = settings.LogPromptBodies
        };

        var workers = Workers(settings);
        var models = new ModelResolver();

        return new ComposedEngine(
            providers,
            workers,
            Router(settings, models),
            models,
            workers.Default.ModelPolicy.Preferred.Model);
    }

    /// <summary>
    /// The mail account <c>send_email</c> uses, as the tool wants it — or
    /// <see cref="MailAccount.None"/> when the person has not filled the section in, and then the
    /// tool is not registered at all.
    ///
    /// <para>Here, like <see cref="Descriptors"/>, because a tool takes what it needs as data and
    /// <c>Enactive.Tools</c> knows nothing about <c>AppSettings</c>. One place turns settings into
    /// engine parts, and this is it.</para>
    /// </summary>
    public static MailAccount Mail(AppSettings settings)
        => settings.Smtp.Configured
            ? new MailAccount(
                settings.Smtp.Host.Trim(),
                settings.Smtp.Port,
                settings.Smtp.StartTls,
                settings.Smtp.User.Trim(),
                settings.Smtp.Password,
                settings.Smtp.From.Trim(),
                settings.Smtp.Recipients
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)
                    .ToArray())
                { SendWithoutAsking = settings.Smtp.SendWithoutAsking }
            : MailAccount.None;

    /// <summary>Every configured endpoint, as the provider factory wants them.</summary>
    public static IReadOnlyList<ProviderDescriptor> Descriptors(AppSettings settings)
        => settings.Providers.Select(p => new ProviderDescriptor(
            p.Id,
            string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName,
            p.Kind,
            p.BaseUrl,
            string.IsNullOrEmpty(p.ApiKey) ? null : p.ApiKey,
            p.Models,
            p.Headers.Count > 0 ? p.Headers : null,
            p.MaxTokens,
            p.ContextWindowTokens,
            p.AnswerReserveTokens,
            p.HandoverAtPercent, p.StreamIdleTimeoutSeconds, p.OpenAiReasoningProfile, p.OllamaKeepAliveSeconds, p.CompletionTimeoutSeconds, p.ReasoningTokenAllowance,
            p.WorkingContextTokens,
            string.IsNullOrWhiteSpace(p.Effort) ? null : p.Effort.Trim().ToLowerInvariant())).ToList();

    /// <summary>
    /// The configured team, or the built-in one when a person has never edited it. Global
    /// instructions and the write-verification rule are folded into each worker here, which is the
    /// only place that happens.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When no model is configured. A worker is a role AND a model; there is no such thing as a
    /// worker with nothing to run on, and returning one with an invented model is what this
    /// refuses. Call <see cref="Missing"/> first.
    /// </exception>
    public static IWorkerProvider Workers(AppSettings settings)
    {
        var fallback = FallbackModel(settings)
            ?? throw new InvalidOperationException(
                "No model is configured, so there is nothing to build a team on.");

        var workers = settings.Workers.Select(w => new Worker(
            w.Id,
            w.Role,
            DefaultWorkers.Augment(w.Instructions, settings.GlobalInstructions, settings.VerifyWrites, w.Tools),
            w.Tools,
            w.Level,
            new ModelPolicy(
                AppSettings.ParseRef(w.Model) ?? fallback,
                AppSettings.ParseRef(w.Fallback)))).ToList();

        if (workers.Count == 0)
            workers = DefaultWorkers
                .Build(fallback, settings.GlobalInstructions, settings.VerifyWrites)
                .ToList();

        var defaultId = workers.Any(w => w.Id == DefaultWorkers.DefaultId)
            ? DefaultWorkers.DefaultId
            : workers[0].Id;

        return new StaticWorkerProvider(workers, defaultId);
    }

    /// <summary>
    /// Which model runs which phase. THIS is what a scheduled run was missing: with no router,
    /// planning and review happen on whatever model the worker uses, and a person who deliberately
    /// bound planning to a stronger model gets none of it without being told.
    /// </summary>
    public static IModelRouter Router(AppSettings settings, ModelResolver models)
    {
        var bindings = new Dictionary<ModelPurpose, ModelRef>();

        if (AppSettings.ParseRef(settings.Bindings.Plan) is { } plan)
            bindings[ModelPurpose.Plan] = plan;
        if (AppSettings.ParseRef(settings.Bindings.Review) is { } review)
            bindings[ModelPurpose.Review] = review;

        return new ModelRouter(
            models,
            bindings,
            AppSettings.ParseRef(settings.Bindings.ExecuteLight),
            AppSettings.ParseRef(settings.Bindings.ExecuteHeavy));
    }

    /// <summary>
    /// The autonomy tier as a policy, with the SHELL setting applied on top.
    ///
    /// <para>Both halves, in one place, because they were in two. <c>AutonomyTiers.PolicyFor</c> is
    /// what the console called; the window called its own copy and then added the shell rule. So
    /// "never run commands" held for a run started in the window and did not hold for the same
    /// workspace run from a command line or a schedule — a setting whose whole purpose is to stop
    /// something, not stopping it.</para>
    /// </summary>
    public static PermissionPolicy PolicyFor(AppSettings settings, int tier)
        => WithShells(AutonomyTiers.PolicyFor(tier), settings.ShellCommands);

    /// <summary>
    /// The shells, decided separately from the autonomy tier.
    ///
    /// <para>Separately because they are a different KIND of permission. Every other tool is asked
    /// for one named action against a path this engine resolves and checks; a shell is handed a
    /// command line and the operating system does the rest, so the workspace is where it starts and
    /// nothing more. At the Autonomous tier that is what "act without asking" already meant, which
    /// is why <see cref="ShellCommandPolicy.Follow"/> is the default and changes nothing — the
    /// control exists so that choosing a high tier for the file tools does not silently choose it
    /// for command execution too.</para>
    /// </summary>
    public static PermissionPolicy WithShells(PermissionPolicy policy, ShellCommandPolicy shells)
        => shells switch
        {
            // Which tools count as a shell is ShellTools' answer, not this file's. It was a literal
            // here, a second literal in RemotePolicy, and the set behind ShellTools.IsShell — three
            // copies of one list, of which only one is consulted when a tool call is checked.
            ShellCommandPolicy.Off => ShellTools.Denied(policy),
            ShellCommandPolicy.Ask => ShellTools.Asked(policy),
            _ => policy
        };
}
