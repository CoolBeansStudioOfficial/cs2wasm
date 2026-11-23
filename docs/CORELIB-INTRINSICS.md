# CoreLib intrinsics: what the IL front end must implement

Part of docs/CORELIB.md (the IL CoreLib built by tools/BuildCoreLib). Every method below is in the built System.Private.CoreLib.dll with the attribute named, as an extern method (RVA 0, no ImplMap) unless the row says it has a body; the IL front end recognises them by identity and emits what the old compiler emitted. Surveyed from the C# front end on 2026-11-23 (file:line references are to src/Il2Wasm at that date).

Source tree: `src/Il2Wasm/Upstream` (218 .cs files); compiler: `src/Il2Wasm/Compiler`. Paths below are relative to `src/Il2Wasm` unless they start with a type name. "Calls.cs:N" is `Compiler/Calls.cs`. Every statement was read from the source; "not found" means no emit case exists.

## Counts

| Group | Count |
|---|---|
| `[Intrinsic]` methods resolving to `Il2Wasm.Runtime.IntrinsicAttribute` (compiler emits them inline: section 1A) | **220** (Mem 19, Rt 138, Atomic 15, Il2WasmArrays 6, Generics 6, TextScan 4, TypeInfo 31, System.Delegate 1) |
| `[Intrinsic]` methods resolving to `System.Runtime.CompilerServices.IntrinsicAttribute` (the Mono half's JIT hints; section 1B) | **20** (Array 5, Debugger 1, Stream 2, Math 1, MathF 1, Object 1, JitHelpers 3, RuntimeHelpers 4, MemoryMarshal 2) |
| `[Intrinsic]` on static fields (section 2) | **11** declarations = **39** fields, all `Rt`, all `static int` |
| All `[Intrinsic]` attribute uses under Upstream | 251 declarations (240 methods + 11 field declarations); grep of the word finds 252 lines, the extra one is a comment in Boundary/Runtime.cs:2 |
| Upstream intrinsics the old compiler implements by name (`Driver.IsUpstreamIntrinsic`, section 3A) | **40** name entries (Unsafe 16, Interlocked 3, SpanHelpers 4, Double/Single.ConvertToIntegerNative 2, RuntimeHelpers 4, Volatile 2, BitOperations 6, Object.GetType 1, Activator.CreateInstance 1, MethodBase.GetCurrentMethod 1); most of their C# declarations live in dotnet/runtime files listed in `manifest.txt`, which are NOT in the repo |
| Effects intrinsics (`Driver.IsEffectsIntrinsic`, section 3B) | **2** (accessors of `Il2Wasm.Effects.Suppressed`) |
| `[Import(module, name)]` methods (section 4) | **198** attributes = **53** distinct declarations + 145 generated copies (5 libraries x 29, byte-identical to the CoreLib's `Interop.Sys.Env.Block` + 28 `Interop.Sys.Fs` imports). All 53 have module `il2wasm`. |

Attribute declarations: `Il2Wasm.Runtime.IntrinsicAttribute` and `ImportAttribute` are declared in `Upstream/System.Private.CoreLib/Boundary/Runtime.cs:12` and `:16`. `System.Runtime.CompilerServices.IntrinsicAttribute` comes from dotnet/runtime's `IntrinsicAttribute.cs` (manifest.txt:1147), not in the repo. Resolution is by the file's namespace/usings: every file in `Boundary/` is `namespace Il2Wasm.Runtime` (own type), so its `[Intrinsic]` is the Il2Wasm one; `Runtime/System/**` Mono-half files are `namespace System...` (or `System.Runtime.CompilerServices`) with `using System.Runtime.CompilerServices` and no `using Il2Wasm.Runtime`, so theirs is the System one; the one exception is `Delegate.Mono.cs:41`, which writes `[Il2Wasm.Runtime.Intrinsic]` in full. No file under Upstream uses a combined attribute list (`[Intrinsic, ...]`, `[MethodImpl(...), Intrinsic]`) or `[IntrinsicAttribute]`: every use is a plain `[Intrinsic]` on its own or on the same line as the declaration (a few have a trailing comment or sit above `[NonVersionable]`/`[MethodImpl]` on following lines).

## What the IL front end must know (surprises, compile-time state)

How the old compiler decides what is an intrinsic (`Driver.cs:995`): `IsIntrinsic(m) = IsLibrary(m) && (m.OriginalDefinition has Il2Wasm.Runtime.IntrinsicAttribute || IsUpstreamIntrinsic(m)) || IsEffectsIntrinsic(m)`. The System `[Intrinsic]` attribute is never looked at by the compiler; only the name allowlist in `IsUpstreamIntrinsic` (section 3A) makes an upstream method intrinsic. A method with a body that is intrinsic has its body ignored (the call is replaced; the body is never compiled for it). Dispatch (`Calls.cs:639-652`): `Driver.IsUpstreamIntrinsic(m) ? EmitUpstreamIntrinsic(m) : EmitIntrinsic(m)`; an unknown name throws `CompileException` ("intrinsic not supported" Calls.cs:919, "unknown intrinsic" Calls.cs:1253).

1. **`EmitIntrinsic` dispatches on `ContainingType.Name + "." + Name` only** (Calls.cs:927), not on namespace and not on overload: all overloads of a name share one case. Arguments are already on the wasm stack when the case runs; refs and pointers are i32 addresses (wasm32), `nint`/`nuint` are i32, `T` for `Take<T>/Borrow<T>` is read as the value at an address.
2. **Post-emit hook** (Calls.cs:643-645): after any intrinsic, if any emitted op writes memory (`Driver.WritesMemory`), the compiler records a write to the "all array elements" key (`D.NoteFieldWrite(E, Driver.ElementsKey)`). That feeds the field/local borrow analyses (FieldBorrows.cs), so an IL front end needs an equivalent "this call may write array elements" fact for memory-writing intrinsics (Mem.Store*, Mem.Copy/Fill, Atomic stores/rmw, TextScan.Upper16/Lower16, Unsafe.Copy/CopyBlock/InitBlock, SpanHelpers.memmove/memset/..., Interlocked narrow ops).
3. **Ref-returning intrinsics** (`Mem.RefI32`, `Rt.NullRef<T>`): the emit leaves the address; the caller loads through it unless it wants the address (Calls.cs:646-651).
4. **Ownership of results** is part of the contract: `Rt.Take` returns Own.Owned, `Rt.Borrow` Own.Unstable, `Rt.TypeName`/`TypeInfo.FullName`/`Name`/`Namespace` Own.Stable, `TypeInfo.Invoke`, `Rt.ObjectArrayDelegate`, `Rt.DynamicInvokeRaw`, `Delegate.TargetOf`, `Object.GetType`, `Activator.CreateInstance`, `MethodBase.GetCurrentMethod` Own.Owned; `Unsafe.As<T>(object)` Own.Unstable. Counted-reference bookkeeping (retain/release) is elided or added from these.
5. **Thread mode is whole-program, discovered by restarting** (`Api.cs:110-142`): compilation starts single-threaded, throws `Driver.ThreadsSeen` when it meets a thread start, and the whole program is compiled again with `ThreadMode = true` (then up to three more rounds with the sharing analysis' prior map). Things that depend on it: `Rt.ThreadMode` (i32.const, and constant-folded at Folding.cs:186 so the other branch is not compiled), `Atomic.CountAdd32`/`CountCompareExchange32` (atomic rmw in thread mode, plain load/store otherwise; Calls.cs:1191-1208), `Rt.RuntimeLockHeld`, `Rt.FusedDrop` (off in thread mode), and every `[Intrinsic]` **field** (thread-static in thread mode, plain static otherwise; section 2).
6. **Compile-time environment variables bake constants** into code: all `Rt.No*`/`*Break`/`*Stats`/`ArrayMoveMode`/etc. switches read `IL2WASM_*` at compile time and emit `i32.const` (Calls.cs:997-1054, plus `Rt.NoThreads` from `Driver.s_noThreads`, `Rt.Checked` from `Options.Checked`, `Rt.MemStats*` from `IL2WASM_MEMSTATS`, `Rt.RipePages/HoldPages`, `Rt.ShareCheck`, `Rt.DeadlockDump`). `Rt.NoPlainParts` is 1 **by default** (inverted switch). The IL front end must reproduce these constants (or pick fixed values) or the runtime's behaviour differs. The compiler's `IL2WASM_SCALAR_SQRT` changes `Rt.Sqrt`'s code.
7. **Late-bound values**: `Rt.HeapBase` and `Rt.TlsSize` are `LateAddr` constants resolved after the memory layout (Driver.cs:1670; `TlsSize` grows as thread statics are first seen, Driver.cs:160-168); `Rt.TypeIdOf`/`MarkConstructed` and `Unbox`'s box offset use `E.Tid` placeholders patched when type ids are numbered at the start of `RuntimeGen.Finish` (TypeInfo.cs). `Rt.PlaceLockWord` and `Rt.RuntimeLockHeld` allocate named static blocks (`rt.ref.locks` 4096 B align 64; `rt.lock.owner` 4 B).
8. **Whole-program generated functions behind intrinsics**: most "call to generated function" rows call a function that exists only in the output module and whose body is written late (`RuntimeGen.Finish`/`EmitArenaSupport`) from whole-program results: type graph / cycle-capable types (`rt.scc_of`, `rt.arena_slots`, `rt.arena_clear/free/visit/finalize/fin_type`, `rt.arena_is_slot`, `arena.link at`, `arena.unlink at`, `arena.move at`, `rt.array_grow`, `rt.part_weak`, `rt.weak_any/weak_dying`), what the program uses (`rt.single_threaded`, `rt.first_chance`, `rt.raise_first_chance`, `rt.frames_wanted`, `rt.wide_sizes`: 0/1 or empty bodies when the feature is absent), reflection tables (all `TypeInfo.*` table lookups are `rt.type_<kind>_by_id` functions "emitted only when the program reaches them", TypeInfo.cs:7-20; `rt.type_name_table`, `rt.program_types`, `rt.generic_instances`, `rt.assembly_names`, `rt.manifest_resources`, `rt.resource_init/unpack`, `rt.meta_of`, `rt.delegate_target`, `rt.object_array_delegate`, `rt.dynamic_invoke`, `rt.delegate_arity`, `rt.delegate_method`, frame-methods table, `rt.throw_sites`). An IL front end must generate each of these bodies from its own whole-program analysis; `RuntimeFns2.cs:530-660` has the doc comment of each.
9. **Intrinsics that change whole-program analysis** (side effects at compile time, no/ordinary code): `Rt.MarkConstructed<T>` and `Activator.CreateInstance<T>` (`D.MarkConstructed(T)`: T becomes a constructed type: dispatch tables, arenas, finalizers), `Rt.IsInstanceOf<T>` (creates `rt.isinst <T>`), `Rt.TypeName`/`TypeInfo.FullName` (`D.WantLazyTypeNames()`), `Il2WasmArrays.BoxesElements` (`D.MarkBoxedElements()`), `Unsafe.As<TFrom,TTo>(ref)` (`Sh.RefAlias`, `D.Arenas.RefAlias`: the reference-graph analyses treat the two types' slots as aliased; Calls.cs:756), `MethodBase.GetCurrentMethod` (needs the caller's frame name, `Meta.FrameMethod`).
10. **Layout-dependent constants**: `Rt.SizeOf`, `Rt.BoxDataOffset`, `Rt.ContainsReferences` (= "needs reference counting": `T.NeedsRc`), `Rt.IsReference`, `Unsafe.Unbox` (adds box data offset), `RuntimeHelpers.GetRawData` (+ `Types.ObjectHeader`), `ObjectHasComponentSize` (type id 1 = string, or type flag bit 4 via `rt.type_flags_by_id`), `IsReferenceOrContainsReferences`/`IsBitwiseEquatable` (also folded to constants at Folding.cs:102-104,`Unsafe.SizeOf<T>` at Folding.cs:150-155). Object header: type id at +4 (`Rt.TypeId`, `Object.GetType`, `ObjectHasComponentSize`); array data offset depends on rank (`Types.ArrayDataOffset`).
11. **NaN canonicalisation is in intrinsics** (SPEC 7.3): `Unsafe.As<float/double, non-float>` canonicalises a NaN in memory first (Calls.cs:759-772); `Rt.CopySign` canonicalises operand b (`rt.canon_f64`); `ConvertToIntegerNative` is saturating/NaN->0 everywhere.
12. **Several `EmitIntrinsic` cases have no declaration under Upstream** (21 labels; dead or for generated code): `Array.ElementSize/RankOf/HasCountedElements`, `ArrayHelpers.CopyElements/ClearElements`, `EnumFormat.Info/InfoOfId`, `Type.FullNameOf/ShortNameOf/FlagsOf/MetaOf`, `Reflect.Invoke`, `Limbo.DestroyObject`, `Sharing.Visit`, `Rt.SetThreadId`, `Rt.SetTlsBase`, `Rt.StackPointer`, `Rt.SetStackPointer` (Calls.cs:1217-1250), `Rt.InvokeExport` (throws "not an intrinsic call", Calls.cs:1251), and the plugin-only `Effects.get_/set_Suppressed` (section 3B). `Rt.StackPointer`/`SetStackPointer` would be `global.get/set` of `D.StackPointer`, `Rt.SetThreadId`/`SetTlsBase` `global.set` of ThreadId/TlsBase. They would only work if a library method with the Il2Wasm attribute and that type/name appeared (a stale name does not fail anything until used).
13. **Generics.\*** (6 methods) have no `EmitIntrinsic` case; they are consumed earlier by folding/special-casing and rely on monomorphization (concrete T). An IL front end working on shared/uninstantiated generics would have to provide this.
14. **`[Intrinsic]` is not the only place the compiler special-cases library methods by name.** Other name- or signature-keyed hooks that an IL front end must also replicate: trimmer-style *substitutions* from `manifest.txt` (`substitute` lines, 156 of them, `Library.Substitution`, Library.cs:129; e.g. `GlobalizationMode.Invariant` true, `RuntimeFeature.IsDynamicCodeSupported` false, `IntPtr.Size` 4, `BitConverter.IsLittleEndian` true, `...X86.*.IsSupported` false; a value `=>Type.Method` redirects to a boundary method); `KnownBool` folding of `IsHardwareAccelerated`/`IsSupported`/`Debugger.IsAttached` etc. (Folding.cs:163-187); `MemoryMarshal.GetArrayDataReference<T>` (RefStores.cs:93, aliasing analysis only); `EqualityComparer<T>.Default.Equals/GetHashCode` for integer T (Calls.cs:46-60); `[Conditional]`/partial-method omission (Calls.cs:569). Also the host-boundary mechanism `BoundaryByDocId` (Driver.Classify, Driver.cs:1033-1044) that turns non-library methods named in the boundary file into imports.

## 1. `[Intrinsic]` methods

### 1A. Resolving to `Il2Wasm.Runtime.IntrinsicAttribute` (compiler: `EmitIntrinsic`, Calls.cs:925-1254; dispatch Calls.cs:639-652)

All 220 are `static extern` methods (none has a C# body)."Calls.cs:N" is the line of the case label; `(result ownership ...)` is the `return Own.X` of the case. Where a row says "call to generated function `rt.x`" the arguments are already on the stack and the function's signature is i32-in/i32-out as given in RuntimeFns*.cs.

#### Il2Wasm.Runtime.Mem (19)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Mem.LoadI32 | `int LoadI32(int addr)` | extern (Runtime.cs:23) | i32.load offset=0 (Calls.cs:967) |
| Mem.LoadU8 | `int LoadU8(int addr)` | extern (Runtime.cs:24) | i32.load8_u offset=0 (Calls.cs:968) |
| Mem.LoadI8 | `int LoadI8(int addr)` | extern (Runtime.cs:25) | i32.load8_s offset=0 (Calls.cs:969) |
| Mem.LoadU16 | `int LoadU16(int addr)` | extern (Runtime.cs:26) | i32.load16_u offset=0 (Calls.cs:970) |
| Mem.LoadI16 | `int LoadI16(int addr)` | extern (Runtime.cs:27) | i32.load16_s offset=0 (Calls.cs:971) |
| Mem.LoadI64 | `long LoadI64(int addr)` | extern (Runtime.cs:28) | i64.load offset=0 (Calls.cs:972) |
| Mem.LoadF32 | `float LoadF32(int addr)` | extern (Runtime.cs:29) | f32.load offset=0 (Calls.cs:973) |
| Mem.LoadF64 | `double LoadF64(int addr)` | extern (Runtime.cs:30) | f64.load offset=0 (Calls.cs:974) |
| Mem.StoreI32 | `void StoreI32(int addr, int v)` | extern (Runtime.cs:31) | i32.store offset=0 (Calls.cs:975) |
| Mem.StoreU8 | `void StoreU8(int addr, int v)` | extern (Runtime.cs:32) | i32.store8 offset=0 (Calls.cs:976) |
| Mem.StoreU16 | `void StoreU16(int addr, int v)` | extern (Runtime.cs:33) | i32.store16 offset=0 (Calls.cs:977) |
| Mem.StoreI64 | `void StoreI64(int addr, long v)` | extern (Runtime.cs:34) | i64.store offset=0 (Calls.cs:978) |
| Mem.StoreF32 | `void StoreF32(int addr, float v)` | extern (Runtime.cs:35) | f32.store offset=0 (Calls.cs:979) |
| Mem.StoreF64 | `void StoreF64(int addr, double v)` | extern (Runtime.cs:36) | f64.store offset=0 (Calls.cs:980) |
| Mem.Copy | `void Copy(int dst, int src, int n)` | extern (Runtime.cs:37) | memory.copy (Calls.cs:981) |
| Mem.Fill | `void Fill(int dst, int value, int n)` | extern (Runtime.cs:38) | memory.fill (Calls.cs:982) |
| Mem.Size | `int Size()` | extern (Runtime.cs:39) | memory.size (Calls.cs:983) |
| Mem.Grow | `int Grow(int pages)` | extern (Runtime.cs:40) | memory.grow (Calls.cs:984) |
| Mem.RefI32 | `ref int RefI32(int addr)` | extern (Runtime.cs:42) | nothing: the i32 address argument stays on the stack and is the ref (Calls.cs:1225) |

#### Il2Wasm.Runtime.Rt (138)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Rt.ArrayMoveMode | `int ArrayMoveMode()` | extern (ArrayMoves.cs:8) | i32.const: 1 if IL2WASM_NO_ARRAY_MOVE=1, else 2 if IL2WASM_ARRAY_MOVE_BREAK=1, else 3 if IL2WASM_ARRAY_MOVE_CYCLE=1, else 0 (compile-time environment) (Calls.cs:1052) |
| Rt.PlaceLockWord | `int PlaceLockWord(int addr)` | extern (GroupLocks.cs:32) | i32.const 2; i32.shr_u; i32.const 63; i32.and; i32.const 6; i32.shl; i32.const <address of static table `rt.ref.locks`, 64*64 bytes, align 64>; i32.add: (addr>>2 & 63)*64 + table (Calls.cs:1241) |
| Rt.AddressOf | `int AddressOf(object o)` | extern (Runtime.cs:82) | nothing: an object reference already is its i32 address (Calls.cs:985) |
| Rt.NullRef | `ref T NullRef<T>()` | extern (Runtime.cs:84) | i32.const 0 (the null ref) (Calls.cs:1224) |
| Rt.Share | `void Share(object o)` | extern (Runtime.cs:86) | call to generated function `rt.share` (Calls.cs:1226) |
| Rt.Take | `T Take<T>(int addr)` | extern (Runtime.cs:88) | nothing: the i32 address is reinterpreted as T; the result is treated as an owned reference (Own.Owned) (Calls.cs:986; result ownership Own.Owned) |
| Rt.Borrow | `T Borrow<T>(int addr)` | extern (Runtime.cs:90) | nothing: the i32 address is reinterpreted as T; the result is treated as an unstable borrow (Own.Unstable) (Calls.cs:987; result ownership Own.Unstable) |
| Rt.TypeId | `int TypeId(object o)` | extern (Runtime.cs:91) | i32.load offset=4 (the type id is the header word at +4) (Calls.cs:988) |
| Rt.TypeIdOf | `int TypeIdOf<T>()` | extern (Runtime.cs:92) | i32 type-id constant of canonical T (E.Tid(D.TypeId(T)); ids are numbered densely at Finish, TypeInfo.cs) (Calls.cs:990) |
| Rt.MarkConstructed | `int MarkConstructed<T>()` | extern (Runtime.cs:93) | same i32 type-id constant (E.Tid(D.MarkConstructed(T))) and records T as constructed (whole-program: decides what dispatch tables, arenas and finalizers exist) (Calls.cs:991) |
| Rt.HeapBase | `int HeapBase()` | extern (Runtime.cs:94) | i32 address constant, late bound (LateAddr over D.HeapBase, known after the memory layout, Driver.cs:1670) (Calls.cs:992) |
| Rt.Retain | `void Retain(object o)` | extern (Runtime.cs:95) | call `rt.retain`, result dropped (Calls.cs:993) |
| Rt.Release | `void Release(object o)` | extern (Runtime.cs:96) | call to generated function `rt.release` (Calls.cs:994) |
| Rt.TypeName | `string TypeName(object o)` | extern (Runtime.cs:97) | D.WantLazyTypeNames() (switches the lazy type-name table on) then call `rt.type_name` (Calls.cs:1076; result ownership Own.Stable) |
| Rt.IsInstanceOf | `bool IsInstanceOf<T>(object o)` | extern (Runtime.cs:98) | call to the per-target-type generated test `rt.isinst <T>` (RuntimeGen.IsInst, RuntimeGen.cs:96; canonical T) (Calls.cs:1077) |
| Rt.Unreachable | `void Unreachable()` | extern (Runtime.cs:99) | unreachable (Calls.cs:1078) |
| Rt.F64Bits | `long F64Bits(double d)` | extern (Runtime.cs:100) | i64.reinterpret_f64 (Calls.cs:1079) |
| Rt.F64FromBits | `double F64FromBits(long l)` | extern (Runtime.cs:101) | f64.reinterpret_i64 (Calls.cs:1080) |
| Rt.F32Bits | `int F32Bits(float f)` | extern (Runtime.cs:102) | i32.reinterpret_f32 (Calls.cs:1081) |
| Rt.F32FromBits | `float F32FromBits(int i)` | extern (Runtime.cs:103) | f32.reinterpret_i32 (Calls.cs:1082) |
| Rt.Sqrt | `double Sqrt(double d)` | extern (Runtime.cs:104) | f64x2.splat; f64x2.sqrt; f64x2.extract_lane 0 (not scalar f64.sqrt, to avoid a register dependency); f64.sqrt instead if IL2WASM_SCALAR_SQRT=1 at compile time (Calls.cs:1083) |
| Rt.Floor | `double Floor(double d)` | extern (Runtime.cs:105) | f64.floor (Calls.cs:1091) |
| Rt.Ceiling | `double Ceiling(double d)` | extern (Runtime.cs:106) | f64.ceil (Calls.cs:1092) |
| Rt.Truncate | `double Truncate(double d)` | extern (Runtime.cs:107) | f64.trunc (Calls.cs:1093) |
| Rt.RoundEven | `double RoundEven(double d)` | extern (Runtime.cs:108) | f64.nearest (Calls.cs:1094) |
| Rt.Abs | `double Abs(double d)` | extern (Runtime.cs:109) | f64.abs (Calls.cs:1095) |
| Rt.Min | `double Min(double a, double b)` | extern (Runtime.cs:110) | f64.min (Calls.cs:1096) |
| Rt.Max | `double Max(double a, double b)` | extern (Runtime.cs:111) | f64.max (Calls.cs:1097) |
| Rt.CopySign | `double CopySign(double a, double b)` | extern (Runtime.cs:112) | call `rt.canon_f64` on the top operand (NaN made canonical), then f64.copysign (SPEC 7.3) (Calls.cs:1099) |
| Rt.SqrtF | `float SqrtF(float d)` | extern (Runtime.cs:113) | f32.sqrt (Calls.cs:1100) |
| Rt.FloorF | `float FloorF(float d)` | extern (Runtime.cs:114) | f32.floor (Calls.cs:1101) |
| Rt.CeilingF | `float CeilingF(float d)` | extern (Runtime.cs:115) | f32.ceil (Calls.cs:1102) |
| Rt.TruncateF | `float TruncateF(float d)` | extern (Runtime.cs:116) | f32.trunc (Calls.cs:1103) |
| Rt.RoundEvenF | `float RoundEvenF(float d)` | extern (Runtime.cs:117) | f32.nearest (Calls.cs:1104) |
| Rt.AbsF | `float AbsF(float d)` | extern (Runtime.cs:118) | f32.abs (Calls.cs:1105) |
| Rt.MinF | `float MinF(float a, float b)` | extern (Runtime.cs:119) | f32.min (Calls.cs:1106) |
| Rt.MaxF | `float MaxF(float a, float b)` | extern (Runtime.cs:120) | f32.max (Calls.cs:1107) |
| Rt.I32Clz | `int I32Clz(int v)` | extern (Runtime.cs:121) | i32.clz (Calls.cs:1108) |
| Rt.I32Ctz | `int I32Ctz(int v)` | extern (Runtime.cs:122) | i32.ctz (Calls.cs:1109) |
| Rt.I32Popcnt | `int I32Popcnt(int v)` | extern (Runtime.cs:123) | i32.popcnt (Calls.cs:1110) |
| Rt.I64Clz | `long I64Clz(long v)` | extern (Runtime.cs:124) | i64.clz (Calls.cs:1111) |
| Rt.I64Ctz | `long I64Ctz(long v)` | extern (Runtime.cs:125) | i64.ctz (Calls.cs:1112) |
| Rt.I64Popcnt | `long I64Popcnt(long v)` | extern (Runtime.cs:126) | i64.popcnt (Calls.cs:1113) |
| Rt.I32Rotl | `int I32Rotl(int v, int n)` | extern (Runtime.cs:127) | i32.rotl (Calls.cs:1114) |
| Rt.I32Rotr | `int I32Rotr(int v, int n)` | extern (Runtime.cs:128) | i32.rotr (Calls.cs:1115) |
| Rt.I64Rotl | `long I64Rotl(long v, long n)` | extern (Runtime.cs:129) | i64.rotl (Calls.cs:1116) |
| Rt.I64Rotr | `long I64Rotr(long v, long n)` | extern (Runtime.cs:130) | i64.rotr (Calls.cs:1117) |
| Rt.SizeOf | `int SizeOf<T>()` | extern (Runtime.cs:131) | i32.const of the storage size of canonical T (T.StorageOf(T).size) (Calls.cs:1118) |
| Rt.IsReference | `bool IsReference<T>()` | extern (Runtime.cs:132) | i32.const 1 if T is a reference type, else 0 (Types.IsRef) (Calls.cs:1119) |
| Rt.ContainsReferences | `bool ContainsReferences<T>()` | extern (Runtime.cs:133) | i32.const 1 if T needs reference counting (T.NeedsRc: it is or holds counted references), else 0 (Calls.cs:1120) |
| Rt.BoxDataOffset | `int BoxDataOffset<T>()` | extern (Runtime.cs:134) | i32.const of the offset of the value in a box of T (BoxDataOffset, Calls.cs; depends on T layout) (Calls.cs:1121) |
| Rt.ReleaseElements | `void ReleaseElements<T>(int addr, int count)` | extern (Runtime.cs:135) | call to the per-element-type generated release-range function (RuntimeFns.ReleaseRange(canonical T)) with (addr, count) (Calls.cs:1122) |
| Rt.RetainElements | `void RetainElements<T>(int addr, int count)` | extern (Runtime.cs:136) | call to the per-element-type generated retain-range function (RuntimeFns.RetainRange(canonical T)) with (addr, count) (Calls.cs:1129) |
| Rt.Rethrow | `void Rethrow(object ex)` | extern (Runtime.cs:138) | call `rt.retain`; if D.UsesFilters (program has exception filters) also tee, call `rt.first_pass`, get; then `throw` of the module exception tag (D.ExceptionTag) (Calls.cs:1245) |
| Rt.WideSizes | `bool WideSizes()` | extern (Runtime.cs:167) | call to generated function `rt.wide_sizes` (Calls.cs:1015) |
| Rt.RipePages | `bool RipePages()` | extern (Runtime.cs:574) | i32.const 1, or 0 if IL2WASM_NO_RIPE_PAGES=1 at compile time (Calls.cs:1026) |
| Rt.HoldPages | `bool HoldPages()` | extern (Runtime.cs:588) | i32.const 1, or 0 if IL2WASM_NO_HOLD_PAGES=1 at compile time (Calls.cs:1027) |
| Rt.ThreadId | `int ThreadId()` | extern (Runtime.cs:668) | global.get of the ThreadId global (D.ThreadId) (Calls.cs:1217) |
| Rt.ShareCheck | `bool ShareCheck()` | extern (Runtime.cs:671) | i32.const 1 if IL2WASM_SHARE_CHECK=1 at compile time (RuntimeFns.ShareCheckOn), else 0 (Calls.cs:1218) |
| Rt.Checked | `bool Checked()` | extern (Runtime.cs:680) | i32.const 1 if the build is `--checked` (D.Options.Checked), else 0 (Calls.cs:1222) |
| Rt.NoThreads | `bool NoThreads()` | extern (Runtime.cs:709) | i32.const 1 if IL2WASM_NO_THREADS=1 at compile time (Driver.s_noThreads, Driver.cs:176), else 0 (Calls.cs:1021) |
| Rt.NoDropCheck | `bool NoDropCheck()` | extern (Runtime.cs:712) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_DROP_CHECK (Calls.cs:1054) |
| Rt.NoTakeIn | `bool NoTakeIn()` | extern (Runtime.cs:715) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_TAKE_IN (Calls.cs:1031) |
| Rt.NoFreshJoin | `bool NoFreshJoin()` | extern (Runtime.cs:718) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_FRESH_JOIN (Calls.cs:1032) |
| Rt.NoArrayReplace | `bool NoArrayReplace()` | extern (Runtime.cs:721) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_ARRAY_REPLACE (Calls.cs:1053) |
| Rt.NoPinnedSkip | `bool NoPinnedSkip()` | extern (Runtime.cs:724) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_PINNED_SKIP (Calls.cs:1041) |
| Rt.ArenaSlots | `int ArenaSlots(int typeId)` | extern (Runtime.cs:728) | call to generated function `rt.arena_slots` (Calls.cs:996) |
| Rt.NoStoreWaits | `bool NoStoreWaits()` | extern (Runtime.cs:731) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_STORE_WAITS (Calls.cs:1050) |
| Rt.NoEarlyGiveBack | `bool NoEarlyGiveBack()` | extern (Runtime.cs:734) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_EARLY_GIVE_BACK (Calls.cs:1051) |
| Rt.ArrayRankAt | `int ArrayRankAt(int array)` | extern (Runtime.cs:738) | call to generated function `rt.array_rank` (Calls.cs:1035) |
| Rt.ArrayElementSizeAt | `int ArrayElementSizeAt(int array)` | extern (Runtime.cs:739) | call to generated function `rt.array_elem_size` (Calls.cs:1036) |
| Rt.NoSlotWalk | `bool NoSlotWalk()` | extern (Runtime.cs:744) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_SLOT_WALK (Calls.cs:1034) |
| Rt.SlotWalkBreak | `bool SlotWalkBreak()` | extern (Runtime.cs:747) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_SLOT_WALK_BREAK (Calls.cs:1033) |
| Rt.DropStats | `bool DropStats()` | extern (Runtime.cs:749) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_DROP_STATS (Calls.cs:1028) |
| Rt.StaleHolds | `bool StaleHolds()` | extern (Runtime.cs:753) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_STALE_HOLDS (Calls.cs:1030) |
| Rt.ArenaReport | `bool ArenaReport()` | extern (Runtime.cs:756) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_ARENA_REPORT (Calls.cs:1029) |
| Rt.SingleThreaded | `bool SingleThreaded()` | extern (Runtime.cs:922) | call to generated function `rt.single_threaded` (Calls.cs:1014) |
| Rt.FirstChanceWanted | `bool FirstChanceWanted()` | extern (Runtime.cs:925) | call to generated function `rt.first_chance` (Calls.cs:1016) |
| Rt.FirstChance | `void FirstChance(object ex)` | extern (Runtime.cs:927) | call to generated function `rt.raise_first_chance` (Calls.cs:1017) |
| Rt.FramesWanted | `bool FramesWanted()` | extern (Runtime.cs:929) | call to generated function `rt.frames_wanted` (Calls.cs:1018) |
| Rt.TakeThrowSite | `int TakeThrowSite()` | extern (Runtime.cs:931) | global.get of `__throw_site` (D.ThrowSite), i32.const 0, global.set: read and clear (Calls.cs:1019) |
| Rt.ThrowSites | `int ThrowSites()` | extern (Runtime.cs:933) | call to generated function `rt.throw_sites` (Calls.cs:1020) |
| Rt.RefLock | `int RefLock(ref int place)` | extern (Runtime.cs:943) | call to generated function `rt.ref_lock` (Calls.cs:1055) |
| Rt.RefUnlock | `void RefUnlock(int held)` | extern (Runtime.cs:944) | call to generated function `rt.ref_unlock` (Calls.cs:1058) |
| Rt.BulkEnter | `void BulkEnter()` | extern (Runtime.cs:947) | call to generated function `rt.bulk_enter` (Calls.cs:1056) |
| Rt.BulkExit | `void BulkExit()` | extern (Runtime.cs:948) | call to generated function `rt.bulk_exit` (Calls.cs:1057) |
| Rt.HoldEpoch | `void HoldEpoch()` | extern (Runtime.cs:968) | call to generated function `rt.fin_hold` (Calls.cs:1059) |
| Rt.ReleaseEpoch | `void ReleaseEpoch()` | extern (Runtime.cs:969) | call to generated function `rt.fin_unhold` (Calls.cs:1060) |
| Rt.ReleaseThreadStatics | `void ReleaseThreadStatics()` | extern (Runtime.cs:971) | call to generated function `rt.release_thread_statics` (Calls.cs:1061) |
| Rt.ObjectArrayDelegate | `System.Delegate? ObjectArrayDelegate(int delegateTypeId, System.Func<object?[], object?> handler)` | extern (Runtime.cs:974) | call to generated function `rt.object_array_delegate` (Calls.cs:1062; result ownership Own.Owned) |
| Rt.DynamicInvokeRaw | `object? DynamicInvokeRaw(System.Delegate d, object?[]? args)` | extern (Runtime.cs:976) | call to generated function `rt.dynamic_invoke` (Calls.cs:1063; result ownership Own.Owned) |
| Rt.DelegateArity | `int DelegateArity(System.Delegate d)` | extern (Runtime.cs:978) | call to generated function `rt.delegate_arity` (Calls.cs:1064) |
| Rt.DelegateMethodEntry | `int DelegateMethodEntry(int fn)` | extern (Runtime.cs:980) | call to generated function `rt.delegate_method` (Calls.cs:1065) |
| Rt.FrameMethods | `int FrameMethods()` | extern (Runtime.cs:983) | call to generated function `frame-methods table function (Reflection.cs:48)` (Calls.cs:1067) |
| Rt.RetainContents | `void RetainContents(object o)` | extern (Runtime.cs:986) | call to generated function `rt.retain_contents` (Calls.cs:989) |
| Rt.TlsBase | `int TlsBase()` | extern (Runtime.cs:990) | global.get of the TlsBase global (D.TlsBase) (Calls.cs:1229) |
| Rt.TlsSize | `int TlsSize()` | extern (Runtime.cs:991) | i32 address constant, late bound (LateAddr over D.TlsSize = thread-static area size rounded up to 16; known only after all thread statics are seen) (Calls.cs:1231) |
| Rt.RuntimeLock | `void RuntimeLock()` | extern (Runtime.cs:992) | call to generated function `rt.lock_enter` (Calls.cs:1232) |
| Rt.RuntimeUnlock | `void RuntimeUnlock()` | extern (Runtime.cs:993) | call to generated function `rt.lock_exit` (Calls.cs:1233) |
| Rt.RuntimeLockHeld | `bool RuntimeLockHeld()` | extern (Runtime.cs:995) | ThreadMode: i32.atomic.load of static `rt.lock.owner`, compare i32.eq with the ThreadId global; otherwise i32.const 0 (Calls.cs:1234) |
| Rt.ThreadMode | `bool ThreadMode()` | extern (Runtime.cs:999) | i32.const 1 if the driver is in thread mode (D.ThreadMode), else 0; also constant-folded by Folding.cs:186 so `if (Rt.ThreadMode())` branches vanish (Calls.cs:1238) |
| Rt.DeadlockDump | `bool DeadlockDump()` | extern (Runtime.cs:1001) | i32.const 1 if IL2WASM_DEADLOCK_DUMP=1 at compile time (RuntimeFns.DeadlockDumpOn), else 0 (Calls.cs:1239) |
| Rt.MemStatsOn | `bool MemStatsOn()` | extern (Runtime.cs:1333) | i32.const 1 if IL2WASM_MEMSTATS=1 at compile time, else 0 (Calls.cs:1025) |
| Rt.MemStatsGrow | `void MemStatsGrow()` | extern (Runtime.cs:1336) | if IL2WASM_MEMSTATS=1: call library method Rt.MsGrow (a normal compiled body); else nothing (Calls.cs:1022) |
| Rt.MemStatsRecord | `void MemStatsRecord(int delta)` | extern (Runtime.cs:1338) | if IL2WASM_MEMSTATS=1: call library method Rt.MsRecord(1 arg); else drop the argument (Calls.cs:1023) |
| Rt.MemStatsHash | `void MemStatsHash(int first)` | extern (Runtime.cs:1342) | if IL2WASM_MEMSTATS=1: call library method Rt.MsHash(1 arg); else drop the argument (Calls.cs:1024) |
| Rt.SccOf | `int SccOf(int typeId)` | extern (Runtime.cs:2209) | call to generated function `rt.scc_of` (Calls.cs:995) |
| Rt.ArenaClearObject | `void ArenaClearObject(int addr)` | extern (Runtime.cs:2210) | call to generated function `rt.arena_clear` (Calls.cs:1068) |
| Rt.ArenaFinalizeObject | `void ArenaFinalizeObject(int addr)` | extern (Runtime.cs:2211) | call to generated function `rt.arena_finalize` (Calls.cs:1010) |
| Rt.ArenaFinType | `int ArenaFinType(int typeId)` | extern (Runtime.cs:2212) | call to generated function `rt.arena_fin_type` (Calls.cs:1011) |
| Rt.ArenaFreeObject | `void ArenaFreeObject(int addr)` | extern (Runtime.cs:2213) | call to generated function `rt.arena_free` (Calls.cs:1069) |
| Rt.ArenaVisitObject | `void ArenaVisitObject(int addr)` | extern (Runtime.cs:2214) | call to generated function `rt.arena_visit` (Calls.cs:1070) |
| Rt.NoWaitingTop | `bool NoWaitingTop()` | extern (Runtime.cs:2465) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_WAITING_TOP (Calls.cs:997) |
| Rt.NoMergeRepoint | `bool NoMergeRepoint()` | extern (Runtime.cs:2539) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_MERGE_REPOINT (Calls.cs:1038) |
| Rt.NoOwnedChain | `bool NoOwnedChain()` | extern (Runtime.cs:2641) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_OWNED_CHAIN (Calls.cs:1042) |
| Rt.ArrayGrow | `int ArrayGrow(int container, int old, int to, int n)` | extern (Runtime.cs:3100) | call to generated function `rt.array_grow` (Calls.cs:1037) |
| Rt.NoShedHolds | `bool NoShedHolds()` | extern (Runtime.cs:3228) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_SHED_HOLDS (Calls.cs:1047) |
| Rt.NoFusedJoin | `bool NoFusedJoin()` | extern (Runtime.cs:3815) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_FUSED_JOIN (Calls.cs:1008) |
| Rt.NoPlainParents | `bool NoPlainParents()` | extern (Runtime.cs:4023) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_PLAIN_PARENTS (Calls.cs:998) |
| Rt.NoPlainParts | `bool NoPlainParts()` | extern (Runtime.cs:4161) | i32.const 1 if IL2WASM_NO_PLAIN_PARTS=1, or if IL2WASM_PLAIN_PARTS is not 1 and IL2WASM_PLAIN_PARTS_BREAK is empty (so 1 by default); else 0 (Calls.cs:1005) |
| Rt.PlainPartsBreak | `int PlainPartsBreak()` | extern (Runtime.cs:4168) | i32.const int.Parse(IL2WASM_PLAIN_PARTS_BREAK) or 0 (compile-time environment; a test hook) (Calls.cs:1007) |
| Rt.PartWeak | `void PartWeak(int addr)` | extern (Runtime.cs:4170) | call `rt.part_weak` (body: Rt.PartMarked in a program with groups, nothing otherwise; RuntimeGen.cs) (Calls.cs:1001) |
| Rt.NoNodePath | `bool NoNodePath()` | extern (Runtime.cs:4259) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_NODE_PATH (Calls.cs:999) |
| Rt.NodePathBreak | `int NodePathBreak()` | extern (Runtime.cs:4268) | i32.const int.Parse(IL2WASM_NODE_PATH_BREAK) or 0 (compile-time environment; a test hook) (Calls.cs:1000) |
| Rt.ArenaIsSlot | `int ArenaIsSlot(int obj, int offset)` | extern (Runtime.cs:4875) | call `rt.arena_is_slot` (object, offset): per-type table, body written with the type graph (Calls.cs:1071) |
| Rt.LinkAt | `int LinkAt(int val, int slot)` | extern (Runtime.cs:4876) | call `arena.link at` (Arenas.LinkAt(D.ObjectType), Arenas.cs:887): generated per program (Calls.cs:1073) |
| Rt.UnlinkAt | `void UnlinkAt(int old, int slot)` | extern (Runtime.cs:4877) | call `arena.unlink at` (Arenas.UnlinkAt(D.ObjectType), Arenas.cs:892): generated per program (Calls.cs:1074) |
| Rt.MoveRefs | `bool MoveRefs(int dst, int src, int count)` | extern (Runtime.cs:4878) | call `arena.move at` (Arenas.MoveAt(), Arenas.cs:400; a placeholder function whose body is written with the arena support) (Calls.cs:1075) |
| Rt.NoDyingUp | `bool NoDyingUp()` | extern (Runtime.cs:5143) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_DYING_UP (Calls.cs:1048) |
| Rt.NoDyingNote | `bool NoDyingNote()` | extern (Runtime.cs:5186) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_DYING_NOTE (Calls.cs:1046) |
| Rt.WeakAny | `int WeakAny()` | extern (Runtime.cs:5270) | call to generated function `rt.weak_any` (Calls.cs:1012) |
| Rt.WeakDying | `void WeakDying(int addr)` | extern (Runtime.cs:5271) | call to generated function `rt.weak_dying` (Calls.cs:1013) |
| Rt.NoFinRevive | `bool NoFinRevive()` | extern (Runtime.cs:5333) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_FIN_REVIVE (Calls.cs:1044) |
| Rt.NoAdoptCheck | `bool NoAdoptCheck()` | extern (Runtime.cs:5334) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_ADOPT_CHECK (Calls.cs:1045) |
| Rt.NoFastDestroy | `bool NoFastDestroy()` | extern (Runtime.cs:5657) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_FAST_DESTROY (Calls.cs:1043) |
| Rt.NoDestroyByBits | `bool NoDestroyByBits()` | extern (Runtime.cs:5812) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_DESTROY_BY_BITS (Calls.cs:1049) |
| Rt.FusedDrop | `bool FusedDrop()` | extern (Runtime.cs:5829) | i32.const 1 if !ThreadMode && !Options.Checked && IL2WASM_NO_FUSED_DROP!=1 && IL2WASM_STALE_HOLDS!=1 (RuntimeGen.FusedDrop, RuntimeGen.cs:809) (Calls.cs:1039) |
| Rt.NoFreeList | `bool NoFreeList()` | extern (Runtime.cs:5852) | i32.const 0/1 fixed at compile time from environment variable IL2WASM_NO_FREE_LIST (Calls.cs:1040) |

#### Il2Wasm.Runtime.Atomic (15)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Atomic.Load32 | `int Load32(ref int location)` | extern (Runtime.cs:6279) | i32.atomic.load align=2 offset=0 (Calls.cs:1186) |
| Atomic.Store32 | `void Store32(ref int location, int value)` | extern (Runtime.cs:6280) | i32.atomic.store align=2 offset=0 (Calls.cs:1187) |
| Atomic.Add32 | `int Add32(ref int location, int value)` | extern (Runtime.cs:6281) | i32.atomic.rmw.add align=2 (returns the old value) (Calls.cs:1188) |
| Atomic.Exchange32 | `int Exchange32(ref int location, int value)` | extern (Runtime.cs:6282) | i32.atomic.rmw.xchg align=2 (Calls.cs:1189) |
| Atomic.CompareExchange32 | `int CompareExchange32(ref int location, int expected, int replacement)` | extern (Runtime.cs:6283) | i32.atomic.rmw.cmpxchg align=2 (Calls.cs:1190) |
| Atomic.CountAdd32 | `int CountAdd32(ref int location, int value)` | extern (Runtime.cs:6288) | ThreadMode (or IL2WASM_ATOMIC_COUNTS=1): i32.atomic.rmw.add align=2; otherwise a plain non-atomic read-add-store through locals returning the old value (i32.load, i32.add, i32.store) (Calls.cs:1191) |
| Atomic.CountCompareExchange32 | `int CountCompareExchange32(ref int location, int expected, int replacement)` | extern (Runtime.cs:6289) | ThreadMode (or IL2WASM_ATOMIC_COUNTS=1): i32.atomic.rmw.cmpxchg align=2; otherwise a plain load, i32.eq test, conditional i32.store, returning the old value (Calls.cs:1199) |
| Atomic.Load64 | `long Load64(ref long location)` | extern (Runtime.cs:6290) | i64.atomic.load align=3 (Calls.cs:1209) |
| Atomic.Store64 | `void Store64(ref long location, long value)` | extern (Runtime.cs:6291) | i64.atomic.store align=3 (Calls.cs:1210) |
| Atomic.Add64 | `long Add64(ref long location, long value)` | extern (Runtime.cs:6292) | i64.atomic.rmw.add align=3 (Calls.cs:1211) |
| Atomic.Exchange64 | `long Exchange64(ref long location, long value)` | extern (Runtime.cs:6293) | i64.atomic.rmw.xchg align=3 (Calls.cs:1212) |
| Atomic.CompareExchange64 | `long CompareExchange64(ref long location, long expected, long replacement)` | extern (Runtime.cs:6294) | i64.atomic.rmw.cmpxchg align=3 (Calls.cs:1213) |
| Atomic.Wait32 | `int Wait32(ref int location, int expected, long timeoutNs)` | extern (Runtime.cs:6296) | memory.atomic.wait32 align=2 (Calls.cs:1214) |
| Atomic.Notify | `int Notify(ref int location, int count)` | extern (Runtime.cs:6297) | memory.atomic.notify align=2 (Calls.cs:1215) |
| Atomic.Fence | `void Fence()` | extern (Runtime.cs:6298) | atomic.fence (Calls.cs:1216) |

#### Il2Wasm.Runtime.Il2WasmArrays (6)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Il2WasmArrays.RankOf | `int RankOf(Array a)` | extern (Arrays.cs:9) | call to generated function `rt.array_rank` (Calls.cs:1136) |
| Il2WasmArrays.ElementSize | `int ElementSize(Array a)` | extern (Arrays.cs:10) | call to generated function `rt.array_elem_size` (Calls.cs:1135) |
| Il2WasmArrays.HasCountedElements | `bool HasCountedElements(Array a)` | extern (Arrays.cs:11) | call to generated function `rt.array_counted` (Calls.cs:1137) |
| Il2WasmArrays.CopyElements | `void CopyElements(Array src, int si, Array dst, int di, int length, int size)` | extern (Arrays.cs:13) | call to generated function `rt.array_copy` (Calls.cs:1138) |
| Il2WasmArrays.ClearElements | `void ClearElements(Array a, int index, int length, int size)` | extern (Arrays.cs:14) | call to generated function `rt.array_clear` (Calls.cs:1139) |
| Il2WasmArrays.BoxesElements | `void BoxesElements()` | extern (Arrays.cs:16) | no code: calls D.MarkBoxedElements() at compile time (whole-program flag: arrays of boxed elements exist) (Calls.cs:1009) |

#### Il2Wasm.Runtime.Generics (6)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Generics.Implements | `bool Implements<T>(string genericInterfaceOfT)` | extern (Generics.cs:9) | never emitted: constant-folded where the call is compiled (Folding.cs:79-85: true iff canonical T implements the named generic interface over T itself); T is always concrete (monomorphized) (no case in EmitIntrinsic) |
| Generics.IsNullable | `bool IsNullable<T>()` | extern (Generics.cs:10) | never emitted: constant-folded (Folding.cs:93): T is System.Nullable<> (no case in EmitIntrinsic) |
| Generics.IsEnum | `bool IsEnum<T>()` | extern (Generics.cs:11) | never emitted: constant-folded (Folding.cs:94): T is an enum (no case in EmitIntrinsic) |
| Generics.New | `object New<T>(string openGenericClass)` | extern (Generics.cs:13) | never emitted by EmitIntrinsic: EmitInvocation special-cases it (Calls.cs:43 -> EmitGenericsNew, Calls.cs:543-551): looks up the open generic class by the constant metadata-name string, closes it over the resolved type arguments, allocates and calls its parameterless ctor (NewObject, Calls.cs:555); result Own.Owned (no case in EmitIntrinsic) |
| Generics.NewOfNullableArg | `object NewOfNullableArg<T>(string openGenericClass)` | extern (Generics.cs:15) | same as Generics.New (Calls.cs:43, 543-551) but the type argument used is the T of Nullable<T> (args[0].TypeArguments[0]) (no case in EmitIntrinsic) |
| Generics.New | `object New<T1, T2>(string openGenericClass)` | extern (Generics.cs:17) | never emitted by EmitIntrinsic: EmitInvocation special-cases it (Calls.cs:43 -> EmitGenericsNew, Calls.cs:543-551): looks up the open generic class by the constant metadata-name string, closes it over the resolved type arguments, allocates and calls its parameterless ctor (NewObject, Calls.cs:555); result Own.Owned (no case in EmitIntrinsic) |

#### Il2Wasm.Runtime.TextScan (4)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| TextScan.Eq16 | `int Eq16(int addr, int value)` | extern (TextScan.cs:18) | v128.load [addr]; i16x8.splat value; i16x8.eq; i16x8.bitmask: 8-bit mask of lanes equal to value (Calls.cs:930) (Calls.cs:930) |
| TextScan.Eq16Any3 | `int Eq16Any3(int addr, int a, int b, int c)` | extern (TextScan.cs:20) | three v128.load [addr] each compared (i16x8.splat + i16x8.eq) with a, b, c, v128.or-ed, then i16x8.bitmask (Calls.cs:939) (Calls.cs:939) |
| TextScan.Upper16 | `int Upper16(int src, int dst)` | extern (TextScan.cs:23) | v128.load src; per lane x - (((x-'a') <u 26) & 32) via i16x8.sub/lt_u/splat/v128.and; v128.store at dst; result = i16x8.bitmask of lanes > 127 (i16x8.gt_u) of src (Calls.cs:950) (Calls.cs:950) |
| TextScan.Lower16 | `int Lower16(int src, int dst)` | extern (TextScan.cs:25) | as Upper16 but from 'A' and i16x8.add: x + (((x-'A') <u 26) & 32); result = bitmask of src lanes > 127 (Calls.cs:950) (Calls.cs:950) |

#### Il2Wasm.Runtime.TypeInfo (31)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| TypeInfo.FullName | `string FullName(int id)` | extern (TypeInfo.cs:10) | D.WantLazyTypeNames() then call `rt.type_name_by_id` (Calls.cs:1144; result ownership Own.Stable) |
| TypeInfo.Name | `string Name(int id)` | extern (TypeInfo.cs:11) | call to generated function `rt.type_short_name_by_id` (Calls.cs:1148; result ownership Own.Stable) |
| TypeInfo.Namespace | `string Namespace(int id)` | extern (TypeInfo.cs:12) | call to generated function `rt.type_namespace_by_id` (Calls.cs:1150; result ownership Own.Stable) |
| TypeInfo.Flags | `int Flags(int id)` | extern (TypeInfo.cs:14) | call to generated function `rt.type_flags_by_id` (Calls.cs:1149) |
| TypeInfo.Flags2 | `int Flags2(int id)` | extern (TypeInfo.cs:16) | call to generated function `rt.type_flags2_by_id` (Calls.cs:1156) |
| TypeInfo.BaseId | `int BaseId(int id)` | extern (TypeInfo.cs:17) | call to generated function `rt.type_base_by_id` (Calls.cs:1151) |
| TypeInfo.ElementId | `int ElementId(int id)` | extern (TypeInfo.cs:18) | call to generated function `rt.type_element_by_id` (Calls.cs:1152) |
| TypeInfo.AssemblyOf | `int AssemblyOf(int id)` | extern (TypeInfo.cs:20) | call to generated function `rt.type_assembly_by_id` (Calls.cs:1160) |
| TypeInfo.GenericParam | `int GenericParam(int id)` | extern (TypeInfo.cs:22) | call to generated function `rt.type_gp_by_id` (Calls.cs:1161) |
| TypeInfo.GenericConstraints | `int GenericConstraints(int id)` | extern (TypeInfo.cs:24) | call to generated function `rt.type_gp_constraints_by_id` (Calls.cs:1163) |
| TypeInfo.GenericParamMethod | `int GenericParamMethod(int id)` | extern (TypeInfo.cs:26) | call to generated function `rt.type_gp_method_by_id` (Calls.cs:1162) |
| TypeInfo.AssemblyNames | `int AssemblyNames()` | extern (TypeInfo.cs:28) | call to generated function `rt.assembly_names` (Calls.cs:1164) |
| TypeInfo.ManifestResources | `int ManifestResources()` | extern (TypeInfo.cs:31) | call to generated function `rt.manifest_resources` (Calls.cs:1165) |
| TypeInfo.ResourceInit | `void ResourceInit(int dst, int offset, int length)` | extern (TypeInfo.cs:34) | call to generated function `rt.resource_init` (Calls.cs:1166) |
| TypeInfo.ResourceUnpack | `void ResourceUnpack(int src, int dst, int end)` | extern (TypeInfo.cs:36) | call to generated function `rt.resource_unpack` (Calls.cs:1167) |
| TypeInfo.DeclaringId | `int DeclaringId(int id)` | extern (TypeInfo.cs:37) | call to generated function `rt.type_declaring_by_id` (Calls.cs:1168) |
| TypeInfo.Rank | `int Rank(int id)` | extern (TypeInfo.cs:38) | call to generated function `rt.type_rank_by_id` (Calls.cs:1153) |
| TypeInfo.Attributes | `int Attributes(int id)` | extern (TypeInfo.cs:40) | call to generated function `rt.type_attrs_by_id` (Calls.cs:1154) |
| TypeInfo.CorElementType | `int CorElementType(int id)` | extern (TypeInfo.cs:42) | call to generated function `rt.type_cor_by_id` (Calls.cs:1155) |
| TypeInfo.Size | `int Size(int id)` | extern (TypeInfo.cs:44) | call to generated function `rt.type_size_by_id` (Calls.cs:1169) |
| TypeInfo.BoxOffset | `int BoxOffset(int id)` | extern (TypeInfo.cs:46) | call to generated function `rt.type_box_offset_by_id` (Calls.cs:1170) |
| TypeInfo.EnumInfo | `int EnumInfo(int id)` | extern (TypeInfo.cs:48) | call to generated function `rt.enum_info_id` (Calls.cs:1141) |
| TypeInfo.GenericArgs | `int GenericArgs(int id)` | extern (TypeInfo.cs:50) | call to generated function `rt.type_generic_args_by_id` (Calls.cs:1157) |
| TypeInfo.ArrayOf | `int ArrayOf(int id)` | extern (TypeInfo.cs:52) | call to generated function `rt.type_array_of_by_id` (Calls.cs:1171) |
| TypeInfo.Meta | `int Meta(int id)` | extern (TypeInfo.cs:54) | call `rt.meta_of` (Calls.cs:1172) |
| TypeInfo.Invoke | `object? Invoke(int thunk, object? target, object?[]? args)` | extern (TypeInfo.cs:56) | locals; call_indirect (typeidx Driver.ThunkType, table bucket Driver.ThunkBucket) of the thunk (thunk-1) with (target, args); result Own.Owned (Calls.cs:1176-1185) (Calls.cs:1176; result ownership Own.Owned) |
| TypeInfo.Interfaces | `int Interfaces(int id)` | extern (TypeInfo.cs:58) | call to generated function `rt.type_interfaces_by_id` (Calls.cs:1158) |
| TypeInfo.NameTable | `int NameTable()` | extern (TypeInfo.cs:61) | call to generated function `rt.type_name_table` (Calls.cs:1145) |
| TypeInfo.ProgramTypes | `int ProgramTypes()` | extern (TypeInfo.cs:63) | call to generated function `rt.program_types` (Calls.cs:1146) |
| TypeInfo.Nested | `int Nested(int id)` | extern (TypeInfo.cs:65) | call to generated function `rt.type_nested_by_id` (Calls.cs:1159) |
| TypeInfo.Instances | `int Instances()` | extern (TypeInfo.cs:67) | call to generated function `rt.generic_instances` (Calls.cs:1147) |

#### System.Delegate (1)

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Delegate.TargetOf | `object? TargetOf(int fn, object? target)` | extern (Delegate.Mono.cs:41) | call `rt.delegate_target` (GeneratedReflection.cs:300): the target object of a singleton delegate for function index fn; result Own.Owned (Calls.cs:1066; result ownership Own.Owned) |

### 1B. Resolving to `System.Runtime.CompilerServices.IntrinsicAttribute` (all have C# bodies; the compiler does not look at the attribute)

For every row, `IsIntrinsic` is true only if the method also appears in the `IsUpstreamIntrinsic` allowlist; otherwise the attribute is ignored and the body is compiled as ordinary C#.

| Type.Method | C# signature | extern / body | what the old compiler emits |
|---|---|---|---|
| Array.GetLength | `int GetLength(int dimension)` | has a body (Runtime/System/Array.Mono.cs:401) | not an intrinsic for the old compiler (Array is not in Driver.cs:1002-1022): attribute ignored, body `GetLengthOf(this, dimension)` compiled; OwnedArrays.cs:521,574 only treat the call as non-escaping |
| Array.GetLowerBound | `int GetLowerBound(int dimension)` | has a body (Array.Mono.cs:408) | same: ignored, body compiled |
| Array.GetElementSize | `int GetElementSize()` | has a body (Array.Mono.cs:415; `=> Il2WasmArrays.ElementSize(this)`) | same: ignored; the body calls the Il2 intrinsic `Il2WasmArrays.ElementSize` (`rt.array_elem_size`, Calls.cs:1135) |
| Array.GetGenericValueImpl | `void GetGenericValueImpl<T>(int pos, out T value)` | has a body (Array.Mono.cs:524) | same: ignored, body compiled (calls an internal call on ObjectHandleOnStack; see Array.Mono.cs for the il2wasm replacement) |
| Array.SetGenericValueImpl | `void SetGenericValueImpl<T>(int pos, ref T value)` | has a body (Array.Mono.cs:531) | same: ignored, body compiled |
| Debugger.Break | `static void Break()` | has a body (Runtime/System/Diagnostics/Debugger.cs:17; empty) | not in the allowlist: ignored, empty body compiled (nothing emitted but a call) |
| Stream.HasOverriddenBeginEndRead | `bool HasOverriddenBeginEndRead()` | has a body (IO/Stream.Mono.cs:13; `=> false`) | ignored, body compiled |
| Stream.HasOverriddenBeginEndWrite | `bool HasOverriddenBeginEndWrite()` | has a body (Stream.Mono.cs:16; `=> false`) | ignored, body compiled |
| Math.FusedMultiplyAdd | `static double FusedMultiplyAdd(double x, double y, double z)` | has a body (Math.Mono.cs:56; `=> CMath.Fma(x,y,z)`) | ignored, body compiled (CMath, Boundary/CMath.cs) |
| MathF.FusedMultiplyAdd | `static float FusedMultiplyAdd(float x, float y, float z)` | has a body (MathF.Mono.cs:56) | ignored, body compiled |
| Object.GetType | `Type GetType()` | has a body (Object.Mono.cs:12; `=> GetType()`, self-recursive placeholder) | **intrinsic** (Driver.cs:1017): `i32.load offset=4` (type id) then call `System.Type.FromId` (D.Method(fromId)); result Own.Owned. The body is ignored (Calls.cs:910-917) |
| JitHelpers.EnumEquals | `static bool EnumEquals<T>(T x, T y) where T : struct, Enum` | has a body (Runtime/CompilerServices/JitHelpers.cs:8) | not found in any emit switch or allowlist: ignored, body compiled |
| JitHelpers.EnumCompareTo | `static int EnumCompareTo<T>(T x, T y) where T : struct, Enum` | has a body (JitHelpers.cs:11) | not found: body compiled |
| JitHelpers.DisableInline | `static void DisableInline()` | has a body (JitHelpers.cs:14; `throw new NotImplementedException()`) | not found: body compiled (throws if called) |
| RuntimeHelpers.GetRawData | `static ref byte GetRawData(this object obj)` | has a body (RuntimeHelpers.Mono.cs:192; self-recursive) | **intrinsic** (Driver.cs:1012): `i32.const Types.ObjectHeader; i32.add` (address of the data after the header; Calls.cs:868). Body ignored |
| RuntimeHelpers.IsBitwiseEquatable | `static bool IsBitwiseEquatable<T>()` | has a body (RuntimeHelpers.Mono.cs:195; self-recursive) | **intrinsic**: `i32.const 1` unless T is a non-primitive, float or double (Calls.cs:869-870); also constant-folded where used (Folding.cs:103). Body ignored |
| RuntimeHelpers.ObjectHasComponentSize | `static bool ObjectHasComponentSize(object obj)` | has a body (RuntimeHelpers.Mono.cs:198; self-recursive) | **intrinsic**: `i32.load offset=4` (type id), then (id == 1 [string]) OR (call `rt.type_flags_by_id` & 4 != 0) (Calls.cs:873-882). Body ignored |
| RuntimeHelpers.ObjectHasReferences | `static bool ObjectHasReferences(object obj)` | has a body (RuntimeHelpers.Mono.cs:201; calls RuntimeTypeHandle.HasReferences) | not in the allowlist (Driver.cs:1012): ignored, body compiled |
| MemoryMarshal.GetArrayDataReference | `static ref T GetArrayDataReference<T>(T[] array)` | has a body (Runtime/InteropServices/MemoryMarshal.Mono.cs:20) | not in the allowlist: body compiled (it is `Unsafe.As` over `Array.RawData`); RefStores.cs:93 only skips an aliasing note for it |
| MemoryMarshal.GetArrayDataReference | `static ref byte GetArrayDataReference(Array array)` | has a body (MemoryMarshal.Mono.cs:38; `ref Unsafe.AsRef<byte>((void*)Il2WasmArrays.Data(array))`) | not in the allowlist: body compiled |

## 2. `[Intrinsic]` on static fields

Declared in `Boundary/Runtime.cs` inside `Il2Wasm.Runtime.Rt` (the `Il2Wasm.Runtime.IntrinsicAttribute`; `[AttributeUsage(Method | Field)]`, Runtime.cs:11). Semantics (Boundary/Runtime.cs:7-11 and Expressions.cs:925): in **thread mode** the field is thread-static (stored at `global TlsBase + D.ThreadStaticOffset(owner, field, type)`, per-thread zeroed storage); **without threads** it is an ordinary static (`D.StaticField`, i32.const address). The compiler decides by attribute simple name `IntrinsicAttribute` on the field (`IsThreadStatic`, Expressions.cs:925 - it matches by `AttributeClass.Name`, not full name), so it is also used by the sharing/escape analyses (Expressions.cs:604, 949-951, Sharing.cs:1224, 1867: a thread-static never publishes). All fields are zero-initialised `static int` with no initialiser (Runtime.cs:3343 notes an initial value would add a static constructor to Rt).

| Field | Declared at | Note |
|---|---|---|
| Rt.s_linkQ | Boundary/Runtime.cs:2419 | `static int` |
| Rt.s_linkN | Boundary/Runtime.cs:2419 | `static int` |
| Rt.s_linkCap | Boundary/Runtime.cs:2419 | `static int` |
| Rt.s_waitSlot | Boundary/Runtime.cs:2498 | `static int` |
| Rt.s_waitObj | Boundary/Runtime.cs:2498 | `static int` |
| Rt.s_waitVal | Boundary/Runtime.cs:2498 | `static int` |
| Rt.s_joinVisit | Boundary/Runtime.cs:2514 | `static int` |
| Rt.s_reevQ | Boundary/Runtime.cs:2514 | `static int` |
| Rt.s_reevN | Boundary/Runtime.cs:2514 | `static int` |
| Rt.s_reevCap | Boundary/Runtime.cs:2514 | `static int` |
| Rt.s_visitObj | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_visitElem | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_destroying | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_killQ | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_killN | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_killCap | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_storeSlot | Boundary/Runtime.cs:2610 | `static int` |
| Rt.s_handFrom | Boundary/Runtime.cs:2935 | `static int` |
| Rt.s_handSum | Boundary/Runtime.cs:2935 | `static int` |
| Rt.s_cutQ | Boundary/Runtime.cs:2940 | `static int` |
| Rt.s_cutN | Boundary/Runtime.cs:2940 | `static int` |
| Rt.s_cutCap | Boundary/Runtime.cs:2940 | `static int` |
| Rt.s_cutOn | Boundary/Runtime.cs:2940 | `static int` |
| Rt.s_trQ | Boundary/Runtime.cs:3339 | `static int` |
| Rt.s_trN | Boundary/Runtime.cs:3339 | `static int` |
| Rt.s_trCap | Boundary/Runtime.cs:3339 | `static int` |
| Rt.s_trial | Boundary/Runtime.cs:3339 | `static int` |
| Rt.s_watch | Boundary/Runtime.cs:3339 | `static int` |
| Rt.s_watchHit | Boundary/Runtime.cs:3339 | `static int` |
| Rt.s_trExt | Boundary/Runtime.cs:3346 | `static int` |
| Rt.s_tkQ | Boundary/Runtime.cs:3349 | `static int` |
| Rt.s_tkN | Boundary/Runtime.cs:3349 | `static int` |
| Rt.s_tkCap | Boundary/Runtime.cs:3349 | `static int` |
| Rt.s_trOwn | Boundary/Runtime.cs:3349 | `static int` |
| Rt.s_lastC | Boundary/Runtime.cs:4696 | `static int` |
| Rt.s_lastGiven | Boundary/Runtime.cs:4696 | `static int` |
| Rt.s_visitArena | Boundary/Runtime.cs:5003 | `static int` |
| Rt.s_visitMode | Boundary/Runtime.cs:5003 | `static int` |
| Rt.s_visitDead | Boundary/Runtime.cs:5003 | `static int` |

(11 declarations: Runtime.cs:2419, 2498, 2514, 2610, 2935, 2940, 3339, 3346, 3349, 4696, 5003. These are separate from the `[System.ThreadStatic]` fields in Rt such as `t_prevSlot`, `t_lkQ`, which are thread-static in every mode.)

## 3. Upstream intrinsics and effects intrinsics

### 3A. `Driver.IsUpstreamIntrinsic` (Driver.cs:999-1024), emitted by `EmitUpstreamIntrinsic` (Calls.cs:718-920)

Selected by `Types.FullName(containingType)` and method name (all overloads that satisfy the predicate). Arguments on the stack as in section 1 (refs/pointers = i32 addresses). `BitOperations` is handled up front (Calls.cs:726-745), the rest by the switch at Calls.cs:746. Declarations are in dotnet/runtime files (manifest.txt: Unsafe.cs:1200, SpanHelpers*.cs:1445-1451, Interlocked.cs:1542, Volatile.cs:1611, BitOperations.cs:879, Double.cs:573, Single.cs:1442, RuntimeHelpers.cs:1186, Activator.cs:343, MethodBase.Mono.cs:261) except Object.GetType and the three RuntimeHelpers Mono-half methods, which are in the repo (section 1B).

| Type.Method (predicate) | what the old compiler emits | Calls.cs |
|---|---|---|
| BitOperations.LeadingZeroCount / TrailingZeroCount / PopCount / RotateLeft / RotateRight / Log2 (first parameter any integer primitive incl. nuint) | 64-bit param (long/ulong): `i64.clz`/`i64.ctz`/`i64.popcnt` then `i32.wrap_i64`; 32-bit (and nuint, i32): `i32.clz`/`i32.ctz`/`i32.popcnt`. RotateLeft/Right: i64: `i64.extend_i32_u; i64.rotl/rotr` (result stays i64, returns early); i32: `i32.rotl/rotr`. Log2: `(v|1)` then `31 ^ clz` (i64: `63 ^ clz`, wrapped); Log2(0)=0. Note 8/16-bit primitives take the i32 path | 726-745 |
| Unsafe.AsPointer, Unsafe.AsRef | nothing (address stays) | 748-750 |
| Unsafe.As (both `As<T>(object)` and `As<TFrom,TTo>(ref)`) | `As<T>(object)`: nothing, result Own.Unstable. `As<TFrom,TTo>`: nothing, plus compile-time alias notes (`Sh.RefAlias`, `D.Arenas.RefAlias`, skipped inside ref-writer code); if TFrom is float/double and TTo is not, a NaN check on the memory read through the address: `tee a; f32/f64.load; get a; f32/f64.load; f32/f64.ne; if; get a; store canonical NaN (0xFFC00000 / 0xFFF8000000000000); end; get a` (SPEC 7.3) | 751-774 |
| Unsafe.AreSame | `i32.eq` | 775 |
| Unsafe.IsAddressGreaterThan / IsAddressLessThan | `i32.gt_u` / `i32.lt_u` | 776-777 |
| Unsafe.AddByteOffset / SubtractByteOffset | `i32.add` / `i32.sub` | 778-779 |
| Unsafe.ByteOffset | (origin, target): `target - origin` via two scratch locals and `i32.sub` | 780-788 |
| Unsafe.Copy | `i32.const sizeof(T); memory.copy`; throws CompileException if T holds counted references (`NoCounted`) | 814-819 |
| Unsafe.CopyBlock / CopyBlockUnaligned | `memory.copy` | 820-822 |
| Unsafe.InitBlock / InitBlockUnaligned | `memory.fill` | 823-825 |
| Unsafe.SkipInit | `drop` | 826 |
| Unsafe.Unbox | `i32.const <box data offset of T>; i32.add` (layout-dependent) | 827-828 |
| Interlocked.MemoryBarrier; Volatile.ReadBarrier / WriteBarrier | `atomic.fence` | 906-909 |
| Interlocked.Exchange (only when param 0's primitive is smaller than 4 bytes: byte, sbyte, short, ushort; other overloads run their C# body) | `i32.atomic.rmw8.xchg_u` / `i32.atomic.rmw16.xchg_u` (align 0/1) then `i32.extend8_s`/`i32.extend16_s` for signed types | 883-891 |
| Interlocked.CompareExchange (same narrow-type condition) | (location, value, comparand) re-ordered through scratch locals to wasm's (location, expected, replacement); expected masked with 0xFF/0xFFFF; `i32.atomic.rmw8/16.cmpxchg_u`; sign-extend for signed types | 892-905 |
| SpanHelpers.memmove, memset (any overload) | `memory.copy` (overlap allowed) / `memory.fill` | 829-830 |
| SpanHelpers.Memmove, ClearWithoutReferences (non-generic only) | `memory.copy`; `ClearWithoutReferences(ref byte, nuint)`: scratch for length, `i32.const 0`, length, `memory.fill` | 831-838 |
| Double.ConvertToIntegerNative<TInteger>, Single.ConvertToIntegerNative<TInteger> (TInteger an integer primitive, not bool/float) | saturating truncation, NaN to 0, same on every machine: `i64.trunc_sat_f32/f64_s/u` for long/ulong; `i32.trunc_sat_f32/f64_s/u` for 32-bit and narrower, narrower types then clamped to their range with compare/if/select sequences | 839-866 |
| RuntimeHelpers.GetRawData | `i32.const ObjectHeader; i32.add` | 868 |
| RuntimeHelpers.IsBitwiseEquatable | `i32.const 1/0` (1 unless Prim none/f32/f64) | 869-870 |
| RuntimeHelpers.IsReferenceOrContainsReferences | `i32.const 1/0`: 1 if T is a reference or `T.NeedsRc(T)`; also folded at Folding.cs:102 | 871-872 |
| RuntimeHelpers.ObjectHasComponentSize | see section 1B | 873-882 |
| Object.GetType | `i32.load offset=4`; call `System.Type.FromId`; result Own.Owned | 910-917 |
| Activator.CreateInstance<T>() (generic, no parameters) | value type: default value. Class with a non-abstract type and a parameterless ctor: `D.MarkConstructed(T)`, `i32.const max(size, ObjectHeader)`, type id, call the allocator (`Rt.AllocObject`), call the ctor, push the object (Own.Owned). Otherwise: string-literal type name, call library `ThrowHelpers.NoDefaultConstructor`, push 0 (as .NET: MissingMethodException when it runs) | 795-813 |
| MethodBase.GetCurrentMethod | `i32.const <address of string literal naming the caller's frame (E.Fn.FrameName ?? Driver.FrameName(OuterMethod))>`; call library `Meta.FrameMethod(string)`; Own.Owned (depends on the call site's enclosing method) | 789-794 |

(`Unsafe.As`, `AsRef`, `AsPointer`, `Unsafe.Add`, `Unsafe.SizeOf` etc.: only the names in the first column are intrinsic; `Unsafe.SizeOf<T>` is not on the allowlist but is folded to a constant at Folding.cs:150-155 `T.StorageOf(T).size`, and `Unsafe.Add`/`ReadUnaligned` etc. run their C# bodies.)

### 3B. `Driver.IsEffectsIntrinsic` (Plugins.cs:62-64; declared by the synthetic source of Plugins.cs:798)

True when plugin sources are present (`PluginTrees.Count > 0`) and the method is a member of `Il2Wasm.Effects` declared in one of the plugin trees. The type is synthesised for every plugin source: `namespace Il2Wasm { static class Effects { public static extern int Suppressed { get; set; } } }` (Plugins.cs:798, file "il2wasm.effects.g.cs"). Routed through `EmitIntrinsic` (not the upstream path, since `IsUpstreamIntrinsic` is false).

| Type.Method | C# signature | what the old compiler emits |
|---|---|---|
| Effects.get_Suppressed | `static extern int Suppressed { get; }` | `global.get` of the `__suppress` global (`D.SuppressGlobal()`, Plugins.cs:55-59: mutable i32, initial 0, exported, per worker instance); Calls.cs:1220 |
| Effects.set_Suppressed | `static extern int Suppressed { set; }` | `global.set` of the same global; Calls.cs:1221 |

Related, not an intrinsic: calls of imports the plugin made suppressible go through a generated wrapper `suppressible <import>` that returns zeros when `__suppress & bits` (Plugins.cs:67-94 `ApplySuppression`; in clones, Calls.cs:663-671).

## 4. `[Import(module, name)]` methods (`Il2Wasm.Runtime.ImportAttribute`, Boundary/Runtime.cs:16)

`Driver.Classify` makes these `MethodImpl.Import` (Driver.cs:1037); the import name comes from the attribute (`Driver.ImportName`, Driver.cs:1026). Pointer parameters (`char*`, `byte*`, `long*`, `int*`) are i32 addresses; `long`/`double` map to i64/f64. Count: 53 distinct declarations, all module `il2wasm`.

Declared in these files (the "file:line" column). Full type names: `Engine`, `CommandLine`, `HostOps.Host` are in namespace `Il2Wasm.Runtime`; `Interop.Sys.Fs`/`Interop.Sys.Env` are global-namespace nested types (`Interop.Sys`); `BrowserHttpInterop.Host` is in `System.Net.Http`.

| Type.Method | C# signature | module | import name | file:line |
|---|---|---|---|---|
| BrowserHttpInterop.Host.CreateController | `int CreateController()` | il2wasm | http_create_controller | System.Net.Http/Boundary/BrowserHttpInterop.cs:45 |
| BrowserHttpInterop.Host.Abort | `void Abort(int controller)` | il2wasm | http_abort | System.Net.Http/Boundary/BrowserHttpInterop.cs:46 |
| BrowserHttpInterop.Host.Dispose | `void Dispose(int controller)` | il2wasm | http_dispose | System.Net.Http/Boundary/BrowserHttpInterop.cs:47 |
| BrowserHttpInterop.Host.Fetch | `void Fetch(int controller, int op, char* uri, int uriLength, char* block, int blockLength, byte* body, int bodyLength)` | il2wasm | http_fetch | System.Net.Http/Boundary/BrowserHttpInterop.cs:50 |
| BrowserHttpInterop.Host.Status | `int Status(int controller)` | il2wasm | http_status | System.Net.Http/Boundary/BrowserHttpInterop.cs:51 |
| BrowserHttpInterop.Host.Type | `int Type(int controller, char* buffer, int size)` | il2wasm | http_type | System.Net.Http/Boundary/BrowserHttpInterop.cs:53 |
| BrowserHttpInterop.Host.Headers | `int Headers(int controller, char* buffer, int size)` | il2wasm | http_headers | System.Net.Http/Boundary/BrowserHttpInterop.cs:55 |
| BrowserHttpInterop.Host.BodyLength | `void BodyLength(int controller, int op)` | il2wasm | http_body_length | System.Net.Http/Boundary/BrowserHttpInterop.cs:57 |
| BrowserHttpInterop.Host.Body | `int Body(int controller, byte* buffer, int size)` | il2wasm | http_body | System.Net.Http/Boundary/BrowserHttpInterop.cs:59 |
| BrowserHttpInterop.Host.StreamRead | `void StreamRead(int controller, int op, byte* buffer, int size)` | il2wasm | http_stream_read | System.Net.Http/Boundary/BrowserHttpInterop.cs:62 |
| HostOps.Host.NextCompletion | `int NextCompletion(int* result, int* messageLength)` | il2wasm | host_next_completion | System.Private.CoreLib/Boundary/HostOps.cs:17 |
| HostOps.Host.CompletionMessage | `void CompletionMessage(int op, char* buffer, int length)` | il2wasm | host_completion_message | System.Private.CoreLib/Boundary/HostOps.cs:19 |
| Interop.Sys.Env.Block | `int Block(byte* buffer, int size)` | il2wasm | env_block | System.Private.CoreLib/Boundary/Interop.Env.cs:16 |
| CommandLine.Block | `int Block(char* buffer, int size)` | il2wasm | args_block | System.Private.CoreLib/Boundary/Interop.Env.cs:55 |
| Interop.Sys.Fs.Open | `int Open(char* path, int len, int flags, int mode)` | il2wasm | fs_open | System.Private.CoreLib/Boundary/Interop.Files.cs:17 |
| Interop.Sys.Fs.Close | `int Close(int fd)` | il2wasm | fs_close | System.Private.CoreLib/Boundary/Interop.Files.cs:18 |
| Interop.Sys.Fs.Read | `int Read(int fd, byte* buffer, int count)` | il2wasm | fs_read | System.Private.CoreLib/Boundary/Interop.Files.cs:19 |
| Interop.Sys.Fs.PRead | `int PRead(int fd, byte* buffer, int count, long offset)` | il2wasm | fs_pread | System.Private.CoreLib/Boundary/Interop.Files.cs:20 |
| Interop.Sys.Fs.Write | `int Write(int fd, byte* buffer, int count)` | il2wasm | fs_write | System.Private.CoreLib/Boundary/Interop.Files.cs:21 |
| Interop.Sys.Fs.PWrite | `int PWrite(int fd, byte* buffer, int count, long offset)` | il2wasm | fs_pwrite | System.Private.CoreLib/Boundary/Interop.Files.cs:22 |
| Interop.Sys.Fs.LSeek | `long LSeek(int fd, long offset, int whence)` | il2wasm | fs_lseek | System.Private.CoreLib/Boundary/Interop.Files.cs:23 |
| Interop.Sys.Fs.FStat | `int FStat(int fd, long* stat)` | il2wasm | fs_fstat | System.Private.CoreLib/Boundary/Interop.Files.cs:25 |
| Interop.Sys.Fs.Stat | `int Stat(char* path, int len, int follow, long* stat)` | il2wasm | fs_stat | System.Private.CoreLib/Boundary/Interop.Files.cs:26 |
| Interop.Sys.Fs.FTruncate | `int FTruncate(int fd, long length)` | il2wasm | fs_ftruncate | System.Private.CoreLib/Boundary/Interop.Files.cs:27 |
| Interop.Sys.Fs.FSync | `int FSync(int fd)` | il2wasm | fs_fsync | System.Private.CoreLib/Boundary/Interop.Files.cs:28 |
| Interop.Sys.Fs.Access | `int Access(char* path, int len, int mode)` | il2wasm | fs_access | System.Private.CoreLib/Boundary/Interop.Files.cs:29 |
| Interop.Sys.Fs.Unlink | `int Unlink(char* path, int len)` | il2wasm | fs_unlink | System.Private.CoreLib/Boundary/Interop.Files.cs:30 |
| Interop.Sys.Fs.MkDir | `int MkDir(char* path, int len, int mode)` | il2wasm | fs_mkdir | System.Private.CoreLib/Boundary/Interop.Files.cs:31 |
| Interop.Sys.Fs.RmDir | `int RmDir(char* path, int len)` | il2wasm | fs_rmdir | System.Private.CoreLib/Boundary/Interop.Files.cs:32 |
| Interop.Sys.Fs.Rename | `int Rename(char* from, int fromLen, char* to, int toLen)` | il2wasm | fs_rename | System.Private.CoreLib/Boundary/Interop.Files.cs:33 |
| Interop.Sys.Fs.Link | `int Link(char* from, int fromLen, char* to, int toLen)` | il2wasm | fs_link | System.Private.CoreLib/Boundary/Interop.Files.cs:34 |
| Interop.Sys.Fs.SymLink | `int SymLink(char* target, int targetLen, char* link, int linkLen)` | il2wasm | fs_symlink | System.Private.CoreLib/Boundary/Interop.Files.cs:35 |
| Interop.Sys.Fs.ReadLink | `int ReadLink(char* path, int len, byte* buffer, int size)` | il2wasm | fs_readlink | System.Private.CoreLib/Boundary/Interop.Files.cs:37 |
| Interop.Sys.Fs.OpenDir | `int OpenDir(char* path, int len)` | il2wasm | fs_opendir | System.Private.CoreLib/Boundary/Interop.Files.cs:38 |
| Interop.Sys.Fs.ReadDir | `int ReadDir(int dir, byte* name, int size, int* type)` | il2wasm | fs_readdir | System.Private.CoreLib/Boundary/Interop.Files.cs:40 |
| Interop.Sys.Fs.CloseDir | `int CloseDir(int dir)` | il2wasm | fs_closedir | System.Private.CoreLib/Boundary/Interop.Files.cs:41 |
| Interop.Sys.Fs.GetCwd | `int GetCwd(byte* buffer, int size)` | il2wasm | fs_getcwd | System.Private.CoreLib/Boundary/Interop.Files.cs:43 |
| Interop.Sys.Fs.ChDir | `int ChDir(char* path, int len)` | il2wasm | fs_chdir | System.Private.CoreLib/Boundary/Interop.Files.cs:44 |
| Interop.Sys.Fs.ChMod | `int ChMod(char* path, int len, int mode)` | il2wasm | fs_chmod | System.Private.CoreLib/Boundary/Interop.Files.cs:45 |
| Interop.Sys.Fs.FChMod | `int FChMod(int fd, int mode)` | il2wasm | fs_fchmod | System.Private.CoreLib/Boundary/Interop.Files.cs:46 |
| Interop.Sys.Fs.UTimens | `int UTimens(char* path, int len, long* times)` | il2wasm | fs_utimens | System.Private.CoreLib/Boundary/Interop.Files.cs:48 |
| Interop.Sys.Fs.FUTimens | `int FUTimens(int fd, long* times)` | il2wasm | fs_futimens | System.Private.CoreLib/Boundary/Interop.Files.cs:49 |
| Engine.ThreadSpawn | `int ThreadSpawn(int stackTop, int tls, int id, int arg, int background)` | il2wasm | thread_spawn | System.Private.CoreLib/Boundary/Runtime.cs:49 |
| Engine.ProcessorCount | `int ProcessorCount()` | il2wasm | processor_count | System.Private.CoreLib/Boundary/Runtime.cs:51 |
| Engine.MainCanBlock | `int MainCanBlock()` | il2wasm | main_can_block | System.Private.CoreLib/Boundary/Runtime.cs:54 |
| Engine.Write | `void Write(int ptr, int len)` | il2wasm | write | System.Private.CoreLib/Boundary/Runtime.cs:56 |
| Engine.ReportException | `void ReportException(int typePtr, int typeLen, int msgPtr, int msgLen, int stackPtr, int stackLen)` | il2wasm | report_exception | System.Private.CoreLib/Boundary/Runtime.cs:58 |
| Engine.NowMs | `double NowMs()` | il2wasm | now_ms | System.Private.CoreLib/Boundary/Runtime.cs:60 |
| Engine.MonotonicNs | `long MonotonicNs()` | il2wasm | monotonic_ns | System.Private.CoreLib/Boundary/Runtime.cs:62 |
| Engine.CaptureStack | `int CaptureStack(int bufPtr, int capacityChars)` | il2wasm | capture_stack | System.Private.CoreLib/Boundary/Runtime.cs:67 |
| Engine.StackDepth | `int StackDepth()` | il2wasm | stack_depth | System.Private.CoreLib/Boundary/Runtime.cs:69 |
| Engine.RandomSeed | `int RandomSeed()` | il2wasm | random_seed | System.Private.CoreLib/Boundary/Runtime.cs:71 |
| Engine.UtcOffsetMinutes | `int UtcOffsetMinutes(double utcMs)` | il2wasm | utc_offset_minutes | System.Private.CoreLib/Boundary/Runtime.cs:73 |

Generated copies: `Generated/Interop.Boundary.g.cs` of **System.Console, System.IO.Compression, System.IO.MemoryMappedFiles, System.Net.Primitives and System.Security.Cryptography** each contain the same 29 imports (`Interop.Sys.Env.Block` + the 28 `Interop.Sys.Fs.*` rows above, same module/names, file:lines 17 and 28-60 of each) = 145 more `[Import]` attributes; they are copies produced by tools/CoreLibManifest from the CoreLib boundary, not additional host functions (the host import set is the 53 distinct names above).

A second path to imports exists that is not `[Import]`: non-library methods listed in the boundary map (`BoundaryByDocId`, Driver.cs:1038-1040) become imports too, and library methods marked `[Il2Wasm.Runtime.MissingCapability(...)]` (Boundary/MissingCapability.cs) are build errors when reached; neither is catalogued here.
