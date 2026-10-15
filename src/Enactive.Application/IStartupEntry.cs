namespace Enactive.Application;

/// <summary>
/// Whether the computer starts Enactive at login, as something that can be read and changed.
///
/// <para>An interface because the real one is the current user's Run key: a test of Save that reached
/// it would switch start-up on the machine running the tests. See <c>StartupEntry.System</c>.</para>
/// </summary>
internal interface IStartupEntry
{
    /// <summary>False where the desktop has no such thing; then nothing is read or changed.</summary>
    bool Supported { get; }

    bool IsEnabled();

    /// <summary>True when start-up is now as asked. False when the system refused.</summary>
    bool Set(bool enabled);
}

/// <summary>
/// A host with no start-up of its own to change: the service, which Windows starts as a service. Its settings page
/// offers no "start with the computer", and saving them must not reach any machine's login.
/// </summary>
internal sealed class NoStartupEntry : IStartupEntry
{
    public static NoStartupEntry Instance { get; } = new();

    public bool Supported => false;

    public bool IsEnabled() => false;

    public bool Set(bool enabled) => false;
}
