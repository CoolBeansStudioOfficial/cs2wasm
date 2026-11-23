# The core library as IL (SPEC 9; docs/IL-FRONTEND.md step 1)

Written 2026-11-23 on `claude/il-corelib`. Everything below was measured on the desk machine unless it says otherwise.

## 1. What is built, and how

`tools/BuildCoreLib` is a .NET console tool. It is a Roslyn build of
`System.Private.CoreLib` ahead of time. It uses the inputs the old compiler's `Library` used at every compile
(src/Il2Wasm/Compiler/Library.cs), which are:

- the 1,676 dotnet/runtime v11.0.0 files that `src/Il2Wasm/Upstream/System.Private.CoreLib/manifest.txt` lists;
- its 25 `define`s, including `MONO`, `TARGET_WASM`, `TARGET_32BIT`, `TARGET_BROWSER` and `FEATURE_MULTITHREADING`;
- its 28 exact `edit`s;
- il2wasm's own files beside the manifest: `Boundary/` (the runtime), `Runtime/` (the Mono-derived runtime half) and
  `Generated/` (SR, unavailable platform invokes). They are named `il2wasm/upstream/System.Private.CoreLib/...` as the
  compiler names them.

```
dotnet build tools/BuildCoreLib -c Release
dotnet tools/BuildCoreLib/bin/Release/net11.0/BuildCoreLib.dll build [--out DIR]     # default artifacts/framework
node tools/build-corelib.mjs [--out DIR] [--check]   # both; --check rebuilds into a new directory and compares every file
```

**The output is a framework directory.** It is the one directory the IL front end loads, and it holds:

| File | What |
|---|---|
| `System.Private.CoreLib.dll`, `.pdb` | il2wasm's CoreLib: about 6.0 MB of IL, with a portable PDB |
| 180 `*.dll` | the stock IL of `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` 11.0.0 (`runtimes/browser-wasm/lib/net11.0`), copied byte for byte (section 2) |
| `il2wasm.substitutions.txt` | the manifest's 156 `substitute` lines, kept for call-site folding |
| `framework.txt` | the SHA-256 and size of every file |

**Build settings.**

- Release optimization; `deterministic: true`; nullable enabled, as upstream builds CoreLib.
- Language version preview, `allowUnsafe`.
- Source paths are logical (`dotnet/runtime/src/...`, `il2wasm/upstream/...`), so no path map is needed. The PDB path
  is the bare file name.
- Sources are read as UTF-8 with SHA-256 checksums.
- The assembly is version 11.0.0.0 with the stock CoreLib's public key (public-signed). The stock libraries
  reference `System.Private.CoreLib, Version=11.0.0.0, PublicKeyToken=7cec85d7bea7798e`, so this identity lets ours
  stand in for theirs.

**Substitutions become bodies.** The manifest's `substitute method` lines are applied to the source before the
build. The new body is what `FunctionCompiler.EmitSubstitutedBody` emitted:

- a constant is returned;
- an empty value gives `{ }` or `default`;
- `=>Type.Method` is a call to that boundary method with the instance first. An enum argument is converted where the
  boundary method declares the underlying type.

A get-only auto-property, such as `RuntimeFeature.IsDynamicCodeSupported`, becomes a property with that getter.
`substitute field` lines (two: `Task.s_asyncDebuggingEnabled`, `BitConverter.IsLittleEndian`) are not source
changes. They stay in `il2wasm.substitutions.txt`.

**Measured.**

- A build takes about 9 to 11 s wall clock: parse 0.9 s, substitutions 1.4 s, emit 7 to 8 s.
- 0 errors. 598 warnings, almost all CS8500 (a pointer to a managed type) and CS0649 (a field never assigned).
- Two builds into different directories gave byte-identical files: every SHA-256 in `framework.txt` matched.

**Two fixes were needed to emit.** These are errors that binding tolerated but emit does not:

- `Boundary/Runtime.cs`: a local `cap` in `Rt.AllocObjectSlow`'s page search shadowed a `cap` declared later in the
  enclosing block (CS0136). It is renamed `pageCap`. Nothing else changes.
