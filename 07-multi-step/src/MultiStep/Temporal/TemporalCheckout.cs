using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Worker;

namespace MultiStep;

public static class Keys
{
    // A service's idempotency key for one step of one checkout attempt.
    public static string For(string opKey, Op op) => $"{opKey}:{op}".ToLowerInvariant();
}

// Engine=Temporal: create the order row, start the workflow (one per opKey: starting it again
// joins the running one), wait for its result.
public sealed class TemporalCheckout(Orders orders, ITemporalClient client, IOptions<CheckoutOptions> checkout, IOptions<TemporalOptions> temporal) : ICheckout
{
    public static string WorkflowId(string opKey) => $"checkout-{opKey}";

    public async Task<CheckoutResult> RunAsync(CheckoutRequest request, string opKey, CancellationToken ct)
    {
        var order = await orders.CreateAsync(request, opKey, ct);
        if (OrderStatus.IsFinished(order.Status))
        {
            return order.Result;
        }
        var input = new CheckoutInput(order.Id, opKey, order.Sku, order.Quantity, order.Card, order.Address, order.Amount,
            checkout.Value.MaxAttempts, checkout.Value.RetryMilliseconds, temporal.Value.ActivityTimeoutSeconds);
        var handle = await client.StartWorkflowAsync((CheckoutWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(WorkflowId(opKey), temporal.Value.TaskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new() { CancellationToken = ct },
            });
        return await handle.GetResultAsync(rpcOptions: new() { CancellationToken = ct });
    }
}

// A Temporal worker in this process (Engine=Temporal): it runs the workflow and the activities.
public sealed class TemporalWorkerService(ITemporalClient client, CheckoutActivities activities, IOptions<TemporalOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var worker = new TemporalWorker(client, new TemporalWorkerOptions(options.Value.TaskQueue)
            {
                // the stub's NotImplementedException fails the workflow instead of retrying it forever
                WorkflowFailureExceptionTypes = [typeof(NotImplementedException)],
            }
            .AddAllActivities(activities)
            .AddWorkflow<CheckoutWorkflow>());
        await worker.ExecuteAsync(stoppingToken);
    }
}
