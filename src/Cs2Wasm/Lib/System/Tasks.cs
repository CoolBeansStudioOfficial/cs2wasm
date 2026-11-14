// Tasks and async support. Task.Run and Parallel run on the thread pool (section 4.5); continuations run where the
// task completes, or are posted to the captured SynchronizationContext. Blocking waits on the engine thread run its
// posted work and timers until the task completes; with no other thread in the program, timers fire in virtual
// time (the earliest at once), so single-threaded programs are deterministic.
#pragma warning disable CS0436, CS0659, CS0661
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Cs2Wasm.Runtime;

namespace System.Threading.Tasks
{
    public enum TaskStatus { Created, WaitingForActivation, WaitingToRun, Running, WaitingForChildrenToComplete, RanToCompletion, Canceled, Faulted }

    public class Task : IDisposable
    {
        internal int _state; // 0 pending, 1 done, 2 faulted, 3 canceled
        internal Exception _exception;
        internal List<Continuation> _continuations;
        static int s_ids;
        int _id;

        internal Task() { }
        internal static Task CreateForAsync() => new Task();
        internal static readonly Task s_completed = new Task { _state = 1 };

        public static Task CompletedTask => s_completed;
        public int Id { get { if (_id == 0) _id = ++s_ids; return _id; } }
        public bool IsCompleted => Atomic.Load32(ref _state) != 0;
        public bool IsCompletedSuccessfully => _state == 1;
        public bool IsFaulted => _state == 2;
        public bool IsCanceled => _state == 3;
        public TaskStatus Status => _state switch { 0 => TaskStatus.WaitingForActivation, 1 => TaskStatus.RanToCompletion, 2 => TaskStatus.Faulted, _ => TaskStatus.Canceled };
        public AggregateException Exception => _state == 2 ? new AggregateException(_exception) : null;

        public TaskAwaiter GetAwaiter() => new TaskAwaiter(this);
        public ConfiguredTaskAwaitable ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredTaskAwaitable(this, continueOnCapturedContext);

        public void Wait()
        {
            Scheduler.RunUntil(this);
            if (_state == 2) throw new AggregateException(_exception);
            if (_state == 3) throw new AggregateException(new TaskCanceledException());
        }
        public bool Wait(int millisecondsTimeout)
        {
            if (millisecondsTimeout == -1) { Wait(); return true; }
            if (!Scheduler.RunUntil(this, Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000)) return false;
            Wait();
            return true;
        }
        public bool Wait(TimeSpan timeout) => Wait((int)timeout.TotalMilliseconds);
        public static void WaitAll(params Task[] tasks) { foreach (var t in tasks) t.Wait(); }
        public static int WaitAny(params Task[] tasks)
        {
            while (true)
            {
                for (int i = 0; i < tasks.Length; i++) if (tasks[i].IsCompleted) return i;
                int g = Atomic.Load32(ref Threads.s_generation);
                for (int i = 0; i < tasks.Length; i++) if (tasks[i].IsCompleted) return i;
                if (!Scheduler.Step()) Threads.WaitGeneration(g);
            }
        }
        public void Dispose() { }

        internal bool TrySetResultCore()
        {
            Rt.RuntimeLock();
            if (_state != 0) { Rt.RuntimeUnlock(); return false; }
            CompleteLocked(1);
            return true;
        }

        internal bool TrySetExceptionCore(object ex)
        {
            Rt.RuntimeLock();
            if (_state != 0) { Rt.RuntimeUnlock(); return false; }
            _exception = (Exception)ex;
            CompleteLocked(ex is OperationCanceledException ? 3 : 2);
            return true;
        }

        internal bool TrySetCanceledCore()
        {
            var ex = new TaskCanceledException();
            Rt.RuntimeLock();
            if (_state != 0) { Rt.RuntimeUnlock(); return false; }
            _exception = ex;
            CompleteLocked(3);
            return true;
        }

        /// <summary>Called holding the runtime lock: publishes the state, releases the lock, then runs continuations.</summary>
        internal void CompleteLocked(int state)
        {
            Atomic.Store32(ref _state, state);
            var list = _continuations;
            _continuations = null;
            Rt.RuntimeUnlock();
            if (Threads.Spawned) Threads.WakeAll();
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) list[i].Run();
        }

        internal void AddContinuation(Action action, bool captureContext)
        {
            var ctx = captureContext ? SynchronizationContext.Current : null;
            var c = new Continuation(action, ctx);
            Rt.RuntimeLock();
            if (_state != 0) { Rt.RuntimeUnlock(); c.Run(); return; }
            (_continuations ??= new List<Continuation>()).Add(c);
            Rt.RuntimeUnlock();
        }

        internal void ThrowIfFailed()
        {
            if (_state == 2 || _state == 3) Rt.Rethrow(_exception);
        }

        public static Task<TResult> FromResult<TResult>(TResult result)
        {
            var t = new Task<TResult>();
            t.TrySetResultT(result);
            return t;
        }
        public static Task FromException(Exception exception) { var t = new Task(); t.TrySetExceptionCore(exception); return t; }
        public static Task<TResult> FromException<TResult>(Exception exception) { var t = new Task<TResult>(); t.TrySetExceptionCore(exception); return t; }
        public static Task FromCanceled(CancellationToken token) { var t = new Task(); t.TrySetCanceledCore(); return t; }

