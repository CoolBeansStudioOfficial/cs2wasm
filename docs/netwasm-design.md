# NetWasm design study for il2wasm (clean room)

This is a study of how NetWasm (https://github.com/zion-sati/NetWasm) solves the problems il2wasm has to solve, written
in this repository's own words so that il2wasm can be built from it. It describes mechanisms, data flow, file formats and
decisions. It quotes no NetWasm code, and nothing here is a translation of NetWasm code. NetWasm files are named only so
the owner can check a statement against its source.

- **Pinned checkout:** commit `d0cecda45cc8b8bc82862af7a57e26da5f81bd00` (2026-11-23, "Merge pull request #266"),
  cloned outside this repository. NetWasm version in its SDK props: 0.4.2.
- **Read on 2026-11-23.** "Unsure" marks a statement inferred from names, comments or docs rather than traced through
  the code.
- **What changed since docs/netwasm-survey.md:** that survey called NetWasm MIT. That holds only for the parts the
  survey read (CoreLib and libraries). The compiler and its tests are not MIT (section 0).

---

## 0. Licence

Files read: `LICENSE.md`, `LICENSE-MAP.md`, `LICENSES/LicenseRef-NetWasm-Community-1.0.txt`, `docs/licensing.md`, the
README's "License" section, `CLA.md`, and the SPDX headers of the test files.

### 0.1 Which parts are under which terms

NetWasm draws its licence boundary by source path. `LICENSE-MAP.md` is the authoritative map, and where several rows
match a path the most specific one applies. Every NetWasm source file has an SPDX header that agrees with the map.

| Terms | Paths (from LICENSE-MAP.md) |
|---|---|
| **NetWasm Community License 1.0** (source-available, not OSI) | `src/NetWasm.Compiler*/**` (the compiler, CLI, browser compiler, tasks, metadata, control flow, Wasm, component model); `src/NetWasm.Sdk/**`, `src/NetWasm.Sdk.Pack/**`; `src/NetWasm.Toolchain/**` (except vendored zoneinfo and wasi-tls WIT); `src/NetWasm.Hosting.Build/**`; `tools/**` (except `tools/NetWasm.TimeZones`, `tools/NetWasm.Hosting.Generator`); `compiler-qualification/**`; and **`tests/**` by default** |
| **MIT** | `src/NetWasm.CoreLib/**`, `src/libraries/**`, `src/NetWasm.Runtime/**` (except the collectors), `src/NetWasm.Runtime.Pack/**`, `src/NetWasm.Runtime.Wasm32/64`, `src/native/**` (except vendored libraries), `src/NetWasm.Hosting/**`, `src/NetWasm.JSImportGenerator/**`, `src/NetWasm.Templates/**`, `src/NetWasm.Ref/**`, `src/NetWasm.Testing.VSTest/**`, `wit/netwasm-*/**`, **`eng/**`**, **`docs/**`**, build metadata; and these test folders: **`tests/libraries/**`**, `tests/NetWasm.Runtime.Tests/**`, `tests/NetWasm.Runtime.Pack.Tests/**`, `tests/NetWasm.Hosting.Tests/**`, `tests/NetWasm.Ref.Tests/**`, `tests/NetWasm.Testing.VSTest.Tests*/**`, `tests/NetWasm.TimeZones.Tests/**`, `tests/NetWasm.Hosting.Generator.Tests/**`, `tests/NetWasm.Wit.Bindings.Tests/Conformance/wit-parser/**` (upstream MIT) |
| **Apache-2.0** | the collectors `src/NetWasm.Runtime/collector/compact/**` and `.../netwasm/**`; `tests/end-to-end/expression-fluentvalidation-consumer/**`; vendored OpenSSL |
| Others | vendored zlib-ng (Zlib), brotli (MIT), WASI WIT (upstream WASI licence), IANA tz data (public domain), wasi-tls WIT (Apache-2.0 with LLVM exception) |

Files copied from dotnet/runtime keep the .NET Foundation MIT header (for example in `tests/libraries`, `src/libraries`).

### 0.2 The Community License's conditions

This is a summary of the licence text (version 1.0, revised 22 October 2026), not legal advice.

- **Scope.** It covers the compiler, linker, optimizer, build tools and related tooling that name this licence. Under
  LICENSE.md, "their tests" are included. It does not cover MIT/Apache parts or Generated Output.
- **Grant (s3).** You may install, run, copy and modify the Software, but only for a *Permitted Free Use*. Separately,
  anyone may view the public source and make internal copies "solely for inspection, security review,
  interoperability analysis" or to decide whether to license it. That inspection right expressly does not cover running
  the Software, modifying it, or using it to develop a product.
- **Permitted Free Uses (s4):**
  - personal non-commercial use;
  - education;
  - Qualifying Open-Source Work: the project's complete source is public under an OSI licence, NetWasm is not used to
    produce a proprietary deliverable, and nobody is paid for or gains direct commercial advantage from that work;
  - contributions to NetWasm;
  - 30 days of evaluation;
  - **internal use of any kind by a Qualifying Organization**, including commercial development and production builds.
    A Qualifying Organization is not a government body, has fewer than 250 employees and has under USD 10 M annual
    revenue, counted with affiliates;
  - written community grants;
  - contractors, when both they and their client qualify.
- **Needs a commercial licence or a GitHub Sponsors tier (s5).** Any use by a larger organization. Any **Commercial
  Offering**, whatever the user's size: bundling or distributing the tooling with a product or SDK, offering it as a
  hosted, cloud, API or build service, or selling access to it. The Sponsors grant (USD 149 to 2,499 a month) covers
  internal use only, never redistribution or hosting.
- **Redistribution (s7).** Only for non-commercial community purposes. The distribution must carry the licence, keep
  all notices, mark modified files with dates, and provide source for modified binaries. **You may not relicense the
  Software or a derivative of it under another licence (s7.4)**, so Community-licensed files cannot become MIT files in
  il2wasm.
- **Generated Output (s8)** is free for any use.
- **Other terms.** Governed by the law of Victoria, Australia. Rights terminate on material breach, with a 30-day cure
  for a first breach.

### 0.3 What this means for il2wasm's use of NetWasm's tests

**NetWasm's main test suite is Community-licensed.** That suite is `tests/NetWasm.Compiler.Tests` (the differential
corpus, its fixtures and its generators), together with `tests/end-to-end` and `compiler-qualification`.

- **Copying it into this repository: no.** The files would stay under the Community License, which il2wasm (MIT) cannot
  absorb (s7.4). Redistribution in a public repository is allowed only for non-commercial community purposes (s7.2), and
  il2wasm feeds commercial products (Bazoodle, a game).
