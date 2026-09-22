using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Execution;
using Enactive.Workspace;
using Enactive.Core.Providers;
using Enactive.Secrets;

namespace Enactive.Settings;

/// <summary>One configured provider endpoint as persisted in settings.json (Docs/MODELS.md).</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted API key ("dpapi:"-prefixed) — this is what lives on disk.</summary>
    public string ApiKeyProtected { get; set; } = string.Empty;

    /// <summary>Plaintext key held only in memory; never serialized (decrypted from <see cref="ApiKeyProtected"/> on load).</summary>
    [JsonIgnore] public string ApiKey { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new();
    public List<string> Models { get; set; } = new();

    /// <summary>Max output tokens for this provider (blank = the provider's built-in default). Anthropic max_tokens.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// How large a prompt this provider's models accept, in tokens — the CONTEXT window, not
    /// <see cref="MaxTokens"/>, which caps the answer.
    ///
    /// <para><b>Why it has to be declared.</b> Nothing in this application can find it out.
    /// <c>IChatProvider.ContextWindow</c> returns null by default and only the Ollama provider
    /// implements it — by echoing back the <c>num_ctx</c> it was handed, so it is a mirror of the
    /// request rather than a fact about the model. Every cloud provider answers "I do not know",
    /// and the callers that need a number then guess one.</para>
    ///
    /// <para>What the guess costs: <c>LogAnalyst</c> assumes 16,000 tokens when nobody says, so a
    /// log analysed by a model with a 200,000-token window was sent about a seventh of what would
    /// have fitted. Worse, the number it was given came from <c>NumCtx</c> — an OLLAMA setting —
    /// whichever provider was actually doing the work.</para>
    ///
    /// <para>Blank keeps the guess. A number that is wrong in the generous direction costs a
    /// failed request, which is why nothing infers one from a model's name.</para>
    /// </summary>
    public int? ContextWindowTokens { get; set; }

    public ProviderConfig Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = Kind,
        BaseUrl = BaseUrl,
        ApiKeyProtected = ApiKeyProtected,
        ApiKey = ApiKey,
        Headers = new Dictionary<string, string>(Headers),
        Models = new List<string>(Models),
        MaxTokens = MaxTokens,
        ContextWindowTokens = ContextWindowTokens
    };
}

/// <summary>
/// Where <c>send_email</c> sends from, and the only addresses it may send to.
///
/// <para><b>The recipients are settings, not an argument.</b> A model reads whatever it was
/// pointed at - a log from somebody's server, a file in a repository - and text it reads is not
/// an instruction, but a tool that mails wherever it is told turns that rule into a promise
/// instead of a boundary. With <c>--approve allow</c> nobody is watching either. So the address
/// is chosen by the person once, in this screen, and the tool can refuse anything else by
/// comparison rather than by judgement.</para>
///
/// <para>Adding a second address is a deliberate act and takes a moment. That is the intended
/// cost: the common case - "mail the report to me" - needs none.</para>
/// </summary>
public sealed class SmtpSettings
{
    public string Host { get; set; } = string.Empty;

    /// <summary>587 is submission with STARTTLS, which is what most providers want.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Upgrade the connection with STARTTLS. Off means implicit TLS (port 465) or none.</summary>
    public bool StartTls { get; set; } = true;

    public string User { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted password ("dpapi:"-prefixed) — this is what lives on disk.</summary>
    public string PasswordProtected { get; set; } = string.Empty;

    /// <summary>Plaintext, in memory only; never serialized. Same rule as a provider's API key.</summary>
    [JsonIgnore] public string Password { get; set; } = string.Empty;

    /// <summary>The From address. Blank falls back to <see cref="User"/>, which is usually right.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Every address <c>send_email</c> is allowed to write to. Empty means the tool is not
    /// offered at all: a mail tool with nowhere to send is a way to be surprised later.
    /// </summary>
    public List<string> Recipients { get; set; } = new();

    /// <summary>
    /// Send without the approval question — off unless a person turns it on.
    ///
    /// <para>A run nobody is watching is not offered a tool whose only outcome is a prompt, so
    /// with this off a scheduled task cannot mail its own report. On, the recipient list above
    /// carries the whole of the consent: see <c>MailAccount.SendWithoutAsking</c>.</para>
    /// </summary>
    public bool SendWithoutAsking { get; set; }

    /// <summary>Whether enough is filled in for the tool to exist at all.</summary>
    [JsonIgnore]
    public bool Configured
        => !string.IsNullOrWhiteSpace(Host) && Recipients.Count > 0;

    public SmtpSettings Clone() => new()
    {
        Host = Host,
        Port = Port,
        StartTls = StartTls,
        User = User,
        PasswordProtected = PasswordProtected,
        Password = Password,
        From = From,
        Recipients = new List<string>(Recipients),
        SendWithoutAsking = SendWithoutAsking
    };
}

/// <summary>One team member (role + its own model) as persisted in settings.json.</summary>
public sealed class WorkerConfig
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<string> Tools { get; set; } = new();
    public PermissionLevel Level { get; set; } = PermissionLevel.Execute;

