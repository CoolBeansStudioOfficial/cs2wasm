# NativeAOT's type system (dotnet/runtime, MIT)

The files under this directory are the sources of `ILCompiler.TypeSystem` from dotnet/runtime, as its project
`src/coreclr/tools/aot/ILCompiler.TypeSystem/ILCompiler.TypeSystem.csproj` compiles them, taken at tag `v11.0.0`
(commit `deae2e32bf0e018e09d64597b7c59b62b73bb3ce`). They are used under the MIT licence: `LICENSE.TXT` is
dotnet/runtime's, and each file keeps its own notice.

Paths: `TypeSystem/`, `Internal/` and `System/` are `src/coreclr/tools/Common/...`; `libraries/` is
`src/libraries/Common/src/...`.

Changes made for il2wasm are marked `// il2wasm:` where they are made, and listed here:

- (none yet)