- **Running it from a pinned checkout outside the repository: yes, under a Permitted Free Use.** Running the files and
  feeding their C# sources to il2wasm counts as "executing, reproducing, modifying" the Software (s3), so a Permitted
  Free Use must apply. The one that fits il2wasm is **s4.6**: internal use by a Qualifying Organization (an individual
  or sole trader, or a company under 250 staff and USD 10 M). s4.3 may not hold if il2wasm or its outputs serve a
  proprietary deliverable. **The owner must confirm that he, or his company, is a Qualifying Organization.** If he is
  not, a Sponsors tier would cover internal test runs.
  - The pinned checkout must be fetched by a script into a cache, never vendored and never a committed copy. A git
    submodule pointer is only a reference, but a fetch script is cleaner.
  - Test runs must stay internal: a homelab CI is internal; a public hosted service that runs NetWasm's tests for third
    parties would not be.
  - The results may be published: they are il2wasm's own outputs and measurements.
- **Expected outputs come from .NET**, never from NetWasm's stored expectations. SPEC 13 requires this anyway. il2wasm's
  harness compiles each case under .NET and records .NET's result itself.
- **MIT parts can be copied** with their notices kept (MIT requires the copyright notice and licence text):
  - `tests/libraries/**`: 24 TUnit projects, about 379 tests, that NetWasm already runs with desktop .NET 11 as the
    oracle;
  - `eng/open-source-suts/**`: 6 real-package programs with pinned package versions (Sep, ImageSharp, MemoryPack,
    SimdCodecs, Microsoft.Extensions.Configuration, Mediator/Jab). Each is built for NetWasm and for desktop, and the
    two printed outputs must match. These suit SPEC 13 proof 2 exactly;
  - `eng/size-canary/**` and `docs/**`.

  If copied, they go under a directory that keeps NetWasm's MIT notice (for example `tests/netwasm-mit/` with
  NetWasm's `LICENSES/MIT.txt` and a provenance note recording the commit).

**Recommendation.** Run `tests/NetWasm.Compiler.Tests`' cases from a pinned checkout through a harness of il2wasm's own
(section 5), on the s4.6 basis once the owner confirms it. Copy the MIT `tests/libraries` and `eng/open-source-suts`
with attribution if wanted. Never copy Community-licensed files, including the C# test sources embedded in them.

---

## 1. Project and package input

Files read: `src/NetWasm.Sdk/Sdk/{Sdk.props,Sdk.targets,NetWasm.Sdk.Tfm.props,NetWasm.FrameworkReferences.targets,
NetWasm.NativeLibraries.targets,NetWasm.App.PackagePruning.props}`, `src/NetWasm.Compiler.Tasks/buildTransitive/
NetWasm.Compiler.Tasks.targets`, `src/NetWasm.Compiler.Tasks/Framework/FrameworkAssemblySelector.cs`,
`src/NetWasm.Sdk.Pack/References/*`, `src/NetWasm.Compiler.Metadata/{ManagedAssemblyResolver,TypeForwarderReader,
FeatureSwitchSubstitutionReader}.cs`, `src/NetWasm.Compiler/Analysis/RuntimeConfigurationRewriter.cs`, `src/NetWasm.
{App.Ref,App.Runtime,Ref}/README.md`, `docs/{project-kinds,netstandard-libraries,cli-build-deployment}.md`.

**How NetWasm does it.**

- **Its own SDK and target framework.** A project uses `Sdk="NetWasm.Sdk"` and `TargetFramework` `net10.0-netwasm`,
  .NET's own framework on a `netwasm` platform, as `net10.0-browser` is for Blazor. NuGet therefore picks a package's
  `net10.0-netwasm` asset first, then `net10.0`, `net9.0`, `net8.0`, then .NET Standard.
- **Compiled against NetWasm's own reference assemblies.** Roslyn compiles against NetWasm's reference packs
  (`NetWasm.Ref`: CoreLib's contract; `NetWasm.App.Ref`: the other framework assemblies, plus a generated
  `netstandard.dll` of forwarders), not Microsoft.NETCore.App.Ref.
  - Because of this, a NetWasm build is not "the project as `dotnet build` builds it": a missing API fails at C#
    compile time.
  - A package's compile asset that names framework types is rewritten into a compile-only "reference view" before
    Roslyn sees it. The view re-binds declaration signatures to NetWasm's CoreLib, using Mono.Cecil, with no method
    bodies. The original implementation assembly still goes to the compiler. (Unsure of the exact trigger; see
    `src/NetWasm.Sdk.Pack/References/FrameworkReferenceViewBuilder.cs`.)
- **Restore is stock NuGet.** Package pruning is on: packages the framework already provides (the list taken from
  Microsoft.NETCore.App.Ref 11's PackageOverrides) are pruned from restore, so a dependency on, say, System.Text.Json
  at or below the framework version takes the framework's copy.
- **What the compiler gets.** NetWasm reads MSBuild items, not deps.json. After `CoreCompile`, an MSBuild target
  assembles:
  - the project's own assembly (`IntermediateAssembly`);
  - every `.dll` in `ReferenceCopyLocalPaths`: package runtime assets as NuGet resolved them from `project.assets.json`,
    and project-reference outputs. Satellite resource assemblies are dropped, because globalization is invariant;
  - the framework implementation assemblies (`NetWasm.App.Runtime`, plain IL under `framework/`, outside NuGet's asset
    folders) that the program's assembly references reach transitively;
  - CoreLib's implementation from the runtime pack.

  `deps.json` is not read. `project.assets.json` is read directly only to find native archives in packages (section 2).
- **Framework conflicts.** When a package carries its own copy of a framework assembly, the higher version wins and a
  tie goes to the framework, as the .NET SDK resolves conflicts. The selection walks through facades: a netstandard
  package's references lead through `netstandard.dll`'s forwarders to the real owners.
- **Assembly resolution inside the compiler** is by simple name only, plus an alias table. Versions and public keys
  play no part in the closed set. Type forwarders are followed.
- **Build facts folded at compile time:**
  - `RuntimeHostConfigurationOption` items (what would go into runtimeconfig.json) are baked into CoreLib's
    AppContext data;
  - the Boolean and void method stubs in each assembly's embedded `ILLink.Substitutions.xml` are applied (for example
    `UseSystemResourceKeys`, `EventSource.IsSupported`). The SDK sets `UseSystemResourceKeys=true` by default, so
    package exceptions carry resource keys, not messages;
  - `OperatingSystem.IsWindows()` and similar checks fold to false.
