using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace MultiStep;

// What TemporalCheckout starts the workflow with. Keys come from OpKey (Keys.For) as in the saga.
public sealed record CheckoutInput(
    long OrderId,
    string OpKey,
    string Sku,
    int Quantity,
    string Card,
    string Address,
    long Amount,
    int MaxAttempts,
    int RetryMilliseconds,
    int ActivityTimeoutSeconds);

// Stage 4: the same checkout as stage 2, as a Temporal workflow.
//
// RunAsync calls the activities in CheckoutActivities (Workflow.ExecuteActivityAsync) and returns
// the result:
//   - activity options: StartToCloseTimeout = ActivityTimeoutSeconds; a RetryPolicy with
//     InitialInterval = RetryMilliseconds, BackoffCoefficient 2, MaximumAttempts = MaxAttempts.
//     Permanent failures must not be retried: name their types in NonRetryableErrorTypes
//   - charge, reserve, ship, then SaveOrderAsync(completed) and return the result
//   - a step that fails for good (ActivityFailureException): undo what was or may have been done
//     (by key, as in the saga), newest first,
//     with undo activities that retry until they succeed (MaximumAttempts 0 means no limit), then
//     SaveOrderAsync(cancelled, reason) and return the result. The reason is the failure's message:
//     look inside the exception's InnerException (an ApplicationFailureException)
//   - Keys.For(input.OpKey, op) gives each step's idempotency key
// Compare what you write here with your stages 2 and 3: retries, saved progress, recovery and
// leases are now Temporal's job. Workflow code must be deterministic: no DateTime.Now, Random,
// I/O or Task.Delay; Workflow.* has replacements.
//
// Run it with Checkout__Engine=Temporal and the compose stack's Temporal server.
// Tests: dotnet test --filter-namespace "*Stage4"
[Workflow]
public class CheckoutWorkflow
{
    [WorkflowRun]
    public Task<CheckoutResult> RunAsync(CheckoutInput input)
    {
        throw new NotImplementedException();
    }
}