- The `StackTrace.ToString(TraceFormat)` substitutions pass the enum to an `int` parameter, which the tool now
  converts.

## 2. The libraries above CoreLib: the pack

**Chosen: `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` 11.0.0** (nuget.org).

- **It matches our sources exactly.** Its nuspec names dotnet/dotnet commit 297acf93. That commit's
  `src/source-manifest.json` pins dotnet/runtime at deae2e32bf0e, which is the `v11.0.0` tag our sources are
  checked out at.
- **CoreLib is a separate assembly in it.** It sits in `runtimes/browser-wasm/native/`, apart from the 180 IL
  assemblies in `lib/net11.0`, so ours replaces it without touching the rest.
- **It is the Mono build, as ours is.** Our CoreLib is compiled with `MONO` and Mono's runtime half.
- **The other pack was ruled out.** `Microsoft.NETCore.App.Runtime.browser-wasm` 11.0.0 (CoreCLR on browser, same
  commit) ships CoreCLR's CoreLib inside `lib/` (181 assemblies). Its CoreLib is the CoreCLR flavour, whose internal
  surface differs from the Mono sources we build.

`tools/BuildCoreLib.csproj` takes the pack with a `PackageDownload` of the exact version, so NuGet fetches it, checks
its signature and caches it.

## 3. Intrinsics

See docs/CORELIB-INTRINSICS.md. It is the complete list of what the IL front end must implement:

- 220 `Il2Wasm.Runtime.IntrinsicAttribute` methods, each with its signature and what the old compiler emitted;
- 39 `[Intrinsic]` static fields;
- 40 upstream intrinsics recognised by name;
- 53 `[Import]` host imports;
- the compile-time state they depend on.

**Allocation**, for the IL front end:

- `string.FastAllocateString(int length)` (Runtime/System/String.Mono.cs) is
  `Rt.Take<string>(Rt.AllocString(length))`.
- `Rt.AllocString` is `AllocObject(16 + 2*length + 2, 1)` followed by `StoreI32(a + 12, length)`. The type id of
  `string` is fixed at 1, the length is at +12 and the chars start at +16, with a terminating NUL char.
- `Rt.Take<T>(int)` is an intrinsic: it reinterprets the address as an owned reference.

## 4. Do the stock libraries find everything they reference in our CoreLib?

`BuildCoreLib refs [DIR]` checks the 180 stock assemblies at the IL level. It takes:

- every TypeRef whose scope is `System.Private.CoreLib`, or a facade of the pack that forwards the type there
  (`System.Runtime`, `System.Threading` and the like);
- every MemberRef whose parent is such a type, or a generic instantiation of one.

Each is looked up in DIR's CoreLib:

- types by full name;
- members by name and decoded signature, with custom modifiers, generic arity and the instance flag, searching the
  base types too;
- types and members must also be visible (public, protected, or nested in visible types).

**Result for our build: 12,172 TypeRefs and 29,032 MemberRefs. 0 types missing, 0 types not public, 0 members
missing, 0 members not public.**

The checker is checked both ways:

- **It finds nothing missing where nothing should be.** Against the pack's own CoreLib it reports 0. It also reports
  0 against the CoreCLR browser pack's CoreLib and the installed 11.0.0 shared CoreLib, whose public surfaces are the
  same.
- **It finds what is missing.** Against .NET 8.0.27's CoreLib it reports 37 types missing, 51 members missing and 2
  members not public (for example `Dictionary<,>.GetAlternateLookup`, `SearchValues.Create(ReadOnlySpan<string>, StringComparison)`).

**References the check does not see** are those made by name:

- **Unsafe accessors.** Two of the stock libraries' unsafe accessors reach CoreLib internals, and both are present in
  our build:
  - `Internal.Runtime.Augments.DynamicDelegateAugments.CreateObjectArrayDelegate` (System.Linq.Expressions; ours is
    in `Boundary/DynamicDelegates.cs`);
  - `AssemblyBuilder.ForceAllowDynamicCode`.

  The others target System.Private.Xml.Linq and System.Windows.Forms (the latter is not in the pack).
- **Reflection by string.** It is not checked.

