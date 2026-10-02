// The browser tests' computer, and their hands on the database. See RemoteHostHarness.csproj.
//
//   RemoteHostHarness [--data <dir>] [--workspace <name>]
//       Reads a connection code from the first line of stdin, pairs, connects and serves until stdin ends.
//       Says what happens on stdout, one line each: PAIRED <hostId>, EPOCH <n>, RUN <runId>,
//       DECIDED <runId> <option>, BADHASH <approvalId>, NOTICE <text>.
//
//   RemoteHostHarness db create|drop|dump <database>
//       Against the server ENACTIVE_E2E_MYSQL names (a connection string with no database).

using RemoteHostHarness;

return args is ["db", ..]
    ? await DatabaseCommand.RunAsync(args[1..])
    : await Computer.RunAsync(args);