        public static Task Delay(int millisecondsDelay) => Delay(millisecondsDelay, default);
        public static Task Delay(TimeSpan delay) => Delay((int)delay.TotalMilliseconds, default);
        public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken)
        {
            if (millisecondsDelay < -1) throw new ArgumentOutOfRangeException("millisecondsDelay");
            if (millisecondsDelay == 0) return CompletedTask;
            var t = new Task();
            Scheduler.AddTimer(t, millisecondsDelay);
            return t;
        }

        public static Task Run(Action action)
        {
            var t = new Task();
            ThreadPool.Queue(() =>
            {
                try { action(); t.TrySetResultCore(); }
                catch (Exception e) { t.TrySetExceptionCore(e); }
            });
            return t;
        }
        public static Task<TResult> Run<TResult>(Func<TResult> function)
        {
            var t = new Task<TResult>();
            ThreadPool.Queue(() =>
            {
                try { t.TrySetResultT(function()); }
                catch (Exception e) { t.TrySetExceptionCore(e); }
            });
            return t;
        }
        public static Task Run(Func<Task> function)
        {
            var t = new Task();
            ThreadPool.Queue(() =>
            {
                Task inner;
                try { inner = function(); }
                catch (Exception e) { t.TrySetExceptionCore(e); return; }
                inner.AddContinuation(() => { if (inner._state == 1) t.TrySetResultCore(); else t.TrySetExceptionCore(inner._exception); }, false);
            });
            return t;
        }
        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function)
        {
            var t = new Task<TResult>();
            ThreadPool.Queue(() =>
            {
                Task<TResult> inner;
                try { inner = function(); }
                catch (Exception e) { t.TrySetExceptionCore(e); return; }
                inner.AddContinuation(() => { if (inner._state == 1) t.TrySetResultT(inner._result); else t.TrySetExceptionCore(inner._exception); }, false);
            });
            return t;
        }

        public static Task WhenAll(params Task[] tasks) => WhenAll((IEnumerable<Task>)tasks);
        public static Task WhenAll(ReadOnlySpan<Task> tasks) => WhenAll((IEnumerable<Task>)tasks.ToArray());
        public static Task<TResult[]> WhenAll<TResult>(ReadOnlySpan<Task<TResult>> tasks) => WhenAll((IEnumerable<Task<TResult>>)tasks.ToArray());
        public static Task<Task> WhenAny(ReadOnlySpan<Task> tasks) => WhenAny(tasks.ToArray());
        public static Task WhenAll(IEnumerable<Task> tasks)
        {
            var list = new List<Task>(tasks);
            var result = new Task();
            int remaining = list.Count;
            if (remaining == 0) { result.TrySetResultCore(); return result; }
            foreach (var t in list)
                t.AddContinuation(() =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        Exception first = null;
                        foreach (var x in list) if (x._state >= 2 && first == null) first = x._exception;
                        if (first != null) result.TrySetExceptionCore(first); else result.TrySetResultCore();
                    }
                }, false);
            return result;
        }
        public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks) => WhenAll((IEnumerable<Task<TResult>>)tasks);
        public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            var list = new List<Task<TResult>>(tasks);
            var result = new Task<TResult[]>();
            int remaining = list.Count;
            if (remaining == 0) { result.TrySetResultT(new TResult[0]); return result; }
            foreach (var t in list)
                t.AddContinuation(() =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        Exception first = null;
                        var values = new TResult[list.Count];
                        for (int i = 0; i < list.Count; i++) { if (list[i]._state >= 2 && first == null) first = list[i]._exception; values[i] = list[i]._result; }
                        if (first != null) result.TrySetExceptionCore(first); else result.TrySetResultT(values);
                    }
                }, false);
            return result;
        }
        public static Task<Task> WhenAny(params Task[] tasks)
        {
            var result = new Task<Task>();
            foreach (var t in tasks) t.AddContinuation(() => result.TrySetResultT(t), false);
            return result;
        }
        public static YieldAwaitable Yield() => default;
    }

    public class Task<TResult> : Task
    {
        internal TResult _result;
        internal Task() { }
        internal static Task<TResult> CreateForAsyncT() => new Task<TResult>();
        public TResult Result
        {
            get
            {
                Wait();
                return _result;
            }
        }
        internal bool TrySetResultT(TResult result)
        {
            Rt.RuntimeLock();
            if (_state != 0) { Rt.RuntimeUnlock(); return false; }
            _result = result;
            CompleteLocked(1);
            return true;
        }
        public new TaskAwaiter<TResult> GetAwaiter() => new TaskAwaiter<TResult>(this);
        public new ConfiguredTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredTaskAwaitable<TResult>(this, continueOnCapturedContext);
    }

    internal sealed class Continuation
    {
        readonly Action _action;
        readonly SynchronizationContext _context;
        public Continuation(Action a, SynchronizationContext c) { _action = ExecutionContext.Flow(a); _context = c; }
        public void Run()
        {
            if (_context != null && !_context.IsDefault) _context.Post(s => ((Action)s)(), _action);
            else _action();
        }
    }

    public class TaskCompletionSource<TResult>
    {
        readonly Task<TResult> _task = new Task<TResult>();
        public TaskCompletionSource() { }
        public TaskCompletionSource(object state) { }
        public Task<TResult> Task => _task;
        public void SetResult(TResult result) { if (!_task.TrySetResultT(result)) throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed."); }
        public bool TrySetResult(TResult result) => _task.TrySetResultT(result);
        public void SetException(Exception exception) { if (!_task.TrySetExceptionCore(exception)) throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed."); }
        public bool TrySetException(Exception exception) => _task.TrySetExceptionCore(exception);
        public void SetCanceled() { if (!_task.TrySetCanceledCore()) throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed."); }
        public bool TrySetCanceled() => _task.TrySetCanceledCore();
    }

    public class TaskCompletionSource
    {
        readonly Task _task = new Task();
        public Task Task => _task;
        public void SetResult() { if (!_task.TrySetResultCore()) throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed."); }
        public bool TrySetResult() => _task.TrySetResultCore();
        public void SetException(Exception exception) { if (!_task.TrySetExceptionCore(exception)) throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed."); }
        public bool TrySetException(Exception exception) => _task.TrySetExceptionCore(exception);
        public void SetCanceled() => _task.TrySetCanceledCore();
    }

    public readonly struct ValueTask
    {
        readonly Task _task;
        public ValueTask(Task task) { _task = task; }
        public static ValueTask CompletedTask => default;
        public bool IsCompleted => _task == null || _task.IsCompleted;
        public bool IsCompletedSuccessfully => _task == null || _task.IsCompletedSuccessfully;
        public Task AsTask() => _task ?? Task.CompletedTask;
        public ValueTaskAwaiter GetAwaiter() => new ValueTaskAwaiter(_task);
        public System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable ConfigureAwait(bool continueOnCapturedContext) => new System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable(_task, continueOnCapturedContext);
        public static ValueTask<TResult> FromResult<TResult>(TResult result) => new ValueTask<TResult>(result);
        public static ValueTask FromException(Exception exception) => new ValueTask(Task.FromException(exception));
        public bool IsFaulted => _task != null && _task.IsFaulted;
        public bool IsCanceled => _task != null && _task.IsCanceled;
        internal Task TaskOrNull => _task;
    }

    public readonly struct ValueTask<TResult>
    {
        readonly Task<TResult> _task;
        readonly TResult _result;
        public ValueTask(TResult result) { _result = result; _task = null; }
        public ValueTask(Task<TResult> task) { _task = task; _result = default; }
        public bool IsCompleted => _task == null || _task.IsCompleted;
        public TResult Result => _task == null ? _result : _task.Result;
        public Task<TResult> AsTask() => _task ?? Task.FromResult(_result);
        public ValueTaskAwaiter<TResult> GetAwaiter() => new ValueTaskAwaiter<TResult>(_task, _result);
        public System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext) => new System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult>(_task, _result, continueOnCapturedContext);
        public bool IsCompletedSuccessfully => _task == null || _task.IsCompletedSuccessfully;
        public bool IsFaulted => _task != null && _task.IsFaulted;
        public bool IsCanceled => _task != null && _task.IsCanceled;
    }

    namespace Sources { }
}

namespace System.Runtime.CompilerServices
{
    using System.Threading.Tasks;

    public interface INotifyCompletion { void OnCompleted(Action continuation); }
    public interface ICriticalNotifyCompletion : INotifyCompletion { void UnsafeOnCompleted(Action continuation); }

    public readonly struct TaskAwaiter : ICriticalNotifyCompletion
    {
        readonly Task _task;
        internal TaskAwaiter(Task t) { _task = t; }
        public bool IsCompleted => _task.IsCompleted;
        public void OnCompleted(Action continuation) => _task.AddContinuation(continuation, true);
        public void UnsafeOnCompleted(Action continuation) => _task.AddContinuation(continuation, true);
        public void GetResult()
        {
            if (!_task.IsCompleted) Scheduler.RunUntil(_task);
            _task.ThrowIfFailed();
        }
    }

    public readonly struct TaskAwaiter<TResult> : ICriticalNotifyCompletion
    {
        readonly Task<TResult> _task;
        internal TaskAwaiter(Task<TResult> t) { _task = t; }
        public bool IsCompleted => _task.IsCompleted;
        public void OnCompleted(Action continuation) => _task.AddContinuation(continuation, true);
        public void UnsafeOnCompleted(Action continuation) => _task.AddContinuation(continuation, true);
        public TResult GetResult()
        {
            if (!_task.IsCompleted) Scheduler.RunUntil(_task);
            _task.ThrowIfFailed();
            return _task._result;
        }
    }

    public readonly struct ConfiguredTaskAwaitable
    {
        readonly Task _task; readonly bool _ctx;
        internal ConfiguredTaskAwaitable(Task t, bool ctx) { _task = t; _ctx = ctx; }
        public ConfiguredTaskAwaiter GetAwaiter() => new ConfiguredTaskAwaiter(_task, _ctx);
        public readonly struct ConfiguredTaskAwaiter : ICriticalNotifyCompletion
        {
            readonly Task _task; readonly bool _ctx;
            internal ConfiguredTaskAwaiter(Task t, bool ctx) { _task = t; _ctx = ctx; }
            public bool IsCompleted => _task.IsCompleted;
            public void OnCompleted(Action continuation) => _task.AddContinuation(continuation, _ctx);
            public void UnsafeOnCompleted(Action continuation) => _task.AddContinuation(continuation, _ctx);
            public void GetResult() { if (!_task.IsCompleted) Scheduler.RunUntil(_task); _task.ThrowIfFailed(); }
        }
    }

    public readonly struct ConfiguredTaskAwaitable<TResult>
    {
        readonly Task<TResult> _task; readonly bool _ctx;
        internal ConfiguredTaskAwaitable(Task<TResult> t, bool ctx) { _task = t; _ctx = ctx; }
        public ConfiguredTaskAwaiter GetAwaiter() => new ConfiguredTaskAwaiter(_task, _ctx);
        public readonly struct ConfiguredTaskAwaiter : ICriticalNotifyCompletion
        {
            readonly Task<TResult> _task; readonly bool _ctx;
            internal ConfiguredTaskAwaiter(Task<TResult> t, bool ctx) { _task = t; _ctx = ctx; }
            public bool IsCompleted => _task.IsCompleted;
            public void OnCompleted(Action continuation) => _task.AddContinuation(continuation, _ctx);
            public void UnsafeOnCompleted(Action continuation) => _task.AddContinuation(continuation, _ctx);
            public TResult GetResult() { if (!_task.IsCompleted) Scheduler.RunUntil(_task); _task.ThrowIfFailed(); return _task._result; }
        }
    }

    public readonly struct ConfiguredValueTaskAwaitable
    {
        readonly Task _task; readonly bool _ctx;
        internal ConfiguredValueTaskAwaitable(Task t, bool ctx) { _task = t; _ctx = ctx; }
        public ConfiguredValueTaskAwaiter GetAwaiter() => new ConfiguredValueTaskAwaiter(_task, _ctx);
        public readonly struct ConfiguredValueTaskAwaiter : ICriticalNotifyCompletion
        {
            readonly Task _task; readonly bool _ctx;
            internal ConfiguredValueTaskAwaiter(Task t, bool ctx) { _task = t; _ctx = ctx; }
            public bool IsCompleted => _task == null || _task.IsCompleted;
            public void OnCompleted(Action continuation) { if (_task == null) continuation(); else _task.AddContinuation(continuation, _ctx); }
            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
            public void GetResult() { if (_task == null) return; if (!_task.IsCompleted) Scheduler.RunUntil(_task); _task.ThrowIfFailed(); }
        }
    }

    public readonly struct ConfiguredValueTaskAwaitable<TResult>
    {
        readonly Task<TResult> _task; readonly TResult _result; readonly bool _ctx;
        internal ConfiguredValueTaskAwaitable(Task<TResult> t, TResult r, bool ctx) { _task = t; _result = r; _ctx = ctx; }
        public ConfiguredValueTaskAwaiter GetAwaiter() => new ConfiguredValueTaskAwaiter(_task, _result, _ctx);
        public readonly struct ConfiguredValueTaskAwaiter : ICriticalNotifyCompletion
        {
            readonly Task<TResult> _task; readonly TResult _result; readonly bool _ctx;
            internal ConfiguredValueTaskAwaiter(Task<TResult> t, TResult r, bool ctx) { _task = t; _result = r; _ctx = ctx; }
            public bool IsCompleted => _task == null || _task.IsCompleted;
            public void OnCompleted(Action continuation) { if (_task == null) continuation(); else _task.AddContinuation(continuation, _ctx); }
            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
            public TResult GetResult() { if (_task == null) return _result; if (!_task.IsCompleted) Scheduler.RunUntil(_task); _task.ThrowIfFailed(); return _task._result; }
        }
    }

    public readonly struct ValueTaskAwaiter : ICriticalNotifyCompletion
    {
        readonly Task _task;
        internal ValueTaskAwaiter(Task t) { _task = t; }
        public bool IsCompleted => _task == null || _task.IsCompleted;
        public void OnCompleted(Action continuation) { if (_task == null) continuation(); else _task.AddContinuation(continuation, true); }
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        public void GetResult() { if (_task == null) return; if (!_task.IsCompleted) Scheduler.RunUntil(_task); _task.ThrowIfFailed(); }
    }

    public readonly struct ValueTaskAwaiter<TResult> : ICriticalNotifyCompletion
    {
        readonly Task<TResult> _task; readonly TResult _result;
        internal ValueTaskAwaiter(Task<TResult> t, TResult r) { _task = t; _result = r; }
        public bool IsCompleted => _task == null || _task.IsCompleted;
        public void OnCompleted(Action continuation) { if (_task == null) continuation(); else _task.AddContinuation(continuation, true); }
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        public TResult GetResult() { if (_task == null) return _result; if (!_task.IsCompleted) Scheduler.RunUntil(_task); _task.ThrowIfFailed(); return _task._result; }
    }

    public readonly struct YieldAwaitable
    {
        public YieldAwaiter GetAwaiter() => default;
        public readonly struct YieldAwaiter : ICriticalNotifyCompletion
        {
            public bool IsCompleted => false;
            public void OnCompleted(Action continuation) => Post(continuation);
            public void UnsafeOnCompleted(Action continuation) => Post(continuation);
            static void Post(Action c)
            {
                c = ExecutionContext.Flow(c);
                var ctx = SynchronizationContext.Current;
                if (ctx != null && !ctx.IsDefault) ctx.Post(s => ((Action)s)(), c);
                else if (Rt.ThreadId() != 1) ThreadPool.Queue(c);
                else Scheduler.Post(c);
            }
            public void GetResult() { }
        }
    }

    [AttributeUsage(AttributeTargets.Method)] public sealed class AsyncStateMachineAttribute : Attribute { public AsyncStateMachineAttribute(Type t) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class IteratorStateMachineAttribute : Attribute { public IteratorStateMachineAttribute(Type t) { } }
}

namespace System.Threading
{
    using System.Threading.Tasks;

    public delegate void SendOrPostCallback(object state);

    public class SynchronizationContext
    {
        [ThreadStatic] static SynchronizationContext s_current;
        public static SynchronizationContext Current => s_current;
        public static void SetSynchronizationContext(SynchronizationContext syncContext) { s_current = syncContext; }
        internal virtual bool IsDefault => Rt.TypeId(this) == Rt.TypeIdOf<SynchronizationContext>();
        public virtual void Post(SendOrPostCallback d, object state) => Scheduler.Post(() => d(state));
        public virtual void Send(SendOrPostCallback d, object state) => d(state);
        public virtual SynchronizationContext CreateCopy() => new SynchronizationContext();
    }

    public readonly struct CancellationToken
    {
        readonly CancellationTokenSource _source;
        internal CancellationToken(CancellationTokenSource s) { _source = s; }
        public static CancellationToken None => default;
        public bool IsCancellationRequested => _source != null && _source.IsCancellationRequested;
        public bool CanBeCanceled => _source != null;
        public void ThrowIfCancellationRequested() { if (IsCancellationRequested) throw new OperationCanceledException(); }
    }

    public class CancellationTokenSource : IDisposable
    {
        int _canceled;
        public bool IsCancellationRequested => Atomic.Load32(ref _canceled) != 0;
        public CancellationToken Token => new CancellationToken(this);
        public void Cancel() { Atomic.Store32(ref _canceled, 1); }
        public void Dispose() { }
    }

    public delegate void WaitCallback(object state);

}

namespace System.Threading.Tasks
{
    using System.Collections.Generic;

    public struct ParallelLoopResult { public bool IsCompleted => true; }
    public class ParallelOptions { public int MaxDegreeOfParallelism { get; set; } = -1; public CancellationToken CancellationToken { get; set; } }

    /// <summary>Parallel loops: the range is split into chunks run on the thread pool and the calling thread.</summary>
    public static class Parallel
    {
        public static ParallelLoopResult For(int fromInclusive, int toExclusive, Action<int> body) => For(fromInclusive, toExclusive, new ParallelOptions(), body);
        public static ParallelLoopResult For(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> body)
        {
            if (body == null) throw new ArgumentNullException("body");
            long n = (long)toExclusive - fromInclusive;
            if (n <= 0) return default;
            int workers = Environment.ProcessorCount;
            if (parallelOptions.MaxDegreeOfParallelism > 0 && parallelOptions.MaxDegreeOfParallelism < workers) workers = parallelOptions.MaxDegreeOfParallelism;
            if (workers < 1) workers = 1;
            int next = fromInclusive;
            Exception failure = null;
            object gate = new object();
            Action run = () =>
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref next) - 1;
                    if (i >= toExclusive || Volatile.Read(ref failure) != null) return;
                    try { body(i); }
                    catch (Exception e) { lock (gate) { if (failure == null) failure = e; } return; }
                }
            };
            var tasks = new Task[workers - 1];
            for (int w = 0; w < tasks.Length; w++) tasks[w] = Task.Run(run);
            run();
            for (int w = 0; w < tasks.Length; w++) tasks[w].Wait();
            if (failure != null) throw new AggregateException(failure);
            return default;
        }
        public static ParallelLoopResult ForEach<TSource>(IEnumerable<TSource> source, Action<TSource> body)
        {
            var items = new List<TSource>(source);
            return For(0, items.Count, i => body(items[i]));
        }
        public static ParallelLoopResult ForEach<TSource>(IEnumerable<TSource> source, ParallelOptions parallelOptions, Action<TSource> body)
        {
            var items = new List<TSource>(source);
            return For(0, items.Count, parallelOptions, i => body(items[i]));
        }
        public static void Invoke(params Action[] actions) => For(0, actions.Length, i => actions[i]());
    }
}

namespace System.Threading
{
    using System.Threading.Tasks;


    /// <summary>Background workers, one per core, taking work from one queue (section 4.5).</summary>
    public static class ThreadPool
    {
        static Collections.Generic.Queue<Action> s_queue;
        static int s_started, s_threads;

        internal static void Queue(Action a)
        {
            a = ExecutionContext.Flow(a);
            Rt.RuntimeLock();
            (s_queue ??= new Collections.Generic.Queue<Action>()).Enqueue(a);
            bool start = s_started == 0;
            s_started = 1;
            Rt.RuntimeUnlock();
            if (start)
            {
                int n = Environment.ProcessorCount;
                if (n < 2) n = 2;
                for (int i = 0; i < n; i++)
                {
                    var t = new Thread(Loop) { IsBackground = true };
                    t._pool = true;
                    t.Start();
                }
                s_threads = n;
            }
            Threads.WakeAll();
        }

        static void Loop()
        {
            while (true)
            {
                Rt.RuntimeLock();
                Action a = s_queue.Count > 0 ? s_queue.Dequeue() : null;
                int g = Atomic.Load32(ref Threads.s_generation);
                Rt.RuntimeUnlock();
                if (a != null) { a(); a = null; continue; }
                Threads.WaitGeneration(g);
            }
        }

        public static bool QueueUserWorkItem(WaitCallback callBack) => QueueUserWorkItem(callBack, null);
        public static bool QueueUserWorkItem(WaitCallback callBack, object state)
        {
            if (callBack == null) throw new ArgumentNullException("callBack");
            Queue(() => callBack(state));
            return true;
        }
        public static int ThreadCount => s_threads;
        public static long PendingWorkItemCount { get { Rt.RuntimeLock(); long n = s_queue == null ? 0 : s_queue.Count; Rt.RuntimeUnlock(); return n; } }
    }

    /// <summary>A value per thread, kept by thread id under the runtime lock.</summary>
    public class ThreadLocal<T> : IDisposable
    {
        readonly Func<T> _factory;
        readonly Collections.Generic.Dictionary<int, T> _values = new Collections.Generic.Dictionary<int, T>();
        public ThreadLocal() { }
        public ThreadLocal(Func<T> valueFactory) { _factory = valueFactory; }
        public ThreadLocal(bool trackAllValues) { }
        public ThreadLocal(Func<T> valueFactory, bool trackAllValues) { _factory = valueFactory; }
        public T Value
        {
            get
            {
                int id = Rt.ThreadId();
                Rt.RuntimeLock();
                bool has = _values.TryGetValue(id, out var v);
                Rt.RuntimeUnlock();
                if (has) return v;
                v = _factory != null ? _factory() : default;
                Rt.RuntimeLock();
                _values[id] = v;
                Rt.RuntimeUnlock();
                return v;
            }
            set { int id = Rt.ThreadId(); Rt.RuntimeLock(); _values[id] = value; Rt.RuntimeUnlock(); }
        }
        public bool IsValueCreated { get { Rt.RuntimeLock(); bool r = _values.ContainsKey(Rt.ThreadId()); Rt.RuntimeUnlock(); return r; } }
        public Collections.Generic.IList<T> Values
        {
            get
            {
                var l = new Collections.Generic.List<T>();
                Rt.RuntimeLock();
                foreach (var kv in _values) l.Add(kv.Value);
                Rt.RuntimeUnlock();
                return l;
            }
        }
        public void Dispose() { Rt.RuntimeLock(); _values.Clear(); Rt.RuntimeUnlock(); }
    }

    public class SemaphoreSlim : IDisposable
    {
        int _count;
        readonly int _max;
        public SemaphoreSlim(int initialCount) : this(initialCount, int.MaxValue) { }
        public SemaphoreSlim(int initialCount, int maxCount)
        {
            if (initialCount < 0 || initialCount > maxCount) throw new ArgumentOutOfRangeException("initialCount");
            _count = initialCount; _max = maxCount;
        }
        public int CurrentCount => Atomic.Load32(ref _count);
        bool TryTake()
        {
            while (true)
            {
                int c = Atomic.Load32(ref _count);
                if (c == 0) return false;
                if (Atomic.CompareExchange32(ref _count, c, c - 1) == c) return true;
            }
        }
        public void Wait()
        {
            while (true)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                if (TryTake()) return;
                if (!Scheduler.Step()) Threads.WaitGeneration(g);
            }
        }
        public bool Wait(int millisecondsTimeout)
        {
            long end = millisecondsTimeout < 0 ? long.MaxValue : Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            while (true)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                if (TryTake()) return true;
                if (Engine.MonotonicNs() >= end) return false;
                if (!Scheduler.Step()) Threads.WaitGeneration(g);
            }
        }
        public Task WaitAsync()
        {
            if (TryTake()) return Task.CompletedTask;
            return Task.Run(() => Wait());
        }
        public int Release() => Release(1);
        public int Release(int releaseCount)
        {
            if (releaseCount < 1) throw new ArgumentOutOfRangeException("releaseCount");
            while (true)
            {
                int c = Atomic.Load32(ref _count);
                if (c + releaseCount > _max) throw new SemaphoreFullException();
                if (Atomic.CompareExchange32(ref _count, c, c + releaseCount) == c) { Threads.WakeAll(); return c; }
            }
        }
        public void Dispose() { }
    }

    public class SemaphoreFullException : SystemException
    {
        public SemaphoreFullException() : base("Adding the specified count to the semaphore would cause it to exceed its maximum count.") { }
    }

    public class ManualResetEventSlim : IDisposable
    {
        int _set;
        public ManualResetEventSlim() { }
        public ManualResetEventSlim(bool initialState) { _set = initialState ? 1 : 0; }
        public bool IsSet => Atomic.Load32(ref _set) != 0;
        public void Set() { Atomic.Store32(ref _set, 1); Threads.WakeAll(); }
        public void Reset() { Atomic.Store32(ref _set, 0); }
        public void Wait()
        {
            while (true)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                if (IsSet) return;
                if (!Scheduler.Step()) Threads.WaitGeneration(g);
            }
        }
        public void Dispose() { }
    }

    public class CountdownEvent : IDisposable
    {
        int _count;
        public CountdownEvent(int initialCount) { _count = initialCount; }
        public int CurrentCount => Atomic.Load32(ref _count);
        public bool IsSet => CurrentCount <= 0;
        public bool Signal() { bool done = Atomic.Add32(ref _count, -1) - 1 <= 0; if (done) Threads.WakeAll(); return done; }
        public void AddCount() => Atomic.Add32(ref _count, 1);
        public void Wait()
        {
            while (true)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                if (IsSet) return;
                if (!Scheduler.Step()) Threads.WaitGeneration(g);
            }
        }
        public void Dispose() { }
    }

    public delegate void ThreadStart();
    public delegate void ParameterizedThreadStart(object obj);

    /// <summary>A real thread: a worker instance on the shared memory (section 4.5).</summary>
    public sealed class Thread
    {
        readonly ThreadStart _start;
        readonly ParameterizedThreadStart _pstart;
        object _arg;
        internal int _id, _state, _stack, _tls; // state: 0 unstarted, 1 running, 2 finished
        internal bool _pool;
        public string Name { get; set; }
        public bool IsBackground { get; set; }

        [ThreadStatic] static Thread t_current;

        public Thread(ThreadStart start) { if (start == null) throw new ArgumentNullException("start"); _start = start; _id = Threads.NewId(); }
        public Thread(ParameterizedThreadStart start) { if (start == null) throw new ArgumentNullException("start"); _pstart = start; _id = Threads.NewId(); }
        public Thread(ThreadStart start, int maxStackSize) : this(start) { }
        Thread(int id) { _id = id; _state = 1; }

        public static Thread CurrentThread => t_current ??= new Thread(Rt.ThreadId());
        internal static void SetCurrent(Thread t) { t_current = t; }
        public int ManagedThreadId => _id;
        public bool IsAlive => Atomic.Load32(ref _state) == 1;
        public bool IsThreadPoolThread => _pool;

        public void Start()
        {
            if (Atomic.CompareExchange32(ref _state, 0, 1) != 0) throw new ThreadStateException("Thread is running or terminated; it cannot restart.");
            if (ExecutionContext.IsFlowing) _flow = ExecutionContext.Flow(() => { if (_start != null) _start(); else _pstart(_arg); });
            Threads.SpawnCore(this);
        }
        public void Start(object parameter)
        {
            if (_pstart == null) throw new InvalidOperationException("The thread was created with a ThreadStart delegate that does not accept a parameter.");
            _arg = parameter;
            Start();
        }

        Action _flow;
        internal void Execute()
        {
            if (_flow != null) { var f = _flow; _flow = null; f(); }
            else if (_start != null) _start(); else _pstart(_arg);
            _arg = null;
        }

        public void Join()
        {
            if (Atomic.Load32(ref _state) == 0) throw new ThreadStateException("Thread has not been started.");
            while (Atomic.Load32(ref _state) != 2)
            {
                if (Rt.ThreadId() == 1) continue; // the engine thread never blocks
                Atomic.Wait32(ref _state, 1, -1);
            }
        }
        public bool Join(int millisecondsTimeout)
        {
            if (millisecondsTimeout == -1) { Join(); return true; }
            long end = Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            while (Atomic.Load32(ref _state) != 2)
            {
                long left = end - Engine.MonotonicNs();
                if (left <= 0) return false;
                if (Rt.ThreadId() != 1) Atomic.Wait32(ref _state, 1, left);
            }
            return true;
        }

        public static void Sleep(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1) throw new ArgumentOutOfRangeException("millisecondsTimeout");
            long end = Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            int dummy = 0;
            while (true)
            {
                long left = end - Engine.MonotonicNs();
                if (left <= 0) return;
                if (Rt.ThreadId() != 1) Atomic.Wait32(ref dummy, 0, left);
            }
        }
        public static void Sleep(TimeSpan timeout) => Sleep((int)timeout.TotalMilliseconds);
        public static bool Yield() => true;
        public static void SpinWait(int iterations) { }
        public static void MemoryBarrier() => Atomic.Fence();
    }

    public class ThreadStateException : SystemException
    {
        public ThreadStateException() : base("Thread was in an invalid state for the operation being executed.") { }
        public ThreadStateException(string message) : base(message) { }
    }

    public class SynchronizationLockException : SystemException
    {
        public SynchronizationLockException() : base("Object synchronization method was called from an unsynchronized block of code.") { }
        public SynchronizationLockException(string message) : base(message) { }
    }

    /// <summary>`lock` compiles to Monitor: a reentrant lock per object, kept in a table by address under the runtime lock.</summary>
    public static class Monitor
    {
        sealed class Entry { public int Owner, Depth, Waiters, Signals; }
        static Collections.Generic.Dictionary<int, Entry> s_locks;

        static Entry Get(object obj, bool create)
        {
            s_locks ??= new Collections.Generic.Dictionary<int, Entry>();
            int key = Rt.AddressOf(obj);
            if (s_locks.TryGetValue(key, out var e)) return e;
            if (!create) return null;
            e = new Entry();
            s_locks[key] = e;
            return e;
        }

        static void Drop(object obj, Entry e) { if (e.Depth == 0 && e.Waiters == 0) s_locks.Remove(Rt.AddressOf(obj)); }

        public static void Enter(object obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            int me = Rt.ThreadId();
            while (true)
            {
                Rt.RuntimeLock();
                var e = Get(obj, true);
                if (e.Owner == 0 || e.Owner == me) { e.Owner = me; e.Depth++; Rt.RuntimeUnlock(); return; }
                int g = Atomic.Load32(ref Threads.s_generation);
                Rt.RuntimeUnlock();
                Threads.WaitGeneration(g);
            }
        }
        public static void Enter(object obj, ref bool lockTaken) { Enter(obj); lockTaken = true; }
        public static bool TryEnter(object obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            int me = Rt.ThreadId();
            Rt.RuntimeLock();
            var e = Get(obj, true);
            bool ok = e.Owner == 0 || e.Owner == me;
            if (ok) { e.Owner = me; e.Depth++; } else Drop(obj, e);
            Rt.RuntimeUnlock();
            return ok;
        }
        public static void TryEnter(object obj, ref bool lockTaken) { lockTaken = TryEnter(obj); }
        public static bool IsEntered(object obj)
        {
            Rt.RuntimeLock();
            var e = Get(obj, false);
            bool r = e != null && e.Owner == Rt.ThreadId();
            Rt.RuntimeUnlock();
            return r;
        }

        public static void Exit(object obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            Rt.RuntimeLock();
            var e = Get(obj, false);
            if (e == null || e.Owner != Rt.ThreadId()) { Rt.RuntimeUnlock(); throw new SynchronizationLockException(); }
            if (--e.Depth == 0) { e.Owner = 0; Drop(obj, e); }
            Rt.RuntimeUnlock();
            Threads.WakeAll();
        }

        public static bool Wait(object obj) => Wait(obj, -1);
        public static bool Wait(object obj, int millisecondsTimeout)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            int me = Rt.ThreadId();
            long end = millisecondsTimeout < 0 ? long.MaxValue : Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            Rt.RuntimeLock();
            var e = Get(obj, false);
            if (e == null || e.Owner != me) { Rt.RuntimeUnlock(); throw new SynchronizationLockException(); }
            int depth = e.Depth;
            e.Owner = 0; e.Depth = 0; e.Waiters++;
            bool signaled = false;
            while (true)
            {
                if (e.Signals > 0) { e.Signals--; signaled = true; }
                if (signaled || Engine.MonotonicNs() >= end)
                {
                    if (e.Owner == 0) { e.Waiters--; e.Owner = me; e.Depth = depth; Rt.RuntimeUnlock(); return signaled; }
                }
                int g = Atomic.Load32(ref Threads.s_generation);
                Rt.RuntimeUnlock();
                Threads.WakeAll();
                Threads.WaitGeneration(g);
                Rt.RuntimeLock();
            }
        }
        public static void Pulse(object obj)
        {
            Rt.RuntimeLock();
            var e = Get(obj, false);
            if (e == null || e.Owner != Rt.ThreadId()) { Rt.RuntimeUnlock(); throw new SynchronizationLockException(); }
            if (e.Signals < e.Waiters) e.Signals++;
            Rt.RuntimeUnlock();
            Threads.WakeAll();
        }
        public static void PulseAll(object obj)
        {
            Rt.RuntimeLock();
            var e = Get(obj, false);
            if (e == null || e.Owner != Rt.ThreadId()) { Rt.RuntimeUnlock(); throw new SynchronizationLockException(); }
            e.Signals = e.Waiters;
            Rt.RuntimeUnlock();
            Threads.WakeAll();
        }
    }

    public static class Interlocked
    {
        public static int Increment(ref int location) => Atomic.Add32(ref location, 1) + 1;
        public static long Increment(ref long location) => Atomic.Add64(ref location, 1) + 1;
        public static int Decrement(ref int location) => Atomic.Add32(ref location, -1) - 1;
        public static long Decrement(ref long location) => Atomic.Add64(ref location, -1) - 1;
        public static int Add(ref int location, int value) => Atomic.Add32(ref location, value) + value;
        public static long Add(ref long location, long value) => Atomic.Add64(ref location, value) + value;
        public static int Exchange(ref int location, int value) => Atomic.Exchange32(ref location, value);
        public static long Exchange(ref long location, long value) => Atomic.Exchange64(ref location, value);
        public static int CompareExchange(ref int location, int value, int comparand) => Atomic.CompareExchange32(ref location, comparand, value);
        public static long CompareExchange(ref long location, long value, long comparand) => Atomic.CompareExchange64(ref location, comparand, value);
        public static long Read(ref long location) => Atomic.Load64(ref location);
        public static void MemoryBarrier() => Atomic.Fence();
    }

    public static class Volatile
    {
        public static int Read(ref int location) => Atomic.Load32(ref location);
        public static long Read(ref long location) => Atomic.Load64(ref location);
        public static bool Read(ref bool location) { bool v = location; Atomic.Fence(); return v; }
        public static T Read<T>(ref T location) where T : class { T v = location; Atomic.Fence(); return v; }
        public static void Write(ref int location, int value) => Atomic.Store32(ref location, value);
        public static void Write(ref long location, long value) => Atomic.Store64(ref location, value);
        public static void Write(ref bool location, bool value) { Atomic.Fence(); location = value; }
    }

}

