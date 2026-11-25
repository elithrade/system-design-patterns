using Microsoft.Extensions.Options;
using Npgsql;

namespace MultiStep;

// Stage 3: finish what a dead process started. The recovery loop (below) calls RecoverAsync every
// RecoverySeconds; POST /admin/recover calls it once; the tests call it directly.
//
// RecoverAsync resumes every abandoned order and returns how many it resumed:
//   - abandoned: status running or compensating, and lease_until (on the injected TimeProvider)
//     has passed. Orders.CreateAsync gives a new order a lease of LeaseSeconds
//   - claim one order at a time, setting a new lease, so that several instances running recovery
//     at once never resume the same order twice; then saga.ResumeAsync(order)
//   - an order that crashes again while resumed (ProcessCrashException) stays abandoned for the
//     next round; let the exception go up
// Stage 2's ResumeAsync has to be right for this to work: a crash can happen after a step's work
// but before you saved its id, or in the middle of compensating.
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class SagaRecovery(NpgsqlDataSource db, SagaCheckout saga, TimeProvider time, IOptions<CheckoutOptions> options)
{
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);

    public Task<int> RecoverAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}

// Runs RecoverAsync every RecoverySeconds (Engine=Saga only).
public sealed class RecoveryLoop(SagaRecovery recovery, IOptions<CheckoutOptions> options, ILogger<RecoveryLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.RecoverySeconds <= 0)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resumed = await recovery.RecoverAsync(stoppingToken);
                if (resumed > 0)
                {
                    logger.LogInformation("Recovery resumed {Count} orders", resumed);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Recovery failed: {Error}", e.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.RecoverySeconds), stoppingToken);
        }
    }
}