    /// <summary>The worker's model as "providerId/model".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Optional fallback model as "providerId/model", or null.</summary>
    public string? Fallback { get; set; }

    public WorkerConfig Clone() => new()
    {
        Id = Id,
        Role = Role,
        Instructions = Instructions,
        Tools = new List<string>(Tools),
        Level = Level,
        Model = Model,
        Fallback = Fallback
    };
}

/// <summary>Which model runs each orchestration phase. Empty Plan = plan on the executing model; empty Review = no review.</summary>
public sealed class PhaseBindings
{
    public string Plan { get; set; } = string.Empty;
    public string Review { get; set; } = string.Empty;

    /// <summary>Execute model for Trivial steps (per-step auto-routing); blank = the worker's own model.</summary>
    public string ExecuteLight { get; set; } = string.Empty;

    /// <summary>Execute model for Complex steps (per-step auto-routing); blank = the worker's own model.</summary>
    public string ExecuteHeavy { get; set; } = string.Empty;

    public PhaseBindings Clone() => new()
    {
        Plan = Plan,
        Review = Review,
        ExecuteLight = ExecuteLight,
        ExecuteHeavy = ExecuteHeavy
    };
}

/// <summary>
/// Persisted app settings. The team-of-models schema (Providers / Workers / Bindings) is authoritative;
/// the legacy single-endpoint + Anthropic-reasoner fields are read once to migrate an old file, then inert.
/// </summary>
/// <summary>
/// How this computer reaches the gateway that a phone talks to, and whether it does at all.
///
/// <para>The token is a bearer credential: whoever holds it can register as this computer and be
/// handed its commands. So it lives beside the API keys and under the same protection - encrypted
/// with DPAPI, never written in the clear, and blanked out of the serialized form the way the
/// provider keys are.</para>
/// </summary>
public sealed class RemoteAccessSettings
{
    /// <summary>
    /// Whether to connect at all. Off by default, and off is not the same as unconfigured: somebody
    /// who has set this up and turned it off wants their settings kept, not forgotten.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The gateway's base address, e.g. https://remote.enactive.dev.</summary>
    public string GatewayUrl { get; set; } = string.Empty;

    // There is deliberately no computer id and no display name here. The first version of this pane
    // asked for both, and neither was ever read: the hub takes the Host from the AUTHENTICATED
    // identity and never from an argument, so the token alone says which computer this is, and the
    // name is the one given when it was registered. Asking for them made the pane look like it
    // needed three things to work when it needed two - and the id box was the one somebody then
    // filled in with the computer's name.

    /// <summary>The device token, DPAPI-encrypted. The only form that reaches disk.</summary>
    public string TokenProtected { get; set; } = string.Empty;

    /// <summary>The token in memory. Never serialized - see <see cref="TokenProtected"/>.</summary>
    [JsonIgnore] public string Token { get; set; } = string.Empty;

    public RemoteAccessSettings Clone() => new()
    {
        Enabled = Enabled,
        GatewayUrl = GatewayUrl,
        TokenProtected = TokenProtected,
        Token = Token
    };
}

/// <summary>
/// What may hand a command line to the operating system.
/// </summary>
public enum ShellCommandPolicy
{
    /// <summary>As the autonomy tier says. The behaviour that shipped, and the default.</summary>
    Follow,

    /// <summary>Always ask, at every tier — including Autonomous.</summary>
    Ask,

    /// <summary>Never. run_command and run_powershell are refused rather than asked about.</summary>
    Off
}

public sealed partial class AppSettings
{
    /// <summary>The newest settings.json schema this build writes. See <see cref="SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 5;

    /// <summary>
    /// settings.json schema version. Files written before 2026-09-06 have no such field and read as 1,
    /// where an EMPTY worker tool list meant "every tool" — the inverted permission the code review
    /// flagged. In version 2 an empty list means "no tools" and full access is spelled "*", so a v1
    /// file's empty lists are rewritten to ["*"] on load: that preserves what those workers were
    /// actually able to do instead of silently disarming them.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// How many runs a workspace keeps, or 0 to keep every one.
    ///
    /// <para>Off by default. Deleting a run deletes the record of what the agent did — the thing
    /// this product is built to keep — so it is not something to start doing to somebody's history
    /// because they upgraded.</para>
    ///
    /// <para>A COUNT, not an age. What makes the list unusable is the number of rows in it, and a
    /// week of heavy use puts more there than a month of light use; an age-based rule would leave
    /// the busy workspace untouched, which is the one with the problem.</para>
    /// </summary>
    public int KeepRuns { get; set; }

    // ── Team-of-models schema (Docs/MODELS.md) ────────────────────────────────
    public List<ProviderConfig> Providers { get; set; } = new();
    public List<WorkerConfig> Workers { get; set; } = new();
    public PhaseBindings Bindings { get; set; } = new();

