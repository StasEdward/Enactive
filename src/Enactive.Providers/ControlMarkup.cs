namespace Enactive.Providers;

using System.Text.RegularExpressions;

/// <summary>
/// Chat-template control markup in text a model is handed - a file it read, a command's output, a request that quotes
/// one - made inert before a local server renders it.
///
/// <para><b>Why.</b> A local server (llama.cpp, and Ollama and LM Studio on top of it) renders the conversation through
/// the model's chat template and tokenizes the result with special tokens parsed. A <c>&lt;/think&gt;</c> inside a tool
/// result is then not text: it is the token that closes the model's reasoning, and a <c>&lt;|im_end|&gt;</c> ends the
/// turn. Reported on Qwen3.6-35B-A3B in Unsloth Studio (issue #7066): a file containing <c>&lt;/think&gt;</c> "breaks
/// thinking blocks and the whole chat". The idea of neutralising it is theirs; nothing of their code.</para>
///
/// <para><b>How.</b> A space after the <c>&lt;</c>, so the text still reads the same and no longer matches the token.
/// Deterministic, so a conversation sent again is sent byte for byte the same and the server's prompt cache holds. Only
/// what the model is handed - a user's message and a tool's result - never its own turns, which go back as they came.</para>
/// </summary>
internal static partial class ControlMarkup
{
    [GeneratedRegex(@"<(?=/?think>|\|im_start\||\|im_end\||\|endoftext\||/?tool_call>|/?tool_response>|function=|/function>|parameter=|/parameter>)")]
    private static partial Regex Marker();

    public static string Neutral(string? text)
        => string.IsNullOrEmpty(text) || !text.Contains('<') ? text ?? "" : Marker().Replace(text, "< ");
}
