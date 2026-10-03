namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;

/// <summary>
/// The order the commands of one Sync are carried out in: every removal, then every endorsement, then the
/// rest as the gateway listed them.
///
/// <para>Removals first because a command is opened under the key current when it is opened, and a removal
/// replaces that key. A computer that was off while a phone was lost and removed receives the removal in the
/// same Sync as whatever was queued under the old key - by the thief, or by the phone before it was lost. Run
/// side by side, those opened under the old key whenever they got there before the rotation committed: a task
/// the person did not start ran after they removed the device, and a stand-in endorsed in the same batch was
/// trusted and given every later key. Opened after the removal, each is refused as sealed before it, and an
/// honest browser is asked to send it again.</para>
///
/// <para>Endorsements next because they change the trusted list too, and a start that follows should meet the
/// list the person meant. The gateway chooses the order it lists commands in, so nothing here relies on it
/// beyond keeping the order of commands of the same rank.</para>
/// </summary>
public static class CommandOrder
{
    /// <summary>The commands in the order they are to be carried out. The sort is stable.</summary>
    public static IReadOnlyList<HostCommand> Arrange(IEnumerable<HostCommand> commands)
        => [.. commands.OrderBy(Rank)];

    private static int Rank(HostCommand command) => command.Kind switch
    {
        CommandKind.RevokeDevice => 0,
        CommandKind.EndorseDevice => 1,
        _ => 2
    };
}
