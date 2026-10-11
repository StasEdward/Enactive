namespace Enactive.Server;

using Enactive.Core.Storage;

/// <summary>
/// One service per data folder: two services on one account's profile would each run their own scheduler over the
/// same schedules and their own engine over the same settings.
///
/// <para>The service's only. The window and a <c>--due</c> console of one account share their data folder today, and
/// an exclusive lock there would stop schedules while the window is open; what keeps hosts out of each other's way is
/// the workspace lease every run takes (ADR 0002 §8).</para>
/// </summary>
internal static class DataFolderLock
{
    public static string PathFor(string dataFolder) => Path.Combine(dataFolder, "service.lock");

    /// <summary>Takes the folder for this service, or returns null and who has it.</summary>
    public static ProcessLease? TryTake(string dataFolder, out LeaseHolder? holder)
        => ProcessLease.TryTake(PathFor(dataFolder), out holder);
}