    public string GlobalInstructions { get; set; } = string.Empty;

    // Ollama context window (options.num_ctx). Null = inherit whatever the model was loaded with.
    public int? NumCtx { get; set; }

    // Send think:false to the local model so a reasoning model (qwen3, ...) answers directly instead of
    // burning a whole turn in <think> with empty content. On by default; only OllamaNative honors it.
    public bool DisableThinking { get; set; } = true;

    // Execute a tool call the model only DESCRIBED in its reply (a ```json block) instead of invoking it.
    // Off by default and deliberately so: a parser cannot tell an intended call from a quoted example,
    // which means anything that can put text in front of the model can put an action in front of the
    // engine. Turn it on only for a weak local model that cannot emit structured tool calls at all.
    public bool AllowImplicitToolCalls { get; set; }

    // When a step runs no commands and only writes text, review the TEXT instead of the (empty)
    // execution evidence. Without this a configured reviewer passes anything such a step produces:
    // there is no exit code in a document, so the execution question has no answer to give. Costs one
    // reviewer call on the written content, which is why it is a switch — but it is on by default,
    // because the alternative is a gate that silently checks nothing for every writing task.
    public bool ReviewContent { get; set; } = true;

    // Ask a step that PASSED review what actually proved it. The reviewer checks whether a report is
    // TRUE against the evidence; a report can be true in every particular while its conclusion
    // follows from none of it — a fix reported over a test that was already failing and stayed
    // failing passes, honestly, and the run finishes green. This pass asks which calls SHOW the
    // objective was met, and the engine looks those calls up rather than believing the answer.
    // Costs one more Review-model call, and only for a step that actually ran something.
    public bool CheckSoundness { get; set; } = true;

    // How many times a rejected step may be redone before the run gives up. 1 means two tries in
    // total, which is what the engine did when this number was hard-coded. It was worth exposing
    // because it is the dial between "the reviewer's feedback gets used" and "a weak model burns the
    // budget arguing with a strong one": in a real run one step needed exactly two attempts and
    // passed, while another used both and was still wrong. Clamped to 0..5 by the orchestrator.
    public int ReviewRetries { get; set; } = 1;

    // How many times a run whose success CRITERIA failed may try to make them pass. A criterion is
    // the one thing in a run that is not somebody's opinion, and until 2026-09-08 a failed one just
    // ended the run: a build left broken was reported as broken and nothing tried to fix it, which
    // is not what "done" means to anyone. 1 gives the agent one attempt with the check's own output
    // in front of it; the criteria are then re-run and they alone decide. 0 restores the old
    // behaviour - check once, and stop. Clamped to 0..5 by the orchestrator.
    public int SuccessRetries { get; set; } = 1;

    // Ask the planner, before any of the work, for commands that would PROVE the request was
    // carried out - and judge the run by them when it was given no criteria of its own.
    //
    // Until 2026-09-21 the one guard that looks at the WORKSPACE instead of the transcript was
    // reachable only through a template: `successCriteria: spec?.SuccessCriteria` in both hosts,
    // and spec is a template. Every ad-hoc run was therefore judged on text a model wrote about
    // its own work. A template's criteria still win outright and the planner is not even asked,
    // so nothing about a template run changes.
    //
    // Safe to leave on: a proposed check can only make a verdict stricter (SuccessReport.Apply
    // never promotes), and it can only do so by RUNNING and failing - one that the shell would not
    // start, or that the policy forbids, reports Unknown and holds nothing back, because nobody
    // asked for it. Turn it off to judge ad-hoc runs the way they were judged before.
    public bool ProposeChecks { get; set; } = true;

    // Put a rejected step's files back to how they were before it ran. Without this the gate stops
    // only the REPORT: the run says Failed while the rejected document stays in the workspace, which
    // is the version someone is most likely to open next. A file changed since the step wrote it is
    // left alone and named in the log — reverting over somebody's edit would be the very thing this
    // is meant to prevent. Turn it off to inspect what a rejected step actually produced.
    public bool RevertRejectedSteps { get; set; } = true;

    // Ask workers to read a file back after writing it, to catch a weak local model fabricating content.
    // Costs an extra LLM round-trip per write — worth turning off when running strong models. On by default.
    public bool VerifyWrites { get; set; } = true;

    // How many independent plan steps may run at once. 1 = the original behaviour: one step at a time on
    // one shared conversation. Above 1 each concurrent step gets its own forked conversation, seeded with
    // a digest of what earlier steps concluded. Only pays off when steps route to different providers —
    // two steps on one Ollama still queue on the GPU.
    public int MaxParallelSteps { get; set; } = 1;

    // How many characters of tool evidence the reviewer is shown, shared between every call the step
    // made. Raise it for work that reads many files: the budget is divided, so thirteen reads under
    // the default leave about 320 characters of each - too little to check anything quoted from one.
    public int EvidenceBudget { get; set; } = ExecutionJournal.DefaultBudget;

