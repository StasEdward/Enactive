namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts.Crypto;

/// <summary>
/// This computer's keys, as sealing needs them: its id, the epoch it seals with now, and any epoch a
/// command may have been sealed under.
///
/// <para>An interface because the key store that keeps them protected for this Windows user comes
/// later, and everything that seals and opens has to be right before it does. Tests hold one fixed
/// key.</para>
/// </summary>
public interface IHostKeys
{
    /// <summary>This computer's id, as the gateway and every trusted device know it. Part of all associated data.</summary>
    string HostId { get; }

    /// <summary>The epoch everything new is sealed under.</summary>
    HostKey Current { get; }

    /// <summary>The key for one epoch, or null when this computer does not hold it.</summary>
    HostKey? Epoch(uint epoch);
}
