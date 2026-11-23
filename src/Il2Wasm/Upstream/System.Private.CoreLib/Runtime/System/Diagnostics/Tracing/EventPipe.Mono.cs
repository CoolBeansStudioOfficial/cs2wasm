// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Runtime.CompilerServices;

namespace System.Diagnostics.Tracing
{

    internal static partial class EventPipeInternal
    {
#if FEATURE_PERFTRACING
        // These ICalls are used by the configuration APIs to interact with EventPipe.
        //
                [Il2Wasm.Runtime.MissingCapability("event tracing")]        private static extern unsafe ulong Enable(char* outputFile, EventPipeSerializationFormat format, uint circularBufferSizeInMB, EventPipeProviderConfigurationNative* providers, uint numProviders);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern void Disable(ulong sessionID);

        //
        // These ICalls are used by EventSource to interact with the EventPipe.
        //
                [Il2Wasm.Runtime.MissingCapability("event tracing")]        private static extern unsafe IntPtr CreateProvider(string providerName, IntPtr callbackFunc, IntPtr callbackContext);

        internal static unsafe IntPtr CreateProvider(string providerName,
            delegate* unmanaged<byte*, int, byte, long, long, Interop.Advapi32.EVENT_FILTER_DESCRIPTOR*, void*, void> callbackFunc,
            void* callbackContext)
            => CreateProvider(providerName, (IntPtr)callbackFunc, (IntPtr)callbackContext);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe IntPtr DefineEvent(IntPtr provHandle, uint eventID, long keywords, uint eventVersion, uint level, byte* pMetadata, uint metadataLength);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe IntPtr GetProvider(char* providerName);

        internal static unsafe IntPtr GetProvider(string providerName)
        {
            fixed (char* p = providerName)
            {
                return GetProvider(p);
            }
        }

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern void DeleteProvider(IntPtr provHandle);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern int EventActivityIdControl(uint controlCode, ref Guid activityId);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe void WriteEventData(IntPtr eventHandle, EventProvider.EventData* pEventData, uint dataCount, Guid* activityId, Guid* relatedActivityId);


        //
        // These ICalls are used as part of the EventPipeEventDispatcher.
        //
                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe bool GetSessionInfo(ulong sessionID, EventPipeSessionInfo* pSessionInfo);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe bool GetNextEvent(ulong sessionID, EventPipeEventInstanceData* pInstance);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe bool SignalSession(ulong sessionID);

                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern unsafe bool WaitForSessionSignal(ulong sessionID, int timeoutMs);
#endif // FEATURE_PERFTRACING

        //
        // This ICall are used as part of getting runtime implemented counter values.
        //

        //
        // NOTE, keep in sync with icall-eventpipe.c, EventPipeRuntimeCounters.
        //
        internal enum RuntimeCounters
        {
            ASSEMBLY_COUNT,
            EXCEPTION_COUNT,
            GC_NURSERY_SIZE_BYTES,
            GC_MAJOR_SIZE_BYTES,
            GC_LARGE_OBJECT_SIZE_BYTES,
            GC_LAST_PERCENT_TIME_IN_GC,
            JIT_IL_BYTES_JITTED,
            JIT_METHODS_JITTED,
            JIT_TICKS_IN_JIT
        }

#if FEATURE_PERFTRACING
                [Il2Wasm.Runtime.MissingCapability("event tracing")]        internal static extern ulong GetRuntimeCounterValue(RuntimeCounters counterID);
#else
        internal static ulong GetRuntimeCounterValue(RuntimeCounters _ /*counterID*/)
        {
            return 0;
        }
#endif
    }
}
