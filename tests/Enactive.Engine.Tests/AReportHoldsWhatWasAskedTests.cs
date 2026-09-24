namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Xunit;

/// <summary>
/// A written result holds what the request asked for; what was checked and found in order is a
/// count, not an entry each.
///
/// <para><b>Measured 2026-09-24.</b> One write_file turn took 72.1 s to generate - 4,709 tokens, a
/// report of 18,000 characters - and 32 ms to write. The request asked for discrepancies; most of the
/// report listed the claims that matched. Generation is the slowest thing a model does on any
/// provider, so the rule is for every role and every kind of report, not for one task.</para>
/// </summary>
public sealed class AReportHoldsWhatWasAskedTests
{
    [Theory]
    [MemberData(nameof(EngineFixture.ShippingRoles), MemberType = typeof(EngineFixture))]
    public void Every_role_is_told_to_write_what_was_asked(string role)
    {
        // What reaches the model: the role's own text with the shared rules appended at build time.
        var instructions = DefaultWorkers.Augment(EngineFixture.Role(role).Instructions);

        Assert.Contains("A report or written result holds what the request asked for", instructions, StringComparison.Ordinal);
        Assert.Contains("ONE line with a count", instructions, StringComparison.Ordinal);
    }
}