## 5. Native entry points

These are the ways out of IL. `BuildCoreLib natives [DIR]` lists them.

**Our CoreLib has no ImplMap (P/Invoke) at all.** Every `Interop.Sys` call is the boundary's code, ending in host
imports. Its methods without IL fall into these groups:

| Kind | Count | Served by |
|---|---|---|
| `[Il2Wasm.Runtime.Intrinsic]` | 220 | the IL front end (docs/CORELIB-INTRINSICS.md) |
| `[Il2Wasm.Runtime.Import]` | 43 | host imports (module `il2wasm`): `Engine.*` (Write, NowMs, MonotonicNs, RandomSeed, UtcOffsetMinutes, ProcessorCount, ThreadSpawn, CaptureStack, StackDepth, ReportException, MainCanBlock), `CommandLine.Block`, `Env.Block`, `HostOps.Host.*` (2), `Interop.Sys.Fs.*` (28) |
| `InternalCall` | 13 | the 9 `String` constructors (the front end lowers `newobj` of them to the matching private static `string Ctor(...)` in String.cs, as NativeAOT and Mono do); `SpanHelpers.memmove`/`memset` (already upstream intrinsics: `memory.copy`/`memory.fill`); `Thread.CurrentThreadIsFinalizerThread`; `Interop.JsGlobalization.GetLocaleInfo` (reached only outside invariant mode; it needs a body or a `MissingCapability` before hybrid globalization is reachable) |
| runtime-implemented delegate members | 308 | the front end (delegate `.ctor`/`Invoke`/`BeginInvoke`/`EndInvoke`) |
| `[MissingCapability]` | 27 | none: a build error where reached (Reflection.Emit's runtime half, `Marshal.StructureToPtr`/`OffsetOf`/delegate marshalling, `NativeLibrary.Load*`, `Environment.Exit`, `MetadataUpdater`) |
| `[UnsafeAccessor]` | 7 | the front end (by name, as the CLR resolves them) |

**The stock libraries have 44 ImplMap methods, which are 28 distinct entry points.** il2wasm's runtime has to export
these, or the front end has to bind them to runtime methods, over host imports where they reach outside:

| Library | Entry point | Signature | Used by |
|---|---|---|---|
| libSystem.Native | SystemNative_Write | int (nint fd, byte*, int) | System.Console (**hello world**) |
| libSystem.Native | SystemNative_ConvertErrorPalToPlatform | int (Interop.Error) | Console, IO.Compression.ZipFile, IO.MemoryMappedFiles, Net.Primitives |
| libSystem.Native | SystemNative_ConvertErrorPlatformToPal | Interop.Error (int) | the same |
| libSystem.Native | SystemNative_StrErrorR | byte* (int, byte*, int) | the same |
| libSystem.Native | SystemNative_GetCryptographicallySecureRandomBytes | int (byte*, int) | IO.Compression, Security.Cryptography |
| libSystem.Native | SystemNative_FStat, SystemNative_Stat | int (nint or byte*, FileStatus*) | IO.Compression.ZipFile |
| libSystem.Native | SystemNative_FTruncate, FcntlSetFD, IsMemfdSupported, MAdvise, MMap, MSync, MUnmap, MemfdCreate, ShmOpen, ShmUnlink, SysConf, Unlink | (see the tool) | IO.MemoryMappedFiles |
| libSystem.IO.Compression.Native | CompressionNative_Crc32, Deflate, DeflateEnd, DeflateInit2_, DeflateReset, Inflate, InflateEnd, InflateInit2_, InflateReset2_ | zlib's | IO.Compression, Net.WebSockets |

**There are few because the browser builds of the libraries are mostly `PlatformNotSupported` builds** (sockets,
processes and the rest), and CoreLib holds the rest of `Interop.Sys`.

- **libSystem.IO.Compression.Native is zlib.** It is a native library to ship as WASM (SPEC 8), or an IL port.
- **libSystem.Globalization.Native is not referenced by any stock library.** CoreLib alone used it, and ours runs
  invariant.

**Other ways out of the stock IL:**

- 13 `InternalCall`s:
  - `System.Console`: `ConsolePal.Clear`;
  - `System.Runtime.InteropServices.JavaScript`: 12 (`Interop.Runtime`: BindJSImportST, InvokeJSImportST,
    InvokeJSFunction, the promise calls, the GC-root calls, ReleaseCSOwnedObject, AssemblyGetEntryPoint,
    BindAssemblyExports, GetAssemblyExport).
- 41 `[JSImport]` methods into the browser build's JavaScript (`INTERNAL.*`):
  - 16 in System.Net.Http (fetch);
  - 7 in System.Net.WebSockets.Client;
  - 18 in System.Runtime.InteropServices.JavaScript.

  These are Mono's JS interop. The web framework (SPEC 8.2) and HTTP over the host's fetch would serve them, or
  replace them, through host imports.

## 6. Hello world's reach

`BuildCoreLib reach PROGRAM.dll` walks the IL with rapid type analysis:

- from the entry point, every called, constructed or `ldftn`'d method;
- the static constructor of every type used;
- every virtual method of every constructed or boxed type.

Its count over-approximates what the IL front end will reach, but the set of assemblies it reaches is the set hello
world needs.

`static void Main() => System.Console.WriteLine(42);` (net11.0, `dotnet build -c Release`):

| Assembly | Methods | Types |
|---|---|---|
| System.Private.CoreLib (ours) | 12,310 | 1,109 |
| System.Console (stock) | 89 | 12 |
| hello | 1 | 1 |

- `System.Runtime`, `System.Runtime.InteropServices` and `System.Threading` are loaded only to follow type
  forwarders.
- Nothing is unresolved.

**The ways out of IL it reaches:**

- libSystem.Native's `SystemNative_Write` (stdout), with `ConvertErrorPalToPlatform`,
  `ConvertErrorPlatformToPal` and `StrErrorR` on its error path;
- 24 host imports (`Engine.Write`, the clocks, `RandomSeed`, `ThreadSpawn` and the `Fs` set);
- the internal calls `String..ctor`, `SpanHelpers.memmove`/`memset` and `JsGlobalization.GetLocaleInfo`.

**What changes from today.** Under SPEC 9, `System.Console` is the stock one: the old compiler's
`Upstream/System.Console/Boundary` replacement goes. Its stdout write then arrives as a P/Invoke of
`SystemNative_Write(fd, buf, len)`, which il2wasm's runtime implements over `Engine.Write`.

## 7. Runtime-provided native libraries: the binding table

`src/Il2Wasm/Upstream/System.Private.CoreLib/natives.txt` is copied into the framework directory as
`il2wasm.natives.txt`. It tells the IL front end which of CoreLib's own methods stand behind a P/Invoke or an
internal call.

Format: tab-separated lines; `#` starts a comment. Type names are metadata names (`Namespace.Type`, nested types after
`/`). Every target is a static method of System.Private.CoreLib.

```
library       <module name>                                           the runtime provides this native library
pinvoke       <module name>  <entry point>  <target type>  <target method>
internalcall  <assembly>  <declaring type>  <method>  <target type>  <target method>
```

**The front end's rule for a method with an ImplMap** (module M, entry point E: the ImplMap's import name, or the
method's own name when that is empty):

1. If a `pinvoke M E` row exists, the call is a direct call of the target. The marshalling around it is already IL
   (LibraryImport's generated stub); the inner DllImports of the stock libraries are blittable.
   - The target's WASM signature equals the P/Invoke's: pointers, byrefs, `IntPtr`/`UIntPtr`, `nint` and function
     pointers are i32; an enum is its underlying type; the other primitives are themselves.
   - `BuildCoreLib bindings` checks every row and every stock P/Invoke against this rule.
   - `SetLastError=true` needs nothing more from the front end. The generated stub reads
     `Marshal.GetLastSystemError()`, which is CoreLib's per-thread errno (`Interop.Sys.GetErrNo`), and the targets
     set that errno.
2. If no row exists but a `library M` line does, the call throws `EntryPointNotFoundException`.
3. If M is named nowhere, the call throws `DllNotFoundException`, and the build warns (SPEC 8).

**The same mechanism serves packages' native WASM libraries later.** A package's library adds `library` and `pinvoke`
lines whose targets are its WASM exports instead of CoreLib methods; the rule above stays the same.

**Internal calls.** A method with `MethodImplAttributes.InternalCall` in assembly A, type T, named N is bound by an
`internalcall A T N` row in the same way. Without a row it is a build error where it is reached, as today's
`MissingCapability` is.

**What is bound** (`BuildCoreLib bindings`: all 56 of the stock libraries' P/Invokes and internal calls bound, 0
with a bad signature, 0 unbound):

- **libSystem.Native, all 19 entry points**, in `Boundary/Natives/SystemNative.cs`. Each behaves as System.Native's C
  does in the browser runtime, built with Emscripten 6.0.2:
  - errno uses Emscripten's numbers, with System.Native's two conversions and musl's messages (`Natives/Errno.g.cs`,
    made by `tools/gen-errno.mjs` from the runtime's and Emscripten's headers).
  - Write: descriptors 0 to 2 go to the engine's log import, other descriptors go to the host's files.
  - FStat, Stat, Unlink and FTruncate go over the host's file system imports.
  - Memory maps follow `emscripten_mmap.c`: no fixed address; anonymous maps zeroed and aligned to a WASM page; file
    maps are copies, written back by msync or munmap when shared and writable; munmap needs the exact start and
    length.
  - SysConf: page size 65,536 and 100 clock ticks, as Emscripten gives them.
  - ShmOpen, ShmUnlink and MemfdCreate set ENOTSUP; IsMemfdSupported returns 0; MAdvise(DONTFORK) sets ENOTSUP.
  - The random bytes are CoreLib's: the host's seeded import, stretched with splitmix64.
  - Not reproduced: FS.mmap's EACCES for a shared writable map of a file not opened read-write. The host does not
    report how a descriptor was opened.
- **libSystem.IO.Compression.Native, all 9 entry points**: deflate and inflate are ports of zlib-ng (section 7.1).
- **System.Console's internal call** `ConsolePal.Clear` goes to the host import `console_clear`, as Mono calls the
  console's own clear.
- **The 11 JavaScript-interop internal calls** (`Natives/InternalCalls.cs`):
  - BindJSImportST, InvokeJSImportST, InvokeJSFunction, CancelPromise, ResolveOrRejectPromise and
    ReleaseCSOwnedObject are host imports (`js_*`) with the same arguments. The 41 `[JSImport]` methods reach
    JavaScript only through these. A host implements them as `dotnet.runtime.js` does, reading the
    JSMarshalerArgument buffers in linear memory. **Today's hosts do not implement them: they throw.**
  - RegisterGCRoot and DeregisterGCRoot are handles and nothing else, because there is no collector.
  - The three `[JSExport]` calls (BindAssemblyExports, GetAssemblyExport, AssemblyGetEntryPoint) throw
    PlatformNotSupportedException. That binding is the compiler's to make.
- **CoreLib's `JsGlobalization.GetLocaleInfo`** (hybrid globalization). It now has a body, by replacing
  `Interop.Locale.Mono.cs`. Under invariant globalization CultureData never calls it, as .NET's invariant mode never
  does; if called, it answers with the invariant culture's names (`Invariant Language##Invariant Country`) and no
  exception.

**A behaviour change in CoreLib's own boundary.** The last P/Invoke error and errno were PAL codes before
(`ConvertErrorPlatformToPal` was the identity). They are now Emscripten's numbers, as in the browser runtime.
`Marshal.GetLastPInvokeError()` and `IOException.HResult` change accordingly. `Interop.Sys.GetErrNo` and `SetErrNo`,
missing before, are the thread's errno.

### 7.1 libSystem.IO.Compression.Native

All 9 entry points are in `Boundary/Natives/CompressionNative.cs`. The layer is pal_zlib.c's: the PAL_ZStream's
fields go into the stream and come back after each call, and `msg` points at a C string of zlib's error text.

**Deflate is zlib-ng 2.2.5 ported to C#** (`Natives/ZLibNgDeflate.cs`, with zlib-ng's licence notice, marked as an
altered version).