- **The compiler's command line** (`nwc`) is csc-like: one input assembly, `-r:` the closed reference set, `-target:exe |
  library | function`, `-export:name=Type::Method`, plus sidecar outputs. The MSBuild task passes PDBs where they
  exist, for stack traces.
- **Incremental builds.** A compilation-identity file lists every input path and digest. MSBuild's Inputs and Outputs
  skip the compile when nothing changed. Debug `O0` builds reuse per-method compiled units.

**What il2wasm should do.**

- **Differ: take the ordinary build.** SPEC 1 says the input is the project as `dotnet build` builds it, so il2wasm
  should not impose its own TFM or reference packs. Let the project compile against Microsoft.NETCore.App.Ref for its
  normal `net11.0` TFM, as NativeAOT does. Then substitute il2wasm's implementation of the framework: its CoreLib and
  libraries, built from dotnet/runtime (section 4). That keeps "every project .NET builds compiles", and an API il2wasm
  lacks becomes a run-time exception, not a C# error.
- **Agree: the input set comes from MSBuild items, not deps.json.** Take the project's own assembly,
  `ReferenceCopyLocalPaths` (or the `ResolvedFileToPublish` set of a publish), and project-reference outputs. Add the
  framework implementation assemblies reached transitively by assembly reference. A small MSBuild target in an
  `il2wasm` package (`buildTransitive`) runs after `CoreCompile`, or after `ComputeResolvedFilesToPublish` for
  `dotnet publish`, and calls the in-process compiler API.
- **Agree on:**
  - higher-version-wins with ties to the framework, and dropping satellites (invariant globalization, SPEC 9);
  - stable name-ordered inputs, for reproducibility (SPEC 7);
  - applying `ILLink.Substitutions.xml` method stubs and RuntimeHostConfigurationOption values at compile time, which
    is what NativeAOT does.
- **Differ: do not default `UseSystemResourceKeys` to true.** Messages are observable output (SPEC 1 correctness).
- **Adapt: resolve by name, but check versions.** Simple-name resolution is enough once the set is closed and
  conflicts are settled, but il2wasm should report a diagnostic when two inputs share a simple name with different
  versions, rather than silently pick one.

---

## 2. P/Invoke and native libraries

Files read: `docs/{static-native-interop,corelib-runtime}.md`; `src/NetWasm.Compiler.Core/NativeInterop/*` (names and
comments, especially `NativeLibraryBinding.cs`, `NativeMarshallingRules.cs`, `NativeMarshallingStub.cs`,
`NativeAggregateAbiPlanner.cs`); `src/NetWasm.Compiler.Metadata/{NativeImportDeclarationReader,
NativeCallbackDeclarationReader,NativeStringMarshalling,NativeStructLayouts}.cs`; `src/NetWasm.Compiler/Interop/
{NativeArchiveFunctionTypeReader,LlvmBitcodeFunctionTypeReader}.cs`; `src/NetWasm.Compiler.Wasm/Emission/
NativeInterop/NativeCallbackObjectWriter.cs`; `src/NetWasm.Compiler.Wasm/Emission/Results/
WasmModuleEmissionResultBuilder.cs`; `src/NetWasm.Runtime.Pack/{Planning/RuntimeLinkPlanner.cs,Materialization/
RuntimeLinkArgumentBuilder.cs,Materialization/RuntimeNativeModuleMaterializer.cs,Materialization/
RuntimeEnvImportRelocator.cs,PackageNatives/PackageNativeArchiveSelector.cs}`;
`src/NetWasm.Compiler.ComponentModel/Merging/*`.

### 2.1 How native libraries are packaged and found

- **The unit is a static archive (`.a`) of WASM object files**, or of LTO bitcode members. There are no dynamic
  libraries and no run-time lookup.
- **Three ways to supply one:**
  1. A `NativeLibrary` MSBuild item with three required fields: the archive path, the logical name
     (`NetWasmLibraryName`, matched exactly against the DllImport or LibraryImport library string), and `WasmTarget`
     (wasm32 or wasm64). Items flow through `ProjectReference` edges, and a NuGet package adds them from
     `buildTransitive` targets with package-relative paths.
  2. Packages already built for .NET's browser-wasm, found with no item at all. A selector reads `project.assets.json`,
     lists each package's files and recognises two layouts:
     - `build[Transitive]/<tfm>/<lib>.a/<emscripten-version>/[<variant>/]<lib>.a`, as SkiaSharp and HarfBuzzSharp
       ship;
     - `runtimes/browser-wasm/nativeassets/<tfm>/<lib>.a`, as SQLitePCLRaw ships. Here the Emscripten version is
       inferred from the TFM, using a table of which Emscripten each .NET release's browser SDK used.

     It picks the newest build no newer than the Emscripten its runtime was built with, the single-threaded variant,
     with SIMD where offered. An explicit `NativeLibrary` item overrides this choice.
  3. `[WasmImportLinkage]` on the declaration. No archive is involved: the library name becomes the WASM import module
     and the entry point the field name, and the host supplies the function.
- **Only reached imports select a provider**, so unreached declarations pull in nothing. Archives are not linked
  whole-archive: lazy archive semantics keep only the members that are used.
- **A DllImport whose library is not linked still compiles.** Its body throws `DllNotFoundException`, and the build
  warns (NW1017) for each one the program reaches.
  - libc is always linked. CoreLib's `Math` uses libm through it.
  - `NativeLibrary.Load`, `TryLoad` and `GetExport` answer as .NET does when a library is missing.
  - A function missing from a linked library is a link error.
- **Emscripten-built archives often import Emscripten's JavaScript functions** (WebGL's `gl*`, for example). NetWasm
  links do-nothing stand-ins for them, so Skia falls back to CPU rendering, unless the project declares which JS module
  supplies them. In that case the linked module's `env` imports are moved to that module name.
- **What else is linked from the C side:** exception handling (legacy archives are translated to exnref), both of
  Emscripten's setjmp/longjmp ABIs, libc++ and libc++abi, and libunwind. A C++ exception or a longjmp that escapes into
  managed code aborts.

### 2.2 How a P/Invoke becomes a call

1. **Read the declarations.** The compiler reads `DllImport` metadata: library, entry point, CharSet, calling
   convention, `SetLastError`, `PreserveSig`, `[MarshalAs]`, `[In]`/`[Out]`, `DisableRuntimeMarshalling` on the
   assembly. `LibraryImport` needs nothing special: the stock source generator has already lowered it to a blittable
   inner DllImport plus ordinary C# marshalling code, and NetWasm ships Microsoft's unmodified generator in its
   reference pack.
2. **Check the signature against the archive.** The compiler reads the linked archive itself:
   - it parses the `ar` format, then each WASM object's type, import and function sections and its `linking` symbol
     table, to find the defined, non-local function symbols and their WASM types;
   - for LTO members it reads LLVM bitcode's module and type tables, which also say whether a function is variadic.

   So the C signature each import must use is known at compile time, and a mismatch is a build error.
3. **Lower to the WASM Basic C ABI.** The planner sets the WASM-level signature: scalars as i32, i64, f32 or f64;
   pointers at the target width; structs by value as the C ABI lays them out (one-field structs flattened, others by
   pointer to a caller copy); sret for struct results; variadic calls through a fixed-signature declaration with a
   buffer of the extra arguments.
4. **Marshal in a stub, generated at compile time.** For a raw DllImport that keeps runtime marshalling, the method's
   body is replaced by a stub generated as IL, the equivalent of desktop's IL stub:
   - `bool` becomes a 4-byte BOOL, or 1 byte with `U1`;
   - `char` becomes ANSI (the first UTF-8 byte) or UTF-16 under `CharSet.Unicode`;
   - a `string` argument becomes a NUL-terminated UTF-8 copy freed after the call, or a pinned UTF-16 buffer under
     Unicode. A string result is decoded and freed with `FreeCoTaskMem`;
   - `SafeHandle` arguments get AddRef/Release around the call, and results and `out` handles are constructed through
     the handle type's parameterless constructor;
   - blittable arrays are pinned, and structs with `bool` or `char` fields are given a native copy, copied back for
     `ref`;
   - `Marshal.StructureToPtr`, `PtrToStructure` and `SizeOf` use the same layout rules.

   The rule sets follow desktop .NET on Unix. Delegates marshalled as function pointers, `StringBuilder`, custom
   marshallers, `SetLastError` and `PreserveSig=false` are rejected at compile time where reached.
5. **Emit the call.** The emitted managed module imports each distinct (library, entry point) pair as a WASM function
   import with the lowered type. At link time (2.4) the import resolves to the archive's definition. A
   `[WasmImportLinkage]` import stays an import of the final module.

### 2.3 Callbacks and function pointers

- **`[UnmanagedCallersOnly(EntryPoint = "name")]` makes a native linker symbol.** The compiler writes a small
  relocatable WASM object, `<output>.callbacks.o`, in LLVM's object format with a `linking` section and relocations,
  that:
  - imports each callback's managed implementation from the managed module;
  - defines the named symbol, so C code can call `name` directly;
  - for address-taken callbacks (`&Method` used as a `delegate* unmanaged`), defines a getter that returns the
    function's table index. The linker owns table placement, so the address is only known after linking;
  - defines one trampoline per unmanaged `calli` signature, which calls its last argument through the linked indirect
    function table with the C ABI type. Managed code therefore calls native function pointers through these
    trampolines.
- A named callback may be called only after managed start-up. Native constructors must not re-enter managed code.
- **Callback signatures are limited** to scalars, pointers and by-value C structs. Delegates passed to native code
  (`Marshal.GetFunctionPointerForDelegate`) are not supported.

### 2.4 Linking and memory

- **The compiler emits a final managed core module, not a relocatable object.** Next to it, it writes
  `runtime-layout.json`, which records the end of the managed static data and the target, and the callbacks object.
- **The runtime pack links the runtime module.** It runs `wasm-ld` over:
  - the C runtime archive;
  - one collector archive;
  - the system libraries (Emscripten's musl libc, compiler-rt, and libc++ when needed);
  - the selected native archives;
  - the callbacks object.

  The link uses `--no-entry`, `--gc-sections`, `--export-memory` and `--export-table`. Its global base is aligned
  above the managed static data, and its heap base comes after the linked data and the 64 KiB stack. An undefined
  symbol is reported by name. The result is cached by a digest of all the input bytes.
- **The two modules are merged into one.**
  - The managed module imports the memory, and the runtime module defines and exports it, so both share one linear
    memory. (Unsure: this comes from the encoder's memory-import option and the linker flags, not traced end to end.)
  - NetWasm's own byte-level merger, which mimics binaryen's `wasm-merge`, turns each import that names the other
    module's export into a direct reference, renumbers indices, and keeps everything else as it is.
  - Binaryen `wasm-opt` then optimizes the merged module.
- **Packaging.** For WASI hosts, `wasm-tools` wraps the module as a WASI 0.3 component. For JavaScript hosts it stays a
  core module with a generated JS adapter.
- **Memory sharing.** The managed heap and native code share one linear memory.
  - `NativeMemory` and `Marshal.AllocHGlobal`/`AllocCoTaskMem` are libc `malloc`/`free`, so either side can free the
    other's blocks.
  - Native storage is not a GC root. A pointer into managed memory is valid only for the call; NetWasm's collector is
    non-moving, so pinning is trivial.
- **Toolchain.** The compiler, Node, `wasm-ld`, binaryen and `wasm-tools` come in one pinned "host tools" NuGet package.
  For browser builds, LLD is compiled to WASM (`src/NetWasm.Toolchain/Browser/LLD`).

### 2.5 What il2wasm should do

- **Agree on packaging:**
  - static `.a` archives of WASM objects as the native library format;
  - the `NativeLibrary`-style item with an exact logical name and a target, propagated through ProjectReference and
    `buildTransitive`;
  - recognising the two existing browser-wasm package layouts, so SkiaSharp, HarfBuzzSharp and SQLitePCLRaw work
    unchanged;
  - linking only the providers that reached imports select, with lazy archive semantics;
  - a missing library compiling to a `DllNotFoundException` body plus a warning.

  This matches SPEC 8 better than a hard error. SPEC 8 says "the only build error", so propose to the owner that the
  error be kept for libraries the program provably calls and the warning used where reachability is not certain.
- **Agree on compile-time signature checks** against the archive's symbol table (WASM objects and LTO bitcode). Cheap,
  and it turns ABI drift into a build error.
- **Agree on marshalling:**
  - compile-time stubs that follow desktop .NET's runtime-marshalling rules (SPEC 8: "as the CLR's marshalling rules
    define it");
  - `LibraryImport` handled through the stock generator's output, needing nothing special.

  SPEC 1 requires full coverage, so il2wasm must also do what NetWasm rejects: delegates as function pointers (a
  reverse-P/Invoke thunk per delegate type, with a handle table keeping the delegate alive), `SetLastError` (a
  thread-local last-error slot written after the call), `PreserveSig=false`, and `StringBuilder`.
- **Agree on the callbacks object.** Named `UnmanagedCallersOnly` symbols, table-index getters and `calli` trampolines,
  written as a relocatable object for `wasm-ld`, are the clean way to let the linker own the function table.
- **Adapt the link to SPEC 2 and SPEC 6.1:**
  - SPEC 2 wants no other compiler in the path for .NET code, but `wasm-ld` for native code is allowed, and SPEC 14
    rules out the component model. Take the same two-step shape: il2wasm's own final managed module, plus a `wasm-ld`
    link of the native side (il2wasm's own small C or WAT shims, libc, and the selected archives) at a base above the
    managed static data, then a merge into one core module;
  - il2wasm already has a WASM back end, so a merge like NetWasm's (index renumbering, imports resolved to exports) is
    straightforward. It avoids making il2wasm's emitter produce relocatable objects;
  - the final pass (SPEC 5) then runs over the merged module, so no binaryen;
  - for browser builds (SPEC 10), `wasm-ld` must run as WASM too, as NetWasm does with LLD compiled to WASM. Note that
    LLD is not C#, so self-hosting (SPEC 10) needs it as a separately built WASM tool. Raise with the owner.
- **Differ on threads (SPEC 4.5).**
  - il2wasm's memory is shared, so native archives must be built with atomics and bulk memory (`-pthread`). Choose the
    *multi-threaded* variant of package archives where one exists, and refuse or warn on single-threaded archives in a
    threaded program.
  - The libc used must be thread-safe (Emscripten's musl with pthreads, or wasi-libc's threads build).
  - The native stack must be per thread: one `__stack_pointer` per worker instance, set by the per-thread start export.
- **Differ on memory.** il2wasm counts references, it has no tracing GC, so "native storage is not a root" becomes:
  - a pointer passed to native code borrows only for the call;
  - a `GCHandle` or `SafeHandle` the native side keeps is an owned count.

  il2wasm is non-moving too, so pinning stays free.
- **Differ on Emscripten JS stand-ins.** Do-nothing stand-ins change behaviour silently (a GL call that does nothing).
  Prefer to require the host to supply them, as host imports (SPEC 6.1). A stub that throws, or a build warning naming
  the function, is acceptable.

---

## 3. IL front end and IR

Files read: `docs/{ARCHITECTURE,how-it-works,optimization,support-status,spec-deviations,LIMITATIONS}.md`;
`src/NetWasm.Compiler.Metadata/{CilDecoder,CilTryReentryLowerer,CilSwitchLowerer,MissingTypeFinder}.cs` (comments);
`src/NetWasm.Compiler.Core/{CilModel,Layout,RuntimeTypeData}.cs`; `src/NetWasm.Compiler.ControlFlow/*` (names,
`TypedStackValidator.cs`, `Structuring/ExceptionGroupStructurer.cs`, `Structuring/OutermostFirstExceptionRegionOrderer.
cs`); `src/NetWasm.Compiler/Analysis/{BeforeFieldInitEntryInitializer,MethodSpecializer}.cs` and directory names;
`src/NetWasm.Compiler.Wasm/Emission/{ValueFrameLayoutPlanner.cs,Instructions/Calls/*Virtual*,Instructions/Calls/
SharedVirtualDispatch*.cs,Instructions/Dispatch/KeyedDispatchEmitter.cs,GeneratedFunctions/Filter*.cs}`.

**How NetWasm does it.**

- **Reading IL.** It uses `System.Reflection.Metadata` (`MetadataReader` plus its own IL decoder), not Cecil. Cecil is
  used only by the SDK to rewrite reference assemblies. It decodes each body into its own instruction list:
  - one enum of CIL operations, with resolved operands (types, methods, fields, constants);
  - a few extra pseudo-operations the optimizer introduces, such as a `select` for branches whose arms only compute
    numbers.
- **No SSA.** The IR stays a typed CIL instruction list per method. On it NetWasm:
  1. builds a control-flow graph;
  2. validates the evaluation stack by type at every instruction and join. ECMA's implicit int32 and native int
     coercions are recorded. A native int meeting a managed pointer at a join is accepted, as desktop's importer
     accepts it;
  3. computes dominators, post-dominators and natural loops;
  4. **structurizes** the CFG into WASM's block, loop and if form. Reducible flow becomes nested blocks and loops.
     What does not fit becomes a dispatcher: a state local plus a `br_table` loop.

  Many optimizations are rewrites on this IL: call rewriters (Activator, ArrayCopy, BitCast, default comparers, enum
  Equals), constant and argument folding, devirtualization, inlining, bounds-check elimination, loop versioning and
  unrolling. The evaluation stack maps to WASM locals and operand stack at emission.
- **Exception regions.**
  - Before structuring, IL that enters a `try` other than by falling into it (a branch to its first instruction, or a
    catch's `leave` back into its own try, which ECMA III.3.46 allows) is rewritten. Each such try gets an entry point
    just before it, plus an entry-number local that the try's head dispatches on.
  - Regions are claimed outermost-first and turned into WASM `try_table` and `exnref` groups.
  - **Filters are compiled to separate funclet functions**, called through a generated per-method filter dispatcher by
    filter id, with the method's frame shared. The fixture `flow-exception-order`, whose expected results are desktop
    .NET's, checks the order of filters and finally blocks. Unsure: how the first-pass search is driven at run time
    (runtime C or emitted code).
  - `finally` and `fault` blocks run on unwind; empty finally blocks are removed.
- **Generics.** Every generic type and method is **instantiated for each set of type arguments the program uses**, with
  no shared canonical code. Generic virtual methods dispatch over the reached instantiations. A generic recursion that
  never ends (`F<T>` calling `F<List<T>>`) is rejected, and run-time generic construction (`MakeGenericType` of a new
  instantiation) is unsupported.
- **Value types.**
  - Structs live in a per-method **value frame** in linear memory: a shadow stack below the 64 KiB native stack.
  - `newobj` of a struct constructs into its own temporary, and the constructor gets the temporary's address.
  - The managed ABI passes struct arguments by pointer and struct results through a return buffer.
  - Short-lived structs whose address does not escape are promoted to scalar locals ("transient aggregate promotion",
    "address-taken scalar promotion").
- **Static constructors.**
  - Every static-field access and static call is guarded by a type-initialization check, emitted inline as a flag
    test plus a call.
  - For `beforefieldinit` types, a check is hoisted to method entry when every path to a return would check anyway, so
    loops do not re-check. Methods with exception regions keep their checks in place, so a failing initializer fails
    inside its handler.
  - A static method of a type checks its own type at entry.
  - Constant stores at the start of a cctor become static data. Static fields no reachable code writes fold.
  - Single-threaded, so there is no locking.
- **Type identity and dispatch: no vtables.**
  - The layout compiler numbers reached types in **pre-order of the class hierarchy**, so a type and all its subtypes
    have consecutive ids [TypeId, LastSubtypeId]. A cast to a class is therefore one unsigned range test on the
    object's header type id.
  - Interface casts test a set of ids.
  - A virtual or interface call becomes a **switch on the receiver's type id** over the targets the call site can
    reach: ranges of consecutive ids served by one target, searched by halving when there are many. Targets with
    identical code share a case.
  - When a declaration has many targets, each call site calls one generated **shared dispatch function** per
    declaration instead of expanding the switch in place.
  - Exact receiver types are devirtualized.
  - Delegates and finalizers are dispatched the same way, by id.
- **Type metadata.**
  - A runtime type table in static data is decoded once at start-up and read in place by CoreLib. Each registered type
    has its object layout's reference offsets (for the GC), the ids a cast accepts, and its generic arguments and
    relations.
  - Each type has a static `Type` object, found by canonical name for `typeof`.
  - Type names are not in the module. They ship in a sidecar file (`*.type-symbols`), and without it a name reads
    `type#<id>`.
  - Method names for stack traces are an opt-in sidecar.
- **Reflection.**
  - `Type` queries work over the reached types.
  - Custom-attribute queries work when the compiler can bound the target and the filter.
  - Member reflection (`GetMethod`, `GetProperties`, `Invoke`), `dynamic` and `Reflection.Emit` throw
    `PlatformNotSupportedException`.
  - Expressions run on upstream's expression interpreter, not compiled.
- **Missing members.** IL that names a member its resolved type lacks compiles to a body that throws
  `MissingMethodException` or `MissingFieldException` with .NET's message, as .NET does when it first compiles such a
  method.

**What il2wasm should do.**

- **Agree:**
  - `System.Reflection.Metadata` for reading. It is fast and is what the self-hosted compiler can carry;
  - the typed stack validation rules, including the native int and managed pointer join;
  - the try-re-entry rewrite;
  - missing-member bodies that throw .NET's exceptions;
  - pre-order type ids with range casts;
  - type-id switch dispatch with shared dispatch functions for wide declarations. Measure it against cs2wasm's current
    dispatch before adopting it; at a site with few targets, a switch on the type id should beat `call_indirect`.
- **Differ: SSA.** SPEC 2 fixes an SSA IR, because the ownership analysis (SPEC 4.1) is per value. Use NetWasm's CFG,
  stack-typing and structuring steps as the front of the pipeline (IL, then CFG, then typed stack, then SSA through
  the stack-to-SSA conversion), and do structuring at emission from the SSA CFG. cs2wasm's back end already structures.
- **Differ: generics.** SPEC 3 asks for sharing between reference-type instantiations where instantiation does not pay.
  NetWasm's monomorphize-everything is simpler and smaller for small programs, but Roslyn-sized programs (SPEC 10) need
  sharing or code size and build time blow up. Start with full instantiation, which NetWasm shows works; add
  canonical-form sharing for reference types behind a switch, measured.
- **Differ: static constructors.** Keep the hoisting rule. With threads (SPEC 3, 4.5), the slow path needs .NET's
  per-type lock and the "initializing on this thread" re-entry rule, plus deadlock detection as the CLR does it. The
  fast-path flag test stays a plain load, with a release store and acquire load around the flag.
- **Differ: reflection (SPEC 6.6).** il2wasm must keep metadata for whatever reflection reaches, NativeAOT-style, and
  must not answer `type#<id>`: names are observable (SPEC 5, "GetType() and reflection results"). It must also support
  `Reflection.Emit` through the bundled IL interpreter (SPEC 3).
- **Differ: exceptions.** SPEC 3 requires the two-pass order. NetWasm's funclet-per-filter structure is the right shape,
  so a first-pass search can call filters before any finally runs; il2wasm should drive it from an explicit per-thread
  handler chain.
- **Adapt: value frames.** cs2wasm keeps structs in WASM locals where it can, and with ownership in the picture (a
  struct carries its fields' ownership, SPEC 4.2) the scalar promotion is the default. A shadow-stack frame is the
  fallback for address-taken structs. Do not copy NetWasm's fixed 64 KiB stack: it is a policy, not a design point.

---

## 4. CoreLib strategy and runtime boundary

Files read: `docs/{corelib-runtime,how-it-works,runtime-garbage-collection}.md`, `docs/netwasm-survey.md` (ours),
`src/NetWasm.CoreLib/` (directory, csproj, attribute counts), `src/libraries/` (directory), `src/NetWasm.Runtime/`
(file list), `src/NetWasm.Compiler.Wasm/Emission/{WasmRuntimeImports,RuntimeImportCatalog}.cs` (comments),
`src/NetWasm.Compiler.Metadata/RuntimeProvidedMembers/*` (names).

**How NetWasm does it.**

- **Its own CoreLib, in the tree.** `NetWasm.CoreLib` (MIT, about 1,600 source files) is a port: dotnet/runtime's
  `System.Private.CoreLib` sources copied at fixed commits where they fit, with hand-written replacements where they do
  not. Rewritten are Task without contingent properties, Encoding, StringBuilder, Array, ordinal-only comparison and
  exception messages; see docs/netwasm-survey.md for the list. The assembly is named `NetWasm.CoreLib`.
- **Its own framework libraries.** `src/libraries` holds about 70 dotnet/runtime libraries copied in-tree, some
  modified. They are built to IL once and shipped in `NetWasm.App.Runtime`.
- **The runtime boundary has three layers:**
  - **`[Intrinsic]` methods** (about 3,600 marks in CoreLib): the compiler substitutes their bodies. These cover type
    tests, `Unsafe`, `RuntimeHelpers`, SIMD, delegate invocation, `GetType` and so on;
  - **`InternalCall` externs** (about 90): array creation, `Array.Copy`, write barriers and the like, bound to runtime
    imports;
  - **runtime imports** by fixed name, which the emitted managed module calls: allocation (an inline bump-allocation
    fast path with a runtime fallback that may collect), root-frame maps for the exact GC, exception helpers,
    finalization, collection requests.

  The runtime itself is C (`src/NetWasm.Runtime/*.c`): allocator, collectors, ephemerons, finalizer safepoints, stdio,
  clocks, random seed, process exit, setjmp/longjmp, and the C++ personality. It sits on Emscripten's musl. System
  services come through WASI 0.3 imports, or through JS imports in raw mode.
- **Collectors.** Compact (default, precise, non-moving), NetWasmGC, NetWasmGen (generational, with write barriers
  recorded in compiled code) and Bump. Exactly one is linked. Hosts call `collect_when_idle` and `collect_when_quiet`.
- **Profile.** Single-threaded; ordinal text; invariant globalization; no member reflection; `UseSystemResourceKeys`
  on.

**What il2wasm should do.**

- **Differ (SPEC 9): use upstream's library, unmodified, as NativeAOT builds it.** il2wasm takes
  `System.Private.CoreLib` and the libraries from dotnet/runtime source at a pinned tag, compiled to IL, with only a
  runtime boundary of its own. NetWasm's in-tree, hand-edited port is what SPEC 9 rules out. Its edits are where its
  deviations come from (ordinal-only comparison, messages, type names).
- **Agree on the boundary's layering:**
  - compiler intrinsics for what must be inlined (type tests, `Unsafe`, SIMD, allocation fast path);
  - a small set of runtime calls for the rest.

  For il2wasm, the runtime is C# in its own boundary assembly (cs2wasm's `Boundary/Runtime.cs` and neighbours), not C.
  That keeps SPEC 2's "no other compiler for .NET code" and lets the ownership analysis see into it.
- **Reuse with care.** NetWasm's CoreLib is MIT, so studying or even copying its NetWasm-specific boundary files would
  be permitted with attribution. But SPEC 9 wants upstream's code, so the useful parts are the compiler-side passes the
  survey lists (unwritten-static folding, constant cctor stores, constant arguments, pure-call evaluation, fixpoint
  reachability). Those are compiler designs; they are Community-licensed as code, but the ideas are general (NativeAOT
  does most of them).
- **Differ on invariant globalization.** NetWasm throws on culture-sensitive comparisons. Under SPEC 9, il2wasm runs
  .NET's own invariant-mode code, which gives .NET's invariant results (`InvariantGlobalization=true`). The oracle must
  run with the same setting (section 5).

---

## 5. Test suite

Files read: `tests/` (layout, line counts), `tests/NetWasm.Compiler.Tests/Correctness/{CorrectnessTestRunner,
DifferentialCorpusRunner,DesktopOracleRunner,KnownNonBugCorpusPartitioner,BigIntegerCorpusTests}.cs` (structure and
comments only), `tests/NetWasm.Compiler.Tests/Correctness/Fixtures/flow-exception-order/*`,
`tests/NetWasm.Testing.OracleHost/*`, `tests/libraries/Directory.Build.props`, `eng/{test-netwasm-app.sh,
qualify-open-source-suts.py}` (headers), `eng/open-source-suts/*/packages.txt`, `docs/spec-deviations.md`.

### 5.1 Layout and size

| Part | Licence | Size | What it is |
|---|---|---|---|
| `tests/NetWasm.Compiler.Tests` | Community | 1,696 files, about 280 k lines; about 4,360 `[Fact]`/`[Theory]` marks in all | The main suite. `Correctness/` (540 files) is the differential corpus: about 254 classes and 319 `runner.Run` calls compile an embedded C# program and compare it with desktop .NET; 75 `Fixtures/` cases have a source file plus a JSON case manifest; generators produce more (language corpus, a valid-C# fuzzer from seeds, CFG property cases). The rest are unit tests of NetWasm's own passes |
| `tests/NetWasm.Compiler.{Wasm,Metadata,ControlFlow,ComponentModel,Core,…}.Tests` | Community | about 150 k lines | Unit tests of NetWasm's internals; not portable |
| `tests/end-to-end` | Community (one dir Apache) | 154 files | SDK consumer projects: browser interop, components, STJ and regex source generation, time zones |
| `compiler-qualification` | Community | small | CIL opcode coverage inventory, benchmarks |
| `tests/libraries` | **MIT** | 24 projects, about 379 TUnit tests | Library behaviour (Linq, Json, Regex, Xml, Http, Compression, Channels, Crypto, Runtime …), run on NetWasm and on desktop .NET 11 as the oracle |
| `eng/open-source-suts` | **MIT** | 6 programs | Real packages at pinned versions; NetWasm's printed output must equal desktop's, run with and without hardware intrinsics |

### 5.2 How a differential test runs

1. **A case** is a C# program with a static `EntryPoint.Run(int input)` that returns an `int`. Most tests fold their
   observations into a running hash, so one int carries a lot of output. A case also has a name and namespace, a list
   of inputs, and options: optimization level (Oz, O3), wasm64, an optimized second run, report-all-mismatches. Some
   cases also expose a static trace that the harness reads after each run. Stored fixtures use a JSON manifest: schema
   version, case id, feature ids, source files, entry method, inputs, and per input the expected return value or
   exception type.
2. **The oracle side.** The harness compiles the source with Roslyn against desktop .NET 11, and a separate .NET process
   (`NetWasm.Testing.OracleHost`) loads the assembly and calls `Run` for each input. It records one of three outcomes
   per input:
   - the return value;
   - the exception type that escaped;
   - the typed trace records (kind, event id, two payload ints), read from the type.
3. **The NetWasm side.** The same source is compiled against NetWasm's CoreLib, through `nwc`, linked with the chosen
   collector, and run in Node. The results are compared input by input.
4. **Accepted deviations** are listed in `KnownNonBugCorpusPartitioner.cs` and `docs/spec-deviations.md`. Some cases
   keep operations "off the desktop comparison" in their source; BigInteger's limb-width-dependent operations are one
   example.

### 5.3 Running them against il2wasm with .NET as the oracle

Plan (all code il2wasm's own, MIT):

1. **Fetch.** `tools/netwasm-tests.mjs fetch` clones NetWasm at the pinned commit into a cache outside the repository.
   Nothing is committed.
2. **Extract.** A Roslyn-based extractor reads the cached test classes. For each `runner.Run(new(...))` it takes the
   name, namespace, source constant and input list, and it reads each fixture's JSON and source files. It writes a case
   list (name, source path in the cache, inputs) into the cache. Only these programs are used, not NetWasm's harness
   code. NetWasm's stored expectations are ignored.
3. **Build.** For each case, generate a tiny `net11.0` console project in the cache with:
   - the case's source;
   - a generated `Main` that calls `Run(i)` for each input and prints `value`, or `throw <type full name>`, and the
     trace if present;
   - `InvariantGlobalization=true`, and the same `UseSystemResourceKeys` setting on both sides.
4. **Oracle.** `dotnet run` gives the expected output. Run it three times to detect cases whose .NET result is not
   stable (GC timing).
5. **Run.** il2wasm compiles the same project, the module runs under il2wasm's host, and the output must be byte-equal.
   Run both modes (checked and as shipped, SPEC 13) and under load, as cs2wasm's suite does.
6. **Exclude** cases that use NetWasm-only APIs, found by compile failure against stock .NET: JSImport/JSExport,
   NetWasm.* namespaces, WIT, collector selection. List each exclusion with its reason.

Which cases encode NetWasm's deviations, so that il2wasm must expect .NET's behaviour instead (with .NET as the oracle
this happens automatically; the point is that these must not be skipped):

- `KnownNonBugCorpusPartitioner.cs` (the accepted-difference skips) and the "off the desktop comparison" carve-outs in
  sources. These include BigInteger operations that depend on limb width, and float paths NetWasm runs through libm
  (`Pow`, `Sin`/`Tan` of negative zero) where SPEC 7 requires WASM-computed math anyway.
- Cases asserting `PlatformNotSupportedException` (8 files in `Correctness/`): culture comparisons, member reflection,
  `DynamicInvoke` through `Delegate`, unrecognised RVA initialisation.
- Cases about type names (`type#<id>`, 9 files) and resource-key messages.
- **GC-dependent cases** (about 46 files mention `GC.Collect`, `WeakReference` or finalizers). These test NetWasm's
  collectors. Under .NET, their results may depend on GC timing. Keep only those whose .NET result is stable across
  runs and in Debug and Release. Under SPEC 4.4, il2wasm must give an outcome .NET allows, and il2wasm frees earlier
  than .NET, which is allowed.