    // How many days of log files to keep. 0 keeps everything, which is what shipped: a file per day,
    // appended forever, deleted by nobody.
    public int LogRetentionDays { get; set; } = FileLogSink.DefaultRetentionDays;

    // Whether the log records the full text of every prompt. On by default - it is what makes these
    // files worth reading when something goes wrong - and it is also most of their size, so turning
    // it off keeps the record of every call and drops the bodies.
    public bool LogPromptBodies { get; set; } = true;

    // What may run a command line. Separate from the autonomy tier because it is a different kind of
    // permission: every other tool asks for one named action against a checked path, a shell is
    // handed a string and the OS does the rest. Follow is the default and changes nothing.
    public ShellCommandPolicy ShellCommands { get; set; } = ShellCommandPolicy.Follow;

    // What the main window's close button does. True - the default - hides it to the tray, where a
    // run it started keeps going; false makes closing the window quit the program, asking first if
    // there is work in flight. Ignored when the desktop has no tray: there is nowhere to hide.
    public bool CloseToTray { get; set; } = true;

    // Whether this computer answers a phone, and how. Off until somebody fills it in.
    public RemoteAccessSettings RemoteAccess { get; set; } = new();

    /// <summary>Where send_email sends from, and the only addresses it may send to.</summary>
    public SmtpSettings Smtp { get; set; } = new();

    // ── Legacy fields (migration source only; superseded by the schema above) ──
    //
    // A URL has a right default: Ollama listens there or it does not, and being wrong costs a
    // connection error that names the address. A MODEL NAME has none. Which models exist is a fact
    // about this machine, and until 2026-09-11 these two lines decided it: a fresh install was
    // configured for "qwen2.5-coder" because that string was compiled in, and the first scheduled
    // runs on a machine with only gemma4 installed died on `model 'qwen2.5-coder' not found`.
    //
    // Empty, therefore. A file that NAMES a model still migrates it - that is a person's choice and
    // is carried over. A machine that has never been configured now says so, which the settings
    // window can answer by asking the provider what it has (ModelFetch) instead of guessing.
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = string.Empty;
    public bool MultiAgent { get; set; }
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string AnthropicApiKeyProtected { get; set; } = string.Empty;
    public string ReasonerModel { get; set; } = string.Empty;
    public string AnthropicWorkspaceId { get; set; } = string.Empty;

    // Main window placement (restored on start).
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SettingsFile()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enactive", "settings.json");

    /// <param name="path">
    /// A parameter so a test can point this at a fixture. The rules here are the ones worth
    /// testing - the migrations, the repairs, the decryption - and until this moved out of the
    /// desktop project no test could reach any of them; a test that read the developer's real
    /// %APPDATA% is one nobody runs twice. Same argument as <c>ScheduleStore</c>'s.
    /// </param>
    public static AppSettings Load(string? path = null)
    {
        var file = path ?? SettingsFile();

        // Parse failures used to fall into the catch below with everything else, so a settings.json
        // this build could not read became DEFAULTS, silently: the app opened on a provider list
        // nobody chose, with the person's own file still on disk and no longer read. The window
        // reports LoadProblems, so this is where the reason has to be put for it to be seen.
        string? unreadable = null;

        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions);
                if (loaded is not null)
                {
                    // Decrypt per-provider keys back into memory (plaintext field is not serialized).
                    foreach (var p in loaded.Providers)
                        if (!string.IsNullOrEmpty(p.ApiKeyProtected))
                            p.ApiKey = Secret.Unprotect(p.ApiKeyProtected);

                    // Legacy key: encrypted form wins; a legacy plaintext value is kept for migration.
                    if (!string.IsNullOrEmpty(loaded.AnthropicApiKeyProtected))
                        loaded.AnthropicApiKey = Secret.Unprotect(loaded.AnthropicApiKeyProtected);

                    if (!string.IsNullOrEmpty(loaded.RemoteAccess.TokenProtected))
                        loaded.RemoteAccess.Token = Secret.Unprotect(loaded.RemoteAccess.TokenProtected);

                    if (!string.IsNullOrEmpty(loaded.Smtp.PasswordProtected))
                        loaded.Smtp.Password = Secret.Unprotect(loaded.Smtp.PasswordProtected);

                    loaded.LoadMcpSecrets();
                    loaded.MigrateIfNeeded();

                    // A file written by an older version can hold duplicate ids, which used to reach
                    // ToDictionary and take the app down on startup — with no way in, and therefore
                    // no way to fix the settings that were the problem. Repair in memory so the app
                    // opens, keep a copy of the original so nothing is lost, and let the window say
                    // what happened.
                    if (loaded.RepairForStartup().Count > 0)
                        KeepACopy(file);

                    return loaded;
                }