- It is the version dotnet/runtime v11.0.0 vendors: zlib-ng commit d54e3769, pinned by the runtime's cgmanifest.
- It ports the generic C paths the WASM build compiles: deflate.c, the stored, fast, medium, slow, quick, huff and rle
  strategies, trees.c, both insert_string variants, longest_match and its slow variant, compare256, slide_hash,
  CRC-32 and Adler-32.
- The gzip header's OS byte is Emscripten's: 3, "Unix" (zutil.h's `OS_CODE`).

**Verified** by `tests/CoreLibNatives` (`dotnet run -c Release`). Each case runs on .NET's own shipped
`System.IO.Compression.Native` (zlib-ng 2.2.5, win-x64) and on the port, through the same PAL entry points with the
same calls.

- The cases cover:
  - 6 kinds of data (zeros, random, text, runs, mixed, periodic);
  - 10 sizes from 0 to 300,000 bytes;
  - levels 0 to 9 and all 5 strategies;
  - windowBits -15, 15, 31, -9 and 12;
  - three call patterns: DeflateStream's, and random input and output chunks with sync, full, partial and block
    flushes.
- **Result: 30,690 deflate cases identical.** That is 611,069,290 compressed bytes and 893,536 calls. In each case
  every return code, `avail_in`, `avail_out` and `msg` was the same.
  - The one byte compared differently is the gzip header's OS byte, by design: 10 on Windows, 3 on Emscripten.
  - x64's SIMD variants of compare256 and longest_match give the generic code's matches on this whole corpus. So the
    desk is a sound oracle for the browser build here.
- CRC-32 is checked against `CompressionNative_Crc32`, including zlib-ng's rule that a null buffer gives 0.

**Inflate is zlib-ng 2.2.5's too, ported** (`Natives/ZLibNgInflate.cs`, with the licence notice, marked as an
altered version).

