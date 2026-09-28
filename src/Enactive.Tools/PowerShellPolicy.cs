namespace Enactive.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>Uses the installed shell's parser, never evaluation, to inspect a bounded script.
/// This is an invocation allowlist, not a sandbox for the effects of permitted programs.</summary>
internal static class PowerShellPolicy
{
    internal static async Task<string?> ValidateAsync(string arguments, TaskActionPolicy policy, IReadOnlyList<TaskRestriction> restrictions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? script;
        try
        {
            using var json = JsonDocument.Parse(arguments);
            script = json.RootElement.GetProperty(ToolArguments.Script).GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return "PowerShell requires a script string."; }
        if (string.IsNullOrWhiteSpace(script)) return "PowerShell requires a nonempty script.";
        // The validator is also passed as EncodedCommand; keep its process command line bounded.
        if (script.Length > 10_000) return "PowerShell policy inspection is limited to 10000 characters; split the script into smaller actions.";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var inspection = ParserScript.Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
        var start = new ProcessStartInfo {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = ProcessExec.Utf8NoBom, StandardErrorEncoding = ProcessExec.Utf8NoBom
        };
        // The script is data embedded in the inspector, never inserted as executable code.
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(inspection)));
        if (start.ArgumentList[^1].Length > RunPowerShellTool.MaxEncodedChars)
            return "PowerShell policy inspection exceeds the command-line limit; split the script into smaller actions.";
        using var process = new Process { StartInfo = start };
        var output = new ProcessExec.CapturedStream();
        var errors = new ProcessExec.CapturedStream();
        process.OutputDataReceived += (_, e) => output.Add(e.Data);
        process.ErrorDataReceived += (_, e) => errors.Add(e.Data);
        try
        {
            var result = await ProcessExec.RunContainedAsync(process, 15, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.Completed || result.OutputCutShort || process.ExitCode != 0)
                return "PowerShell policy parser did not complete; nothing was executed.";
            using var parsed = JsonDocument.Parse(output.ToString());
            if (parsed.RootElement.GetProperty("error").ValueKind == JsonValueKind.String)
                return parsed.RootElement.GetProperty("error").GetString();
            var commands = parsed.RootElement.GetProperty("commands");
            if (commands.GetArrayLength() == 0) return "PowerShell script contains no commands.";
            foreach (var command in commands.EnumerateArray())
            {
                var tokens = command.GetProperty("tokens").EnumerateArray().Select(t => t.GetString()!).ToArray();
                if (tokens.Length > 0 && tokens[0].Equals("Remove-Item", StringComparison.OrdinalIgnoreCase)
                    && restrictions.Any(r => r.Effect == ForbiddenTaskEffect.FileDeletion))
                    return "Remove-Item conflicts with the task's no-deletion restriction.";
                if (!policy.CommandPrefixes.Any(prefix => PrefixMatches(prefix, tokens)))
                    return $"PowerShell command '{string.Join(" ", tokens.Take(2))}' is outside command_prefixes (line {command.GetProperty("line")}).";
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        { return "PowerShell policy parser unavailable or returned invalid evidence; nothing was executed."; }
    }

    private static bool PrefixMatches(string prefix, string[] tokens)
    {
        var parts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.Length <= tokens.Length
            && parts.Select((p, i) => string.Equals(p, tokens[i], StringComparison.OrdinalIgnoreCase)).All(x => x);
    }

    private const string ParserScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        try {
            $text = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__'))
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ('Invalid PowerShell syntax: ' + $errors[0].Message) }
            if ($null -ne $ast.ScriptRequirements) { throw '#requires directives are not supported by the task policy.' }
            if ($tokens | Where-Object { $_.Text -eq '--%' }) { throw 'Stop-parsing syntax is not supported by the task policy.' }
            $types = @('ScriptBlockAst','NamedBlockAst','PipelineAst','CommandAst','CommandParameterAst','StringConstantExpressionAst','ConstantExpressionAst')
            foreach ($node in $ast.FindAll({param($n) $true}, $true)) {
                if ($node.GetType().Name -notin $types) {
                    throw ('Unsupported PowerShell policy syntax: ' + $node.GetType().Name + ' at line ' + $node.Extent.StartLineNumber + '. Use static commands and literal arguments.')
                }
                if ($node -is [System.Management.Automation.Language.PipelineAst] -and $node.Background) { throw 'Background pipelines are not supported by the task policy.' }
            }
            $commands = @()
            foreach ($command in $ast.FindAll({param($n) $n -is [System.Management.Automation.Language.CommandAst]}, $true)) {
                if ($command.InvocationOperator -ne 'Unknown') { throw 'Call operators and dot sourcing are not supported by the task policy.' }
                $name = $command.GetCommandName()
                if (!$name -or $name -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Use a literal command name without a path or module qualifier.' }
                if (Get-Alias -Name $name -ErrorAction SilentlyContinue) { throw ('Use the full command name instead of alias ' + $name) }
                if ($name -in @('Invoke-Expression','Invoke-Command')) { throw 'Dynamic code evaluation is not supported by the task policy.' }
                $words = @()
                foreach ($element in $command.CommandElements) {
                    if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                        $words += ('-' + $element.ParameterName)
                        if ($null -ne $element.Argument) { $words += [string]$element.Argument.Value }
                    } else { $words += [string]$element.Value }
                }
                $commands += @{ tokens = @($words); line = $command.Extent.StartLineNumber }
            }
            @{error=$null;commands=@($commands)} | ConvertTo-Json -Depth 8 -Compress
        } catch {
            @{error=$_.Exception.Message;commands=@()} | ConvertTo-Json -Depth 8 -Compress
        }
        """;
}
