# NetWasm suite triage: failing cases grouped by cause

Source: main's results file `results.json` (work-d0cecda45cc8, shipped mode). 450 cases recorded: 283 pass, 167 do not (159 fail, 7 compile-fail, 1 timeout).

Groups are sorted by count, largest first. Each failing case is in exactly one group; the grouping uses the first differing line of the diff, then the exception, compile error, or stderr. For wrong-value cases the diff carries no cause, so those are labelled by what is known and need per-case work.

## Group table

| Cause | Count | Cases |
|---|---|---|
| GC-dependent: value differs on a `Gc` case (no exception) | 93 | `AggregateReferenceLaneGcTests.PromotedReferenceLanesKeepTheirObjectsAlive-0`, `AggregateStackStorageGcTests.PreservedAggregateAndManagedPointeeStorag-68e205e34aa8`, `AggregateStackStorageGcTests.ProjectedByrefLocalsRetainTheirOwnerBeforeAndAfterReload`, `AggregateStackStorageGcTests.ProjectedByrefLocalsRetainTheirOwnerAcros-da645cae1795`, `AggregateStackStorageGcTests.CapturedByrefLocalAnchorSurvivesFilterCollection`, `AggregateStackStorageGcTests.ProjectedByrefLocalsSurviveFilterOutcomesAndNestedUnwind`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-0`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-1`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-2`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-3`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-4`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-5`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-6`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-7`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-8`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-9`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-10`, `ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-11`, `DelegateInvocationGcTests.InvokerKeepsArgumentsAcrossMulticastTargetsAndTypeInitializers`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-0`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-1`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-2`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-3`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-4`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-5`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-6`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-7`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-8`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-9`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-10`, `InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-11`, `InterlockedExceptionGcTests.NullGenericCompareExchangePreservesReferen-13cba598a34f`, `InterlockedExceptionGcTests.ThrowingFilterPreservesItsNewLocalBeforeAnUnexecutedOverwrite`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-0`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-1`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-2`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-3`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-4`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-5`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-6`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-7`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-8`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-9`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-10`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-11`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-12`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-13`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-14`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-15`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-16`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-17`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-18`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-19`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-20`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-21`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-22`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-23`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-24`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-25`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-26`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-27`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-28`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-29`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-30`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-31`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-32`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-33`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-34`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-35`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-36`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-37`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-38`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-39`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-40`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-41`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-42`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-43`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-44`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-45`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-46`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-47`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-48`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-49`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-50`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-51`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-52`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-53`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-54`, `RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-55`, `RuntimeCloneRootsGcTests.RuntimeClonesKeepTheirCallersReferencesAcrossCollections`, `ScalarCallPreservationGcTests.FloatingPreservationStorageDoesNotDisplaceLiveRoots-0`, `ValueTypeEqualityRootsGcTests.DefaultEqualityAndHashKeepTheirValuesAcrossFieldCalls`, `YoungCollectionGcTests.CollectingGenerationZeroFreesOnlyYoungObjects-0` |
| Wrong value, no exception (non-Gc name) | 26 | `ArrayElementAccessCorpusTests.ElementAccessThroughArrayMatchesDesktop-0`, `AttributeExceptionSemanticsTests.ConstructionAndSetterFailuresPreserve-31ab7a7eae54`, `CommonValueModelsCompatibilityCorpusTests.CommonValueModelsMatchDeskto-f975a9539abe`, `CookieContainerCorpusTests.CookieContainerMatchesDesktop-0`, `FusedMultiplyAddCorpusTests.FusedMultiplyAddRoundsOnceAsDesktopHardwareDoes`, `GenericMathEdgeCorpusTests.GenericMathMatchesDesktopAtEdgeValues-0`, `InterlockedTests.NullLocationsThrowBeforeReadingOrWriting`, `LoopInvariantFieldHoistingCompatibilityTests.StableAggregateFieldsSurv-2941b40b2265`, `MethodGroupTargetCompilationTests.AMethodGroupOfAnyKindOfMethodRunsAsDesktopDoes-0`, `OptimizerSiblingCompilationTests.ASiblingRunsAsDesktopDoesAtEveryPreset-6`, `PortableThreadingTests.VolatileAndTimeoutPreserveDesktopSemantics-0`, `RootFunctionsCorpusTests.RootFunctionsMatchDesktop-0`, `RuntimeHelpersCompilerContractsCompatibilityTests.RuntimeHelpersAndCom-bb334b1183ce`, `SimdVectorCorpusTests.SimdVectorsMatchDesktopAcrossCompilerProfilesAndTargets`, `UnsafeAccessorFailureTests.AccessorDispatchAndManagedBindingFailuresMatchDesktop`, `fixture-flow.exception-order`, `fixture-math.boundaries`, `fixture-numeric.async-representation-transport`, `fixture-numeric.floating-math-domains`, `fixture-numeric.floating-small-saturation`, `fixture-numeric.managed-math-full-range`, `fixture-numeric.representation-transport`, `fixture-numeric.unsigned-precision`, `fixture-rtti.array-operations`, `fixture-rtti.array-shapes`, `fixture-rtti.cast-relationships` |
| Missing feature: Array.CreateInstance of an unseen array type | 9 | `AttributeLifetimeSemanticsTests.QueriesCreateFreshObjectsAndPayloadsWh-307057664901`, `AttributeMaterializationGcTests.MaterializationPreservesCallerAndAttri-8afdd9cb7790`, `AttributeNestedEnumSemanticsTests.ClosedNestedEnumPayloadsMatchDesktop-ee2bd3349558`, `AttributeSelectionSemanticsTests.UsageDefaultsDuplicatesAndInterfaceBoundariesMatchDesktop`, `GrowingReductionOrderCompilationTests.CallSitesALaterReductionReachesKeepTheirArguments`, `HttpJsonModernSurfaceCompatibilityCorpusTests.ModernHttpJsonSurfaceMat-8063a2b22923`, `JsonMetadataCollectionsCompatibilityCorpusTests.GeneratedCollectionAnd-21a79b03ea0d`, `OpenGenericAttributeQueryTests.OpenGenericAttributeFiltersMatchEveryCo-c3ff396c742c`, `RuntimeAllocationSiblingGcTests.RuntimeAllocationsKeepTheirCallersRefe-f46e1bad734f` |
| Compile error: missing intrinsic Rt.ObjectArrayDelegate | 4 | `ExpressionMemberExecutionRootsGcTests.ConstructedObjectsAndStructResul-0f57f5c8b831`, `JsonPolymorphismCompatibilityCorpusTests.PolymorphicValuesMatchDesktop`, `NullableMemberGcTests.NullableMemberResultsAndExpressionReceiversSurviveCollections`, `StaticInitializationTests.ConstructorAndValueMethodInitializationPreservePreciseRoots` |
| Exception mismatch: InvalidCastException on a cast that should succeed | 3 | `DynamicInterfaceCastableCompilationTests.DynamicCastsAndDispatchMatchDesktop`, `FrozenDictionaryCompatibilityCorpusTests.FrozenDictionariesMatchDeskto-f7285e2b0d77`, `PriorityQueueCompatibilityCorpusTests.PriorityQueuesMatchDesktopAcross-e7322fe06a73` |
| Exception mismatch: ArgumentException on structure marshaling (should compute layout) | 3 | `FormattedClassAndStringStructureMarshallingCompilationTests.FormattedC-a56c872bae5d`, `FunctionPointerArrayCompilationTests.FunctionPointerArraysBehaveAsDesktopDoes`, `StructureMarshallingGapsCompilationTests.StructureMarshallingMatchesDe-2ae2f974644d` |
| Compile error: missing intrinsic Rt.DelegateArity | 3 | `HandlerReachedCodeCompilationTests.CallsThatNeedStaticTypesBehaveAsDes-cd891d2e48bc`, `ReferenceWriteBarrierGcTests.EveryStoreOfAReferenceIntoAnOldObjectKeepsTheStoredObject-0`, `fixture-flow.unwind-rewind` |
| Wasm trap: `unreachable` at runtime (exit 1) | 3 | `InlineArrayStructureMarshallingCompilationTests.InlineArraysAndFixedBuffersMatchDesktop`, `MethodGroupTargetCompilationTests.AMethodGroupOfAnyKindOfMethodRunsAsDesktopDoes-15`, `NativeLayoutStructureMarshallingCompilationTests.NativeLayoutStructuresMatchDesktop` |
| Environment: line endings only (expected CRLF, actual LF) | 3 | `sut-configuration`, `sut-sep`, `sut-simdcodecs` |
| Exception mismatch: PlatformNotSupportedException | 3 | `MethodGroupTargetCompilationTests.AMethodGroupOfAnyKindOfMethodRunsAsDesktopDoes-4`, `MethodGroupTargetCompilationTests.AMethodGroupOfAnyKindOfMethodRunsAsDesktopDoes-5`, `sut-switchmediator` |
| Exception mismatch: PlatformNotSupportedException | 3 | `MethodGroupTargetCompilationTests.AMethodGroupOfAnyKindOfMethodRunsAsDesktopDoes-4`, `MethodGroupTargetCompilationTests.AMethodGroupOfAnyKindOfMethodRunsAsDesktopDoes-5`, `sut-switchmediator` |
| Missing feature: boxing an array element of a struct with references | 2 | `ArrayCopyBoxingCompatibilityCorpusTests.ArrayCopyBoxesAndUnboxesAcross-e313bc949d39`, `NullableArrayReadbackLinkedTests.NullableReadbackUsesNativeBoxingAndCollector-0` |
| Exception mismatch: NullReferenceException | 2 | `CoreLibScalarCorpusTests.ClampSignAndAbsThrowWithDesktopsMessages`, `HttpModernSurfaceCompatibilityCorpusTests.ModernHttpSurfaceMatchesDesk-1b876fd1d58b` |
| Missing feature: pinned arrays | 2 | `UninitializedArrayCompilationTests.AllocatesAsDesktopOnTheNativeRuntime-0`, `sut-memorypack` |
| Missing feature: storing a boxed struct with references into an array | 1 | `ArrayCreateInstanceLinkedTests.RuntimeTypesCreateExactArraysWithNativeCollector-0` |
| Expected exception not thrown: CopySign | 1 | `FloatingCopySignTests.CopySignPreservesMagnitudePayloadAndReplacesOnlyTheSign` |
| Exception mismatch: ArgumentOutOfRangeException (implicit exception identity) | 1 | `ImplicitExceptionIdentitySiblingTests.EachImplicitExceptionIsAnObjectOfItsOwn` |
| Expected exception not thrown: ArgumentException for basePath | 1 | `ManagedIoCompatibilityCorpusTests.ManagedIoMatchesDesktopAcrossCompilerProfilesAndTargets` |
| Exception mismatch: InvalidCastException on array downcast | 1 | `NonGenericArrayCompatibilityTests.NonGenericVectorOperationsMatchDesktopOnRealRuntimes` |
| Test-harness contract: await scheduler | 1 | `TaskSchedulerAwaitTests.AwaitAndYieldCaptureTheCurrentSchedulerOrContext-0` |
| Test-harness contract: unobserved task exception event | 1 | `TaskSchedulerEventTests.UnobservedEventsPreserveSenderAndExceptionLifetimes-0` |
| Missing feature: TypedReference | 1 | `TypedReferenceRootsGcTests.TypedReferencesKeepWhatTheyReferenceAlive` |
| Exception mismatch: OverflowException on negating MinValue | 1 | `VectorSurfaceCorpusTests.EveryVector128MethodComputesWhatDesktopDoes-0` |
| Timeout | 1 | `YoungObjectStoreGcTests.StoresIntoObjectsThatMayBeOldKeepWhatTheyStore-0` |
| Host/worker: threads entry missing | 1 | `sut-imagesharp` |

Total: 170

## Sample messages

### GC-dependent: value differs on a `Gc` case (no exception) (93)

Mostly `[n] value 42` expected, `[n] value 1` actual (91 cases); 1->0 and 42->20 in ValueTypeEqualityRootsGc and YoungCollectionGc

Sample: exp `[0] value 42` / act `[0] value 1`

### Wrong value, no exception (non-Gc name) (26)

Cause not visible in the diff; needs a per-case look (numeric/library results, mostly fixture-*)

Sample: exp `[3] value 5` / act `[3] value -100` (ArrayElementAccessCorpusTests)

### Missing feature: Array.CreateInstance of an unseen array type (9)

Exact text

Sample: `il2wasm: Array.CreateInstance of an array type the program never makes is not supported yet`

### Compile error: missing intrinsic Rt.ObjectArrayDelegate (4)

Reached from Delegate.Bind via DynamicDelegateAugments.CreateObjectArrayDelegate

Sample: `il2wasm-il: error: [S.P.CoreLib]Internal.Runtime.Augments.DynamicDelegateAugments.CreateObjectArrayDelegate(Type,Func`2<object[],object>): the intrinsic Rt.ObjectArrayDelegate reached from: [S.P.CoreLib]System.Delegate.Bind(Type,object,MethodInfo,bool,bool)`

### Exception mismatch: InvalidCastException on a cast that should succeed (3)

Type names differ per case

Sample: `System.InvalidCastException: Unable to cast object of type 'DynamicInterfaceCastable.Proxy' to type 'DynamicInterfaceCastable.IGreeter'.`

### Exception mismatch: ArgumentException on structure marshaling (should compute layout) (3)

Exact text

Sample: `System.ArgumentException: Type cannot be marshaled as an unmanaged structure; no meaningful size or offset can be computed.`

### Compile error: missing intrinsic Rt.DelegateArity (3)

Reached from Delegate.DynamicInvokeImpl

Sample: `il2wasm-il: error: [S.P.CoreLib]System.Delegate.DynamicInvokeImpl(object[]): the intrinsic Rt.DelegateArity reached from:`

### Wasm trap: `unreachable` at runtime (exit 1) (3)

Trap inside a generic Bytes[T] helper and NativeLayout / MethodGroup entry points

Sample: stderr: `RuntimeError: unreachable at ... NetWasm.Correctness.InlineArrays.EntryPoint.Bytes[T](Int32 hash, T value, Func`2 back)`

### Environment: line endings only (expected CRLF, actual LF) (3)

Output otherwise identical

Sample: expected `"App = \r"` / actual `"App = "`

### Exception mismatch: PlatformNotSupportedException (3)

MethodGroupTarget cases throw it; sut-switchmediator dies with it unhandled at AsyncHelpers.SystemJS_MarkAsyncMain (exit 3)

Sample: `System.PlatformNotSupportedException: Operation is not supported on this platform.`

### Exception mismatch: PlatformNotSupportedException (3)

MethodGroupTarget cases throw it; sut-switchmediator dies with it unhandled at AsyncHelpers.SystemJS_MarkAsyncMain (exit 3)

Sample: `System.PlatformNotSupportedException: Operation is not supported on this platform.`

### Missing feature: boxing an array element of a struct with references (2)

Exact text

Sample: `il2wasm: boxing an array element of a struct with references is not supported yet`

### Exception mismatch: NullReferenceException (2)

Desktop returns a value

Sample: `System.NullReferenceException: Object reference not set to an instance of an object.`

### Missing feature: pinned arrays (2)

Exact text (sut-memorypack also exits 3 with it)

Sample: `il2wasm: pinned arrays are not supported yet`

### Missing feature: storing a boxed struct with references into an array (1)

Exact text

Sample: `il2wasm: storing a boxed struct with references into an array is not supported yet`

### Expected exception not thrown: CopySign (1)

Desktop throws; cs2wasm returns a value

Sample: exp `throw System.InvalidOperationException: Single CopySign changed magnitude bits.` / act `value 196`

### Exception mismatch: ArgumentOutOfRangeException (implicit exception identity) (1)

Exact text

Sample: `System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')`

### Expected exception not thrown: ArgumentException for basePath (1)

Desktop throws; cs2wasm returns a value

Sample: exp `throw System.ArgumentException: Basepath argument is not fully qualified. (Parameter 'basePath')` / act `value 1665301620`

### Exception mismatch: InvalidCastException on array downcast (1)

Exact text

Sample: `System.InvalidCastException: At least one element in the source array could not be cast down to the destination array type.`

### Test-harness contract: await scheduler (1)

Exact text

Sample: `System.Exception: await scheduler contract`

### Test-harness contract: unobserved task exception event (1)

Exact text

Sample: `System.Exception: unobserved event contract`

### Missing feature: TypedReference (1)

Exact text

Sample: `il2wasm: TypedReference is not supported yet`

### Exception mismatch: OverflowException on negating MinValue (1)

Exact text

Sample: `System.OverflowException: Negating the minimum value of a twos complement number is invalid.`

### Timeout (1)

No output

Sample: status `timeout`

### Host/worker: threads entry missing (1)

Harness worker calls exports.__thread_entry, which the module lacks

Sample: `worker failed: TypeError: instance.exports.__thread_entry is not a function`