                unreadable = "settings.json holds nothing this build could read.";
            }
        }
        catch (Exception ex)
        {
            unreadable =
                $"settings.json could not be read ({ex.GetType().Name}: {ex.Message}). Running on "
                + "defaults — your file has NOT been changed. Fix it, or open Settings and save to "
                + "replace it.";
        }

        var seeded = SeedFromEnvironment();
        seeded.MigrateIfNeeded();

        // Said, not swallowed. Defaults are the right thing to RUN on - refusing to start over a
        // damaged file leaves nowhere to fix it from - but they are the wrong thing to run on
        // silently, because from the outside a defaulted configuration and a chosen one look alike.
        if (unreadable is not null)
            seeded.LoadProblems = new[] { unreadable };

        return seeded;
    }

    /// <summary>
    /// Copies the settings file next to itself before the app runs on a repaired version of it, so
    /// the user still has exactly what they had. Best-effort: failing to keep a copy must not stop
    /// the app from starting, which is the whole point of the repair.
    /// </summary>
    private static void KeepACopy(string file)
    {
        try
        {
            var copy = Path.Combine(
                Path.GetDirectoryName(file)!,
                $"settings.before-repair-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            if (!File.Exists(copy))
                File.Copy(file, copy);
        }
        catch { /* the repaired settings are in memory either way */ }
    }

    /// <summary>
    /// Why the last <see cref="Save"/> failed, or null when it did not. A caller that only knows
    /// "false" can say nothing useful: "check disk access and Windows credential encryption" is a
    /// shrug, and the one failure that matters here - the secret could not be encrypted - has a
    /// precise thing to tell the user.
    /// </summary>
    [JsonIgnore]
    public string? LastSaveError { get; private set; }

    public bool Save()
    {
        LastSaveError = null;
        try
        {
            var file = SettingsFile();
            SaveMcpSecrets();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Encrypt every provider key; plaintext is [JsonIgnore] so it never reaches disk.
            foreach (var p in Providers)
                p.ApiKeyProtected = Secret.Protect(p.ApiKey);

            // The device token, same rule: the encrypted form is the only one that reaches disk.
            RemoteAccess.TokenProtected = Secret.Protect(RemoteAccess.Token);

            // And the mail password. [JsonIgnore] on the plaintext is what keeps it off disk.
            Smtp.PasswordProtected = Secret.Protect(Smtp.Password);

            // Legacy key: persist only the encrypted form, blanking the plaintext during serialization.
            AnthropicApiKeyProtected = Secret.Protect(AnthropicApiKey);
            var legacyPlain = AnthropicApiKey;
            AnthropicApiKey = string.Empty;
            try
            {
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temporary, file, overwrite: true);
            }
            finally
            {
                AnthropicApiKey = legacyPlain;
            }
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Brings an older settings.json up to <see cref="CurrentSchemaVersion"/>. Unlike the team-schema
    /// migration below, this runs on EVERY load, because it must also reach a file that already has
    /// providers. It is written to be idempotent.
    /// </summary>
    private void MigrateSchemaVersion()
    {
        if (SchemaVersion < 2)
        {
            // v1 semantics: an empty tool list meant "every tool". Spell that out as "*" so the worker
            // keeps the access it had, while the new, honest meaning of [] applies from here on.
            foreach (var w in Workers.Where(w => w.Tools.Count == 0))
                w.Tools.Add("*");
        }

        if (SchemaVersion < 3)
        {
            // edit_file shipped without ever being handed to anyone: it was registered by the host and
            // named by no worker, so the only way to change a file stayed write_file - a full rewrite.
            // A 12B model asked to add one menu entry to a 414-line page returned 168 lines of it, and
            // nothing about that was the model misbehaving: it was asked to retype a document to
            // express a two-line change.
            //
            // Adding it to the DEFAULT roles does not reach a settings.json that already lists its
            // workers, which is every installation that has been opened once. A worker that may write
            // a file may edit one - the capability is strictly narrower, so this cannot widen anyone's
            // access.
            foreach (var w in Workers.Where(w =>
                         w.Tools.Contains("write_file", StringComparer.OrdinalIgnoreCase)
                         && !w.Tools.Contains("edit_file", StringComparer.OrdinalIgnoreCase)))
                w.Tools.Insert(w.Tools.IndexOf("write_file") + 1, "edit_file");
        }

        // Deliberately "< CurrentSchemaVersion" rather than "< 4": every future tool added to the
        // implication table then reaches everybody on the next version bump, instead of reaching
        // new installations and waiting for somebody to notice. That waiting is the whole defect
        // being fixed here - it happened to edit_file, then to three more tools, then to copy_file
        // - and a gate pinned to one number would have set it up to happen again. WithImplied is
        // idempotent and only ever adds, so running it on every upgrade is safe.
        if (SchemaVersion < CurrentSchemaVersion)
        {
            // The same thing happened again, three times over. search_files, create_directory and
            // move_file were added to the default roles and reached nobody who already had a
            // settings.json - which is everybody. They were written, tested and documented, and
            // were dead the whole time.
            //
            // It showed up as a task started from a phone that could not rename a file: with the
            // shells now denied for a remote run, and move_file never granted, the model had no
            // tool that could move anything and correctly gave up. The tool it wanted did exist.
            //
            // Handled by WorkerTools.WithImplied rather than three more lines here, so the rule -
            // and the argument that each of these is a capability the worker already has under
            // another name - is written down once and can be tested.
            foreach (var w in Workers)
            {
                var granted = WorkerTools.WithImplied(w.Tools);

                if (granted.Count == w.Tools.Count)
                    continue;

                w.Tools.Clear();
                w.Tools.AddRange(granted);
            }
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>
    /// First load of a file that predates the team schema: synthesize Providers / Workers / Bindings from the
    /// legacy fields. Runs only while <see cref="Providers"/> is empty, so it never clobbers a migrated file.
    /// </summary>
    private void MigrateIfNeeded()
    {
        MigrateSchemaVersion();

        if (Providers.Count > 0)
            return;

        // The endpoint, with whatever model the legacy file named - and NO model when it named
        // none. An empty list is a provider a person can finish setting up: the provider editor
        // asks it what it has (ModelFetch) and offers the answer. A list holding one invented name
        // looks finished and is not, which is how a run reaches an endpoint asking for a model that
        // was never installed.
        Providers.Add(new ProviderConfig
        {
            Id = "ollama",
            DisplayName = "Ollama (local)",
            Kind = ProviderKind.OllamaNative,
            BaseUrl = BaseUrl,
            Models = NamedOrEmpty(Model)
        });

        var hasAnthropic = !string.IsNullOrWhiteSpace(AnthropicApiKey);
        if (hasAnthropic)
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(AnthropicWorkspaceId))
                headers["anthropic-workspace-id"] = AnthropicWorkspaceId;

            Providers.Add(new ProviderConfig
            {
                Id = "anthropic",
                DisplayName = "Anthropic",
                Kind = ProviderKind.Anthropic,
                BaseUrl = "https://api.anthropic.com",
                ApiKey = AnthropicApiKey,
                Headers = headers,
                Models = NamedOrEmpty(ReasonerModel)
            });
        }

        // The team, on whatever the legacy file named. With no model named, the workers are seeded
        // with an EMPTY model reference - which is what "nobody has chosen yet" looks like, and is
        // what EngineComposition refuses to run on rather than substituting something.
        if (Workers.Count == 0)
            foreach (var w in DefaultWorkers.Seed(new ModelRef("ollama", Model)))
                Workers.Add(new WorkerConfig
                {
                    Id = w.Id,
                    Role = w.Role,
                    Instructions = w.Instructions,
                    Tools = w.ToolAllowlist.ToList(),
                    Level = w.DefaultLevel,
                    Model = FormatRef(w.ModelPolicy.Preferred),
                    Fallback = w.ModelPolicy.Fallback is { } f ? FormatRef(f) : null
                });

        // MultiAgent on + a key present == plan & review on the Anthropic reasoner. Off == empty (single-agent).
        if (MultiAgent && hasAnthropic)
        {
            Bindings.Plan = $"anthropic/{ReasonerModel}";
            Bindings.Review = $"anthropic/{ReasonerModel}";
        }
    }

    /// <summary>A one-model list, or an empty one when there is no name to put in it.</summary>
    private static List<string> NamedOrEmpty(string model)
        => string.IsNullOrWhiteSpace(model) ? new List<string>() : new List<string> { model };

    /// <summary>
    /// Deep copy — so an editor can work on a throwaway copy and discard it on Cancel.
    ///
    /// <para><b>Every settable property has to be here, and a test now says so.</b> The settings
    /// window edits a clone and saves THAT object over the file, so a property this method forgets
    /// is not merely unreadable in the window - it is reset to its default on the next Save from
    /// any pane. Reported 2026-09-22: the SMTP section was filled in, saved, and came back empty,
    /// because <c>Smtp</c> was never copied; <c>KeepRuns</c> and <c>ProposeChecks</c> were being
    /// silently reset the same way, with nobody looking at them to notice.</para>
    /// </summary>
    public AppSettings Clone() => new()
    {
        // Must be copied: a clone that fell back to 1 would be saved as a v1 file, and the next load
        // would re-run the migration and hand "*" back to a worker the user had just emptied.
        SchemaVersion = SchemaVersion,
        GlobalInstructions = GlobalInstructions,
        NumCtx = NumCtx,
        DisableThinking = DisableThinking,
        AllowImplicitToolCalls = AllowImplicitToolCalls,
        ReviewContent = ReviewContent,
        CheckSoundness = CheckSoundness,
        ReviewRetries = ReviewRetries,
        SuccessRetries = SuccessRetries,
        RevertRejectedSteps = RevertRejectedSteps,
        VerifyWrites = VerifyWrites,
        MaxParallelSteps = MaxParallelSteps,
        EvidenceBudget = EvidenceBudget,
        LogRetentionDays = LogRetentionDays,
        LogPromptBodies = LogPromptBodies,
        ShellCommands = ShellCommands,
        CloseToTray = CloseToTray,
        BaseUrl = BaseUrl,
        Model = Model,
        MultiAgent = MultiAgent,
        AnthropicApiKey = AnthropicApiKey,
        AnthropicApiKeyProtected = AnthropicApiKeyProtected,
        ReasonerModel = ReasonerModel,
        AnthropicWorkspaceId = AnthropicWorkspaceId,
        WindowX = WindowX,
        WindowY = WindowY,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        KeepRuns = KeepRuns,
        ProposeChecks = ProposeChecks,
        Bindings = Bindings.Clone(),
        RemoteAccess = RemoteAccess.Clone(),
        Smtp = Smtp.Clone(),
        McpServers = McpServers.Select(x => x.Clone()).ToList(),
        Providers = Providers.Select(x => x.Clone()).ToList(),
        Workers = Workers.Select(x => x.Clone()).ToList()
    };

    /// <summary>
    /// What was wrong with the file this configuration was loaded from, and what was done about it.
    /// Empty for a healthy file. Not serialized: it describes one load, not the settings.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> LoadProblems { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Makes a configuration runnable without throwing away what the user typed.
    ///
    /// Validation was added on the SAVE path, which stops the UI writing a broken file but does
    /// nothing for the people whose settings.json was already broken by an older version: load did
    /// not validate, and duplicate ids went straight into the <c>ToDictionary</c> that builds the
    /// provider and worker registries, so the app threw on startup with nowhere to fix it from.
    ///
    /// Nothing is deleted for having a duplicate id — the later entry is renamed, so both remain
    /// visible and editable in Settings. An entry with no id at all is the exception: it cannot be
    /// referenced by anything and cannot be renamed into something meaningful.
    /// </summary>
    public IReadOnlyList<string> RepairForStartup()
    {
        var repairs = new List<string>();

        repairs.AddRange(DropUnnamed(Providers, p => p.Id, "provider"));
        repairs.AddRange(DropUnnamed(Workers, w => w.Id, "worker"));
        repairs.AddRange(RenameDuplicates(Providers, p => p.Id, (p, id) => p.Id = id, "provider"));
        repairs.AddRange(RenameDuplicates(Workers, w => w.Id, (w, id) => w.Id = id, "worker"));
        repairs.AddRange(UnencryptedSecrets());
        repairs.AddRange(UnreadableRemoteToken());

        LoadProblems = repairs;
        return repairs;
    }

    /// <summary>
    /// Keys sitting in settings.json in the clear. Left working - locking someone out of their own
    /// settings would be worse - but never left unsaid. This is the residue of a build whose
    /// encryption failed quietly and stored the plaintext in the field named "protected"; the next
    /// successful save encrypts them.
    /// </summary>
    private IEnumerable<string> UnencryptedSecrets()
    {
        foreach (var provider in Providers)
            if (!string.IsNullOrEmpty(provider.ApiKeyProtected) && !Secret.IsProtected(provider.ApiKeyProtected))
                yield return $"The API key for provider '{provider.Id}' is stored UNENCRYPTED in settings.json. "
                           + "It will be encrypted the next time settings are saved.";

        if (!string.IsNullOrEmpty(AnthropicApiKeyProtected) && !Secret.IsProtected(AnthropicApiKeyProtected))
            yield return "The stored Anthropic API key is UNENCRYPTED in settings.json. "
                       + "It will be encrypted the next time settings are saved.";

        if (!string.IsNullOrEmpty(RemoteAccess.TokenProtected) && !Secret.IsProtected(RemoteAccess.TokenProtected))
            yield return "The remote access device token is stored UNENCRYPTED in settings.json. "
                       + "It will be encrypted the next time settings are saved. Anyone who can read "
                       + "that file can register as this computer, so consider revoking it and "
                       + "pasting a new one.";
    }

    /// <summary>
    /// A device token that is encrypted but cannot be decrypted here: the settings file was copied
    /// from another computer, or the Windows account was rebuilt. DPAPI ciphertext bound to a user
    /// and machine that no longer exist will never open again, so it is not something to keep and
    /// retry - it is a token that has to be reissued.
    ///
    /// <para>Said out loud because the alternative is the worst version of this: remote access
    /// simply stops working, the pane still shows a token stored, and the next Save quietly
    /// replaces the unreadable bytes with nothing.</para>
    /// </summary>
    private IEnumerable<string> UnreadableRemoteToken()
    {
        if (Secret.IsProtected(RemoteAccess.TokenProtected) && string.IsNullOrEmpty(RemoteAccess.Token))
            yield return "The remote access device token cannot be decrypted by this Windows account "
                       + "- these settings were most likely copied from another computer. Issue a new "
                       + "token and paste it under Remote access; this computer cannot connect until "
                       + "you do.";
    }

    private static IEnumerable<string> DropUnnamed<T>(List<T> items, Func<T, string> id, string what)
    {
        var removed = items.RemoveAll(item => string.IsNullOrWhiteSpace(id(item)));
        if (removed > 0)
            yield return $"Removed {removed} {what}(s) with no id — nothing could refer to them.";
    }

    private static IEnumerable<string> RenameDuplicates<T>(
        List<T> items, Func<T, string> get, Action<T, string> set, string what)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var id = get(item).Trim();
            if (seen.Add(id))
                continue;

            var renamed = id;
            for (var n = 2; !seen.Add(renamed); n++)
                renamed = $"{id}-{n}";

            set(item, renamed);
            yield return $"Two {what}s shared the id \"{id}\"; the second is now \"{renamed}\".";
        }
    }

    /// <summary>
    /// Everything wrong with this configuration, in plain language, or empty when it is sound.
    ///
    /// Save used to write settings.json FIRST and build the runtime objects afterwards, where
    /// <c>ToDictionary</c> rejects duplicate ids. The exception took down the settings window — and
    /// then the next launch read the same file, hit the same exception in the MainWindow
    /// constructor, and the app would not start at all. Validating before writing means a
    /// configuration that cannot be built is never persisted. Ids are compared case-insensitively
    /// because the dictionaries are: "foo" and "Foo" are one id here.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var server in McpServers)
            if (server.Validate() is { } error) problems.Add($"MCP '{server.Id}': {error}");
        if (McpServers.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            problems.Add("MCP server IDs must be unique.");

        foreach (var provider in Providers)
            if (string.IsNullOrWhiteSpace(provider.Id))
                problems.Add("A provider has no id.");

        foreach (var duplicate in Providers
                     .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                     .GroupBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add($"Two providers share the id \"{duplicate.Key}\" (ids ignore case).");

        foreach (var worker in Workers)
            if (string.IsNullOrWhiteSpace(worker.Id))
                problems.Add("A worker has no id.");

        foreach (var duplicate in Workers
                     .Where(w => !string.IsNullOrWhiteSpace(w.Id))
                     .GroupBy(w => w.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add($"Two workers share the id \"{duplicate.Key}\" (ids ignore case).");

        // A model bound to a provider that is not configured cannot run; better to say so here than
        // to fail on the first request.
        foreach (var (label, reference) in new[]
                 {
                     ("Plan", Bindings.Plan),
                     ("Review", Bindings.Review),
                     ("Execute · light", Bindings.ExecuteLight),
                     ("Execute · heavy", Bindings.ExecuteHeavy)
                 })
        {
            if (ParseRef(reference) is { } model
                && !Providers.Any(p => string.Equals(p.Id, model.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"The {label} model uses provider \"{model.ProviderId}\", which is not configured.");
            }
        }

        foreach (var worker in Workers)
        {
            if (ParseRef(worker.Model) is { } model
                && !Providers.Any(p => string.Equals(p.Id, model.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"Worker \"{worker.Id}\" uses provider \"{model.ProviderId}\", which is not configured.");
            }
        }

        return problems;
    }

    /// <summary>All configured models as "providerId/model" strings, for model pickers.</summary>
    public IReadOnlyList<string> ModelCatalog()
    {
        var list = new List<string>();
        foreach (var p in Providers)
            foreach (var m in p.Models)
                if (!string.IsNullOrWhiteSpace(m))
                    list.Add($"{p.Id}/{m}");
        return list;
    }

    /// <summary>Finds a provider by id (case-insensitive), creating and appending it when absent.</summary>
    public ProviderConfig EnsureProvider(string id, string displayName, ProviderKind kind)
    {
        var existing = Providers.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;
        var created = new ProviderConfig { Id = id, DisplayName = displayName, Kind = kind };
        Providers.Add(created);
        return created;
    }

    /// <summary>"providerId/model" for a <see cref="ModelRef"/>.</summary>
    public static string FormatRef(ModelRef r) => $"{r.ProviderId}/{r.Model}";

    /// <summary>Parses "providerId/model" into a <see cref="ModelRef"/>; null/blank or malformed returns null.</summary>
    public static ModelRef? ParseRef(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var idx = s.IndexOf('/');
        if (idx <= 0 || idx >= s.Length - 1)
            return null;
        return new ModelRef(s[..idx], s[(idx + 1)..]);
    }

    private static AppSettings SeedFromEnvironment() => new()
    {
        BaseUrl = Environment.GetEnvironmentVariable("ENACTIVE_OLLAMA_URL") ?? "http://localhost:11434/v1",
        // No default. A machine with ENACTIVE_MODEL set has been told which model to use; one
        // without it has not, and the honest answer is none rather than a name from this file.
        Model = Environment.GetEnvironmentVariable("ENACTIVE_MODEL") ?? string.Empty,
        GlobalInstructions = string.Empty
    };
}
