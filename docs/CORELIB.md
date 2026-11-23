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
