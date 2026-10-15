# The service's web UI as the desktop window - implementation plan

**Status (2026-10-15): implemented and accepted on `feature/application-layer`, PR #57 to `master`.**
All 22 delivery tasks are complete. Checkboxes record delivered outcomes; the original red/green commands and
suggested commit messages below are the implementation recipe, not a claim that each was executed verbatim.
No mandatory WEB UI feature remains in this plan.

Validation: Server 505/505, Engine 4566/4566, Desktop UI 22/22 and Playwright Chromium 36/36 passed locally.
The user confirmed that the installed Windows service works, closing the manual service acceptance item.
Release integration (clean-checkout build and CI) is tracked separately from feature completion.

Implemented differences from the initial recipe:
- A past run has no MODEL PERFORMANCE panel: call timings are session data, not saved in its record, as on the desktop.
- Source is offered only beside rendered text; Timeline and Environment already show their source text.
- Template and settings editors open in dialogs; workspace actions say Add workspace.
- Deleting filtered history takes the IDs shown when confirmed, so newer runs are never deleted unseen.

**Goal:** Replace the service's form pages with a live web UI that is the desktop window: its shell, its Runs
screen, and a page for each of its other windows.

**Architecture:** Blazor Server, every page but sign-in interactive (`InteractiveServer`, no prerender). Pages are thin;
what a screen decides lives in plain C# classes beside it, tested without a browser; actions go to the server's
existing services, which keep authorizing at the action. What the window computes is shared: projections and
Avalonia-free view models move from `Enactive.App.Ui` to `Enactive.Application` instead of being written again.

**Tech Stack:** .NET 10, Blazor Server (Razor components), xUnit, bUnit (new), Playwright (`tests/server-e2e`).

**Spec:** [plans/web-ui-desktop-parity-design.md](web-ui-desktop-parity-design.md)

## Global Constraints

- All UI copy in English; labels, tooltips and order as the desktop's (`MainWindow.axaml`, the windows named per task).
- Colours only from the `Brand.cs` tokens; which state gets which colour only through `Looks` (Task 2).
- Font `'Segoe UI', system-ui, sans-serif`; monospace `Consolas, Menlo, monospace`.
- Every action authorized in the server service it calls (`ServerAuth.StillValid` + role), never only by hiding it.
- Nothing the window computes is written a second time: move it to `Enactive.Application` unchanged, with its tests.
- A workspace is named in an address by its id (`WorkspaceInfo.For(root).Id`), never its path.
- Each change comes with a test that fails without it (AGENTS.md); a limit or rule carries a comment on what went wrong without it.
- Work on `feature/application-layer`; one commit per task; the service builds and its tests pass after each.
- Engine-text numbers via `Invariant` (EngineNumbersAreInvariantTests scans `src/`, Components included).

## Review Focus

1. **A role changed or an account disabled while its page is open** - the next action is refused by the service and
   the page says "Your session has ended" or the role refusal; Task 11 test.
2. **The connection drops mid-action** - actions are disabled while Reconnecting… and nothing is half-sent; Task 4.
3. **Two people answer one question at once** - the second is told it was answered already (existing
   `RunControl.Answer` rule) and the card disappears for both; Task 12 bUnit + Task 22 e2e.
4. **A workspace whose folder is gone** - the switcher shows it red, the Run button refuses with the composer's words,
   nothing crashes; Task 10.
5. **A long run with thousands of events open in several tabs** - each tab redraws at most ten times a second and a
   tab that falls behind re-reads (existing `RunFollower` / `RunSession` rules); Task 12 test with a slow redraw.

**One departure from the spec's order:** the spec puts every old-address redirect in its step 1. Here each redirect
lands with the task that replaces its page - before that the old page still answers at its own address, and a
redirect to a page that does not exist yet would be a dead link.

---

## Stage 1 - test tooling, theme, shell

### Task 1: bUnit in the server tests, and a signed-in component host

**Files:**
- Modify: `tests/Enactive.Server.Tests/Enactive.Server.Tests.csproj` (add the `bunit` package, latest stable for net10.0)
- Create: `tests/Enactive.Server.Tests/Components/ComponentHost.cs`
- Test: `tests/Enactive.Server.Tests/Components/ComponentHostTests.cs`

**Interfaces:**
- Produces: `abstract class ComponentHost : BunitContext, IDisposable` with a temp data folder, a real `UserStore`
  holding `ada` (Admin), `alice` (Operator), `vera` (Viewer), the server's services registered as `ServerApp.Build`
  registers them (over a scripted `IRunExecutor`), and `void SignIn(string name)` that cascades
  `ServerAuth.PrincipalOf(account)` as the `AuthenticationState`.

