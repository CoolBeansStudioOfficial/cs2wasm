using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cs2Wasm.Runtime
{
    /// <summary>What the compiler's async state machines call (StateMachine.cs): the method's task is a promise-style
    /// task (upstream's), completed by the machine when the method returns or throws.</summary>
    internal static class AsyncSupport
    {
        public static Task CreateForAsync() => new Task();
        public static Task<T> CreateForAsyncT<T>() => new Task<T>();

        public static bool TrySetResultCore(Task task) => task.TrySetResult();
        public static bool TrySetResultT<T>(Task<T> task, T result) => task.TrySetResult(result);

        /// <summary>An exception that escapes the method faults its task; OperationCanceledException cancels it.</summary>
        public static bool TrySetExceptionCore(Task task, object exception) =>
            exception is OperationCanceledException oce ? task.TrySetCanceled(oce.CancellationToken, oce) : task.TrySetException(exception);

        /// <summary>The kickoff of an async method keeps the caller's ExecutionContext (AsyncLocal values), as
        /// AsyncMethodBuilderCore.Start does: saved before the first MoveNext, put back after it.</summary>
        public static object? SaveContext() => Thread.CurrentThread._executionContext;

        public static void RestoreContext(object? saved)
        {
            var t = Thread.CurrentThread;
            var current = t._executionContext;
            if (!ReferenceEquals(current, saved)) ExecutionContext.RestoreChangedContextToThread(t, (ExecutionContext?)saved, current);
        }

        /// <summary>An async iterator's MoveNextAsync and DisposeAsync results, over its promise.</summary>
        public static ValueTask<bool> Wrap(Task<bool> t) => new ValueTask<bool>(t);
        public static ValueTask WrapDispose(Task<bool> t) => new ValueTask(t);
    }
}