- It ports inflate.c, inftrees.c and inffast_tpl.h in its generic form. lenfix and distfix are built at start-up by
  inflate_table, as zlib's makefixed builds them.
- zlib-ng's chunked copies (chunkcopy_safe, CHUNKCOPY, CHUNKMEMSET, CHUNKUNROLL) become byte copies. Each writes the
  bytes that an LZ77 copy of that length and distance gives.

**Verified** with the same harness, against native `CompressionNative_Inflate*`. The inputs are every stream the
deflate corpus produced, each read eight ways:

- as DeflateStream reads (8192-byte input, 4096-byte output);
- with random input and output sizes and flushes (Z_SYNC_FLUSH, Z_FINISH, Z_BLOCK);
- auto-detected (windowBits 47);
- truncated at a random point;
- with bytes after the stream;
- with 1 to 3 bits flipped, four times over.

**Result: 72,240 inflate runs identical.** That is 2,568,441,758 output bytes and 833,572 calls. In each call the
return code, `avail_in`, `avail_out` and `msg` matched native.

18 distinct error messages came up, each matching native:
- header crc mismatch, incorrect data check, incorrect header check, incorrect length check;
- invalid bit length repeat, invalid block type, invalid code -- missing end-of-block, invalid code lengths set;
- invalid distance code, invalid distance too far back, invalid distances set;
- invalid literal/length code, invalid literal/lengths set, invalid stored block lengths;
- invalid window size, too many length or distance symbols, unknown compression method, unknown header flags set.

The old managed inflater, `ZLibInflate.cs`, is removed from CoreLib.

**The browser pack's own wasm libz.a was not needed as an oracle.** No case differed between the desk's x64 zlib-ng
and the port.

### 7.2 errno tests

`tests/CoreLibNatives` also spot-checks `Errno.g.cs` against the sources:
- Emscripten's numbers (EBADF 8, ENOENT 44, EINVAL 28, EAGAIN 6, ENOTSUP 138);
- the PAL codes, and the round trips;
- the default ENONSTANDARD;
- musl's messages, including "Unknown error" for codes it has no message for.

The SystemNative_* methods themselves call host imports, so they are not run under .NET. They are compiled into
CoreLib and their bindings are checked by `BuildCoreLib bindings`.
