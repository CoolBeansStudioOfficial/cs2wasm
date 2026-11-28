# Why `Console.WriteLine(42)` is 1.16 MB on the IL front end, and the plan to get it under 28,383 B

Written 2026-11-28 on `claude/size-hello`, from main at d96eb52 (the ownership planner the default). Every number
below was measured on the desk machine with Release builds unless it is marked **estimate**.

## 0. The short version

- `class P { static void Main() { System.Console.WriteLine(42); } }` compiles to **1,157,045 B** and prints 42. (The
  task named 1,155,384 B; the difference is main moving since then.)
- The module is big because a few **leaks** each pull in a general-purpose part of the library:
  - the stock libraries' resource lookup;
  - `string.Format` over every formattable type;
  - number formatting with a format string, which brings culture data, `ArrayPool` and the environment-variable
    parser;
  - reflection's `Type.FullName`;
  - the per-type tables and names, which grow with all of these.
- Each leak sits on an error path that hello world never runs. No single leak but the first is worth much on its own,
  because the others still reach the same machinery.
- **One leak dominates.** `SR.GetResourceString` of the stock System.Console reaches `ResourceManager`, with
  `Activator`, the binder, attribute parsing, assembly-name parsing, satellite lookup and `Task`/`Lock`.
  - Removing it alone: **1,157,045 -> 357,779 B (-799,266)**.
  - With the other eight leaks removed, it alone brings back **1,046,954 B**.
  - The rule that fixes it: fold `SR.GetResourceString("key")` with a constant key to the neutral `.resources` string
    at compile time. CoreLib's `Generated/SR.g.cs` already does exactly this at build time.
- With all nine leaks removed (a measurement only, section 3), the module is **86,115 B**. That is still 3x the bar.
  - What is left is the stock Console's real path: `StreamWriter`, the UTF-8 encoder with its fallback, culture
    lookup in `int.ToString`, il2wasm's runtime, and the frames.
  - The old front end's 27,980 B came with **its own Console boundary** (no `StreamWriter`, no encoder, no
    `SafeFileHandle`; `src/Il2Wasm/Upstream/System.Console/Boundary/EngineConsole.cs`). With the stock System.Console
    IL (SPEC 9), getting under 28,383 B needs the plan's later items as well (section 5), and possibly decision D2.

## 1. How it was measured

- Framework: `node tools/build-corelib.mjs --out .vb/fw`. Compile:
  `dotnet run -c Release --no-build --project tests/Il2Wasm.IL.Tests -- framework .vb/fw hello.cs out.wasm`. Run:
  `node host/run.mjs out.wasm`.
- `IL2WASM_NAMES=1 IL2WASM_IL_REACH=reach.txt IL2WASM_SIZES_FILE=sizes.txt` gives the first-reach chains and each
  function's size. They are read with `tools/il-reach.mjs` and `tools/size-report.mjs`.
- **Added on this branch** (diagnostics only; the module is byte-identical without the switches, checked with `cmp`):
  - **Generator records in the reach file.** A method reached by the compiler's generated code (no method's code
    reached it: the entry, finalizers, the static-init checks, `Rt.ReportUnhandled`) is written as
    `M name G <TAB> gen <the compiler's methods that asked>`, from a stack trace taken only under IL2WASM_IL_REACH.
    Before this, 285 methods (545 KB of subtree) had no parent.
  - **Variant parents.** A method compiled only as a specialization variant (`|own6`, `|retborrow`) is named by its
    first variant, so chains no longer break at it (199 broken links before).
  - **Data records.**
    - `L bytes who text`: each literal, with the method that first asked for it or the generator.
    - `B bytes who`: each data blob, with its generator.
  - **`tools/il-reach.mjs --tree N`**: the first-reach tree from the roots, every subtree of at least N bytes.
  - **`tools/il-reach.mjs --literals`**: the data by what asked for it.
  - **`IL2WASM_SIZE_CUT=text|text...`**: a measurement aid that **changes behaviour**. A method whose name contains one
    of the texts gets no body, so it traps, and nothing that only it reached is imported (`Lower/Phases.cs`,
    `ImportMethod`). It measures the size a fix that makes a path unreachable would remove. Section 3 uses it.