- [x] Step 1: test `A_component_sees_the_signed_in_account_and_role` - a two-line test component rendering
  `@context.User.Identity.Name` and the role claim inside `<AuthorizeView>` shows `alice` / `Operator` after `SignIn("alice")`.
- [x] Step 2: run `dotnet test tests/Enactive.Server.Tests --filter ComponentHostTests` - fails (no `ComponentHost`).
- [x] Step 3: add the package and `ComponentHost`; services come from one shared registration method
  `ServerApp.AddServices(IServiceCollection, ServerOptions, string dataFolder)` extracted from `Build` (no behaviour change).
- [x] Step 4: the test passes; the whole server suite passes.
- [x] Step 5: commit "Server tests: bUnit, and a signed-in component host".

### Task 2: the theme and `Looks`

**Files:**
- Create: `src/Enactive.Server/wwwroot/theme.css` (tokens as custom properties, named after `Brand.cs`: `--ink-950`…`--ink-50`,
  `--ember-200`…`--ember-800`, `--amber-400`, `--success`, `--warning`, `--danger`, `--info`, `--info-light`,
  `--card-fill` (white 10%), `--card-fill-hover` (14%), `--card-fill-active` (19%), `--input-fill` (black 22%),
  `--decision-fill` `#2A1B05`, `--go-500/600/700`); base element styles; the classes of `Styles/Controls.axaml`:
  `.panel-header`, `.hint`, `.label`, `.btn`, `.btn.go`, `.btn.go-soft`, `.btn.destructive`, `.btn.primary`, `.tab`,
  `.card`, `.pill`, `.edge`
- Delete: `src/Enactive.Server/wwwroot/app.css` (its rules folded into `theme.css` where still used)
- Create: `src/Enactive.Server/Components/Looks.cs`
- Test: `tests/Enactive.Server.Tests/Components/LooksTests.cs`

**Interfaces:**
- Produces: `internal static class Looks` with `string Card(FeedCardStatus)`, `string Run(RunStandingKind)`,
  `string Timeline(TimelineTone)`, `string Reach(ModelReach)`, `string Change(bool applied, bool rejected)`,
  `string Autonomy(int level)`, `string Ci(CiState)`, `string Health(ProviderHealth)` - each returns a CSS class
  (`tone-done`, `tone-running`, `tone-failed`, `tone-open`, `tone-idle`, `tone-skipped`, `tone-unverified`,
  `tone-local`, `tone-cloud`, `tone-faint`, `tier-0`…`tier-3`) mirroring `Palette.cs` value for value, and throws
  `ArgumentOutOfRangeException` on a value it does not name.

- [x] Step 1: `Every_value_of_every_state_has_a_look` - for each enum above, `Enum.GetValues` mapped without throwing;
  `Card(FeedCardStatus.Running) == "tone-running"`, `Run(RunStandingKind.Failed) == "tone-failed"`,
  `Autonomy(3) == "tier-3"`; `The_looks_are_the_window_s` - the colour behind each class in `theme.css` equals the
  `Brand` colour `Palette.cs` uses for the same value (parse `theme.css`, compare hex).
- [x] Step 2: run - fails.
- [x] Step 3: write `Looks.cs` and `theme.css`; link `theme.css` in `App.razor` instead of `app.css`.
- [x] Step 4: passes; `dotnet build` clean.
- [x] Step 5: commit "Server: the desktop's palette as a theme, and Looks".

### Task 3: the shell - top bar and icon rail

**Files:**
- Create: `src/Enactive.Server/Components/Shell/TopBar.razor`, `Shell/Rail.razor`, `Shell/RailItem.cs`
- Modify: `src/Enactive.Server/Components/Layout/MainLayout.razor` (top bar + rail + body)
- Test: `tests/Enactive.Server.Tests/Components/ShellTests.cs`

**Interfaces:**
- Consumes: `ComponentHost` (Task 1), `Looks` (Task 2).
- Produces: `record RailItem(string Href, string Label, string Icon, Role Needs)` and
  `static IReadOnlyList<RailItem> RailItem.For(Role role)` - order: Runs `/`, Timeline `/timeline`, Templates
  `/templates`, Schedules `/schedules`, Inbox `/inbox`, Proposals `/proposals`, Log `/log`; bottom: Environment
  `/environment`, Settings `/settings` (Admin), Accounts `/accounts` (Admin), Audit `/audit` (Admin). Tooltips as the
  desktop's ("Timeline · project memory", "Templates · a saved task", "Schedules · run it on a day and a time",
  "Global log", "Environment", "Settings"). Inbox shows the unread count (`ServerInbox.ListAsync`), Proposals a dot
  while `ServerStaging.List` has any.

