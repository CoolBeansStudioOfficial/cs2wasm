# NetWasm survey: why it changed the upstream library, and what cs2wasm can do instead

NetWasm (https://github.com/zion-sati/NetWasm, MIT) builds `Console.WriteLine(42)` as a 28,383-byte WASI 0.3
component. Its CoreLib (`src/NetWasm.CoreLib`, ~1,600 files) is partly upstream code copied at dotnet/runtime
`deae2e32` or `c22108eb` and partly hand-written replacements. This survey lists each change that matters on the
`Console.WriteLine(int)` path and says how cs2wasm could get the same saving from the *unmodified* sources. That
means a general compiler rule (A), a platform-fact substitution (B) or a runtime-boundary method (C). Changes that
alter .NET behaviour (D) or exist only for NetWasm's compiler or GC (E) are listed so they are not copied.

## Measurements

All counts are WASM instructions from `CS2WASM_STATS`, taken at HEAD `cdedd62` with `CS2WASM_UPSTREAM=1` and a
Release compiler:

| Build | Size | Methods | Instructions |
| --- | ---: | ---: | ---: |
| `Console.WriteLine(42)` | 844,431 bytes | 1,992 | 126,864 |
| Empty module (no export) | 578,054 bytes | 1,320 | 90,083 |

- **The empty module is already 578 KB.** Most of that size is in every module: exception reporting, finalizers,
  `object.ToString`, and the culture cluster that boxed-int formatting reaches.
- **Console adds 663 methods and 35,913 instructions.** The Console plumbing itself (Console, StreamWriter,
  SyncTextWriter, ConsoleEncoding, BrowserConsoleStream) is only about 4,000 of them. The rest is what that plumbing
  drags in: Lock and WaitSubsystem, AppContext configuration parsing, encoder fallbacks and culture data.

**How the estimates were made.** They sum the instruction counts of the methods that would become unreachable,
using first-caller chains, so most are upper bounds (±20%). Items marked **measured** were checked another way. The
rule was simulated by editing a private copy of the upstream sources (`CS2WASM_RUNTIME_SOURCES`), and the module was
rebuilt.

**Overlap.** Several items cut the same subgraph, so the savings do not add up. The combination that was measured is
given under item 2.

### What NetWasm actually is

Read this before copying anything. NetWasm is a *bounded profile*, not a faithful .NET:

- one thread;
- ordinal-only text comparison (a culture-sensitive `StringComparison` throws `PlatformNotSupportedException`);
- type names `type#<id>` unless a sidecar is shipped;
- rewritten exception messages;
- rewritten `Encoding`, `StringBuilder` and `Array`.

See its `docs/spec-deviations.md` and `docs/corelib-runtime.md`. Much of its CoreLib is therefore category D for
cs2wasm. The parts that are copyable are mostly what its compiler does:

| NetWasm compiler pass | What it does |
| --- | --- |
| `UnwrittenStaticFieldProgramReducer` | Folds loads of static fields that no reachable code writes |
| `ConstantStaticFieldRewriter` | Turns constant stores at the start of a cctor into constants |
| `ConstantArgumentProgramReducer` | Substitutes a parameter when every reachable caller passes the same constant |
| `ConstantCallEvaluator` | Interprets pure callees whose arguments are all constant |
| `ReducingReachabilityClosureBuilder` | Rebuilds reachability after each reduction, to a fixpoint |

Upstream code that NetWasm copied *unchanged* gets its savings from these passes:

- `SpanHelpers*`, `PackedSpanHelpers`, `MemoryExtensions`, `Ascii`, `Utf8Utility` validation, `ValueStringBuilder`,
  `Dictionary`, `List`, the `ThrowHelper` switches.

---

## Ranked list (most savings first)

### 1. Instance methods of classes that are never allocated are dead (A). About 12,000–19,000.

**Upstream code**
- `Threading/Tasks/Task.cs`, `Dispose(bool)`: `ManualResetEventSlim? ev = cp.m_completionEvent; if (ev != null) { ...
  ContingentProperties.SetEvent(ev); ev.Dispose(); }`.
- `ManualResetEventSlim.Set(bool)` calls `m_lock.EnterScope()` / `PulseAll()`.
- `m_lock` is assigned only in `EnsureLockObjectCreated`, which is reached only from `Wait`.
- `m_completionEvent` is assigned only in `Task.CompletedEvent`, which is unreachable.

**Why the chain is live today**
- Task is constructed: `StreamWriter._asyncWriteTask = Task.CompletedTask` touches `Task.s_cachedCompleted`.
- Some `IDisposable.Dispose` call site exists, so `Task.Dispose(bool)` becomes a root.
- From there: `SetEvent` → `MRES.Set` → `Lock.EnterScope` → `Lock.TryEnterSlow` (653) → `CreateWaitEvent` →
  `AutoResetEvent` → `WaitSubsystem` (`ThreadWaitInfo.*`, `WaitableObject.*`, `HandleManager`, plus GCHandles).
- Separately, `Lock.TryInitializeStatics` → `DetermineMaxSpinCount` → `GetInt16Config` reaches the short-parsing code.
- Neither `new ManualResetEventSlim` nor `new Lock()` appears in the build.

**What NetWasm did (source edit).** It rewrote Task without `ContingentProperties`:
`s_releaseCompletionEvent?.Invoke(this)` is set only when a wait handle is handed out (`Tasks.cs`). `MRES.Set` became
`_isSet = true; _event?.Set();`.

**Rule for cs2wasm.** A class C is *instantiated* if any reachable `new` of C or a subclass exists. Runtime and
boundary allocations, `GetUninitializedObject`, and runtime-thrown exceptions also count. If C is not instantiated:
- Its non-virtual instance methods are unreachable.
- `e.M()` with static receiver type C becomes "evaluate `e` and the arguments, then throw NullReferenceException".
- `e != null` folds to false.
- `e?.M()` folds to null.
- `lock (e)` becomes ArgumentNullException.

Iterate this with RTA to a fixpoint. It is exact with threads; NativeAOT's scanner does the same.

**Variants that cut the same chain**
- Field value-sets: a field whose only reachable stores write null is null.
- Type-flow RTA: `Task.Dispose` is live only if a Task can reach a Dispose receiver.

**Savings**
- Lock and Lock.State: about 3,850.
- WaitSubsystem, handles and LowLevelLock: about 7,400.
- MRES and Condition: about 490.
- Through `GetInt16Config`: the short-parsing chain in item 2.

That is 10–15% of the whole build. Confidence: high.

### 2. Fields that are never written read as default; type patterns on a known null fold (A). Measured −11,363 alone.

**Upstream code**
- `AppContext.GetData`: `if (s_dataStore == null) return null; lock (s_dataStore) ...`.
- `s_dataStore` is written only by `Setup` (called by the host) and by `SetData` (through
  `Interlocked.CompareExchange(ref s_dataStore, ...)`).
- `s_switches` is written only by `SetSwitch`.
- `AppContextConfigHelper.GetInt16Config(string, short, bool)`: `object? config = GetData(name); switch (config) {
  case uint ...; case string str: str.StartsWith("0x") ... Convert.ToInt16(str, 16) ... short.Parse(...); case
  IConvertible c: c.ToInt16(...) }`, inside try/catch.

**What it costs today**
- Parsing: `Number.TryParseNumber<char>` (1,307), `TryParseBinaryIntegerStyle<char,short>` (681), the hex and binary
  parsers (434 + 434), `ParseNumbers.StringToInt` / `GrabInts` (513 / 194), `Convert.ToInt16` (110).
- The culture-sensitive `string.StartsWith(string)` (291), which is one root of the culture cluster (item 3).
- `IConvertible.ToInt16` → `Enum.ToInt16` → `Enum.GetValue` (158). That boxes `long`/`ulong`, which makes
  `FormatInt64Slow`, `TryFormatInt64Slow` and the `UInt64` variants live (about 1,300).

**What NetWasm did.** It has no `AppContextConfigHelper` and no spin configuration (single thread). Its `AppContext`
keeps upstream's null-checked shape over a compiler-supplied configuration string, and its compiler folds the
unwritten static.

**Rule for cs2wasm, in three parts**
- (a) Unwritten fields:
  - A static or instance field with no reachable store, no `ref` or `&` taken, and no boundary or reflection write
    holds its default. A store of the default counts as no store.
  - Fold its loads, keeping the cctor trigger.
  - Iterate with reachability.
- (b) Known-null type tests: `x is T`, `x is T v`, and type-pattern `switch` arms are false when `x` is known null.
  - Today `FoldKnownNull` handles only `== null` and `is null`. Replacing `GetData(...)` with a literal `null` saved
    nothing, which shows the type-pattern case is missing.
- (c) Known-null callees: a callee is known-null when every return is null.

**Condition.** The boundary never calls `AppContext.Setup`, and `SetData`/`SetSwitch` are unreachable. If cs2wasm
ever passes runtimeconfig properties, fold the keys it passes as constants instead.

**More from the same rule**
- `SR`'s static constructor → `GetUsingResourceKeysSwitchValue` → `TryGetSwitch` folds to false. That removes
  `TryGetSwitch` (173), `GetData` (141), `Dictionary<string,bool>` and `Dictionary<string,object>` (about 710), the
  `bool.TryParse` / `TrimWhiteSpaceAndNull` chain (about 500), and `char.IsWhiteSpace`'s table reads. About 1,900, in
  every module.
- Once `UsingResourceKeys()` is false, every specialized `SR.Format` copy loses its
  `string.Join(", ", resourceFormat, p1, ...)` branch. That removes `string.Join`/`JoinCore` (287) and about half of
  each of the ~24 constant-format copies, about 1,200.
- Do not substitute `GetUsingResourceKeysSwitchValue` unconditionally. ILLink stubs it to `true` only under the
  feature switch.

Confidence: high.

### 3. Never build a `CultureInfo` on the Console path. Measured −29,167 instructions (−23%) for the four changes together.

**The combination.** Item 2's `GetInt16Config` fold plus 3c, 3d and 3e (below) leave *no* reachable `CultureInfo`:
97,697 instructions and 1,581 methods.
- Leaving out any one of the four brings `CultureInfo` back. The cost of leaving each out is: item 2's
  `GetInt16Config` fold 27,792, 3c (NumberFormatInfo) 7,636, 3d (Type filters) 18,204, 3e (TextWriter) 8,058.
- Items 2, 3a and 3b with 3d, needing no condition about culture assignment, give 100,995 (−25,869).
- `CultureInfo`, `CultureData`, `CalendarData` and the `DateTimeFormatInfo` cluster are already in the empty-module
  baseline, so most of this applies to every module.

**What NetWasm did (copy only the idea).** A process-global mirror, `CurrentCultureMirror` / `CurrentNumberFormatInfo`:
- `CultureInfo.OnCurrentCultureChanged()` runs from the `AsyncLocal` callback and the `DefaultThreadCurrentCulture`
  setter.
  - For the invariant culture it stores null.
  - Otherwise it stores the culture's `GetFormat(typeof(NumberFormatInfo))`.
- `NumberFormatInfo.CurrentInfo => CurrentNumberFormatInfo.Value ?? InvariantInfo`.
- `InvariantInfo` is a standalone read-only instance.
- Console formats with a null provider.

In a program that never assigns a culture, the mirror has no reachable store, so its compiler reads it as null.
For cs2wasm the global mirror is D/E: culture is per thread and per async flow, and the mirror caches `GetFormat` for
`CultureInfo` subclasses. Use 3c instead.

**3a. Constant sets per virtual slot (A). −8,186 alone.**
- **Upstream:** `CultureInfo.GetFormat(Type)` has `typeof(NumberFormatInfo)` and `typeof(DateTimeFormatInfo)`
  branches. Every reachable call passes `typeof(NumberFormatInfo)`: `GetProviderNonNull`, and `CurrentInfo`'s
  `_isInherited` path.
- **Rule:**
  - For each slot and parameter, collect the constants passed at all reachable call sites. The set is "unknown" if any
    argument is not constant or the method is visible to reflection.
  - Fold `param == typeof(X)` inside every implementation.
  - Re-scan to a fixpoint as call sites appear.
- **Removes:** `CultureInfo.DateTimeFormat`/`Calendar`, the `DateTimeFormatInfo` constructor and
  `InitializeOverridableProperties` (329), the `GregorianCalendar` constructors, `CalendarData.CreateInvariant`
  (1,170), the `CalendarData` constructor (330), and the `GregorianCalendarTypes` `SR.Format` that boxes an enum.
  About 3,100 of `System.Number` goes too.

**3b. Optimistic field-value analysis (A). −6,506 alone.**
- **Upstream:** `CultureInfo.CompareInfo => _compareInfo ??= (UseUserOverride ? GetCultureInfo(_name).CompareInfo :
  new CompareInfo(this))`.
- `CultureData._bUseOverridesUserSetting` is stored as false at `CultureData.cs:551`. The stores at `:783`/`:801` are
  on paths that die once `Invariant`/`PredefinedCulturesOnly` fold `GetCultureData` to null.
- **Rule:** assume a field holds the constant from its only live store, and confirm at the fixpoint that no other store
  is reachable.
- **Removes:** `GetCultureInfo(string)` (290), `CachedCulturesByName`, `Dictionary<string,CultureInfo>` (1,475),
  `CultureNotFoundException` (`Message` 159, `FormattedInvalidCultureId` 112), `string.Equals(string,
  StringComparison)` (223), `TextInfo.ToLowerAsciiInvariant` (252), and about 800 in `Interop+Sys`.
- Agreeing constant arguments (item 9) also cut the other `GetCultureData` root:
  `cultureName.Equals(UndeterminedCultureName, OrdinalIgnoreCase)`.

**3c. Decouple `NumberFormatInfo.CurrentInfo` / `InvariantInfo` from `CultureInfo` (B+C). −7,636 once the other roots
are gone (+353 alone).**
- **Upstream:**
  - `NumberFormatInfo.cs:411`: `CurrentInfo` → `CultureInfo.CurrentCulture`.
  - `:224`: `InvariantInfo => field ??= CultureInfo.InvariantCulture.NumberFormat`. This builds the invariant culture:
    `CreateCultureWithInvariantData` (1,483), `NumberFormatInfo(CultureData)` (453), `GetNFIValues` (702).
- **Three substitutions:**
  - `InvariantInfo => field ??= new NumberFormatInfo { _isReadOnly = true }`. The defaults equal what `GetNFIValues`
    writes for the invariant culture.
  - `CultureInfo.NumberFormat` returns `InvariantInfo` for `s_InvariantCultureInfo`, so the two objects stay
    identical.
  - `CurrentInfo`: `var c = s_currentThreadCulture ?? s_DefaultThreadCurrentCulture; if (c is null) return
    InvariantInfo;` followed by upstream's code.
- **Why this is exact:** in invariant mode `GetUserDefaultCulture()` is always `InvariantCulture`
  (`CultureInfo.Unix.cs`).
- **Where the gain comes from:** the culture fields then fold to null through item 2's rule (a), when the
  `CurrentCulture`, `DefaultThreadCurrentCulture` and `Thread.CurrentCulture` setters are unreachable.

**3d. Drop initializers of static fields that are never read (A). −1,717 alone; it is one of the four culture roots.**
- **Upstream:** `Type.cs:887-893` sets `FilterAttribute = FilterAttributeImpl`,
  `FilterName = (m, c) => FilterNameImpl(m, c, Ordinal)`, `FilterNameIgnoreCase`, and also `EmptyTypes`, `Missing` and
  `Delimiter`, all in one cctor.
- The cctor is triggered by `Type.EmptyTypes` in `RuntimeType.GetGenericArguments` (`RuntimeType.Mono.cs:1424`).
- **Rule:** if a static field has no reachable load outside its own cctor store, and its initializer has no side
  effects, drop the store and do not compile the initializer. Side-effect-free means a delegate over a static lambda
  or method group, a literal, `Array.Empty`, or `new T()` whose constructor writes only `this`.
- **Removes:** `FilterAttributeImpl` (568), `FilterNameImpl` (258), `MemoryExtensions.Equals`/`StartsWith(...,
  StringComparison)` (470), `EndsWith`/`Trim`/`LastIndexOf` (241), and `CompareInfo.Compare`/`IsPrefix`.
- **The same rule elsewhere:** `Task.s_ecCallback` keeps `Task<VoidTaskResult>.InnerInvoke` (177) and two `Func.Invoke`
  bodies (about 300). `ArrayPoolEventSource.Log` is read only as the receiver of calls substituted to false (about 80).
- NetWasm has no such pass; it avoids this only because its `RuntimeType` never touches `EmptyTypes`.

**3e. `TextWriter.Write(int)` evaluates the virtual `FormatProvider` first (C, guarded by class-hierarchy analysis).
−8,058 once the other roots are gone.**
- **Upstream:** `TextWriter.cs` has `Write(int value) => Write(value.ToString(FormatProvider))`, and
  `FormatProvider => _internalFormatProvider ?? CultureInfo.CurrentCulture`.
- `Number.FormatInt32(value, 0, null, provider)` reads the provider only when `value < 0`. The JIT never builds a
  culture for non-negative values only because it inlines everything; cs2wasm evaluates `CurrentCulture` eagerly.
- **What NetWasm did:** `ConsoleTextWriter` calls `value.ToString()`.
- **Substitution:** `Write(int/uint/long/ulong/...)` becomes
  `Write(_internalFormatProvider is { } p ? value.ToString(p) : value.ToString())`, the same for each numeric type.
- **Why it is exact:** for primitives, `ToString(CultureInfo.CurrentCulture)` equals `ToString()`, because both resolve
  `NumberFormatInfo` the same way.
- **Condition:**
  - No instantiated `TextWriter` subtype inherits `Write(T)` while overriding `FormatProvider`. In this build
    `SyncTextWriter` and `NullTextWriter` override both, and `StreamWriter` overrides neither.
  - Optionally, no instantiated `CultureInfo` subclass whose `GetFormat` has side effects.

**Do not copy:** NetWasm's culture-sensitive `StringComparison` throwing, the `[ThreadStatic]` culture fields turned
into plain statics, the global mirror, and its 169-line stand-in `CultureData`.

### 4. Environment reads reach the whole UTF-8 decoder (A, or B under a whole-program condition). About 8,200–12,000.

**Upstream chain.** `SharedArrayPoolStatics.GetPartitionCount` / `GetMaxArraysPerPartition` →
`TryGetInt32EnvironmentVariable` → `Environment.GetEnvironmentVariableCore_NoArrayPool` → `GetEnvironmentVariableCore`.
- That is `Environment.Variables.Unix.cs`: `if (s_environment == null) return
  Utf8StringMarshaller.ConvertToManaged(Interop.Sys.GetEnv(variable));`.
- It continues: `Marshal.PtrToStringUTF8` → `string.CreateStringFromEncoding` → the virtual
  `UTF8Encoding.GetCharCount`/`GetChars`.
- This entry point bypasses the public `GetEnvironmentVariable` substitution.

**What it costs**
- `Utf8Utility.TranscodeToUtf16` (1,450), `GetPointerToFirstInvalidByte` (932), `Utf8.ToUtf16` (341),
  `Encoding.GetCharsWithFallback` (416), `UTF8Encoding.GetCharsWithFallback` (255), `Rune.DecodeFromUtf8` (346), and
  the whole `DecoderFallbackBuffer` family.
- `ThrowLastBytesRecursive` (273). It is probably the only user of `StringBuilder.AppendInterpolatedStringHandler`,
  `DefaultInterpolatedStringHandler` and `byte.TryFormat`, which are about 3,000 more (not verified).
- `Dictionary<string,string>` (375), `IndexOfNullByte` (326), `TrimStringOnFirstZero`.

**What NetWasm did.** `GetPartitionCount() => Environment.ProcessorCount` with no environment read, and
`ProcessorCount => 1`. The `ProcessorCount` part is D for cs2wasm.

**Rule for cs2wasm**
- `s_environment` is unwritten unless `SetEnvironmentVariable` or `GetEnvironmentVariables` is reachable, so item 2's
  rule (a) folds it.
- The boundary's `Interop.Sys.GetEnv => null` makes `ConvertToManaged` known-null (rule c).
- The `is string envVar` in `TryGetInt32EnvironmentVariable` folds by rule (b).
- With these rules there is no need for a hard substitution, which removes an existing deviation:
  - Substituting `GetEnvironmentVariable(string)` to empty drops `ArgumentNullException.ThrowIfNull(variable)`.
  - It also ignores values set with `SetEnvironmentVariable`.

Confidence: high on the chain, medium on the StringBuilder part.

### 5. Do not register `Gen2GcCallback`, so no SharedArrayPool trimming (B). About 5,300.

**What it removes.** `~Gen2GcCallback`, `SharedArrayPool<T>.Trim` (667), the `Partitions` trim methods (418),
`GetMemoryPressure`, `GC.GetGCMemoryInfo`, all of `ConditionalWeakTable<SharedArrayPoolThreadLocalArray[],object>`
(Container and Enumerator, about 2,800), `DependentHandle` and `WeakGCHandle`.

**Fact.** cs2wasm has no generational collections, and .NET never promises a gen-2 GC. So
`Gen2GcCallback.Register` → no-op and `_allTlsBuckets` → unused are platform facts.

**What NetWasm did.** It gates registration on a monotonic-clock import and drops the CWT because it is single-threaded
(E). For cs2wasm, keep the per-thread buckets; only the trimming goes.

This appears to be in progress in the working tree's manifest already.

### 6. A getter whose body is a foldable expression must fold (A). About 4,250 (4,207 of it in the baseline).

**Upstream.** `SpanHelpers.Packed.cs:18`: `public static bool PackedIndexOfIsSupported => Sse2.IsSupported;`. It guards
`SpanHelpers.T.cs` (`IndexOfValueType`, `IndexOfAnyValueType`, `ContainsValueType`, `IndexOfAnyInRange`) and
`String.Manipulation.cs:1376`.

**Why it is not folded.** `Folding.KnownBool()` folds a library getter only when its body is a literal. Because the left
operand of `&&` is not then "pure", the chain stays live even for `TValue = int`. The dump shows
`CanUsePackedIndexOf<int>` reached.

**Rule**
- Fold static, parameterless, non-generic getters or methods whose body is one expression or one `return`, recursively
  with a depth limit.
- Treat a static property read whose getter folds as pure, so `A && false && C` folds.
- The same shape: `IndexOfAnyAsciiSearcher.IsVectorizationSupported` and `AsyncInstrumentation.Is*Supported`.

**Removes.** All `PackedSpanHelpers.IndexOf*` bodies (652 + 3 × 500), `PackSources`, `ComputeFirstIndex*`, and the
`Vector128<byte/short>`, `Vector64` and `Sse2.PackUnsignedSaturate` operators and their `ThrowForUnsupported*` calls.

NetWasm copied these files unchanged; its compiler folds the guard.

### 7. Format slow paths for boxed integers (A). Up to 9,922 (measured bound: 97,697 → 87,775 after item 3).

**What stays live.** `FormatInt32Slow` (345), `TryFormatInt32Slow` (363), the `UInt32` variants (304/313),
`NumberToStringFormat` (1,836), `FormatFixed` (523), `NumberToString` (439), `GetCurrencyFormat`/`GetPercentFormat`,
`FindSection`, `ParseFormatSpecifier`.

They are live as the `IFormattable.ToString` / `ISpanFormattable.TryFormat` implementations of `int`/`uint`. They are
called from `ValueStringBuilder.AppendFormatHelper`, which a non-lowered `SR.Format(string, object)` copy reaches, and
there the item format is parsed at run time.

**What NetWasm did.**
- `Number.WithoutFormat.cs` adds `FormatInt32WithoutFormat` and `TryFormatWithoutFormat<T>` for format-less
  interpolation holes.
- `Number.IntegerFormatting.cs:245` adds a separate `FormatInt32(int, IFormatProvider?)`.
- Its own comment says upstream relies on the JIT inlining an `[AggressiveInlining]` method that has a constant format.

**Rules**
- Use item 3a's per-slot constant sets: if every reachable call into a slot passes `format == null` or `default`, then
  `string.IsNullOrEmpty(format)` / `format.Length == 0` folds.
- Or lower *every* `SR.Format` / `string.Format` so that `AppendFormatHelper` becomes unreachable.
- Extend constant-argument specialization to callees whose top-level `if` is decided by the constant, such as
  `int.ToString(IFormatProvider)` → `FormatInt32(m_value, 0, null, provider)`. That method is neither
  `[AggressiveInlining]` nor a forwarder.
- The `$"\\x{b:X2}"` in `DecoderFallbackBuffer.ThrowLastBytesRecursive` keeps the hex path unless item 4 lands.

### 8. Lower `ThrowHelper` switch-to-string tables to data (A). About 2,200.

**Upstream.** `ThrowHelper.GetArgumentName(ExceptionArgument)` (1,214) and `GetResourceString(ExceptionResource)`
(1,010) are big `switch` statements in which every arm returns a literal.

**What NetWasm did.** Identical source. Its `ConstantCallEvaluator` folds each call to a string once the argument is a
constant passed through a `[DoesNotReturn]` helper.

**Rules (either one)**
- (a) Compile "a switch on an integer parameter whose arms all return literals, plus a default" as a bounds-checked
  load from a string table in the data segment. That is about 15 instructions each.
- (b) Extend constant-argument specialization to the tiny `[DoesNotReturn]` throw helpers
  (`ThrowArgumentNullException(ExceptionArgument)` and similar), then evaluate the pure callee.

Rule (a) is more general.

### 9. Agreeing constant arguments with a reachability fixpoint (A). About 2,100 in the baseline, plus part of 3b.

**`RuntimeType.ToString()`.** It is `getFullName(false, false)`, and `getFullName` starts with
`if ((full_name || assembly_qualified) && !IsFullNameRoundtripCompatible(this)) return null;`. With constant
`false, false` the following die: `IsFullNameRoundtripCompatible`, `GetRootElementType`, `ContainsGenericParameters`
(232), `GetGenericArguments(+Internal)`, the `FunctionPointer*` methods (406), `IsGenericTypeDefinition`/`TypeCache`
and `SafeGPtrArrayHandle`. That is about 2,084 in 41 methods, and it also removes the `Type` cctor trigger from 3d.

**In the 42 build.** `FullName.get` passes `(true, false)`, which is reached through `ObjectDisposedException`. So
clone `getFullName` per constant call site.

**Rule (NetWasm's `ConstantArgumentProgramReducer`).** For a non-virtual, non-exported method, if every reachable call
site passes the same constant for parameter i, and the parameter is never stored or address-taken, substitute the
constant and fold again. Otherwise clone per constant when the body branches on that parameter.

**Also covered**
- `GC.AllocateUninitializedArray<T>` → `AllocateArray<T>(len, pinned: false)` (about 80).
- `string.Equals(a, b, <constant StringComparison>)` and `MemoryExtensions.Equals/StartsWith(..., StringComparison)`
  (150–300 here, more in general).
  - NetWasm edited `NonRandomizedStringEqualityComparer` to call `EqualsOrdinalIgnoreCase` directly; its stated reason
    is that the culture branches dragged in CultureInfo.
  - Specializing on the constant enum gives the same result without editing the source.

### 10. StreamWriter's `encoding ?? UTF8NoBOM` and `UTF8Encoding._isThrowException` (A). About 1,400.

**Upstream chain**
- `StreamWriter.cs:139`: `_encoding = encoding ?? UTF8NoBOM;` makes `EncodingCache.UTF8NoBOM = new UTF8Encoding(false,
  throwOnInvalidBytes: true)` reachable. It is the only reachable caller of `UTF8Encoding(bool, bool)`.
- The virtual `UTF8Encoding.SetDefaultFallbacks` then reads `if (_isThrowException)` and builds both *exception*
  fallbacks, so all their buffer overrides become live.

**Rules**
- (a) Non-null return inference. A method whose every return is `new ...`, or a parameter or `this` already
  dereferenced on all paths, returns non-null, so `x ?? y` folds to `x`. `ConsoleEncoding.RemovePreamble` reads
  `encoding.Preamble.Length` before `return encoding;`.
- (b) Item 2(a) for `readonly` instance fields: a field assigned only in unreachable constructors reads as default.
- RTA then sees that the exact type `UTF8Encoding` is never constructed (only `UTF8EncodingSealed` and
  `ConsoleEncoding` are).

**Removes.** `EncoderExceptionFallbackBuffer.Fallback` ×2 (235 + 80), `EncoderFallbackException` constructors (201),
`DecoderExceptionFallbackBuffer.Throw` (266), two `SR.Format` copies (238), and `UTF8Encoding.Preamble.get` /
`GetMaxByteCount` (192).

### 11. Replace the UTF-16→UTF-8 transcoder with a boundary method (C). About 1,450.

**What it costs.** `Utf8Utility.TranscodeToUtf8` (988) with its helpers and `Ascii.NarrowUtf16ToAscii` (280): 1,759
instructions in 23 methods, all already scalar.

**Substitution.** A boundary method with exactly the same contract:
- returns `OperationStatus` (`Done`, `DestinationTooSmall`, `InvalidData`, or `NeedMoreData` for a trailing high
  surrogate);
- leaves the out pointers at the first position not consumed and not written.

A compact implementation is about 250 instructions. The same applies to `TranscodeToUtf16` and
`GetPointerToFirstInvalidByte` (3,134) if item 4 does not land.

This is the exact version of what NetWasm did by rewriting `UTF8Encoding`, which is D: it lost the custom-fallback,
recursive-fallback and message behaviour.

### 12. Merge identical code across generic instantiations (A). About 1,700.

- **Duplicate bodies:**
  - `Dictionary<,>.GetBucket(uint)` (74) ×7.
  - `FindValue(string)` (237) ×4, which differ only in the reference type of TValue.
  - `TryGetValue` ×4.
  - `Unsafe.NullRef<>` / `IsNullRef<>` many times.
  - `Interlocked.CompareExchange<T>` / `Exchange<T>` (60–61 each) for 12 reference types. For a reference type `T`
    each is just `CompareExchange(ref Unsafe.As<T, object>(ref loc), ...)` plus a cast.
- **Rule:** merge functions whose emitted bodies are identical after normalizing type and RC indices. Or share
  reference-type instantiations that have the same layout and RC behaviour (`__Canon`-style), or inline them where the
  attribute allows.
- **Do not copy:** NetWasm's Interlocked is non-atomic (D).

### 13. `Array.Copy` with statically identical `T[]` types (A). About 950, plus 265.

**What it costs.** Mono's `Array.CopyImpl` falls to `CopySlow` (444), which pulls in `Cs2WasmArrays.SetBoxed` (197) /
`GetBoxed` (73), `TypeInfo.IsAssignable` (93), `NotYet` and `ArrayTypeMismatchException`. The callers are
`Dictionary.Resize` and `List<T>.Capacity.set`, and both pass the same `T[]` on each side.

**Rules**
- When both static types are `T[]`, and `T` is a value type or a sealed reference type, lower `Array.Copy` to:
  - upstream's null and range checks, with the same exception types and messages;
  - then an RC-aware `Buffer.Memmove<T>`.
- `RuntimeHelpers.ObjectHasReferences(x)` folds when x's static type is an array of a reference-free value type. That
  removes `ClearWithReferences` (216) and `ObjectHasReferences` (49).

**Do not copy:** NetWasm's `Array.cs`, which uses a runtime `InternalCopy` and throws exceptions without messages (D).

### 14. Bulk memory for `Memmove` and `Clear` (C/B). About 750.

**What NetWasm did.** `SpanHelpers.ByteMemOps.cs` under `#if NETWASM` drops the 17–64-byte and block paths, so anything
over 16 bytes goes to `memory.copy`/`memory.fill`.

**Substitution for cs2wasm.**
- Replace the body of `SpanHelpers.Memmove(ref byte, ref byte, nuint)` (401) with the `memmove` boundary.
- Replace `ClearWithoutReferences(ref byte, nuint)` (362) with `memset(..., 0, ...)`.

`memory.copy` handles overlap, so the result is identical.

### 15. Smaller items (A unless noted). Each 100–400.

- **The write-error path in `BrowserConsoleStream.Write` is dead (A or B, about 370).**
  - `ConsolePal.Browser.cs` has `if (bytesWritten < 0) { ... throw Interop.GetIOException(errorInfo); }`.
  - cs2wasm's boundary `Interop.Sys.Write` never returns a negative value.
  - Propagate the ranges of non-virtual callee return values, or declare it as a boundary fact.
  - Removes `StrErrorR` (134), `StrError`, `GetIOException`, `GetLastErrorInfo` and `ErrorInfo`.
- **Static constructors that cannot throw need no `TypeInitFailed` path (about 230).**
  - If a static constructor's body cannot throw after folding, emit no TypeInitializationException path, which removes
    its `SR.Format` copy.
  - NetWasm likewise splits runtime-raised exceptions into one `RuntimeExceptionHelpers.GetXxxException()` per check
    kind, "so a program links only the exceptions its checks can raise". Apply that to
    `ThrowHelpers.Overflow`/`OutOfMemory`/`DivideByZero`, which `Rt.AllocCore` roots.
- **Fold callees that return a constant before folding branches (A plus a B fact, about 220).**
  - `Interop.Sys.SchedGetCpu() => -1` is true even with threads, so `ProcessorIdCache.ProcessorNumberSpeedCheck`
    takes its `< 0` exit.
  - That drops the Stopwatch loop.
  - Do not copy NetWasm's `GetCurrentProcessorId() => 0`.
- **`EqualityComparer<T>.Default` devirtualization and value-type null tests (200–400).**
  - Make `Fold()` return constants for `x == null` when x is a non-nullable value type, as `EmitPattern` already does.
  - Devirtualize `EqualityComparer<T>.Default.Equals/GetHashCode` for a closed T to the sealed `CreateComparer` result,
    as Mono's `mini_handle_call_res_devirt` does.
- **A readonly field's value-set is the union of its reachable constructor stores (about 100).**
  - Every reachable `SafeHandle` is built with `ownsHandle: false` (`ConsolePal.OpenStandardHandle`), so the release
    path never calls `ReleaseHandle`. That removes the `SafeFileHandle.ReleaseHandle` root and `Interop.Sys.Close`.

### 16. The Console plumbing itself. About 4,000 in total; the gain is mostly indirect.

**What NetWasm did.**
- `Console.Out => s_out ?? StandardOutput.Writer`, with no lock, no `Volatile`, no `SyncTextWriter` and no
  `StreamWriter` (`RuntimeServices.cs`).
- The writer is `ConsoleTextWriter` (`Runtime/InteropServices/ConsoleServices/ConsoleTextWriter.cs`). It encodes each
  string with `Utf8.FromUtf16` into a byte array and calls the host directly. Numbers use `value.ToString()`.
  `WriteLine(int)` is one write of `value.ToString() + NewLine`.

**Why this is D for cs2wasm.** The observable differences:
- `Console.Out` is not a synchronized `StreamWriter` wrapper.
- `Console.Out.Encoding` is `Encoding.UTF8`, not the console encoding with the preamble removed.
- `Console.OutputEncoding`'s setter throws.
- There is no thread safety.

**What cs2wasm should do instead.** Keep upstream's Console. The heavy parts it reaches are removed by items 1
(Lock/Task chain), 2, 3e and 10.
- What remains is `StreamWriter` (1,523), `SyncTextWriter` (about 300), `ConsoleEncoding` (about 200), `Console`
  (about 400) and `BrowserConsoleStream` (about 200). That is the honest cost of .NET's Console behaviour.

---

## Category D: NetWasm changes cs2wasm must not copy

**Threads**
- Lock and Monitor are bare recursion counters (no owner check). Interlocked is non-atomic. LowLevelMonitor is a
  no-op.
- `MRES.Wait`, WaitSubsystem waits and LowLevelLock waits throw where .NET blocks.
- `SpinCount => 1`, `ProcessorCount => 1`, `CurrentManagedThreadId => 1`.
- The `WaitSleepJoin` thread state is not recorded.
- The SharedArrayPool is single-threaded (no CWT, one TLS array).

**Globalization**
- A culture-sensitive `StringComparison` throws `PlatformNotSupportedException`.
- The `[ThreadStatic]` culture fields are plain statics, and there is a global culture mirror.
- Integer parse exceptions lose their messages that include the value.

**Exceptions**
- Rewritten messages:
  - `Exception.Message` defaults to "An exception was thrown.".
  - `ArgumentNullException(paramName)` has no "Value cannot be null.".
  - `ArgumentOutOfRangeException` has no "Actual value was X.".
  - `ThrowIfNegative` and its relatives use fixed texts.
  - ObjectDisposedException and ArgumentException are built by concatenation.
- `Array.Copy`/`Clear` exceptions have no messages.
- Formatter-serialization constructors throw.

**Text**
- The `Encoding`, `Encoder`, `Decoder` and fallback rewrite:
  - custom fallbacks are rejected;
  - messages are generic;
  - there is no recursive-fallback detection;
  - the `Preamble` getter allocates;
  - `UTF8EncodingSealed` is gone.
- `StringBuilder` uses a single buffer (no chunks, observable through `GetChunks`).
- `String.Intern` keeps interned strings in a linear `List<string>`.
- `Rune` and `Utf8` are subsets.

**Types and runtime**
- Type names are `type#<id>`, `Namespace` is null, and assembly names are a placeholder.
- Attribute equality is by identity.
- `EnsureSufficientExecutionStack` is a no-op.
- `Environment.StackTrace` throws.
- `TargetFrameworkName` is null.
- `SafeHandle` no longer saves the last P/Invoke error around `ReleaseHandle`.

**Console**
- `Console.Out` has no synchronization.
- `Out.Encoding` is `Encoding.UTF8`.
- `OutputEncoding`'s setter throws.

**Fix in cs2wasm**
- The current `GetEnvironmentVariable(string)` → empty substitution drops `ThrowIfNull` and ignores
  `SetEnvironmentVariable`.
- Replace it with item 4's rules, or guard it with "SetEnvironmentVariable and GetEnvironmentVariables are
  unreachable" and keep the null check.

## Category E: NetWasm-only (no action)

- String layout through `GetRawStringData()`.
- `Buffer.BulkMoveWithWriteBarrier` as a plain memmove, and the fake `Thread.FastPollGC`.
- `IntPtr.Size == 4 ||` in place of `TARGET_64BIT`, because one CoreLib serves both pointer widths.
- The Dictionary serialization members removed.
- The `Number.TryFormatWithoutFormat` edits in `MemoryExtensions` and `DefaultInterpolatedStringHandler`. These exist
  because its reachability runs before inlining; cs2wasm's constant-argument specialization already covers them.
- `PreallocatedOutOfMemoryException` frozen in the image.
- The collector-specific GC, GCHandle and WeakReference runtimes.
- The Task reimplementation tied to its scheduler.
- WIT and host plumbing.

## Already equivalent in cs2wasm (C, keep)

- Enum names from compiler metadata: `Boundary/EnumFormat.cs` corresponds to NetWasm's `EnumAlgorithms` /
  `EnumMetadataView`.
- Stack traces from the runtime: `Rt.StackTraceOf` corresponds to `StackTrace.NetWasm.cs`.
- The Marvin seed from an engine import.
- `Exception.ToString` is already cheap (333).

## Suggested implementation order

1. **Unwritten-field folding, known-null callees, and type patterns on a known null** (items 2 and 4, and the basis of
   3c). This is about 15,000–20,000. It also lets the deviating `GetEnvironmentVariable` substitution be retired.
2. **No instance methods for never-instantiated classes** (item 1, about 12,000 or more).
3. **Dead static-field initializers and foldable expression-bodied getters** (items 3d and 6, about 6,000). Both are
   small changes.
4. **The two culture substitutions** (3c and 3e) and per-slot constant sets (3a). Together with the rules above,
   these give the measured −29,167 for culture.
5. **Agreeing-constant propagation** (items 9 and 3b), **switch tables** (item 8), and the **format slow paths**
   (item 7).
6. **The rest:** items 10–15.
