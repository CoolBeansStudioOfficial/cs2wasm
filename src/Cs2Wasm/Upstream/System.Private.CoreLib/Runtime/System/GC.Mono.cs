// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Tracing;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System
{
    public enum GCCollectionMode
    {
        Default = 0,
        Forced = 1,
        Optimized = 2,
        Aggressive = 3,
    }

    public enum GCNotificationStatus
    {
        Succeeded = 0,
        Failed = 1,
        Canceled = 2,
        Timeout = 3,
        NotApplicable = 4
    }

    public static partial class GC
    {
        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static int GetCollectionCount(int generation) => 0;

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static int GetMaxGeneration() => 0;

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static void InternalCollect(int generation) { }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static void AddPressure(ulong bytesAllocated) { }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static void RemovePressure(ulong bytesRemoved) { }

        // TODO: Move following to ConditionalWeakTable
        // cs2wasm: no collector; counting frees objects as they become unreachable
        internal static void register_ephemeron_array(Ephemeron[] array) { }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static object get_ephemeron_tombstone() => new object();

        internal static readonly object EPHEMERON_TOMBSTONE = get_ephemeron_tombstone();

        // cs2wasm: no collector; counting frees objects as they become unreachable
        public static long GetAllocatedBytesForCurrentThread() => Cs2Wasm.Runtime.Rt.HeapUsed();

        // cs2wasm: no collector; counting frees objects as they become unreachable
        public static long GetTotalAllocatedBytes(bool precise = false) => Cs2Wasm.Runtime.Rt.HeapUsed();

        public static void AddMemoryPressure(long bytesAllocated)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesAllocated);
            if (IntPtr.Size == 4)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesAllocated, int.MaxValue);
            }
            AddPressure((ulong)bytesAllocated);
        }

        public static void RemoveMemoryPressure(long bytesAllocated)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesAllocated);
            if (IntPtr.Size == 4)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesAllocated, int.MaxValue);
            }
            RemovePressure((ulong)bytesAllocated);
        }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        public static int GetGeneration(object obj) => 0;

        public static void Collect(int generation)
        {
            Collect(generation, GCCollectionMode.Default);
        }

        public static void Collect()
        {
            InternalCollect(MaxGeneration);
        }

        public static void Collect(int generation, GCCollectionMode mode) => Collect(generation, mode, true);

        public static void Collect(int generation, GCCollectionMode mode, bool blocking) => Collect(generation, mode, blocking, false);

        public static void Collect(int generation, GCCollectionMode mode, bool blocking, bool compacting)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(generation);
            if ((mode < GCCollectionMode.Default) || (mode > GCCollectionMode.Aggressive))
                throw new ArgumentOutOfRangeException(nameof(mode), SR.ArgumentOutOfRange_Enum);

            InternalCollect(generation);
        }

        public static int CollectionCount(int generation)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(generation);
            return GetCollectionCount(generation);
        }

        [MethodImplAttribute(MethodImplOptions.NoInlining)] // disable optimizations
        public static void KeepAlive(object? obj)
        {
        }

        public static int GetGeneration(WeakReference wo)
        {
            object? obj = wo.Target;
            ArgumentNullException.ThrowIfNull(obj, nameof(wo));
            return GetGeneration(obj);
        }

        public static int MaxGeneration
        {
            get
            {
                return GetMaxGeneration();
            }
        }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        public static void WaitForPendingFinalizers() { }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static void _SuppressFinalize(object o) => Cs2Wasm.Runtime.ObjectMarks.Set(o, Cs2Wasm.Runtime.ObjectMarks.FinalizerSuppressed);

        public static void SuppressFinalize(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            _SuppressFinalize(obj);
        }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static void _ReRegisterForFinalize(object o)
        {
            Cs2Wasm.Runtime.ObjectMarks.Clear(o, Cs2Wasm.Runtime.ObjectMarks.FinalizerSuppressed);
            Cs2Wasm.Runtime.Rt.ClearFinalized(o);
        }

        public static void ReRegisterForFinalize(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            _ReRegisterForFinalize(obj);
        }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(Finalize))]
        private static extern void CallFinalize(object o);
        private static void GuardedFinalize(object o)
        {
            try
            {
                CallFinalize(o);
            }
            catch (Exception ex) when (ExceptionHandling.IsHandledByGlobalHandler(ex))
            {
                // the handler returned "true" means the exception is now "handled" and we should continue.
            }
        }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        public static long GetTotalMemory(bool forceFullCollection) => Cs2Wasm.Runtime.Rt.HeapUsed();

        private static bool _RegisterForFullGCNotification(int maxGenerationPercentage, int largeObjectHeapPercentage)
        {
            throw new NotImplementedException();
        }

        private static bool _CancelFullGCNotification()
        {
            throw new NotImplementedException();
        }

        private static GCNotificationStatus _WaitForFullGCApproach(int millisecondsTimeout)
        {
            throw new NotImplementedException();
        }

        private static GCNotificationStatus _WaitForFullGCComplete(int millisecondsTimeout)
        {
            throw new NotImplementedException();
        }

        public static void RegisterForFullGCNotification(int maxGenerationThreshold, int largeObjectHeapThreshold)
        {
            if ((maxGenerationThreshold <= 0) || (maxGenerationThreshold >= 100))
                throw new ArgumentOutOfRangeException(nameof(maxGenerationThreshold),
                                                       SR.Format(SR.ArgumentOutOfRange_Bounds_Lower_Upper, 1, 99));
            if ((largeObjectHeapThreshold <= 0) || (largeObjectHeapThreshold >= 100))
                throw new ArgumentOutOfRangeException(nameof(largeObjectHeapThreshold),
                                                       SR.Format(SR.ArgumentOutOfRange_Bounds_Lower_Upper, 1, 99));

            if (!_RegisterForFullGCNotification(maxGenerationThreshold, largeObjectHeapThreshold))
                throw new InvalidOperationException(SR.InvalidOperation_NotWithConcurrentGC);
        }

        public static void CancelFullGCNotification()
        {
            if (!_CancelFullGCNotification())
                throw new InvalidOperationException(SR.InvalidOperation_NotWithConcurrentGC);
        }

        public static GCNotificationStatus WaitForFullGCApproach()
        {
            return (GCNotificationStatus)_WaitForFullGCApproach(-1);
        }

        public static GCNotificationStatus WaitForFullGCApproach(int millisecondsTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);

            return _WaitForFullGCApproach(millisecondsTimeout);
        }

        public static GCNotificationStatus WaitForFullGCComplete()
        {
            return _WaitForFullGCComplete(-1);
        }

        public static GCNotificationStatus WaitForFullGCComplete(int millisecondsTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);
            return _WaitForFullGCComplete(millisecondsTimeout);
        }

        private static bool StartNoGCRegion(long totalSize, bool hasLohSize, long lohSize, bool disallowFullBlockingGC)
        {
            throw new NotImplementedException();
        }

        public static bool TryStartNoGCRegion(long totalSize) => StartNoGCRegion(totalSize, false, 0, false);

        public static bool TryStartNoGCRegion(long totalSize, long lohSize) => StartNoGCRegion(totalSize, true, lohSize, false);

        public static bool TryStartNoGCRegion(long totalSize, bool disallowFullBlockingGC) => StartNoGCRegion(totalSize, false, 0, disallowFullBlockingGC);

        public static bool TryStartNoGCRegion(long totalSize, long lohSize, bool disallowFullBlockingGC) => StartNoGCRegion(totalSize, true, lohSize, disallowFullBlockingGC);

        public static void EndNoGCRegion()
        {
            throw new NotImplementedException();
        }

        internal static ulong GetSegmentSize()
        {
            // coreclr default
            return 1024 * 1024 * 16;
        }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static void _GetGCMemoryInfo(out long highMemoryLoadThresholdBytes,
                                        out long memoryLoadBytes,
                                        out long totalAvailableMemoryBytes,
                                        out long totalCommittedBytes,
                                        out long heapSizeBytes,
                                        out long fragmentedBytes) {
            highMemoryLoadThresholdBytes = memoryLoadBytes = totalAvailableMemoryBytes = fragmentedBytes = 0;
            totalCommittedBytes = heapSizeBytes = Cs2Wasm.Runtime.Rt.HeapUsed();
        }

        public static GCMemoryInfo GetGCMemoryInfo()
        {
            var data = new GCMemoryInfoData();

            _GetGCMemoryInfo(out data._highMemoryLoadThresholdBytes,
                             out data._memoryLoadBytes,
                             out data._totalAvailableMemoryBytes,
                             out data._totalCommittedBytes,
                             out data._heapSizeBytes,
                             out data._fragmentedBytes);

            return new GCMemoryInfo(data);
        }

        public static GCMemoryInfo GetGCMemoryInfo(GCKind kind)
        {
            throw new PlatformNotSupportedException();
        }

        // cs2wasm: no collector; counting frees objects as they become unreachable
        private static Array AllocPinnedArray(Type t, int length) => throw new NotSupportedException("cs2wasm: pinned arrays are not supported yet");

        public static T[] AllocateUninitializedArray<T>(int length, bool pinned = false)
        {
            // Mono only does explicit zeroing if the array is too big for the nursery, but less than 1 Mb - 4 kb.
            // If it is bigger than that, we grab memory directly from the OS which comes pre-zeroed.
            // Experimentation shows that if we just skip the zeroing in this case, we do not save a measurable
            // amount of time. So we just allocate the normal way here.
            // Revist if we change LOS implementation.
            return AllocateArray<T>(length, pinned);
        }

        public static T[] AllocateArray<T>(int length, bool pinned = false)
        {
            if (pinned) {
                return Unsafe.As<T[]>(AllocPinnedArray(typeof(T[]), length));
            }

            return new T[length];
        }

        internal static ulong GetGenerationSize(int generation)
        {
            switch (generation) {
            case 0 :
                return EventPipeInternal.GetRuntimeCounterValue(EventPipeInternal.RuntimeCounters.GC_NURSERY_SIZE_BYTES);
            case 1 :
            case 2 :
                return EventPipeInternal.GetRuntimeCounterValue(EventPipeInternal.RuntimeCounters.GC_MAJOR_SIZE_BYTES);
            case 3 :
                return EventPipeInternal.GetRuntimeCounterValue(EventPipeInternal.RuntimeCounters.GC_LARGE_OBJECT_SIZE_BYTES);
            case 4:
                // Pinned object heap.
                return 0;
            default:
                return 0;
            }
        }

        internal static int GetLastGCPercentTimeInGC()
        {
            return (int)EventPipeInternal.GetRuntimeCounterValue(EventPipeInternal.RuntimeCounters.GC_LAST_PERCENT_TIME_IN_GC);
        }

        public static TimeSpan GetTotalPauseDuration()
        {
            return TimeSpan.Zero;
        }

        public static System.Collections.Generic.IReadOnlyDictionary<string, object> GetConfigurationVariables()
        {
            return new System.Collections.Generic.Dictionary<string, object>();
        }

        public static void RefreshMemoryLimit()
        {
            throw new PlatformNotSupportedException();
        }

        public static void RegisterNoGCRegionCallback(long totalSize, Action callback)
        {
            throw new PlatformNotSupportedException();
        }

        internal static long GetGenerationBudget(int generation)
        {
            // avoid IDE0060: Remove unused parameter 'generation'
            return -1 + 0 * generation;
        }
    }
}