- **Pointer-size cases.** Under wasm32, `IntPtr.Size` is 4, while desktop .NET x64 says 8. **Open question for the
  owner:** for pointer-size-dependent output, is the oracle .NET on x64, or a 32-bit .NET (x86 or arm32)? NetWasm
  compares wasm32 against x64 and carves such cases out.

Effort: about a day for fetch and extract and a day for the project generator and runner, reusing `tools/suite.mjs`'s
sharding. The suite size (about 320 runner cases plus 75 fixtures, each with several inputs, plus the 379 MIT library
tests) makes it larger than cs2wasm's 264 tests.

---

## 6. Other things il2wasm needs to know

Files read: `docs/{targets-and-outputs,javascript-interop,web-workers,host-capabilities,size-and-methodology,
security}.md` (skimmed), `src/NetWasm.Compiler.Browser/README.md`, `src/NetWasm.Toolchain/Browser/LLD/README.md`,
`src/NetWasm.Hosting/` and `src/NetWasm.Runtime/*.mjs` (names).

- **MSBuild integration.** All of it is in `buildTransitive` targets of the SDK and of the packs:
  - compile after `CoreCompile`;
  - native archive collection before the link;
  - runtime materialization, merge, optimisation and packaging;
  - publish items added to `ResolvedFileToPublish`.

  Errors use MSBuild's `error NWxxxx:` form. il2wasm can follow this shape with one `il2wasm` package whose
  `buildTransitive` targets call the in-process compiler task. No custom SDK is needed (section 1).
