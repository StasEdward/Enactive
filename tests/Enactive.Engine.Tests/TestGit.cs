namespace Enactive.Engine.Tests;

using System.Diagnostics;

/// <summary>The git a test sets a workspace up with: a repository of its own, committed, line endings left as written.</summary>
internal static class TestGit
{
    public static void Run(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    /// <summary>A repository at <paramref name="root"/> with everything in it committed.</summary>
    public static void Repository(string root)
    {
        Run(root, "init", "-q");
        Run(root, "config", "user.email", "t@example.com");
        Run(root, "config", "user.name", "t");
        Run(root, "config", "core.autocrlf", "false");
        Commit(root);
    }

    public static void Commit(string root)
    {
        Run(root, "add", "-A");
        Run(root, "commit", "-q", "--allow-empty", "-m", "start");
    }
}
