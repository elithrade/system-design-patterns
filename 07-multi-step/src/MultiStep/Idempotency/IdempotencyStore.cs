using Microsoft.Extensions.Options;
using Npgsql;

namespace MultiStep;

// What POST /checkout answered: replayed as is for a repeated key.
public sealed record StoredResponse(int StatusCode, string Body);

// Stage 1. POST /checkout with an Idempotency-Key header calls RunAsync. The endpoint computes
// fingerprint (a hash of the request body) and passes handler, which runs the checkout with
// opKey = the key and returns the response to send.
//
// Rules, in the style of Stripe's API:
//   - first time for a key: run handler, store its response, return it
//   - the key again, finished: return the stored response; do not run handler
//   - the key again, still running: return 409 with a short JSON body; do not run handler
//   - the key again with a different fingerprint: return 422; the key belongs to another request
//   - handler throws: nothing was decided, so release the key (a retry runs handler again), and
//     rethrow. Except ProcessCrashException: it stands for the process dying, so the key stays
//     locked as a real crash would leave it. A lock older than KeyLockSeconds (on the injected
//     TimeProvider) may be taken over by a new request.
// Ten requests with the same key at the same moment must run handler once. Write a migration,
// db/migrations/002_<name>.sql, with the table you need.
//
// The tests check that a retry of a checkout that failed halfway charges the card once. Look at
// how NaiveCheckout derives the services' keys (Order.KeyFor) to see why that can work.
//
// Tests: dotnet test --filter-namespace "*Stage1"
public sealed class IdempotencyStore(NpgsqlDataSource db, TimeProvider time, IOptions<CheckoutOptions> options)
{
    private readonly TimeSpan _lock = TimeSpan.FromSeconds(options.Value.KeyLockSeconds);

    public Task<StoredResponse> RunAsync(string key, string fingerprint, Func<CancellationToken, Task<StoredResponse>> handler, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