- The first-reach tree is a spanning tree, not a dominator tree, so a subtree's inclusive size is only an upper bound
  on what cutting it removes. The cut experiments give the exact numbers.

## 2. The module, broken down (1,157,045 B)

### 2.1 Sections

| Section | Bytes |
|---|---:|
| code (3,487 bodies) | 675,454 |
| data (one segment, not packed) | 424,944 |
| custom `il2wasm.frames` (packed) | 51,716 |
| function | 3,492 |
| type, import, element, export, global, table, tag, `il2wasm.lines`, name stub | 1,439 |

`IL2WASM_MEM_NAIVE=1` (the naive plan): 1,246,383 B, so the planner saves 89,338 B. `IL2WASM_IL_NO_OPT=1` (no final
WASM pass): 1,609,679 B, so the final pass already saves 452,634 B.

### 2.2 Code by kind (670,897 B of function bodies, from IL2WASM_SIZES_FILE)

| Kind | Bytes | Functions |
|---|---:|---:|
| library methods | 411,424 | 2,311 |
| generic instantiations | 147,794 | 409 |
| `rt.*` runtime helpers | 54,595 | 87 |
| `Il2Wasm.Runtime` methods | 24,898 | 92 |
| dispatch stubs | 15,501 | 247 |
| shared bodies | 10,877 | 165 |
| static initializers | 4,358 | 52 |
| attribute factories | 1,450 | 124 |

The largest `rt.*` helpers scale with the number of constructed types:

| Helper | Bytes |
|---|---:|
| `rt.destroy` | 17,164 |
| `rt.retain_contents` | 11,214 |
| `rt.create_instance` (Activator's switch over every constructed type) | 10,964 |
| `rt.array_clear` | 4,628 |
| `rt.array_copy` | 4,136 |

### 2.3 Code by type (exact, summed by name)

| Type | Bytes | Why it is here |
|---|---:|---|
| System.Number | 58,540 | float (Dragon4), custom numeric formats, parsing |
| System.Globalization.* | 38,149 | culture data, calendars, DateTimeFormatInfo |
| System.TimeZoneInfo | 33,649 | DateTime's "O" format in string.Format's dispatch |
| System.RuntimeType | 32,064 | reflection behind resources and FullName |
| System.Resources.* | 28,989 | ResourceManager, ResourceReader, grovelers |
| System.DefaultBinder | 19,144 | Activator.CreateInstance and Type.GetProperty in resource setup |
| System.SpanHelpers | 17,893 | `SequenceEqual<AdjustmentRule>` / `<string>` (3.9 KB each) and the like |
| System.DateTimeFormat | 16,972 | string.Format's ISpanFormattable dispatch |
| System.Collections.Generic.Dictionary`2 | 12,776 | |
| System.Text.UnicodeEncoding | 10,783 | |
| System.Reflection.AssemblyNameParser | 10,682 | satellite-assembly lookup |
| System.Reflection.CustomAttribute | 10,259 | NeutralResourcesLanguageAttribute lookup |
| System.Text.Unicode.Utf8Utility | 9,679 | |
| System.Reflection.MethodBaseInvoker | 8,721 | |

### 2.4 Data (408,306 B counted by the compiler; the segment is 424,944 with alignment and the name table)

| What | Bytes | Count | Made by |
|---|---:|---:|---|
| type names (`FullName`) of reflectable types | 167,414 | 1,145 | GenerateLate (`IsReflectable`: every constructed type, every typeof, and what the tables lead to) |
| literals in method code | 74,800 | 953 | `ldstr` of reached methods; ThrowHelper's `GetResourceString(ExceptionResource)` switch alone is 17,556 |
| per-type-id tables (12 + 6 tables of 1,246 ids x 4 B) | 89,712 | 18 | GenerateLate / EmitTypeTables, one table per `TypeInfo.*` kind the code reads |
| short names and namespaces | 22,074 | 412 | EmitTypeTables |
| interface and generic-argument id lists | 20,852 | 668 | EmitTypeTables |
| field RVA data (`<PrivateImplementationDetails>`) | 26,920 | 76 | the largest are 12,528, 6,912 and 2,176 (not identified) |
| enum info, assembly table, attribute values, System.Console's `.resources` (LZ-packed): blobs and literals | 6,534 | | |

- The data segment is **not packed**. Only resources and frames go through `DataCompression`.
- **Estimate:** deflate-raw level 9 of the payload is 59,841 B. That is a bound on what packing could reach; the
  module's LZ77 is likely a little worse.

## 3. The reach chains, and what cutting each one removes

### 3.1 The chains

These are first-reach chains from `IL2WASM_IL_REACH`. Each line is reached by the one above it; `->` marks a step,
and `[dispatch on T]` means a virtual or interface call resolved for type T. The number is the subtree's inclusive
bytes (an upper bound).

**Chain A: resources (the dominant one).**

- `Main` -> `Console.WriteLine(int)` -> `Console.get_Out` -> `EnsureInitialized` -> `CreateOutputWriter|own1` ->
  `StreamWriter..ctor|own6` -> `Stream.get_Position` [dispatch on BrowserConsoleStream] -> `ConsoleStream.get_Position`
  -> `Error.GetSeekNotSupported` -> `SR.get_NotSupported_UnseekableStream` -> `SR.GetResourceString` (265,991).
- From `SR.GetResourceString`, two branches:
  - `ResourceManager.GetString(string, CultureInfo)` (176,785), which branches three ways:
    - `InternalGetResourceSet` -> `ManifestBasedResourceGroveler.GrovelForResourceSet` (116,858), which branches
      again:
      - `CreateResourceSet|own7` -> `Activator.CreateInstance(Type, object[])` -> `RuntimeType.CreateInstanceImpl`
        (49,918) -> `DefaultBinder.BindToMethod` (11,621 B on its own), and `RuntimeConstructorInfo.Invoke` ->
        `MethodBaseInvoker.InvokeWithOneArg` / `CheckArguments`.
      - `GetSatelliteAssembly` -> `RuntimeAssembly.InternalGetSatelliteAssembly|own7` -> `Assembly.Load` ->
        `AssemblyName..ctor(string)` -> `AssemblyNameParser.Parse` (18,088).
    - `IDisposable.Dispose` [dispatch on Task] -> `Task.Dispose(bool)` -> `ContingentProperties.SetEvent` ->
      `ManualResetEventSlim.Set` -> `Lock.EnterScope` -> `Lock.TryEnterSlow` -> `Lock.TryInitializeStatics` ->
      `DetermineMaxSpinCount` -> `AppContextConfigHelper.GetInt16Config` -> `IConvertible.ToInt16` [dispatch]
      (36,468).
    - `RuntimeResourceSet.GetObject` (14,375).
  - `SR.get_ResourceManager` -> `ResourceManager..ctor(Type)|own2` -> `CommonAssemblyInit` ->
    `GetNeutralResourcesLanguage` -> `GetCustomAttribute<NeutralResourcesLanguageAttribute>` -> `Attribute.GetAttr` ->
    `CustomAttribute.GetCustomAttributes` (75,637), which branches three ways:
    - `GetBase` -> `GetBasePropertyDefinition` -> `Type.GetProperty` -> `RuntimeType.GetPropertyImpl` ->
      `DefaultBinder.SelectProperty`;
    - `MethodInfo.op_Equality` -> `object.Equals` [dispatch over every constructed type];
    - `GetBaseEventDefinition`.

  The other System.Console keys that reach `GetResourceString` are `NotSupported_UnreadableStream`,
  `NotSupported_UnwritableStream` and `ObjectDisposed_FileClosed`: 4 keys in all.

**Chain B: string.Format over every formattable type.**

- `Console.get_Out` (its static-init check, generated) -> `ThrowHelpers.TypeInitFailed` ->
  `TypeInitializationException..ctor(string, Exception)|own4` -> `SR.Format(string, object)` -> `string.Format` ->
  `FormatHelper` -> `ValueStringBuilder.AppendFormatHelper` (214,437).
- From `AppendFormatHelper`, three branches:
  - `ISpanFormattable.TryFormat` [dispatch over every constructed ISpanFormattable] (148,401):
    - `DateTime.TryFormat` -> `DateTimeFormat.TryFormat` -> `TryFormatO` -> `TimeZoneInfo.GetUtcOffset` -> ... ->
      `CacheTransitionsForYear` (17,372 B on its own; subtree 79,639);
    - `float.TryFormat` -> `Number.FormatFloat` -> `Dragon4` (27,101);
    - `int.TryFormat` with a format -> `TryFormatInt32Slow` -> `NumberToStringFormat` (24,356).
  - `object.ToString` [dispatch over every type] -> `RuntimeType.ToString` -> `getFullName` (41,479).
  - `ValueStringBuilder.Grow` -> `ArrayPool<char>.Shared.Rent` -> `SharedArrayPoolStatics..cctor` ->
    `GetPartitionCount` -> `TryGetInt32EnvironmentVariable` -> `Environment.GetEnvironmentVariableCore` ->
    `GetSystemEnvironmentVariables` -> UTF-8 `GetString`, and `Dictionary<string,string>` (59,602).

**Chain C: UTF-8 decoding in il2wasm's own `SystemNative_Write`, and its fallback's message.**

- `SystemNative_Write` turns the console's UTF-8 back into UTF-16 for the `write` import:
  `Encoding.UTF8.GetChars` -> `GetCharsWithFallback` -> `DecoderFallbackBuffer.TryInternalFallbackGetChars` ->
  `DecoderReplacementFallbackBuffer.Fallback` -> `ThrowLastBytesRecursive` ->
  `StringBuilder.AppendInterpolatedStringHandler.AppendFormatted<byte>(b, "X2")` -> `IFormattable.ToString`
  [dispatch] -> `int.ToString(string, IFormatProvider)` -> `Number.FormatInt32` -> `FormatInt32Slow` (88,112 under
  `SystemNative_Write`).
- From `FormatInt32Slow`, three branches:
  - `NumberToStringFormat` (5,850 B on its own);
  - `NumberFormatInfo.GetInstance` -> `CultureInfo.GetFormat` [dispatch];
  - `ValueListBuilder.Dispose` -> `ArrayPool.Return` -> `SharedArrayPoolStatics..cctor` -> the environment, as in B.

**Chain D: the console write's error path.**

- `StreamWriter.Flush` -> `Stream.Write` [dispatch on BrowserConsoleStream] -> `BrowserConsoleStream.Write(SafeFileHandle,
  byte*, int)` -> (result < 0) `Interop.GetIOException` -> `ErrorInfo.GetErrorMessage` -> `Interop.Sys.StrError` ->
  `Utf8StringMarshaller.ConvertToManaged` -> `Marshal.PtrToStringUTF8` -> UTF-8 decoding with fallback -> chain C's
  formatting (53,643).

**Chain E: `ObjectDisposedException`'s message.**

- (finalizers, generated: SafeFileHandle is constructed) `SafeHandle.Finalize` -> `SafeHandle.Dispose(bool)`
  [dispatch on SafeFileHandle] -> `InternalRelease` -> `ThrowHelper.ThrowObjectDisposedException(object)` ->
  `Type.get_FullName` [dispatch] -> `RuntimeType.getFullName` (27,675).
- From `getFullName`, two branches:
  - `QualifiedName`;
  - `IsFullNameRoundtripCompatible` -> `ContainsGenericParameters` -> `GetGenericArguments` -> `Type..cctor` ->
    `Type.<>c` lambdas -> `FilterNameImpl` -> `MemoryExtensions.Equals` / `StartsWith` with `StringComparison`.

**Chain F: culture lookup in `int.ToString`.** This is the real path, still there at the floor.

- `TextWriter.Write(int)` -> `int.ToString(IFormatProvider)` -> `Number.FormatInt32` -> `NumberFormatInfo.GetInstance`
  -> `CurrentInfo` -> `CultureInfo.GetFormat(Type)` [dispatch] (7,894).
- `GetFormat` reaches both `get_NumberFormat` and `get_DateTimeFormat`. The second leads to `DateTimeFormatInfo..ctor` ->
  `CultureData.LongDates` -> `CalendarData..cctor` -> `CreateInvariant` (2,981).

**Chain G: UTF8Encoding's preamble.** `RemovePreamble` -> `UTF8Encoding.get_Preamble` (its `GetType() ==
typeof(UTF8Encoding)` test) -> `Type.FromId` -> `RuntimeType.FromId` -> `RuntimeType..cctor` (4,101).

`ConsoleStream.get_Position`, the first step of chain A, is not a cause by itself. Cutting it alone saves 58 B,
because the other three System.Console SR getters reach the same `GetResourceString`.

### 3.2 Cut experiments (IL2WASM_SIZE_CUT; plain modules, no name section)

There are three ways to read each cut, so ordering does not hide anything:

- **alone**: only that cut, from the base;
- **cumulative**: the cuts in the order listed;
- **unique**: the size with every other cut, minus the size with all nine cuts (86,115). This is what only that path
  keeps in the module once everything else is fixed.

| # | Cut (path made unreachable) | Alone | Cumulative | Unique at the floor |
|---|---|---:|---:|---:|
| 0 | (none) | 1,157,045 | 1,157,045 | |
| 1 | `System.SR.GetResourceString(` (chain A) | 357,779 (-799,266) | 357,779 | **1,046,954** |
| 2 | `ThrowHelpers.TypeInitFailed` (chain B's entry) | 1,156,042 (-1,003) | 356,565 | 894 |
| 3 | `System.SR.Format(` (chain B) | 1,147,323 (-9,722) | 347,899 | **64,726** |
| 4 | `ConsoleStream.get_Position` | 1,156,987 (-58) | 347,899 | 0 |
| 5 | `Interop.GetIOException` (chain D) | 1,148,854 (-8,191) | 339,373 | **81,966** |
| 6 | `Type+<>c.<.cctor>b__` (Type's filter lambdas) | 1,154,041 (-3,004) | 328,984 | 0 |
| 7 | `UTF8Encoding.GetChars(uint8*,...)` (chain C's entry) | 1,156,368 (-677) | 318,202 | **78,804** |
| 8 | `ThrowHelper.ThrowObjectDisposedException(object)` (chain E) | 1,156,744 (-301) | 272,428 | **82,363** |
| 9 | `SharedArrayPoolStatics.TryGetInt32EnvironmentVariable` (the environment) | 1,156,031 (-1,014) | **86,115** | **186,313** |

Cuts 7 and 9 break the program's output (cut 7 cuts the console's decode). Every other row still prints 42.

At the end of the cumulative column (86,115 B), the reach file shows these left behind:

- 229 literals, 13,478 B;
- other blobs 236 B (the per-type tables are gone, because no reached code reads `TypeInfo.*` any more);
- frames 7,825 B;
- code 62,173 B.

At that floor, the naive plan is 99,826 B and no final pass is 125,434 B.

## 4. The causes, ranked by bytes, with the rule that fixes each

The numbers are the unique-at-the-floor bytes from section 3.2 unless marked. Paths overlap: once the shared
formatting machinery stops being reached (cause 3), causes 4, 5 and 6 cost much less than their unique numbers. Each
rule must keep .NET's behaviour exactly (SPEC 5), messages included.

1. **Stock libraries' resource lookup.**
   - **Bytes:** 799,266 alone; 1,046,954 unique. The real fix adds one literal per key reached (4 keys in hello world;
     **estimate** under 0.5 KB).
   - **Rule: compile-time SR folding.** A call `SR.GetResourceString(k)` (and its two-argument form) in a stock
     library, with `k` a constant, folds to the string the assembly's `FxResources.<Assembly>.SR.resources` holds
     for `k`. The compiler reads that string at compile time.
   - **Why it is exact:**
     - The IL CoreLib is invariant (`Features`: `System.Globalization.Invariant=true`), so `CurrentUICulture` is the
       invariant culture.
     - Browser-wasm has no satellite assemblies, so `ResourceManager` always returns the neutral resource.
     - `UseSystemResourceKeys` is `false` (Features), and the fold applies only under that value, as ILLink's
       substitution does.
   - This is the IL-level form of what `tools/BuildCoreLib` already does for CoreLib (`Generated/SR.g.cs`: every SR
     member a `const string`).
   - A non-constant key, and any key the `.resources` lacks, keep the call. User code that uses `ResourceManager`
     still gets it.
   - The `.resources` blob, the assembly's resource table entries and `FxResources.*` type names then go when nothing
     else reads them.
2. **Environment variables reached through `ArrayPool<char>.Shared`.**
   - **Bytes:** 186,313 unique.
   - **The chain:** `SharedArrayPoolStatics..cctor` reads `DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_*` from the
     environment. That parses the whole environment block (UTF-8 decoding, `Dictionary<string,string>`,
     OrdinalIgnoreCase hashing). The decoding's fallback leads back into cause 3's
     formatting.
   - The read itself is behaviour .NET has, so it stays. What has to go is what reaches the pool.
   - **Rule: the same as cause 3.** Every hello-world path into `ArrayPool` is a `ValueListBuilder` or
     `ValueStringBuilder` in formatting code with a format string (`FormatInt32Slow`, `AppendFormatHelper`).
   - **Estimate:** most of the 186 KB goes once cause 3's specialization and cause 4's lowering are in.
3. **Formatting with a constant format string, specialized at the call.**
   - **Bytes:** chain C's 78,804 unique, plus a share of cause 2.
   - **The calls:**
     - `$"\\x{b:X2}"` in `DecoderFallbackBuffer.ThrowLastBytesRecursive`;
     - `CultureInfo.GetFormat(typeof(NumberFormatInfo))` (chain F).
   - **Rule: constant-argument specialization.**
     - A callee whose body folds on a constant argument at a site (`ParseFormatSpecifier("X2")` is pure on a
       constant; `formatType == typeof(NumberFormatInfo)` folds) gets a variant for that site's constants. The
       variant mechanism (`VariantFor`) and the SCCP's constant arguments (IL2WASM_NO_CONSTANT_ARGS) are there
       today, but only for arguments that every caller passes the same.
     - Specialized, `TryFormatUInt32Slow(b, "X2")` takes the hex path only: no `NumberToStringFormat`, no
       NumberFormatInfo, no ArrayPool.
   - **The other half, at the runtime boundary (ours):** `SystemNative_Write` should hand the console's UTF-8 to the
     host as it is, through a write import that takes UTF-8. The host decodes it with `TextDecoder`, which replaces
     ill-formed input with U+FFFD per maximal subpart, as UTF8Encoding does. That removes the decode, and the
     `char[]` it allocates, from every console write. It changes the host ABI (every host's `write` import), so it is
     not done here.
4. **Message formatting through `string.Format`.**
   - **Bytes:** chain B's 64,726 unique; 9,722 alone.
   - **Rule: format lowering** (the old front end's `Compiler/FormatLowering.cs`). `string.Format` and `SR.Format`
     with a constant format string, whose arguments' value types are all `string` (or null), lower to
     `string.Concat` of the pieces. That is exact: a string argument formats as itself, and null formats as "".
   - With CoreLib's SR constants and cause 1's folding, `SR.TypeInitialization_Type` is a constant at
     `TypeInitializationException(string, Exception)`, and `fullTypeName` is a `string` parameter.
   - **The general rule behind it:** dispatch only over the receiver's type set at the site (the `arg is
     ISpanFormattable` test and the `object.ToString` dispatch in `AppendFormatHelper`), using TypeSets.
   - Static initializers that cannot throw need no `TypeInitFailed` (894 B unique).
5. **`ObjectDisposedException(GetType().FullName)` in the SafeHandle finalizer.**
   - **Bytes:** chain E's 82,363 unique.
   - **Rule: evaluate reflection on a known type at compile time.**
     - `GetType()` on a receiver whose type set is one exact type (`this` in `SafeHandle.InternalRelease`, with
       SafeFileHandle the only constructed SafeHandle) folds to that type's Type object.
     - `FullName` (and `Name`, `Namespace`) of a constant Type folds to its literal.
   - **The second rule: drop unread static initializers.** The stores `Type..cctor` makes to `FilterAttribute`,
     `FilterName` and `FilterNameIgnoreCase` are never read by reached code (only `FindMembers` and
     `FindInterfaces` read them), so their delegates and targets need not be made. Measured: 3,004 B alone.
6. **The console write's error path (`result < 0`).**
   - **Bytes:** chain D's 81,966 unique; 8,191 alone.
   - **Rule: integer ranges in the SCCP, through returns and field value sets.**
     - `BrowserConsoleStream`'s handle is `SafeFileHandle(1 or 2)` (field value sets, IL2WASM_NO_FIELD_VALUES).
     - `SystemNative_Write` for descriptors 0 to 2 returns 0 or `bufferSize`, and never a negative value (return
       values, IL2WASM_NO_RETURN_VALUES, extended from constants to ranges).
     - So `if (bytesWritten < 0)` is false, and the IOException path is not reached.
   - This matches what docs/AUDIT.md's IL front-end entry already names as "integer ranges and flow-sensitive field
     values are the next general rules".
7. **Reflectable types and their tables.**
   - **Bytes:** 167,414 B of type names, 89,712 B of tables, 22,074 B of short names and namespaces, 20,852 B of id
     lists, and `rt.create_instance` 10,964 B. That is 310 KB in the base. It falls with causes 1 to 6: at cut 1 it is
     391 names (48,586 B); at the floor, no tables at all.
   - **Rule (on top):** a constructed type is reflectable only when a Type object of it can reach code that reads
     the tables: the TypeSets of `GetType()` receivers, plus typeof. Today every constructed type is reflectable
     (`Runtime2.cs`, `_constructedOrder`).
   - **Estimate:** after cause 1, about 40 KB of the 58 KB of names. At the floor, little.
8. **The data segment is not packed.**
   - **Bytes:** data 424,944 B in the base (**estimate:** deflate 59,841); 15,063 B at the floor (**estimate:** deflate
     3,630).
   - **Rule:** SPEC 5's packing of data. Pack the whole segment with `DataCompression`, as the old front end did and
     as the IL front end already does for resources and frames.
   - **Estimate:** about 11 KB at the floor.
9. **`GetType() == typeof(X)` and exact-type tests.**
   - **Bytes:** chain G, 4,101 B at the floor; `RuntimeType..cctor` and `ThrowHelpers.InvalidCast` come with it.
   - **Rule:** an object-type test against a type set of one exact type folds (devirtualization of `GetType()`).
   - **Estimate:** about 4 KB.
10. **Culture lookup in `int.ToString(IFormatProvider)` (chain F).**
    - **Bytes:** 7,894 B at the floor, of which the DateTimeFormatInfo half is 2,981.
    - **Rule:** cause 3's specialization, at `CultureInfo.GetFormat(typeof(NumberFormatInfo))`.
    - **Estimate:** about 3 KB.

Already in, and measured:

- the ownership planner: -89,338 B in the base, -13,711 B at the floor;
- the final WASM pass: -452,634 B in the base, -39,319 B at the floor.

## 5. The plan, in order

The order runs from what removes the most to what needs the others first. The bytes are this report's measurements
unless marked **estimate**.

1. **Compile-time SR folding** (cause 1).
   - **Expected:** 1,157,045 -> about 358 KB (measured alone at 357,779, plus under 0.5 KB of literals: **estimate**).
   - A conformance test is needed whose output changes when the fold is broken: a stock library's exception message,
     for example `new FileStream` on a missing file, or `Console.OpenStandardInput().Position`.
2. **Format lowering and site type sets in `AppendFormatHelper`** (cause 4).
3. **Constant-argument specialization** (causes 2, 3 and 10). The runtime boundary change, UTF-8 to the host, comes
   with it.
   - Measured together with item 2 and cut 9: 357,779 -> 86,115 once items 4 and 5 are in too.
   - The cut column cannot split items 2 to 5 exactly, because they share the formatting machinery.
4. **Integer ranges through returns and field value sets** (cause 6).
5. **Compile-time reflection on exact types, and unread static initializers dropped** (causes 5 and 9).
6. **Reflectable types narrowed** (cause 7), then **the data segment packed** (cause 8).
   - **Estimate:** 86,115 -> about 72 KB (-11 KB packing, -4 KB exact-type tests; little from names at the floor).
7. **What remains, about 70 KB (estimate),** is the stock Console's real path. At the floor it is:
   - il2wasm's runtime (`Il2Wasm.Runtime` + `rt.*`): 17,418 B (allocator, `PageAlloc`, ObjectMarks for
     `GC.SuppressFinalize`, ReturnKeepers, Monitor, IntMap);
   - the UTF-8 encoder with its fallback buffers and `EncoderNLS` (ConsoleEncoding wraps UTF-8, so its `GetBytes`
     takes Encoding's generic `GetBytesWithFallback`): about 15 KB;
   - globalization: about 8 KB;
   - `StreamWriter`: 1.6 KB;
   - `Errno` tables: 2,017 B (from `SystemNative_ConvertErrorPlatformToPal`);
   - `Rt.ReportUnhandled` and `Exception.Message`: about 3 KB;
   - frames: 7,825 B.

   Getting this under 28,383 B needs one of two things:
   - **Decision D2** (docs/IL-FRONTEND.md section 4): an il2wasm boundary for System.Console, as the old front end's
     `EngineConsole.cs`. Console.Out becomes a TextWriter that hands UTF-16 to the host, with no StreamWriter, no
     encoder and no SafeFileHandle. That is how 27,980 B was reached. SPEC 9 says the libraries above CoreLib are
     stock and unmodified, so this is the owner's call.
   - **Or code density work** on the runtime half and the encoder path, which this report did not measure.

## 6. What was not measured

- **The real fixes.** Every saving in section 3 comes from making a path trap (IL2WASM_SIZE_CUT). The real rules
  leave a small residue: literals, a specialized variant, a range check. Those residues are estimates.
- **The overlap between causes 2, 3, 4 and 6.** They share the formatting machinery, so their unique numbers do not
  add up. The cumulative column is exact only for that order.
- **How NetWasm's 28,383 B is made up.** NetWasm is a reference only, and how it treats System.Console was not
  examined.
- **The old front end's 27,980 B module, broken down.** Its compiler is no longer built here. Its Console boundary
  is read from the source above.
- **What `DataCompression` itself achieves** on the main data segment. The deflate figures are a stand-in.
- **The cost of the frames section** against what stack traces need. It is required for traces and was left as it is.
- **The suite** was not run: the switches added here are off by default, and the hello module is byte-identical with
  them off (`cmp`).
