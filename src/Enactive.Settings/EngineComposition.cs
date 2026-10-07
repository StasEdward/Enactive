namespace Enactive.Settings;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Mail;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Tools;
using Enactive.Tools.Mcp;
using Enactive.Workspace;

/// <summary>
/// The engine's pieces, built from what a person configured: everything a run needs that is not about
/// any one run - the providers, the team, the models, the tools, the settings as they stood when it was
/// built. A host holds the current one and hands it to <see cref="RunComposer"/> with each request; it
/// assembles none of it.
///
/// <para><b>Why one object, built in one place.</b> Until 2026-10-08 each host put a run's environment
/// together itself from about a dozen objects, and the copies had drifted: the window cloned the MCP
/// configurations and the console did not, the window passed its session approvals and the console
/// passed none, the window built a model resolver and router of its own and threw away the ones
/// <see cref="EngineComposition.Build"/> had made, and the two named the worker differently - a role
/// name in one, an id in the other. Built here, there is no second copy to drift.</para>
///
/// <para>A snapshot: the window builds a new one when the settings are saved, and a run keeps the one it
/// was started with - so a run whose settings changed underneath it is not a thing that can happen.</para>
/// </summary>
/// <param name="Session">
/// The approvals given "for this session" in this process. The host's, handed in, because it outlives
/// the engine: saving the settings builds a new engine, and must not forget what was allowed.
/// </param>
public sealed record ComposedEngine(
    IChatProviderFactory Providers,
    IWorkerProvider Workers,
    IModelRouter Router,
    ModelResolver Models,
    IToolRegistry BuiltInTools,
    IReadOnlyList<McpServerConfig> McpServers,
    AppSettings Settings,
    LogHub Log,
    SessionApprovals Session)
{
    public Planner Planner { get; init; } = new();

    public IPermissionEngine Permissions { get; init; } = new PermissionEngine();

    /// <summary>The approvals given "for this workspace"; null for the store every host shares. Set by tests only.</summary>
    public ApprovalStore? Approvals { get; init; }

    /// <summary>The settings' own section, as it stood when the engine was built: a record, so the editor's later changes are a new one.</summary>
    public EngineOptions EngineOptions { get; } = Settings.Engine;

    /// <summary>The default worker's preferred model, for a host that wants to show what it is about to run on.</summary>
    public string DefaultModel => Workers.Default.ModelPolicy.Preferred.Model;

    /// <summary>
    /// The worker a saved or typed name refers to - its id, or null when there is none by that name.
    ///
    /// <para>By id first, then by role name, either way ignoring case. The id is what is saved now; the
    /// role name is what the window saved against a workspace until 2026-10-08 (the field was called
    /// WorkerId all along), and what a person types. Looking a role name up as an id quietly found the
    /// default worker instead - which is what the log analysis did for every role whose name was not its
    /// id.</para>
    /// </summary>
    public string? WorkerIdFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var wanted = name.Trim();
        return (Workers.All.FirstOrDefault(w => string.Equals(w.Id, wanted, StringComparison.OrdinalIgnoreCase))
                ?? Workers.All.FirstOrDefault(w => string.Equals(w.Role.Trim(), wanted, StringComparison.OrdinalIgnoreCase)))?.Id;
    }
}

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
    /// The whole engine - providers, team, router, tools, settings - from what a person configured.
    /// </summary>
    /// <param name="session">
    /// The host's "for this session" approvals, which must survive the engine being rebuilt; a host
    /// that has no session (the console, one run per process) passes none and gets an empty one.
    /// </param>
    /// <param name="metrics">Told of every model call's cost - the window's performance panel.</param>
    /// <exception cref="InvalidOperationException">
    /// When <see cref="Missing"/> is not empty. A precondition, not a runtime path: there is nothing
    /// to compose an engine out of, and the alternative — inventing a model name so the call can
    /// return something — is the defect this whole class was written for.
    /// </exception>
    public static ComposedEngine Build(AppSettings settings, HttpClient http, LogHub log,
        SessionApprovals? session = null, Action<ModelCallMetrics>? metrics = null)
    {
        if (Missing(settings) is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(" ", problems));

        var providers = new ChatProviderFactory(Descriptors(settings), http, log)
        {
            PromptBodies = settings.LogPromptBodies,
            MetricsReported = metrics
        };

        var models = new ModelResolver();

        return new ComposedEngine(
            providers,
            Workers(settings),
            Router(settings, models),
            models,
            Tools(settings),
            // Cloned, as the window always did: a run reads them minutes after it was started, and a
            // configuration object edited after that must not change what the run connects to.
            settings.McpServers.Select(c => c.Clone()).ToArray(),
            settings,
            log,
            session ?? new SessionApprovals());
    }

    /// <summary>
    /// The built-in tools, configured as the person set them. The configured MCP servers are connected on
    /// top of these for each run (RunComposer), because they are processes that belong to the run.
    ///
    /// <para>Built with the engine, so rebuilt whenever the settings are: reported 2026-09-22, an SMTP account
    /// filled in and saved while <c>send_email</c> went on telling the agent it was unavailable, because the
    /// window had read the account once when it opened.</para>
    /// </summary>
    public static IToolRegistry Tools(AppSettings settings)
        => new ToolRegistry(BuiltInTools.Create(Mail(settings), Web(settings)));

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

    /// <summary>What the person allowed of the web, as the web tools want it.</summary>
    public static Enactive.Core.Web.WebAccess Web(AppSettings settings)
        => new(settings.Web.Enabled, settings.Web.SearchUrl.Trim())
            { UseWithoutAsking = settings.Web.UseWithoutAsking };

    /// <summary>Every configured endpoint, as the provider factory wants them.</summary>
    public static IReadOnlyList<ProviderDescriptor> Descriptors(AppSettings settings)
        // By name, every one: six int? follow one another here, and positionally two of them could change
        // places without the compiler or anybody reading the line noticing.
        => settings.Providers.Select(p => new ProviderDescriptor(
            Id: p.Id,
            DisplayName: string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName,
            Kind: p.Kind,
            BaseUrl: p.BaseUrl,
            ApiKey: string.IsNullOrEmpty(p.ApiKey) ? null : p.ApiKey,
            Models: p.Models,
            Headers: p.Headers.Count > 0 ? p.Headers : null,
            MaxTokens: p.MaxTokens,
            ContextWindowTokens: p.ContextWindowTokens,
            AnswerReserveTokens: p.AnswerReserveTokens,
            HandoverAtPercent: p.HandoverAtPercent,
            StreamIdleTimeoutSeconds: p.StreamIdleTimeoutSeconds,
            OpenAiReasoningProfile: p.OpenAiReasoningProfile,
            OllamaKeepAliveSeconds: p.OllamaKeepAliveSeconds,
            CompletionTimeoutSeconds: p.CompletionTimeoutSeconds,
            ReasoningTokenAllowance: p.ReasoningTokenAllowance,
            WorkingContextTokens: p.WorkingContextTokens,
            Effort: string.IsNullOrWhiteSpace(p.Effort) ? null : p.Effort.Trim().ToLowerInvariant(),
            Temperature: double.TryParse(p.Temperature?.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t) && t is >= 0 and <= 2 ? t : null,
            ServerTemperature: string.Equals(p.Temperature?.Trim(), "server", StringComparison.OrdinalIgnoreCase),
            SendReasoningBack: p.SendReasoningBack)).ToList();

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
