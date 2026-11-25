# NetWasm "GC-dependent" cases: diagnosis

Source: main's recorded results (`mf-base/work-d0cecda45cc8/results.json`, shipped mode) and the cached case drivers (`cases/<case>/Program.cs`). Nothing was re-run; the diffs are main's recorded ones.

The triage group has 93 cases. 91 show `[n] value 42` expected and `[n] value 1` actual. The other 2 (ValueTypeEqualityRootsGc 1->0, YoungCollectionGc 42->20) are not covered here.

## Why the value is 1

NetWasm has no collector. `GC.GetCollectionCount` always returns 0 (`src/Il2Wasm/Upstream/System.Private.CoreLib/Runtime/System/GC.Mono.cs:31,108-111`), and `RuntimeFeature.IsDynamicCodeSupported` is `false` (`manifest.txt:33`). Several case drivers therefore return the sentinel `1` before their real checks run.

## Samples (8 classes)

| Case (class) | Check that returns 1 | What it assumes | Class |
|---|---|---|---|
| RootLifetimeGcTests.WhatACaseHoldsSurvivesCollections-0 | `EntryPoint.Run`: `if (!IsDynamicCodeSupported && GC.CollectionCount(0) == collections) return 1;` (shared template, see below) | Two `new object()` allocations on an AOT runtime cause a collection, so the count moves | (a) GC timing, enforced by the harness probe |
| ByRefTargetRootsGcTests.WhatACalleeStoresThroughAByrefSurvivesCollections-0 | Same template guard (line 182) | Same | (a) |
| InlineArrayRootsGcTests.WhatACalleeReadsThroughItsArgumentsSurvivesCollections-0 | Same template guard (line 216) | Same | (a) |
| AggregateReferenceLaneGcTests.PromotedReferenceLanesKeepTheirObjectsAlive-0 | `Spans()`: `if (!Collect()) return -1;` then `Run` returns 1 at `if (Spans(start) != ...) return 1;`. `Collect()` is `GC.Collect()` followed by a check that `CollectionCount(0)` rose | `GC.Collect()` increments `CollectionCount(0)` | (a) |
| AggregateStackStorageGcTests.ProjectedByrefLocalsRetainTheirOwnerBeforeAndAfterReload | `Consume(...)` returns `1` unless `collected == 1`; `Collect()` returns 1 only if `CollectionCount(0)` rose after `GC.Collect()` | Same as above | (a) |
| ScalarCallPreservationGcTests.FloatingPreservationStorageDoesNotDisplaceLiveRoots-0 | `Probe()` returns 4 when `!collected`; `Run` returns 1 at `if (Probe(count, false) != 42) return 1;` | Same as above | (a) |
| DelegateInvocationGcTests.InvokerKeepsArgumentsAcrossMulticastTargetsAndTypeInitializers | `if (multicast(...) != input \|\| (Witness.Finalized & 2) != 0) return 1;` | The witness passed to the multicast call has not been finalized by the time the caller's next statement runs. On desktop no GC runs in that window. On NetWasm refcounting frees it at the call's last use and the finalizer runs immediately | (a) finalizer timing, which SPEC 4.4 leaves free |
| InterlockedExceptionGcTests.NullGenericCompareExchangePreservesReferen-13cba598a34f | `Run` line 1 is `Interlocked.CompareExchange<string?>(ref Unsafe.NullRef<string?>(), "next", null); return 1;`. The `catch (NullReferenceException) when (Collect())` branch never runs | A null-ref write through `Interlocked.CompareExchange<T>` raises `NullReferenceException`. No collector or finalizer is involved: no exception was raised, so the code fell through to `return 1` | (b) real bug, not GC timing |

The sibling `InterlockedExceptionGcTests.ThrowingFilterPreservesItsNewLocalBeforeAnUnexecutedOverwrite` has the same first line and the same `value 1` result, so it is also (b).

## Shared helper

Of the 91 cases, the breakdown by guard:

- **81 cases: the `EntryPoint.Run` template guard** `if (!RuntimeFeature.IsDynamicCodeSupported && GC.CollectionCount(0) == collections) return 1;`. The text is identical in 80 files, and one more (RuntimeCloneRoots) has the same guard. Per class: RootLifetimeGcTests 56, InlineArrayRootsGcTests 12, ByRefTargetRootsGcTests 12, RuntimeCloneRootsGcTests 1. This template is the single shared piece that explains most of the group. It is generated, not a base class.
- **7 cases: a local `Collect()` helper** that compares `CollectionCount(0)` before and after `GC.Collect()`. Per class: AggregateStackStorageGcTests 5, AggregateReferenceLaneGcTests 1, ScalarCallPreservationGcTests 1.
- **3 cases: finalizer-flag checks**: DelegateInvocationGcTests 1 and InterlockedExceptionGcTests 2 (one of the two is (b) above).

So 88 of 91 are in (a) and 2 are (b). The Delegate case is (a) through a finalizer flag, not a count.

## Recommendation

- **Legitimate, tag as gc-dependent: 89 cases** (81 template-guarded, 7 Collect-helper, 1 finalizer). Each one depends on the collector running or a finalizer not having run, which SPEC 4.4 leaves free.
- **Bug: 2 Interlocked cases.** `Interlocked.CompareExchange` on `Unsafe.NullRef<T>()` does not throw `NullReferenceException`, which breaks a .NET guarantee. Move these out of the GC group. The wrong-value group already contains `InterlockedTests.NullLocationsThrowBeforeReadingOrWriting`, which may be the same bug (not verified).

Caveat on the 89: the guards return before the real liveness assertions run, so these cases check nothing on NetWasm today. The tag is correct, but it hides the coverage gap. The next step should be a separate probe-free run of these cases, which is where a real retention bug would show.