- **Required WASM features:** exception handling (`try_table`, `exnref`), fixed-width SIMD, bulk memory, reference
  types, multi-value, saturating conversions, sign extension. The minimum browsers are Chrome and Edge 137, Firefox
  131 and Safari 18.4, and Node 24.15. This is the same baseline il2wasm has, plus threads.
- **The loader.** For JS hosts the build writes an ES module whose `open()` (for a library) or `run()` (for a program)
  does the following:
  1. fetches the `.wasm` and a JSON manifest;
  2. checks each file against the SHA-256 digests in the manifest;
  3. compiles the module;
  4. connects the program's `[JSImport]` declarations to JS functions, passed in explicitly by the caller (no implicit
     browser APIs);
  5. returns the `[JSExport]` methods as JS functions, with Promises for `Task`.

  Each `open()` is an independent instance. Exceptions crossing to JS become `JSException` or managed-error objects.
  The manifest has the integrity digests, import modules, export shapes and sidecar names. il2wasm's host can adopt
  digest-checked loading and explicit import modules; JSImport/JSExport are a web-framework-package concern for
  il2wasm (SPEC 8.2), not the compiler's.
- **Workers.** NetWasm's "workers" are separate module instances in Web Workers, with no shared heap, called by message
  passing. il2wasm's threads (SPEC 4.5) are different: one shared memory, one instance per worker.