namespace System
{
    public class AggregateException : Exception
    {
        readonly Exception[] _inner;
        public AggregateException(params Exception[] innerExceptions) : base(Compose(innerExceptions), innerExceptions.Length > 0 ? innerExceptions[0] : null) { _inner = innerExceptions; }
        public AggregateException(string message, params Exception[] innerExceptions) : base(message, innerExceptions.Length > 0 ? innerExceptions[0] : null) { _inner = innerExceptions; }
        static string Compose(Exception[] inner)
        {
            var s = "One or more errors occurred.";
            foreach (var e in inner) if (e != null) s = s + " (" + e.Message + ")";
            return s;
        }
        public System.Collections.ObjectModel.ReadOnlyCollection<Exception> InnerExceptions => new System.Collections.ObjectModel.ReadOnlyCollection<Exception>(_inner);
    }

}

namespace System.Threading.Tasks
{
    public class TaskCanceledException : OperationCanceledException
    {
        public TaskCanceledException() : base("A task was canceled.") { }
        public TaskCanceledException(string message) : base(message) { }
    }
}

namespace System.Collections.ObjectModel
{
    public class ReadOnlyCollection<T> : System.Collections.Generic.IReadOnlyList<T>
    {
        readonly T[] _items;
        public ReadOnlyCollection(T[] items) { _items = items; }
        public T this[int index] => _items[index];
        public int Count => _items.Length;
        public System.Collections.Generic.IEnumerator<T> GetEnumerator() => ((System.Collections.Generic.IEnumerable<T>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace Cs2Wasm.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    /// <summary>Work posted for later, and timers (Task.Delay), run when something waits or the engine pumps.</summary>
    public static class Scheduler
    {
        static Queue<Action> s_work;
        static List<Timer> s_timers;
        static int s_seq;

        sealed class Timer { public long Due; public int Seq; public Task Task; }

        /// <summary>Work for the engine thread (the default SynchronizationContext posts here).</summary>
        public static void Post(Action a)
        {
            Rt.RuntimeLock();
            (s_work ??= new Queue<Action>()).Enqueue(a);
            Rt.RuntimeUnlock();
            if (Threads.Spawned) Threads.WakeAll();
        }

        internal static void AddTimer(Task t, int ms)
        {
            long due = Engine.MonotonicNs() / 1000000 + ms;
            Rt.RuntimeLock();
            (s_timers ??= new List<Timer>()).Add(new Timer { Due = due, Seq = ++s_seq, Task = t });
            Rt.RuntimeUnlock();
        }

        static Action TakeWork()
        {
            if (Rt.ThreadId() != 1) return null;
            Rt.RuntimeLock();
            Action a = s_work != null && s_work.Count > 0 ? s_work.Dequeue() : null;
            Rt.RuntimeUnlock();
            return a;
        }

        /// <summary>The earliest timer, if it is due by `now` (or any, when `now` is long.MaxValue).</summary>
        static Task TakeTimer(long now)
        {
            Rt.RuntimeLock();
            Task r = null;
            if (s_timers != null && s_timers.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < s_timers.Count; i++)
                    if (s_timers[i].Due < s_timers[best].Due || (s_timers[i].Due == s_timers[best].Due && s_timers[i].Seq < s_timers[best].Seq)) best = i;
                if (s_timers[best].Due <= now) { r = s_timers[best].Task; s_timers.RemoveAt(best); }
            }
            Rt.RuntimeUnlock();
            return r;
        }

        /// <summary>Runs one posted item (engine thread) or a timer; false if nothing could run now. Without other
        /// threads, the earliest timer fires at once (virtual time); with them, timers fire when due.</summary>
        public static bool Step()
        {
            var a = TakeWork();
            if (a != null) { a(); return true; }
            var t = TakeTimer(Threads.Spawned ? Engine.MonotonicNs() / 1000000 : long.MaxValue);
            if (t != null) { t.TrySetResultCore(); return true; }
            return false;
        }

        public static bool RunOne() => Step();

        internal static void RunUntil(Task t) => RunUntil(t, long.MaxValue);

        /// <summary>Waits for t, running what this thread may run meanwhile; false at the deadline (monotonic ns).</summary>
        internal static bool RunUntil(Task t, long deadlineNs)
        {
            while (!t.IsCompleted)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                if (t.IsCompleted) break;
                if (Step()) continue;
                if (!Threads.Spawned) throw new InvalidOperationException("The task cannot complete: nothing left to run on this thread.");
                if (deadlineNs != long.MaxValue && Engine.MonotonicNs() >= deadlineNs) return false;
                Threads.WaitGeneration(g);
            }
            return true;
        }

        /// <summary>Runs posted work and the timers that are due (the engine calls this once per frame).</summary>
        public static void RunPending()
        {
            Action a;
            while ((a = TakeWork()) != null) a();
            long now = Engine.MonotonicNs() / 1000000;
            Task t;
            while ((t = TakeTimer(now)) != null)
            {
                t.TrySetResultCore();
                while ((a = TakeWork()) != null) a();
            }
        }
    }
}

namespace System
{
    public interface IAsyncDisposable { System.Threading.Tasks.ValueTask DisposeAsync(); }
}

namespace System.Collections.Generic
{
    using System.Threading;
    using System.Threading.Tasks;
    public interface IAsyncEnumerable<out T> { IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default); }
    public interface IAsyncEnumerator<out T> : IAsyncDisposable { T Current { get; } ValueTask<bool> MoveNextAsync(); }
}

