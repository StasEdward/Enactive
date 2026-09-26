namespace Enactive.Workspace;

using System.Diagnostics;
using System.Text;

/// <summary>Git used for observation, never as authority to execute repository helpers.</summary>
internal static class AutomaticGit
{
    internal static ProcessStartInfo StartInfo(string root, IEnumerable<string> args, string? index = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            psi.Environment.Remove(key);
        var empty = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = empty;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (index is not null) psi.Environment["GIT_INDEX_FILE"] = index;
        foreach (var option in new[] { "--no-pager", "-c", "core.fsmonitor=false", "-c",
                     "core.hooksPath=" + empty, "-c", "core.attributesFile=" + empty, "-c", "core.worktree=" + Path.GetFullPath(root) })
            psi.ArgumentList.Add(option);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    public static async Task<(int Exit, string Output)> RunAsync(string root, IReadOnlyList<string> args,
        CancellationToken ct, int timeoutMs = 30000, string? index = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(timeoutMs);
        try
        {
            // Reading config does not execute its values. Follow includes/worktree config too.
            // Fail closed on filters/drivers rather than trying to reconstruct their semantics.
            var config = await RunProcessAsync(StartInfo(root,
                ["config", "--includes", "--name-only", "--get-regexp", "^(filter\\.|diff\\.external$|diff\\..*\\.(command|textconv)$)"], index), lifetime.Token);
            if (config.Exit != 1) return (-1, ""); // 1 = no matching keys; any error also refuses.
            return await RunProcessAsync(StartInfo(root, args, index), lifetime.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (-1, ""); }
        catch (OperationCanceledException) { throw; }
        catch { return (-1, ""); }
    }

    private static async Task<(int Exit, string Output)> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) return (-1, "");
        process.StandardInput.Close();
        // Keep draining after the limit, but never pass a truncated snapshot as complete.
        var output = ReadBoundedAsync(process.StandardOutput, ct);
        var errors = ReadBoundedAsync(process.StandardError, ct);
        try
        {
            await process.WaitForExitAsync(ct);
            var text = await output;
            var stderr = await errors;
            return text is null || stderr is null ? (-1, "") : (process.ExitCode, text);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        var truncated = false;
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
        {
            if (text.Length + count > 2 * 1024 * 1024) truncated = true;
            if (!truncated) text.Append(buffer, 0, count);
        }
        return truncated ? null : text.ToString();
    }
}