- [x] Step 1: `A_viewer_is_shown_no_admin_section`, `An_admin_is_shown_every_section` (by `RailItem.For` and by the
  rendered `nav` button names), `The_inbox_icon_counts_what_is_unread`, `The_top_bar_names_who_is_signed_in_and_their_role`
  (`alice`, `Operator`, Sign out form posting to `/signout` with an antiforgery token).
- [x] Step 2: run - fails.
- [x] Step 3: implement; icon paths are the desktop rail's `Path Data` (MainWindow.axaml lines 33-152) as inline SVG,
  `aria-label` = tooltip; active item by `NavigationManager` path prefix.
- [x] Step 4: passes; the 15 e2e scenarios still pass (`cd tests/server-e2e && npx playwright test`) - only the
  header changed; fix selectors that named the old nav links.
- [x] Step 5: commit "Server: the desktop's shell - top bar and icon rail".

### Task 4: everything live, sign-in static, the connection said

**Files:**
- Modify: `src/Enactive.Server/Components/App.razor` (render mode on `<Routes>` and `<HeadOutlet>`:
  `InteractiveServer` without prerender when `HttpContext.AcceptsInteractiveRouting()`, else static)
- Modify: `Components/Pages/SignIn.razor` (`[ExcludeFromInteractiveRouting]`)
- Remove the per-page `@rendermode` lines (RunPage, LogsPage, SettingsPage)
- Create: `Components/Shell/ReconnectState.razor` (the element Blazor drives with `components-reconnect-show` /
  `-failed` / `-rejected`: a transparent overlay that blocks pointer events plus the top bar's state text)
- Test: `tests/server-e2e/server.spec.mjs` (new scenario), `ShellTests` (static marker)

**Interfaces:**
- Produces: every routed page interactive; `/signin` static.

- [x] Step 1: e2e `a lost connection disables actions until it is back` - open a run page, `page.context().setOffline(true)`,
  expect the top bar text "Reconnecting…" and the page's buttons not clickable (`trial: true` click times out); back
  online, "Connected" and they work. bUnit `Sign_in_is_not_an_interactive_page` asserts the attribute on `SignIn`.
- [x] Step 2: run - fails.
- [x] Step 3: implement.
- [x] Step 4: all e2e pass (the ones that relied on static form posts still work: those forms still post until their
  pages are replaced).
- [x] Step 5: commit "Server: every page live, sign-in static, a lost connection said and blocking".

### Task 5: workspace ids in addresses

**Files:**
- Create: `src/Enactive.Server/Runs/WorkspaceIds.cs`
- Test: `tests/Enactive.Server.Tests/WorkspaceIdsTests.cs`

**Interfaces:**
- Produces: `internal sealed class WorkspaceIds(ServerEngine engine)` with `Guid IdOf(string root)` and
  `WorkspaceEntry? Find(Guid id)` (a known workspace whose `WorkspaceInfo.For(entry.RootPath).Id == id`, else null);
  registered as a singleton.

- [x] Step 1: `A_known_workspace_is_found_by_its_id`, `An_unknown_id_finds_nothing`, `A_folder_not_known_is_not_found_by_its_id`.
- [x] Step 2: fails. - [x] Step 3: implement. - [x] Step 4: passes.
- [x] Step 5: commit "Server: a workspace named by its id in an address".

## Stage 2 - the Runs screen and what it needs from the server

### Task 6: the panel figures of a run, folded where every host can read them

**Files:**
- Create: `src/Enactive.Application/Projections/RunPanel.cs`
- Test: `tests/Enactive.Engine.Tests/RunPanelTests.cs`

**Interfaces:**
- Produces: `public sealed class RunPanel(Func<string?, ModelReach> reachOf)` with `RunRouting Routing`,
  `ModelWorkSplit Work`, `ModelPerformance Performance`, `int PromptTokens`, `int CompletionTokens`, `int Artifacts`,
  `bool Apply(WorkEvent ev)` (Routed → `Routing.Apply(ev.Summary, ev.PayloadJson)`; UsageReported →
  tokens + `Work.Add(ev.ProviderId(), reachOf(id), in, out)`; ArtifactProduced → `Artifacts++`; true when it changed
  something) and `void Add(ModelCallMetrics m)` → `Performance.Add(m)`. The window's own switch stays as it is: the
  rules are already in `RunRouting` / `ModelWorkSplit` / `ModelPerformance`; this is the dispatch, for the service.

- [x] Step 1: tests `A_routed_event_names_the_phase_s_model`, `Usage_counts_tokens_and_where_they_ran`,
  `Each_artifact_produced_counts_once`, `Other_events_change_nothing` (Apply returns false).
- [x] Step 2: fails. - [x] Step 3: implement. - [x] Step 4: passes; ApplicationLayerTests pass.
- [x] Step 5: commit "Application: a run's panel figures, folded for any host".

### Task 7: the session folds the panel, knows who watches, and gets its run's model figures

**Files:**
- Modify: `src/Enactive.Server/Runs/RunSession.cs`, `Runs/RunFollower.cs`, `Runs/ServerEngine.cs`,
  `Runs/RunControl.cs` (`EngineExecutor.ExecuteAsync`)
- Create: `src/Enactive.Server/Runs/ServerRunScope.cs`
- Test: `tests/Enactive.Server.Tests/RunSessionTests.cs`, `ServerRunScopeTests.cs`

**Interfaces:**
- Consumes: `RunPanel` (Task 6).
- Produces: `RunSession.Read` gives the panel: `T Read<T>(Func<RunFeed, RunActivity, RunPanel, long, T> read)`
  (the old three-argument overload removed, callers updated); `RunSession(string workspace, string owner, string title,
  Func<string?, ModelReach>? reachOf = null, int tailCapacity = 4096)` (null: every provider `ModelReach.Unknown`);
  `IRunExecutor.ReachOf` (`Func<string?, ModelReach>`; `EngineExecutor`: `ModelReaches.Of(engine.Settings, id)` when
  there are settings) handed by `RunControl` to each `ServerRun`'s session; `Subscription Subscribe(long afterSeq, string? watcher = null)`;
  `IReadOnlyList<string> Watchers` (names of live subscriptions, each once, ordered); `void AddMetrics(ModelCallMetrics m)`;
  `RunFollower(RunSession session, Func<long> read, Func<Task> redraw, string? watcher = null)`.
  `static class ServerRunScope { static RunSession? Current { get; set; } }` over an `AsyncLocal<RunSession?>`;
  `EngineExecutor.ExecuteAsync` sets it to `run.Session` first; `ServerEngine.Reload` passes
  `metrics: m => ServerRunScope.Current?.AddMetrics(m)` to `EngineComposition.Build`.

- [x] Step 1: tests `The_panel_is_folded_with_the_feed`, `Watchers_are_named_once_and_leave_when_they_unsubscribe`,
  `Model_figures_reach_the_run_whose_flow_reported_them` (two concurrent `Task.Run` flows each with its own
  `ServerRunScope.Current`; a callback in each lands in its own session only).
- [x] Step 2: fails. - [x] Step 3: implement. - [x] Step 4: server suite passes.
- [x] Step 5: commit "Server: a run's panel, its watchers and its model figures in its session".

### Task 8: a workspace's run settings from the browser

**Files:**
- Modify: `src/Enactive.Server/Runs/ServerHistory.cs`
- Test: `tests/Enactive.Server.Tests/RunControlTests.cs` (history section)

**Interfaces:**
- Produces: `string? SaveSettings(ClaimsPrincipal user, string root, int autonomy, string? workerId, bool stageChanges)`
  - null when saved (`WorkspaceRegistry.SaveSettings`, audit "workspace settings changed" with
  `"{root}: {AutonomyTiers name}, worker {id or default}, staging {on|off}"`), else the refusal: not an Operator,
  a folder the service does not know, an autonomy outside `0..AutonomyTiers.Highest`, a worker id the settings do not
  have. `IReadOnlyList<(string Id, string Name)> Workers()` - the settings' workers, for the role picker.

- [x] Step 1: tests `An_operator_saves_a_workspace_s_level_worker_and_staging_and_it_is_audited`,
  `A_viewer_cannot_change_a_workspace_s_settings`, `A_level_out_of_range_is_refused`,
  `Settings_of_a_folder_the_service_does_not_know_are_refused`, and that the next `EngineExecutor` request reads them
  (`saved.Defaults.Level == 3`, `saved.StageChanges`).
- [x] Step 2: fails. - [x] Step 3: implement. - [x] Step 4: passes.
- [x] Step 5: commit "Server: a workspace's level, worker and staging set from the browser".

### Task 9: run in background, and deleting the runs a filter shows

**Files:**
- Modify: `src/Enactive.Server/Runs/RunControl.cs`, `Runs/ServerHistory.cs`
- Test: `tests/Enactive.Server.Tests/RunControlTests.cs`

**Interfaces:**
- Produces: `IRunControl.StartBackground(ClaimsPrincipal user, string workspace, string prompt) : StartResult`;
  `ServerRun.Background` (bool, init); a background run is asked through `ParkingDecisionHandler` (as the window's
  `BackgroundRun`) and, in `LifeAsync`'s end, files one Inbox item from its outcome
  (`InboxLines.For(outcome …)`, title `RunState.Title(run)`) whatever its ending; audit "run started in the background".
  `ServerHistory.DeleteShownAsync(ClaimsPrincipal user, string root, RunFilter filter, DateTimeOffset now, CancellationToken ct)
  : Task<(string Said, int Deleted)>` - the rows `RunHousekeeping` selects for that filter, deleted with
  `RunHistoryService.DeleteAsync(root, runs, ct)`, each audited "run deleted"; Operator only.

- [x] Step 1: tests `A_background_run_asks_nobody_and_parks_its_question` (the scripted executor records the handler
  is a `ParkingDecisionHandler`), `A_background_run_ends_in_one_inbox_item`, `A_viewer_cannot_start_in_the_background`,
  `Delete_these_forgets_exactly_the_runs_the_filter_shows` (three runs, one older than a week; filter
  `OlderThanAWeek` deletes one), `A_viewer_cannot_delete_runs`.
- [x] Step 2: fails. - [x] Step 3: implement. - [x] Step 4: passes.
- [x] Step 5: commit "Server: runs in the background, and the runs a filter shows deleted at once".

### Task 10: the workspace and runs column

**Files:**
- Create: `src/Enactive.Server/Components/Runs/RunsScreen.razor` (route `/`, `/runs/{RunId:guid}`,
  `/history/{WorkspaceId:guid}/{PastRunId:guid}`; `?ws={id}` selects a workspace), `Runs/WorkspaceColumn.razor`,
  `Runs/WorkspaceColumnState.cs`, `Components/Ui/WorkspaceSwitcher.razor`, `Ui/Dialog.razor`, `Ui/PanelHeader.razor`,
  `Ui/Pill.razor`
- Modify: `ServerApp.cs` (the old `/history?ws={path}` and `/workspaces` redirect to `/?ws={id}`)
- Delete: `Components/Pages/Home.razor`, `Pages/HistoryPage.razor`, `Pages/Workspaces.razor`, `Pages/BrowsePage.razor`
  (the folder browser becomes `Ui/FolderBrowser.razor` inside a `Dialog`), the `/workspaces/act` and `/runs/resume`
  endpoints
- Test: `tests/Enactive.Server.Tests/Components/WorkspaceColumnTests.cs`

**Interfaces:**
- Consumes: `WorkspaceIds` (5), `ServerHistory` (incl. Task 8-9), `IRunControl`, `Looks`.
- Produces: `WorkspaceColumnState` - `Selected` (WorkspaceEntry?), `Filter` (RunFilter), `Rows` (the history rows
  shown), `DeleteLabel` = `"Delete these {n}"`, `Running` (`IRunControl.List` not ended), `Unfinished` (checkpoints).

- [x] Step 1: bUnit tests `The_switcher_lists_every_known_workspace_with_its_edge` (current `edge-current`, missing
  `edge-missing`), `A_viewer_is_offered_no_add_folder_delete_or_carry_on`, `The_delete_button_says_how_many_it_takes`,
  `Choosing_a_history_row_opens_it_without_stopping_the_live_run`, `A_missing_workspace_is_red_and_its_run_is_refused`.
- [x] Step 2: fails. - [x] Step 3: implement as `MainWindow.axaml` lines 153-457 (WORKSPACE card and popup, RUNS +
  Refresh, RUNNING with Open, filter + Delete, UNFINISHED with carry on / forget icons, history rows with status edge
  and the "×N" attempts pill).
- [x] Step 4: passes; e2e "a folder under the roots is added as a workspace by browsing…" rewritten for the dialog.
- [x] Step 5: commit "Server: the Runs screen's workspace and runs column".

### Task 11: the command bar

**Files:**
- Create: `Components/Runs/CommandBar.razor`, `Runs/CommandBarState.cs`, `wwwroot/js/commandbar.js`
- Create: `src/Enactive.Application/CommandVerbs.cs` (moved from the window: the verbs and `PrefixInput`)
- Modify: `src/Enactive.App.Ui/ViewModels/MainWindowViewModel.cs` (uses `CommandVerbs`)
- Test: `tests/Enactive.Engine.Tests/CommandVerbsTests.cs`, `tests/Enactive.Server.Tests/Components/CommandBarTests.cs`

**Interfaces:**
- Produces: `public static class CommandVerbs { IReadOnlyList<string> All = ["Fix","Build","Investigate","Explain","Refactor"]; string Prefix(string input, string verb) }`;
  `CommandBarState` - `Input`, `AutonomyLevel`, `WorkerId`, `StageChanges`, `AutonomyLabel` (`AutonomyTiers` label),
  `RunAsync()` → `IRunControl.Start` then navigate to `/runs/{id}`, `RunInBackgroundAsync()` → `StartBackground`, stays,
  clears the input; `StopAsync()` → `Cancel` of the open run; a change of level / worker / staging →
  `ServerHistory.SaveSettings`, the refusal shown under the bar. `commandbar.js` `attach(textarea, dotNetRef)`:
  Enter without Ctrl/Shift calls `Run`, Ctrl+Enter inserts a newline.

- [x] Step 1: `Prefix_puts_the_verb_at_the_front` (`""`→`"Fix "`, `"the build"`→`"Fix the build"`); bUnit
  `A_viewer_sees_the_settings_read_only_and_no_run_buttons`, `Moving_the_slider_saves_the_workspace_s_level`,
  `Run_in_background_starts_and_stays`, `A_refusal_is_said_under_the_bar`, the label colour class `tier-{n}`,
  `An_account_disabled_while_the_page_is_open_is_told_its_session_ended` (disable `alice` in the `UserStore`, press
  Run: "Your session has ended. Sign in again." under the bar, no run started).
- [x] Step 2: fails. - [x] Step 3: implement as `MainWindow.axaml` lines 459-545 (chips, hint "Ctrl+K to focus ·
  Enter to run · Ctrl+Enter = newline", input 88 px, Run 86 / Stop 64 / Run in background, divider, worker 118 +
  Stage changes, label on the slider).
- [x] Step 4: passes; the window's suites pass.
- [x] Step 5: commit "Server: the command bar, its verbs shared with the window".

### Task 12: the live run - header, tabs, feed, status column

**Files:**
- Create: `Components/Runs/LiveRun.razor`, `Runs/StatusColumn.razor`, `Runs/RunArtifacts.razor`,
  `Components/Ui/StepCard.razor`, `Ui/Tile.razor`, `Ui/Tabs.razor`, `Ui/DecisionCard.razor`, `Ui/DiffView.razor`,
  `Ui/RepoBar.razor`
- Move: `src/Enactive.App.Ui/ViewModels/RepoBarViewModel.cs` → `src/Enactive.Application/Projections/RepoBar.cs`
  (renamed class `RepoBar`, unchanged otherwise; the window uses it)
- Modify: `Runs/WebQuestions.cs` (`WebQuestion` gains `DateTimeOffset AskedAt`), `IRunControl` gains
  `TimeSpan? QuestionWait { get; }`
- Delete: `Components/Pages/RunPage.razor`
- Test: `tests/Enactive.Server.Tests/Components/LiveRunTests.cs`

**Interfaces:**
- Consumes: Tasks 6-11.
- Produces: header (title, request, "started by {owner} at {HH:mm}", phase pill, "also watching: {names}"); tabs
  Execution / Artifacts ({n}) / Log; Current action; repo bar (`RepoStatusReader.ReadAsync`); status column AI STATUS
  tiles (Artifacts, Tool calls, Tokens `TokenCounts.Compact`, Elapsed), ROUTING, WHERE IT RAN, MODEL PERFORMANCE
  folded to "{model} · {tok/s}", USER DECISION REQUIRED with `Request.FullText`, buttons per choice (Operator), the
  remember choices only for the owner, "waits {m} min more, then goes to the Inbox".

- [x] Step 1: bUnit `The_feed_shows_each_step_with_its_tone`, `A_viewer_sees_the_question_and_no_answer_buttons`,
  `Only_the_owner_is_offered_to_allow_for_longer`, `A_question_answered_elsewhere_leaves_the_card`,
  `The_tiles_follow_the_panel`, `Other_watchers_are_named`, `A_slow_redraw_does_not_hold_up_the_run` (redraw blocked,
  2,000 events emitted, the session ends; the follower re-reads once released).
- [x] Step 2: fails. - [x] Step 3: implement as `MainWindow.axaml` 546-920 and 1244-1300; artifacts: written files
  (Review → `/file` viewer dialog) and, once ended, the run's kept proposals (Diff / Apply / Reject through
  `ServerStaging.ActAsync`).