namespace System.Threading.Tasks
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Runtime.CompilerServices;

    public static class TaskAsyncEnumerableExtensions
    {
        public static ConfiguredCancelableAsyncEnumerable<T> ConfigureAwait<T>(this IAsyncEnumerable<T> source, bool continueOnCapturedContext) => new ConfiguredCancelableAsyncEnumerable<T>(source, default);
        public static ConfiguredCancelableAsyncEnumerable<T> WithCancellation<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken) => new ConfiguredCancelableAsyncEnumerable<T>(source, cancellationToken);
        public static ConfiguredAsyncDisposable ConfigureAwait(this IAsyncDisposable source, bool continueOnCapturedContext) => new ConfiguredAsyncDisposable(source);
    }
}

namespace System.Runtime.CompilerServices
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public readonly struct ConfiguredCancelableAsyncEnumerable<T>
    {
        readonly IAsyncEnumerable<T> _source;
        readonly CancellationToken _token;
        internal ConfiguredCancelableAsyncEnumerable(IAsyncEnumerable<T> source, CancellationToken token) { _source = source; _token = token; }
        public ConfiguredCancelableAsyncEnumerable<T> ConfigureAwait(bool continueOnCapturedContext) => this;
        public ConfiguredCancelableAsyncEnumerable<T> WithCancellation(CancellationToken cancellationToken) => new ConfiguredCancelableAsyncEnumerable<T>(_source, cancellationToken);
        public Enumerator GetAsyncEnumerator() => new Enumerator(_source.GetAsyncEnumerator(_token));
        public readonly struct Enumerator
        {
            readonly IAsyncEnumerator<T> _e;
            internal Enumerator(IAsyncEnumerator<T> e) { _e = e; }
            public T Current => _e.Current;
            public ConfiguredValueTaskAwaitable<bool> MoveNextAsync() => _e.MoveNextAsync().ConfigureAwait(true);
            public ConfiguredValueTaskAwaitable DisposeAsync() => _e.DisposeAsync().ConfigureAwait(true);
        }
    }

    public readonly struct ConfiguredAsyncDisposable
    {
        readonly IAsyncDisposable _source;
        internal ConfiguredAsyncDisposable(IAsyncDisposable source) { _source = source; }
        public ConfiguredValueTaskAwaitable DisposeAsync() => _source.DisposeAsync().ConfigureAwait(true);
    }

    [AttributeUsage(AttributeTargets.Method)] public sealed class AsyncIteratorStateMachineAttribute : Attribute { public AsyncIteratorStateMachineAttribute(Type t) { } }
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class EnumeratorCancellationAttribute : Attribute { }
}

