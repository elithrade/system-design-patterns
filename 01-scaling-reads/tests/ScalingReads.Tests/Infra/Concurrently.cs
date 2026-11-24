namespace ScalingReads.Tests;

// Starts count actions on the thread pool, holds them at one gate, then releases them together so
// they race, and waits for every one.
internal static class Concurrently
{
    public static async Task<T[]> RunAsync<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await action(i);
            }))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public static Task RunAsync(int count, Func<int, Task> action) =>
        RunAsync(count, async i =>
        {
            await action(i);
            return true;
        });
}
