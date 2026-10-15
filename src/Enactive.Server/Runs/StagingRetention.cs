namespace Enactive.Server.Runs;

/// <summary>
/// Drops, once an hour, the proposed changes nobody decided within server.json's <c>ProposalsKeptDays</c>
/// (ServerStaging.ExpireAsync). An hour because the keeping is counted in days; 0 days keeps them until decided.
/// </summary>
internal sealed class StagingRetention(ServerStaging staging, ServerOptions options, ILogger<StagingRetention> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        if (options.ProposalsKeptDays == 0)
            return;
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                var dropped = await staging.ExpireAsync(DateTimeOffset.Now, TimeSpan.FromDays(options.ProposalsKeptDays), stopping);
                if (dropped > 0)
                    log.LogInformation("Dropped the proposed changes of {Count} run(s), not decided in {Days} days.", dropped, options.ProposalsKeptDays);
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                // One bad look is not the end of the keeping: said, and the next hour looks again.
                log.LogWarning(ex, "The proposed changes could not be looked through for ones kept too long.");
            }
        }
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
    }
}
