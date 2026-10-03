namespace Enactive.Remote.Contracts;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The identity that binds an approval to the exact action it approved.
///
/// <para><b>Why it is defined here.</b> The preview left the canonical form "to the Host
/// implementation" and had the gateway treat the result as opaque. That is right about the
/// gateway and wrong about the definition: an identity with no specification is one that changes
/// the next time somebody edits the code that builds the string, and when it changes every pending
/// approval silently stops matching. Nothing fails loudly - the owner just presses Allow and the
/// Host answers that the action does not match. So the form is written down, versioned, and pinned
/// by a test with a hard-coded value.</para>
///
/// <para><b>Why the fields are length-prefixed</b> rather than joined with a separator. Arguments
/// are arbitrary text - a file's whole contents, a command line, a commit message - so any
/// separator can appear inside a field. With a plain join, a value containing the separator can be
/// arranged to produce the same string as a different set of fields, which is exactly the hole a
/// hash is supposed to close. A byte count in front of each field removes the ambiguity
/// completely, whatever the field contains.</para>
///
/// <para>The gateway never computes this. It stores it and compares it for equality, and that
/// asymmetry is the point: the machine that will perform the action is the one that says what the
/// action is.</para>
/// </summary>
public static class ActionIdentity
{
    /// <summary>
    /// Prefixed into the hashed text, so changing the form is a deliberate act with a visible name
    /// rather than a quiet difference in output.
    /// </summary>
    public const string Version = "enactive-action-v1";

    /// <summary>
    /// The hash of one bound action, as lowercase hex.
    /// </summary>
    /// <param name="runId">The run the action belongs to.</param>
    /// <param name="toolCallId">The model's own id for this call - what makes two identical
    /// commands in one run two different actions.</param>
    /// <param name="tool">Registered tool name.</param>
    /// <param name="workingDirectory">Where the action will run, as a full path. Separators and a
    /// trailing separator are normalised so the same folder written two ways hashes the same; the
    /// path is NOT resolved against the current directory, because the caller already knows the
    /// real one and resolving here would make the answer depend on the process's state.</param>
    /// <param name="argumentsJson">The exact argument text the tool will be handed. Not
    /// re-serialised, not reformatted, not sorted: the point is to identify what will happen, and
    /// a tidied copy is a different string that produces a different action.</param>
    public static string Hash(
        string runId, string toolCallId, string tool, string workingDirectory, string argumentsJson)
    {
        var canonical = Canonical.Text(Version, runId, toolCallId, tool, NormaliseDirectory(workingDirectory), argumentsJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// One spelling for one folder: forward slashes, no trailing separator. A model writes
    /// <c>C:\work\p</c>, <c>C:/work/p</c> and <c>C:\work\p\</c> for the same place across three
    /// consecutive calls, and an approval that stops matching because of a slash is an approval
    /// nobody can give.
    /// </summary>
    private static string NormaliseDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var normalised = path.Replace('\\', '/').TrimEnd('/');

        // A drive root is the one place where the trailing separator is the path: "C:" is not a
        // folder, it is a drive-relative reference to wherever that drive's cursor happens to be.
        return normalised.Length == 2 && normalised[1] == ':' ? normalised + '/' : normalised;
    }
}
