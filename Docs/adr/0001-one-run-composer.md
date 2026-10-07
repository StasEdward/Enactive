# Every run is composed in one place; hosts keep only who answers, where events go, and cancellation

A run used to be assembled by hand in each host - the desktop window, the unattended path (background
runs and runs from a phone) and the console - and the unattended composer deliberately left the
window's interactive run out, on the grounds that it streams into the window and owns its own
cancellation. The copies drifted where nobody looked: the console connected no MCP servers, recorded
runs without their settings and never honoured a remembered approval; a template run started in the
background dropped its template; background and console resumes ran under today's slider instead of
the permissions the run was started with. So every host now composes through `RunComposer`
(`src/Enactive.Settings/RunComposer.cs`) from a run request, and keeps only what really differs
between hosts: the decision handler, the event sink, and cancellation.

## Consequences

- Everything else about a run - the policy (checkpoint, then template, then host), what is recorded,
  whether changes are staged, the tools connected, remembered approvals - is decided by the composer
  for every host alike. A rule added there holds everywhere; a rule added in a host is a new copy.
- Staging is refused for any run without someone at the screen, in the composer's own words, which
  hosts show before starting anything.
- Remembered approvals answer for every host except a phone (which must be asked afresh each time) and
  an explicit answer given for one invocation (the console's `--approve`).
- A host does not assemble the engine either: it holds the one `EngineComposition.Build` made from the
  settings (`ComposedEngine`) and hands it over with each request. What governs the run - the workspace
  defaults, a level and a worker - travels in the request, and the composer turns them into the policy
  and the record. Assembled by hand, the window's and the console's copies had drifted again (MCP
  configurations, session approvals, a worker named by role in one and by id in the other).