- **The browser compiler.** `NetWasm.Compiler.Browser` is a virtual-file adapter: inputs are byte arrays, and nothing
  reads host paths. It runs in a worker, with LLD built to WASM and binaryen.js as separate tools. Units are reused per
  method across compilations, with exports and imports to IndexedDB. This matches SPEC 10's in-browser toolchain.
  il2wasm's in-process API (SPEC 2) should take inputs the same way, as bytes plus virtual paths, so the same entry
  point serves the CLI, MSBuild and the browser.
- **Determinism.** Inputs are name-ordered, the toolchain is pinned in `eng/toolchain.json` with SHA-256 checks,
  package archives are made deterministic, and NetWasm verifies repeatable host-tool builds. Its NaNs are not
  canonicalized at run time, where SPEC 7 is stricter, and its math is libm's.
- **Size.** `Console.WriteLine(42)` is 28,383 B as a WASI 0.3 component (eng/size-canary), the number cs2wasm already
  beats (memory note: 27,980 B).

---

## Summary: what il2wasm takes

| Area | Take | Change because of SPEC |
|---|---|---|
| Project input | MSBuild items (`ReferenceCopyLocalPaths`, publish items), not deps.json; transitive framework selection; version conflict rule; feature-switch and runtimeconfig folding; name-ordered inputs | Use the stock `net11.0` build and reference packs, not a custom TFM; keep full messages |
| Native libraries | `.a` archives; the `NativeLibrary` item; both browser-wasm package layouts; reached-only providers; archive signature checks; `WasmImportLinkage` to host imports | Multi-threaded archives and libc; per-thread native stack; full CLR marshalling (delegates, SetLastError, StringBuilder) |
| Link | Own final managed module, `wasm-ld` of native side above managed data, byte-level merge into one core module; callbacks object for UnmanagedCallersOnly and calli trampolines | No binaryen or component model; LLD as a WASM tool for the browser |
| Front end | System.Reflection.Metadata; typed stack validation; try re-entry rewrite; missing-member bodies; pre-order type ids; type-id switch dispatch with shared dispatch functions | SSA IR; generic sharing for reference types; thread-safe cctors; two-pass filters; full reflection metadata and names |
| CoreLib | Intrinsic plus runtime-call layering | Upstream's CoreLib unmodified, runtime in C# |
| Tests | Differential cases from a pinned checkout through il2wasm's own harness, .NET as oracle; MIT `tests/libraries` and `eng/open-source-suts` may be copied with notice | Expectations from .NET only; the owner confirms Qualifying Organization status (s4.6) |
