// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's runtime half of Thread, over cs2wasm's threads (section 4.5): a thread is a worker instance of the
// module on the shared memory, started by the engine's thread_spawn import (Cs2Wasm.Runtime.Threads). Mono's internal
// calls are implemented here; the members the shared Thread.cs uses are Mono's.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cs2Wasm.Runtime;

namespace System.Threading
{
    public partial class Thread
    {
        // cs2wasm's state: the worker's stack and thread-local block, its id, and _run (0 unstarted, 1 running,
        // 2 finished) that joiners wait on.
        internal int _stack, _tls, _run;
        internal int managed_id;
        private ThreadState _threadState;
        private int priority = (int)ThreadPriority.Normal;
        internal bool threadpool_thread;
        internal bool external_eventloop;

        private string? _name;
        private StartHelper? _startHelper;
        internal ExecutionContext? _executionContext;
        internal SynchronizationContext? _synchronizationContext;
#if TARGET_UNIX || TARGET_BROWSER || TARGET_WASI
        internal WaitSubsystem.ThreadWaitInfo? _waitInfo;
#endif

        private bool _mayNeedResetForThreadPool;

        private Thread()
        {
            Initialize();
        }

        public bool IsAlive
        {
            get
            {
                ThreadState state = GetState(this);
                return (state & (ThreadState.Unstarted | ThreadState.Stopped | ThreadState.Aborted)) == 0;
            }
        }

        public bool IsBackground
        {
            get
            {
                ThreadState state = ValidateThreadState();
                return (state & ThreadState.Background) != 0;
            }
            set
            {
                ValidateThreadState();
                if (value)
                {
                    SetState(this, ThreadState.Background);
                }
                else
                {
                    ClrState(this, ThreadState.Background);
                    _mayNeedResetForThreadPool = true;
                }
            }
        }

        public bool IsThreadPoolThread
        {
            get
            {
                ValidateThreadState();
                return threadpool_thread;
            }
            internal set
            {
                threadpool_thread = value;
            }
        }

        public int ManagedThreadId => managed_id;

        internal static int OptimalMaxSpinWaitsPerSpinIteration => 7;

#if TARGET_UNIX || TARGET_BROWSER || TARGET_WASI
        internal WaitSubsystem.ThreadWaitInfo WaitInfo
        {
            get
            {
                return Volatile.Read(ref _waitInfo) ?? AllocateWaitInfo();

                WaitSubsystem.ThreadWaitInfo AllocateWaitInfo()
                {
                    Interlocked.CompareExchange(ref _waitInfo, new WaitSubsystem.ThreadWaitInfo(this), null!);
                    return _waitInfo;
                }
            }
        }
#endif

        public ThreadPriority Priority
        {
            get
            {
                ValidateThreadState();
                return (ThreadPriority)priority;
            }
            set
            {
                priority = (int)value;
                if (value != ThreadPriority.Normal)
                {
                    _mayNeedResetForThreadPool = true;
                }
            }
        }

        public ThreadState ThreadState => GetState(this);

        public ApartmentState GetApartmentState() => ApartmentState.Unknown;

        public void DisableComObjectEagerCleanup()
        {
            // no-op
        }

        public void Interrupt()
        {
#if TARGET_UNIX || TARGET_BROWSER || TARGET_WASI // TODO: https://github.com/dotnet/runtime/issues/49521
            WaitSubsystem.Interrupt(this);
#endif
        }

        public bool Join(int millisecondsTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
            return JoinInternal(this, millisecondsTimeout);
        }

        private void Initialize()
        {
            managed_id = Threads.NewId();
            _threadState = ThreadState.Unstarted;
        }

        public static void SpinWait(int iterations)
        {
        }

        // Called from the worker (Cs2Wasm.Runtime.Threads.Run)
        internal void StartCallback()
        {
            StartHelper? startHelper = _startHelper;
            Debug.Assert(startHelper != null);
            _startHelper = null;

            startHelper.Run();
        }

        internal static void ThrowThreadStartException(Exception ex) => throw new ThreadStartException(ex);