namespace Cs2Wasm.Runtime
{
    using System.Threading.Tasks;
    public static class AsyncIter
    {
        public static ValueTask<bool> Wrap(Task<bool> t) => new ValueTask<bool>(t);
        public static ValueTask WrapDispose(Task<bool> t) => new ValueTask(t);
    }
}

namespace System.Threading
{
    using Cs2Wasm.Runtime;

    /// <summary>AsyncLocal values: an immutable chain per thread, captured where work is handed on (continuations,
    /// the pool, Task.Run, Thread.Start) and restored around each async method's synchronous part, as in .NET.</summary>
    public sealed class ExecutionContext
    {
        internal sealed class Node
        {
            public readonly object Key, Value; public readonly Node Next;
            public Node(object k, object v, Node n) { Key = k; Value = v; Next = n; }
        }

        [ThreadStatic] internal static Node t_current;
        internal static bool s_active;
        readonly Node _node;
        ExecutionContext(Node n) { _node = n; }

        internal static bool IsFlowing => s_active;

        public static ExecutionContext Capture() => new ExecutionContext(t_current);
        public static void Run(ExecutionContext executionContext, ContextCallback callback, object state)
        {
            var saved = t_current;
            t_current = executionContext._node;
            try { callback(state); } finally { t_current = saved; }
        }
        public static bool IsFlowSuppressed() => false;

        internal static Action Flow(Action a)
        {
            if (!s_active) return a;
            var captured = t_current;
            return () =>
            {
                var saved = t_current;
                t_current = captured;
                try { a(); } finally { t_current = saved; }
            };
        }

        internal static object Get(object key)
        {
            for (var n = t_current; n != null; n = n.Next) if (n.Key == key) return n.Value;
            return null;
        }
        internal static bool Has(object key)
        {
            for (var n = t_current; n != null; n = n.Next) if (n.Key == key) return true;
            return false;
        }
        internal static void Set(object key, object value) { t_current = new Node(key, value, t_current); }

        /// <summary>The kickoff of an async method keeps the caller's context: what the method sets stays inside it.</summary>
        public static object SaveContext() => t_current;
        public static void RestoreContext(object saved) { t_current = (Node)saved; }
    }

    public delegate void ContextCallback(object state);

    public sealed class AsyncLocal<T>
    {
        public AsyncLocal() { ExecutionContext.s_active = true; }
        public T Value
        {
            get { object v = ExecutionContext.Get(this); return v == null ? default : (T)v; }
            set { ExecutionContext.Set(this, value); }
        }
    }
}
