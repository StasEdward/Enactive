namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>
/// What makes a tool call THAT call — shared by <see cref="OpenFailures"/> (has the failed one
/// been made to work?) and <see cref="StepProgress"/> (is this anything new?), so "the same call
/// again" means one thing in this file.
///
/// <para>The name plus the arguments as an ACTION. <c>expectedExitCodes</c> is not part of it:
/// that argument says how to READ a result, not what to do, so a command and the same command
/// declaring that 1 is an answer are one call. Without that, the only route this engine offers
/// out of such a failure — run it again and declare — would produce a different identity, leave
/// the first failure open, and kill the step anyway. Advice that cannot be followed is worse
/// than none.</para>
///
/// <para>Every call is canonicalised, not only the ones carrying that argument: a raw string and
/// a rebuilt one would never meet. Property order stops mattering as a free consequence, which
/// it never should have.</para>
/// </summary>
internal static class CallIdentity
{
    internal static string Of(ToolCall call)
        => call.Name + "\0" + AsAction(call.ArgumentsJson);

    private static string AsAction(string? argumentsJson)
    {
        var text = (argumentsJson ?? string.Empty).Trim();
        if (text.Length == 0)
            return text;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return text;

            var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
                if (!string.Equals(property.Name, ToolArguments.ExpectedExitCodes, StringComparison.Ordinal))
                    kept[property.Name] = property.Value.GetRawText();

            return string.Join("\0", kept.Select(p => p.Key + "=" + p.Value));
        }
        catch (JsonException)
        {
            // Unparseable arguments are their own identity — two identical broken calls are the
            // same broken call, which is what the stall detector needs to see.
            return text;
        }
    }
}
