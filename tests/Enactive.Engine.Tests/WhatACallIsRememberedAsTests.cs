namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// A tool call is SENT as the model wrote it and REMEMBERED shorter.
///
/// <para><b>Measured, 2026-09-22.</b> One run reported 21,684,301 prompt tokens over 197 turns —
/// and 99% of them were cache hits, so the bill was about thirty cents. The cost that matters is
/// the other one: the last prompt of that run was 601,560 characters, every turn re-sent all of
/// it, and 177,608 of those characters were the ARGUMENTS of earlier tool calls. 93,588 came from
/// 49 <c>edit_file</c> calls whose <c>new_string</c> had long since been written to a file.</para>
///
/// <para><b>Why not simply trim later.</b> Because a provider's prompt cache keys on the prefix.
/// Rewriting an old message invalidates everything after it: the run that was given a 64k window
/// went from 21.7M tokens to 7.8M and from 98% cached to 59%, handing most of the saving back.
/// Shortening at the moment of recording costs the cache nothing, because the message is never
/// changed again.</para>
/// </summary>
public sealed class WhatACallIsRememberedAsTests
{
    private static ToolCall Call(string name, string argumentsJson) => new("id-1", name, argumentsJson);

    /// <summary>
    /// The shape the measurement pointed at: a large value goes, its head and its size stay, and
    /// the small values around it are untouched — a path the model needs is not "big".
    /// </summary>
    [Fact]
    public void A_file_sized_argument_is_remembered_by_its_head_and_its_length()
    {
        var content = new string('x', 5_000);
        var json = JsonSerializer.Serialize(new { path = "Docs/DRIFT.md", new_string = content });

        var remembered = Transcript.ShortenArguments(json);

        using var doc = JsonDocument.Parse(remembered);
        Assert.Equal("Docs/DRIFT.md", doc.RootElement.GetProperty("path").GetString());

        var kept = doc.RootElement.GetProperty("new_string").GetString()!;
        Assert.StartsWith(new string('x', 200), kept, StringComparison.Ordinal);
        Assert.Contains("5,000 characters", kept, StringComparison.Ordinal);
        Assert.True(remembered.Length < 600, $"still {remembered.Length} characters");
    }

    /// <summary>
    /// The ordinary call — a path and a pattern — comes back as the SAME instance. Most calls are
    /// this, and parsing them would be work done to find out there is nothing to do.
    /// </summary>
    [Fact]
    public void A_small_call_is_not_touched_at_all()
    {
        var json = """{"path":"src/Program.cs","pattern":"class"}""";

        Assert.Same(json, Transcript.ShortenArguments(json));
    }

    /// <summary>
    /// A long argument that is not a string — an array of a hundred paths — is left alone. This
    /// shortens content, and only where the content is obviously content.
    /// </summary>
    [Fact]
    public void A_long_list_of_short_values_is_left_alone()
    {
        var json = JsonSerializer.Serialize(new { paths = Enumerable.Range(0, 200).Select(i => $"file{i}.cs") });

        var remembered = Transcript.ShortenArguments(json);

        using var doc = JsonDocument.Parse(remembered);
        Assert.Equal(200, doc.RootElement.GetProperty("paths").GetArrayLength());
    }

    /// <summary>
    /// Anything unparsable is a call this has no business editing — and it has to be LONG to get
    /// that far, because the short-circuit above returns anything small untouched without looking.
    /// </summary>
    [Fact]
    public void What_cannot_be_read_is_returned_as_it_came()
    {
        var notJson = new string('?', 900);
        var notAnObject = JsonSerializer.Serialize(new string('t', 900));

        Assert.Same(notJson, Transcript.ShortenArguments(notJson));
        Assert.Same(notAnObject, Transcript.ShortenArguments(notAnObject));
        Assert.Same("", Transcript.ShortenArguments(""));
    }

    /// <summary>
    /// THE ONE THAT KEEPS IT HONEST: the call that is INVOKED is never the shortened one. The
    /// engine passes the model's own text to the tool and records the short form separately, and a
    /// change that got this backwards would write 200 characters into somebody's file.
    /// </summary>
    [Fact]
    public void Shortening_produces_a_new_list_and_leaves_the_original_alone()
    {
        var content = new string('y', 4_000);
        var original = new[] { Call("write_file", JsonSerializer.Serialize(new { path = "a.md", content })) };

        var remembered = Transcript.ForHistory(original)!;

        Assert.NotSame(original, remembered);
        Assert.Contains(content, original[0].ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(content, remembered[0].ArgumentsJson, StringComparison.Ordinal);

        // Same call, same id: a provider pairs the result to the call by this, and a new id would
        // make the conversation malformed.
        Assert.Equal(original[0].Id, remembered[0].Id);
        Assert.Equal(original[0].Name, remembered[0].Name);
    }

    /// <summary>A list with nothing to shorten is the same list, not a copy of it.</summary>
    [Fact]
    public void A_turn_of_ordinary_calls_is_kept_as_it_is()
    {
        var calls = new[] { Call("read_file", """{"path":"a.md"}""") };

        Assert.Same(calls, Transcript.ForHistory(calls));
    }

    [Fact]
    public void Nothing_to_remember_is_not_an_error()
    {
        Assert.Null(Transcript.ForHistory(null));
        Assert.Empty(Transcript.ForHistory(Array.Empty<ToolCall>())!);
    }

    /// <summary>
    /// What it is worth on the traffic that prompted it: the 49 <c>edit_file</c> calls of that run
    /// averaged 1,909 characters of arguments each, and the saving is paid on every turn after.
    ///
    /// <para>Less than half, not a tenth, because the ANCHOR stays: an <c>old_string</c> under the
    /// threshold is kept whole, and that is deliberate — it is what the model matched on, it is
    /// usually short, and a call whose anchor has been replaced by a summary reads as a call that
    /// was never made.</para>
    /// </summary>
    [Fact]
    public void The_case_it_was_built_for_costs_less_than_half_of_what_it_did()
    {
        var edit = JsonSerializer.Serialize(new
        {
            path = "Docs/DRIFT_ollama.md",
            old_string = new string('o', 400),
            new_string = new string('n', 1_500)
        });

        var remembered = Transcript.ShortenArguments(edit);

        Assert.True(remembered.Length * 2 < edit.Length,
            $"{edit.Length} characters became {remembered.Length}, which is not worth the code");
    }
}