        private void StartCore()
        {
            if (Atomic.CompareExchange32(ref _run, 0, 1) != 0) throw new ThreadStateException(SR.ThreadState_AlreadyStarted);
            ClrState(this, ThreadState.Unstarted);
            Threads.SpawnCore(this);
        }

        private void ThreadNameChanged(string? value)
        {
        }

        public static bool Yield()
        {
            Threads.SafePoint();
            return true;
        }

        private static bool SetApartmentStateUnchecked(ApartmentState state, bool throwOnError)
        {
            if (state != ApartmentState.Unknown)
            {
                if (throwOnError)
                {
                    throw new PlatformNotSupportedException(SR.PlatformNotSupported_ComInterop);
                }

                return false;
            }

            return true;
        }

        private ThreadState ValidateThreadState()
        {
            ThreadState state = GetState(this);
            if ((state & ThreadState.Stopped) != 0)
                throw new ThreadStateException(SR.ThreadState_Dead_State);
            return state;
        }

        internal void SetWaitSleepJoinState()
        {
            SetState(this, ThreadState.WaitSleepJoin);
        }

        internal void ClearWaitSleepJoinState()
        {
            ClrState(this, ThreadState.WaitSleepJoin);
        }

        /// <summary>The worker finished: what Mono's runtime does when a thread exits.</summary>
        internal static void OnThreadExiting(Thread thread)
        {
#if TARGET_UNIX || TARGET_BROWSER || TARGET_WASI
            thread._waitInfo?.OnThreadExiting();
#endif
            SetState(thread, ThreadState.Stopped);
            Atomic.Store32(ref thread._run, 2);
            Atomic.Notify(ref thread._run, -1);
        }

        private static ulong GetCurrentOSThreadId() => (ulong)Rt.ThreadId();

        /// <summary>The thread object of a thread that was not started by Thread.Start (the engine's, at first use).</summary>
        private Thread(int id)
        {
            managed_id = id;
            _run = 1;
            _threadState = ThreadState.Running;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Thread InitializeCurrentThread()
        {
            t_currentThread = new Thread(Rt.ThreadId());
            return t_currentThread;
        }

        /// <summary>The worker side: the thread becomes current there (Cs2Wasm.Runtime.Threads.Run).</summary>
        internal static void SetCurrent(Thread? t) => t_currentThread = t;

        private static ThreadState GetState(Thread thread) => (ThreadState)Atomic.Load32(ref Unsafe.As<ThreadState, int>(ref thread._threadState));

        private static void SetState(Thread thread, ThreadState set)
        {
            ref int s = ref Unsafe.As<ThreadState, int>(ref thread._threadState);
            int old;
            do old = Atomic.Load32(ref s); while (Atomic.CompareExchange32(ref s, old, old | (int)set) != old);
        }

        private static void ClrState(Thread thread, ThreadState clr)
        {
            ref int s = ref Unsafe.As<ThreadState, int>(ref thread._threadState);
            int old;
            do old = Atomic.Load32(ref s); while (Atomic.CompareExchange32(ref s, old, old & ~(int)clr) != old);
        }

        private static string? GetName(Thread thread) => thread._name;

        private static void SetName(Thread thread, string? name) => thread._name = name;

        private static bool JoinInternal(Thread thread, int millisecondsTimeout)
        {
            if (Atomic.Load32(ref thread._run) == 0) throw new ThreadStateException(SR.ThreadState_NotStarted);
            long end = millisecondsTimeout < 0 ? long.MaxValue : Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            while (Atomic.Load32(ref thread._run) != 2)
            {
                long left = end - Engine.MonotonicNs();
                if (left <= 0) return false;
                Threads.SafePoint();
                Threads.Block(ref thread._run, 1, left > 10000000 ? 10000000 : left);
            }
            return true;
        }

        internal int GetSmallId() => managed_id;

        internal bool HasExternalEventLoop
        {
            get => external_eventloop;
            set => external_eventloop = value;
        }

#if TARGET_WINDOWS
        internal static void CheckForPendingInterrupt()
        {
        }
#endif
    }
}