- [x] Step 4: passes; e2e run scenarios 1-6 rewritten for the new markup and passing.
- [x] Step 5: commit "Server: the live run as the window shows it".

### Task 13: a past run on the Runs screen

**Files:**
- Create: `Components/Runs/PastRun.razor`
- Delete: `Components/Pages/PastRunPage.razor`, `Pages/ConfirmPage.razor`, `Pages/FilePage.razor` (questions become a
  `Dialog` with the window's words from `ServerHistory.AskAsync`), endpoint `/history/act`; old `/history/run?ws=&id=`
  and `/file?ws=&path=` redirect to `/history/{id}/{run}`
- Test: `tests/Enactive.Server.Tests/Components/PastRunTests.cs`

**Interfaces:**
- Consumes: `ServerHistory.OpenAsync / AskAsync / DoAsync`, `WorkspaceIds`.
- Produces: header with Retry (go) / Run again (goSoft) and "Attempt {n} of {m}"; tabs Execution / Timeline
  (DECISIONS above TIMELINE) / Artifacts (Review, Delete after the question); THIS RUN tiles, ROUTING, WHERE IT RAN,
  RAN AS, MODEL PERFORMANCE.

- [x] Step 1: bUnit `Retry_starts_the_same_run_and_opens_it`, `Deleting_a_file_asks_first_in_the_window_s_words`,
  `A_viewer_is_offered_neither_retry_nor_delete`, `An_old_past_run_address_lands_on_the_new_one` (server test).
- [x] Step 2: fails. - [x] Step 3: implement as `MainWindow.axaml` 923-1243.
- [x] Step 4: passes; e2e "a past run is tried again…" rewritten and passing.
- [x] Step 5: commit "Server: a past run on the Runs screen".

## Stage 3 - the other windows

### Task 14: Inbox

**Files:** Create `Components/Pages/InboxScreen.razor` (`/inbox`), `Pages/InboxState.cs`; delete
`Pages/InboxPage.razor` and the `/inbox/answer` endpoint. Test `Components/InboxTests.cs`.

**Interfaces:** consumes `ServerInbox.ListAsync / AnswerAsync`, `InboxStoreFactory…MarkReadAsync / MarkAllReadAsync`
(a new `ServerInbox.MarkAllReadAsync(user, ct)`, Operator); layout as `InboxWindow.axaml` (list 360 with Unread only,
Mark all read, Refresh; WAITING FOR YOUR DECISION, RUN, ARTIFACTS, DECISIONS).

- [x] Step 1: `An_operator_answers_and_the_run_carries_on`, `A_viewer_reads_and_cannot_answer`, `Unread_only_hides_read_items`,
  `Mark_all_read_marks_every_workspace_s_items` (server test for the new method).
- [x] Steps 2-4: fails / implement / passes, e2e inbox scenario passes.
- [x] Step 5: commit "Server: the Inbox as the window's".

### Task 15: Proposals

**Files:** Create `Pages/ProposalsScreen.razor` (`/proposals`, `/proposals/{WorkspaceId:guid}/{Set}`); delete
`ProposalsPage.razor`, `ProposalPage.razor`, `/proposals/act`. Test `Components/ProposalsTests.cs`.

**Interfaces:** consumes `ServerStaging.List / Open / ActAsync`, `DiffView`, `WorkspaceIds`.

- [x] Step 1: `An_operator_applies_one_proposal_and_it_is_written`, `A_set_still_being_written_offers_nothing_to_do`,
  `A_viewer_reads_the_diff_and_is_offered_nothing`.
- [x] Steps 2-4, e2e "a viewer reads a staged run's proposal, and an operator applies it" passes.
- [x] Step 5: commit "Server: proposed changes in the shell".

### Task 16: Templates and the template editor

**Files:**
- Move: `src/Enactive.App.Ui/ViewModels/TemplateEditViewModel.cs` → `src/Enactive.Application/TemplateEditViewModel.cs`
  (unchanged; `ParameterRow`, `CriterionRow`, `ToolPermissionRow` with it; its tests with it); the window uses it from there
- Create: `Pages/TemplatesScreen.razor` (`/templates`), `Pages/TemplateEditor.razor` (`/templates/{Id}`)
- Delete: `LibraryPage.razor`, `TemplatePage.razor`, `/library/template/act`
- Test: `Components/TemplatesTests.cs`

**Interfaces:** the editor binds `TemplateEditViewModel(draft, idEditable, scopes, toolNames, saved)`; `saved` →
`ServerLibrary.SaveTemplate(user, root, JsonSerializer.Serialize(template), scope)`; Form / JSON tabs as
`TemplateEditWindow.axaml`; the library as `TemplatesWindow.axaml` (list 300, "What it will be asked to do", Run →
`IRunControl.Again` with the template resolved, as the window runs one).

- [x] Step 1: `The_form_saves_what_the_json_tab_shows`, `A_template_that_does_not_validate_is_not_saved_and_says_why`,
  `A_viewer_reads_templates_and_is_offered_no_edit_or_run`, the window's TemplateEdit tests pass from their new place.
- [x] Steps 2-4. - [x] Step 5: commit "Server: templates and their editor, the window's form shared".

### Task 17: Schedules

**Files:** Create `Pages/SchedulesScreen.razor` (`/schedules`), `Pages/SchedulesState.cs`; delete `SchedulePage.razor`,
`/library/schedule/act`. Test `Components/SchedulesTests.cs`.

**Interfaces:** consumes `ServerLibrary.SchedulesAsync / SaveSchedule(ScheduleForm) / SwitchSchedule / DeleteSchedule /
RepeatableAsync`; layout as `SchedulesWindow.axaml` (list 320 with New / Edit / Delete; Name, What it runs, How often,
Time, Day, Date, Time zone, What it may do; "What this will do" from the form resolved by `ScheduleDrafts`; "What came
of it" = the schedule's Inbox items by `ScheduleId`).

- [x] Step 1: `A_new_schedule_says_when_it_runs_next_before_it_is_saved`, `What_came_of_it_lists_the_schedule_s_outcomes`,
  `A_viewer_is_offered_no_new_edit_or_delete`.
- [x] Steps 2-4, e2e "an operator writes a template and schedules it…" passes. - [x] Step 5: commit.

### Task 18: Log, analysis, Timeline, Environment

**Files:** Create `Pages/LogScreen.razor` (`/log`), `Pages/TimelineScreen.razor` (`/timeline`),
`Pages/EnvironmentScreen.razor` (`/environment`), `Ui/TextViewer.razor` (Copy all, Source); delete `LogsPage.razor`.
Modify `ServerLogs` (new `SetCapture(ClaimsPrincipal user, bool trace) : string?`, Admin, sets the `LogHub.MinLevel`,
audited). Test `Components/LogTests.cs`, `ServerLogsTests` (capture).

**Interfaces:** Log as `LogWindow.axaml`: Level ≥, Sources, Pause, Auto-scroll, Clear (this view only), Export
(`LogText`), AI Analyze (Operator) → the result in `TextViewer`; Timeline: `ProjectMemory.Render(runs, entries, root)` for
the selected workspace; Environment: the probe the window uses (`ShowEnvironmentAsync`: `info.Summary()` +
`snapshot.Describe()`) for the selected workspace.

- [x] Step 1: `Capture_raw_wire_is_an_admin_s`, `Clear_empties_this_view_only`, `The_timeline_is_the_workspace_s_project_memory`.
- [x] Steps 2-4, e2e log scenario passes. - [x] Step 5: commit.

### Task 19: Settings

**Files:** Create `Pages/SettingsScreen.razor` (`/settings`, `/settings/{Section}`) and one component per section under
`Components/Settings/`; delete `SettingsPage.razor`. Test `Components/SettingsTests.cs`.

**Interfaces:** consumes `ServerSettings.Open / Save / TestMcpAsync`, `McpServerForm`, `EngineSwitches`; navigation as
`SettingsWindow.axaml`: General, Writable folders (the service's `WorkspaceRoots`, read-only), SMTP, Web, About; AI -
General, Providers, Team, Phases, MCP, Templates. No Account, no Remote access. Save / Cancel at the bottom.

- [x] Step 1: `Only_an_admin_opens_settings`, `Writable_folders_are_the_service_s_roots_read_only`,
  `There_is_no_account_or_remote_access_section`, `A_key_is_never_sent_back_to_the_page`.
- [x] Steps 2-4, e2e settings + MCP scenarios pass. - [x] Step 5: commit.

### Task 20: Accounts, Audit, Sign in

**Files:** Create `Pages/AccountsScreen.razor` (`/accounts`), `Pages/AuditScreen.razor` (`/audit`); restyle
`SignIn.razor`; delete `UsersPage.razor`, `AuditPage.razor`, `/admin/users/act`; old `/admin/users`, `/admin/audit`,
`/admin/settings` redirect. Test `Components/AccountsTests.cs`.

- [x] Step 1: `An_admin_adds_an_account_and_it_signs_in`, `The_last_admin_cannot_be_disabled_from_the_page`,
  `An_operator_is_refused_the_accounts_page`.
- [x] Steps 2-4, e2e account scenarios pass. - [x] Step 5: commit.

## Stage 4 - narrow screens and the whole browser set

### Task 21: narrow screens

**Files:** `theme.css` (below 900 px: the rail a bottom bar, the workspace column a drawer opened from the top bar,
the status column under the feed); `Shell/TopBar.razor` (drawer button). Test: e2e at 390×844.

- [x] Step 1: e2e `on a phone the runs screen stacks and a run can be started and answered`.
- [x] Steps 2-4. - [x] Step 5: commit.

### Task 22: the browser set complete, the old pages gone

**Files:** `tests/server-e2e/server.spec.mjs`; delete any page and endpoint the earlier tasks left behind; `ServerApp.cs`
(only `/signin`, `/signout`, `/health` and the redirects remain as endpoints); `Docs/README.md` server row.

- [x] Step 1: e2e adds `two people answer one question at once and the second is told it was answered`,
  `a background run reaches the inbox and is carried on from there`, `the workspace switcher adds a folder and
  switches to it`; the 15 earlier scenarios all present.
- [x] Step 2: `npx playwright test` - all pass; `dotnet test` for Engine, Server, App.Ui - all pass.
- [x] Step 3: commit "Server: the web UI as the desktop window - the browser set complete".
