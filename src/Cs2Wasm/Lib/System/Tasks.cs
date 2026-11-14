// Tasks and async support. Until threads exist (section 4.5), everything runs on the calling thread:
// continuations run when the awaited task completes (or are posted to the captured SynchronizationContext),
// and blocking waits run pending work (posted callbacks, due timers) until the task completes.
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
        public bool IsCompleted => _state != 0;
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
        public bool Wait(int millisecondsTimeout) { Wait(); return true; }
        public void Dispose() { }

        internal bool TrySetResultCore()
        {
            if (_state != 0) return false;
            _state = 1;
            RunContinuations();
            return true;
        }

        internal bool TrySetExceptionCore(object ex)
        {
            if (_state != 0) return false;
            if (ex is OperationCanceledException) { _state = 3; _exception = (Exception)ex; }
            else { _state = 2; _exception = (Exception)ex; }
            RunContinuations();
            return true;
        }

        internal bool TrySetCanceledCore()
        {
            if (_state != 0) return false;
            _state = 3;
            _exception = new TaskCanceledException();
            RunContinuations();
            return true;
        }

        internal void AddContinuation(Action action, bool captureContext)
        {
            var ctx = captureContext ? SynchronizationContext.Current : null;
            var c = new Continuation(action, ctx);
            if (_state != 0) { c.Run(); return; }
            (_continuations ??= new List<Continuation>()).Add(c);
        }

        void RunContinuations()
        {
            var list = _continuations;
            _continuations = null;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) list[i].Run();
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
            Scheduler.Post(() =>
            {
                try { action(); t.TrySetResultCore(); }
                catch (Exception e) { t.TrySetExceptionCore(e); }
            });
            return t;
        }
        public static Task<TResult> Run<TResult>(Func<TResult> function)
        {
            var t = new Task<TResult>();
            Scheduler.Post(() =>
            {
                try { t.TrySetResultT(function()); }
                catch (Exception e) { t.TrySetExceptionCore(e); }
            });
            return t;
        }
        public static Task Run(Func<Task> function)
        {
            var t = new Task();
            Scheduler.Post(() =>
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
            Scheduler.Post(() =>
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
                    if (--remaining == 0)
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
                    if (--remaining == 0)
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
            if (_state != 0) return false;
            _result = result;
            return TrySetResultCore();
        }
        public new TaskAwaiter<TResult> GetAwaiter() => new TaskAwaiter<TResult>(this);
        public new ConfiguredTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredTaskAwaitable<TResult>(this, continueOnCapturedContext);
    }

    internal sealed class Continuation
    {
        readonly Action _action;
        readonly SynchronizationContext _context;
        public Continuation(Action a, SynchronizationContext c) { _action = a; _context = c; }
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
        public static ValueTask<TResult> FromResult<TResult>(TResult result) => new ValueTask<TResult>(result);
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
                var ctx = SynchronizationContext.Current;
                if (ctx != null && !ctx.IsDefault) ctx.Post(s => ((Action)s)(), c);
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
        static SynchronizationContext s_current;
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
        bool _canceled;
        public bool IsCancellationRequested => _canceled;
        public CancellationToken Token => new CancellationToken(this);
        public void Cancel() { _canceled = true; }
        public void Dispose() { }
    }

    /// <summary>`lock` compiles to Monitor; with one thread, entering never waits.</summary>
    public static class Monitor
    {
        public static void Enter(object obj) { if (obj == null) throw new ArgumentNullException("obj"); }
        public static void Enter(object obj, ref bool lockTaken) { if (obj == null) throw new ArgumentNullException("obj"); lockTaken = true; }
        public static void Exit(object obj) { }
        public static bool TryEnter(object obj) => true;
        public static void Wait(object obj) { }
        public static void Pulse(object obj) { }
        public static void PulseAll(object obj) { }
    }

    public static class Interlocked
    {
        public static int Increment(ref int location) => ++location;
        public static long Increment(ref long location) => ++location;
        public static int Decrement(ref int location) => --location;
        public static long Decrement(ref long location) => --location;
        public static int Add(ref int location, int value) => location += value;
        public static long Add(ref long location, long value) => location += value;
        public static int Exchange(ref int location, int value) { int o = location; location = value; return o; }
        public static int CompareExchange(ref int location, int value, int comparand) { int o = location; if (o == comparand) location = value; return o; }
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

    public readonly struct TimeSpan
    {
        readonly long _ticks;
        public TimeSpan(long ticks) { _ticks = ticks; }
        public long Ticks => _ticks;
        public double TotalMilliseconds => _ticks / 10000.0;
        public double TotalSeconds => _ticks / 10000000.0;
        public int Milliseconds => (int)(_ticks / 10000 % 1000);
        public int Seconds => (int)(_ticks / 10000000 % 60);
        public static TimeSpan FromMilliseconds(double ms) => new TimeSpan((long)(ms * 10000));
        public static TimeSpan FromSeconds(double s) => new TimeSpan((long)(s * 10000000));
        public static readonly TimeSpan Zero = new TimeSpan(0);
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

        public static void Post(Action a) => (s_work ??= new Queue<Action>()).Enqueue(a);

        internal static void AddTimer(Task t, int ms)
        {
            long due = Engine.MonotonicNs() / 1000000 + ms;
            (s_timers ??= new List<Timer>()).Add(new Timer { Due = due, Seq = ++s_seq, Task = t });
        }

        /// <summary>Runs one posted item or the earliest timer; false if nothing is pending.</summary>
        public static bool RunOne()
        {
            if (s_work != null && s_work.Count > 0) { s_work.Dequeue()(); return true; }
            if (s_timers != null && s_timers.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < s_timers.Count; i++)
                    if (s_timers[i].Due < s_timers[best].Due || (s_timers[i].Due == s_timers[best].Due && s_timers[i].Seq < s_timers[best].Seq)) best = i;
                var t = s_timers[best];
                s_timers.RemoveAt(best);
                t.Task.TrySetResultCore();
                return true;
            }
            return false;
        }

        internal static void RunUntil(Task t)
        {
            while (!t.IsCompleted)
                if (!RunOne()) throw new InvalidOperationException("The task cannot complete: nothing left to run on this thread.");
        }

        /// <summary>Runs posted work and the timers that are due (the engine calls this once per frame).</summary>
        public static void RunPending()
        {
            while (s_work != null && s_work.Count > 0) s_work.Dequeue()();
            if (s_timers == null) return;
            long now = Engine.MonotonicNs() / 1000000;
            while (true)
            {
                int best = -1;
                for (int i = 0; i < s_timers.Count; i++)
                    if (s_timers[i].Due <= now && (best < 0 || s_timers[i].Due < s_timers[best].Due || (s_timers[i].Due == s_timers[best].Due && s_timers[i].Seq < s_timers[best].Seq))) best = i;
                if (best < 0) break;
                var t = s_timers[best];
                s_timers.RemoveAt(best);
                t.Task.TrySetResultCore();
                while (s_work != null && s_work.Count > 0) s_work.Dequeue()();
            }
        }
    }
}
