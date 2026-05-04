using System.Collections.Concurrent;
using Creuser.Sagas.Commands;

namespace Creuser.Sagas;

/// <summary>
/// Singleton service that lets the synchronous <c>POST /jobs/{id}/run</c>
/// endpoint await saga completion. The endpoint registers a
/// <see cref="TaskCompletionSource{JobRunFinished}"/> keyed on the run id
/// before publishing <see cref="StartJobRun"/>, then awaits the task.
/// When the saga publishes <see cref="JobRunFinished"/>,
/// <see cref="JobRunFinishedHandler"/> resolves the TCS and the endpoint
/// returns the persisted <c>JobRun</c>.
///
/// <para>
/// This is a single-instance optimisation. Multi-instance deployments
/// where the saga can complete on a different host need a Redis pub/sub
/// backplane (or polling); v1 doesn't ship that. Documenting the
/// constraint where it matters: the architecture doc's "Single image,
/// single command" principle assumes a single host serving HTTP, which is
/// the deployment shape this waiter supports.
/// </para>
///
/// <para>
/// Cancellation: callers pass their own <see cref="CancellationToken"/>
/// (typically <c>HttpContext.RequestAborted</c>); when the request is
/// aborted, the awaiter cancels its wait without affecting the saga,
/// which continues running and produces the <see cref="JobRunFinished"/>
/// signal as usual. Stale entries are pruned on signal — no memory leak
/// from an aborted request.
/// </para>
/// </summary>
public sealed class RunCompletionWaiter
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JobRunFinished>> _waiters =
        new();

    /// <summary>
    /// Register a waiter for a run. Call before publishing
    /// <see cref="StartJobRun"/> so a saga that finishes very quickly
    /// doesn't signal before the waiter exists.
    /// </summary>
    public Task<JobRunFinished> RegisterAndWait(Guid runId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JobRunFinished>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _waiters[runId] = tcs;

        if (ct.CanBeCanceled)
            ct.Register(() =>
            {
                _waiters.TryRemove(runId, out _);
                tcs.TrySetCanceled(ct);
            });

        return tcs.Task;
    }

    /// <summary>
    /// Signal completion. No-op if no waiter is registered (the saga ran
    /// fully async — no synchronous caller is waiting).
    /// </summary>
    public void Signal(JobRunFinished outcome)
    {
        if (_waiters.TryRemove(outcome.RunId, out var tcs))
            tcs.TrySetResult(outcome);
    }
}

/// <summary>
/// Wolverine handler for <see cref="JobRunFinished"/>. The saga publishes
/// this as the last step of run completion; the handler signals any
/// registered waiter so the synchronous endpoint can return.
/// </summary>
public static class JobRunFinishedHandler
{
    public static void Handle(JobRunFinished msg, RunCompletionWaiter waiter) => waiter.Signal(msg);
}
