#nullable enable
using System.Collections.Generic;
using System.Linq;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Memory;

/// <summary>What an intrinsic may write (its contribution to a caller's Mods, docs/MEMORY-IR.md 2.8, 4.1).</summary>
public enum IntrinsicWrites : byte
{
    /// <summary>Nothing a program key names: a pure value, a constant, a table lookup, a count word or another
    /// runtime-private word (3.6: count words, the allocator's and the groups' records are not program keys).</summary>
    None,
    /// <summary>Through a <c>ref T</c> first parameter: any slot of type T (RefStores' rule: <see cref="MemKey.Slot"/>).</summary>
    RefSlot,
    /// <summary>Char data through raw addresses (TextScan's case changes): char elements and char slots.</summary>
    Chars,
    /// <summary>The runtime's table of type objects (RuntimeType.FromId's cache): <see cref="MemKey.Private"/> of
    /// RuntimeType.</summary>
    TypeTable,
    /// <summary>Raw memory, a block copy or fill, or code the compiler cannot see (a thunk, a delegate, a
    /// constructor called by name): any key.</summary>
    Wild,
    /// <summary>A raw clear of memory that holds no counted reference (SpanHelpers.ClearWithoutReferences: its callers
    /// call it only when RuntimeHelpers.IsReferenceOrContainsReferences, which lowering answers with Kinds.Counted, or
    /// ObjectHasReferences says so): it writes no key a borrow, a persistent static or a type set reads, since those
    /// are all of counted references. The trust boundary is in docs/AUDIT.md. IL2WASM_NO_CLEAR_NO_REFS=1: Wild.</summary>
    NoReferences,
}

/// <summary>An intrinsic's hand-written signature (4.1: "the only hand-written signatures are the intrinsics, whose
/// bodies the compiler emits"). Every intrinsic lends all its arguments (they are marshalled to WASM values) and its
/// result is owned as the lowering case leaves it.</summary>
/// <param name="Writes">The keys it may write itself.</param>
/// <param name="Releases">It may release a reference (rt.release, a range release, an unlink): a destroy can run,
/// so its Mods include the program's release effects (3.6).</param>
/// <param name="Cheap">Not a further call for the call rule (4.3, Q8): it runs no program code and does not wait.
/// A releasing intrinsic is cheap only when no release can run program code (no finalizer, no weak-handle
/// table).</param>
/// <param name="Returns">It can return (false: Rt.Unreachable, Rt.Rethrow: a dead end or a throw).</param>
public readonly record struct IntrinsicSignature(IntrinsicWrites Writes, bool Releases = false, bool Cheap = true, bool Returns = true)
{
    /// <summary>An intrinsic not in the table: may write anything, may run anything (the sound default).</summary>
    public static readonly IntrinsicSignature Unknown = new(IntrinsicWrites.Wild, true, false);
}

/// <summary>The hand table of the compiler's intrinsics (docs/CORELIB-INTRINSICS.md): the 220 methods marked
/// <c>[Il2Wasm.Runtime.Intrinsic]</c>, keyed as <c>IlCompiler.TryIntrinsic</c> dispatches them ("Type.Method"), the
/// upstream methods <c>IlCompiler.TryUpstreamIntrinsic</c> replaces by name (Runtime2.cs's list, keyed by full type
/// name), and Activation.cs's RuntimeType cases. Each entry states what the lowering case at IlCompiler.cs:660-904
/// (Runtime2.cs:180-330, Activation.cs) emits, or its contract where the case is a stub of S2's group machinery (a
/// stub that writes nothing today still says what its real lowering will write, so the solution stays sound when the
/// stub is replaced). It replaces ProgramSolution.IntrinsicEffects' default (every intrinsic Wild), so CoreLib
/// methods stop being Wild for Mods.</summary>
public static class IntrinsicSignatures
{
    static readonly IntrinsicSignature Pure = new(IntrinsicWrites.None);
    static readonly IntrinsicSignature RefSlot = new(IntrinsicWrites.RefSlot);
    static readonly IntrinsicSignature Raw = new(IntrinsicWrites.Wild);
    /// <summary>Runs code the compiler does not see as a call edge (a thunk, a delegate, a constructor by name,
    /// first-chance handlers, a finalizer): Wild and a further call.</summary>
    static readonly IntrinsicSignature RunsCode = new(IntrinsicWrites.Wild, true, false);
    static readonly IntrinsicSignature Release = new(IntrinsicWrites.None, true);
    static readonly IntrinsicSignature RawRelease = new(IntrinsicWrites.Wild, true);
    static readonly IntrinsicSignature Waits = new(IntrinsicWrites.None, Cheap: false);
    static readonly IntrinsicSignature NoReturn = new(IntrinsicWrites.None, Returns: false);

    static Dictionary<string, IntrinsicSignature> Table(params (IntrinsicSignature Sig, string Names)[] groups)
    {
        var d = new Dictionary<string, IntrinsicSignature>();
        foreach (var (sig, names) in groups)
            foreach (var n in names.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)) d.Add(n, sig);
        return d;
    }

    /// <summary>The [Il2Wasm.Runtime.Intrinsic] methods (220: CORELIB-INTRINSICS.md 1A), plus Rt.TypeNameById
    /// (IlCompiler.cs:892), keyed "Type.Method" (all overloads of a name share one case, as in TryIntrinsic).</summary>
    static readonly Dictionary<string, IntrinsicSignature> s_il2wasm = Table(
        // ---- Mem (19): loads, size and grow are pure; stores, copy and fill write raw memory
        (Pure, "Mem.LoadI32 Mem.LoadU8 Mem.LoadI8 Mem.LoadU16 Mem.LoadI16 Mem.LoadI64 Mem.LoadF32 Mem.LoadF64 Mem.Size Mem.Grow Mem.RefI32"),
        (Raw, "Mem.StoreI32 Mem.StoreU8 Mem.StoreU16 Mem.StoreI64 Mem.StoreF32 Mem.StoreF64 Mem.Copy Mem.Fill"),
        // ---- Rt (138 + TypeNameById)
        // numbers (WASM instructions)
        (Pure, "Rt.F64Bits Rt.F64FromBits Rt.F32Bits Rt.F32FromBits Rt.Sqrt Rt.Floor Rt.Ceiling Rt.Truncate Rt.RoundEven Rt.Abs Rt.Min Rt.Max "
            + "Rt.CopySign Rt.SqrtF Rt.FloorF Rt.CeilingF Rt.TruncateF Rt.RoundEvenF Rt.AbsF Rt.MinF Rt.MaxF "
            + "Rt.I32Clz Rt.I32Ctz Rt.I32Popcnt Rt.I64Clz Rt.I64Ctz Rt.I64Popcnt Rt.I32Rotl Rt.I32Rotr Rt.I64Rotl Rt.I64Rotr"),
        // addresses, reinterpretation, type ids, layout constants (Rt.Take/Borrow: the address as T; Borrow retains)
        (Pure, "Rt.AddressOf Rt.NullRef Rt.Take Rt.Borrow Rt.TypeId Rt.TypeIdOf Rt.MarkConstructed Rt.HeapBase Rt.TypeName Rt.TypeNameById "
            + "Rt.IsInstanceOf Rt.SizeOf Rt.IsReference Rt.ContainsReferences Rt.BoxDataOffset Rt.PlaceLockWord Rt.ArenaIsSlot Rt.SccOf "
            + "Rt.ArenaFinType Rt.ArenaSlots Rt.ArrayRankAt Rt.ArrayElementSizeAt Rt.TlsBase Rt.TlsSize Rt.ThreadId Rt.DelegateArity "
            + "Rt.DelegateMethodEntry Rt.FrameMethods Rt.ThrowSites Rt.WeakAny"),
        // compile-time switches and whole-program flags (constants, or generated functions returning one)
        (Pure, "Rt.ArrayMoveMode Rt.WideSizes Rt.RipePages Rt.HoldPages Rt.ShareCheck Rt.Checked Rt.NoThreads Rt.NoDropCheck Rt.NoTakeIn "
            + "Rt.NoFreshJoin Rt.NoArrayReplace Rt.NoPinnedSkip Rt.NoStoreWaits Rt.NoEarlyGiveBack Rt.NoSlotWalk Rt.SlotWalkBreak "
            + "Rt.DropStats Rt.StaleHolds Rt.ArenaReport Rt.SingleThreaded Rt.FirstChanceWanted Rt.FramesWanted Rt.ThreadMode "
            + "Rt.DeadlockDump Rt.MemStatsOn Rt.NoWaitingTop Rt.NoMergeRepoint Rt.NoOwnedChain Rt.NoShedHolds Rt.NoFusedJoin "
            + "Rt.NoPlainParents Rt.NoPlainParts Rt.PlainPartsBreak Rt.NoNodePath Rt.NodePathBreak Rt.NoDyingUp Rt.NoDyingNote "
            + "Rt.NoFinRevive Rt.NoAdoptCheck Rt.NoFastDestroy Rt.NoDestroyByBits Rt.FusedDrop Rt.NoFreeList Rt.RuntimeLockHeld"),
        // count words, locks, epochs, the thread-start sharing hook, the throw-site global: runtime-private words
        (Pure, "Rt.Retain Rt.RetainContents Rt.RetainElements Rt.Share Rt.TakeThrowSite Rt.RefLock Rt.RefUnlock Rt.BulkEnter Rt.BulkExit "
            + "Rt.HoldEpoch Rt.ReleaseEpoch Rt.RuntimeLock Rt.RuntimeUnlock Rt.MemStatsGrow Rt.MemStatsRecord Rt.MemStatsHash "
            + "Rt.ArenaVisitObject Rt.LinkAt Rt.ObjectArrayDelegate"),
        (NoReturn, "Rt.Unreachable Rt.Rethrow"),
        // releases: a destroy may run (the release effects of 3.6)
        (Release, "Rt.Release Rt.ReleaseElements Rt.UnlinkAt"),
        // the group fallback's and the thread statics' entry points, lowered as no-ops while no type is cycle-capable
        // and there are no threads (S1, IlCompiler.cs's intrinsic cases: drops, a 0): nothing to write or release. When
        // S2/S3 lower them for real they go back to RawRelease (old values cleared or moved) and RunsCode (a
        // finalizer)
        (Pure, "Rt.ArenaClearObject Rt.ArrayGrow Rt.MoveRefs Rt.ReleaseThreadStatics Rt.WeakDying Rt.PartWeak Rt.ArenaFreeObject Rt.ArenaFinalizeObject"),
        // runs code the compiler does not see as an edge: first-chance handlers, a delegate
        (RunsCode, "Rt.FirstChance Rt.DynamicInvokeRaw"),
        // ---- Atomic (15): through ref int / ref long
        (Pure, "Atomic.Load32 Atomic.Load64 Atomic.Fence Atomic.Notify"),
        (RefSlot, "Atomic.Store32 Atomic.Add32 Atomic.Exchange32 Atomic.CompareExchange32 Atomic.CountAdd32 Atomic.CountCompareExchange32 "
            + "Atomic.Store64 Atomic.Add64 Atomic.Exchange64 Atomic.CompareExchange64"),
        (Waits, "Atomic.Wait32"),
        // ---- Il2WasmArrays (6): copies and clears release the old elements of an array of any element type
        (Pure, "Il2WasmArrays.RankOf Il2WasmArrays.ElementSize Il2WasmArrays.HasCountedElements Il2WasmArrays.BoxesElements"),
        (RawRelease, "Il2WasmArrays.CopyElements Il2WasmArrays.ClearElements"),
        // ---- Generics (6): folded by the instantiation. New allocates and runs the default constructor of a type
        // named by a literal: the constructor is a call edge of the caller's summary (Summary.cs), so the allocation
        // itself writes nothing
        (Pure, "Generics.Implements Generics.IsNullable Generics.IsEnum Generics.New Generics.NewOfNullableArg"),
        // ---- TextScan (4): Upper16/Lower16 write eight chars at dst
        (Pure, "TextScan.Eq16 TextScan.Eq16Any3"),
        (new IntrinsicSignature(IntrinsicWrites.Chars), "TextScan.Upper16 TextScan.Lower16"),
        // ---- TypeInfo (31): the reflection tables are read-only; resources are unpacked into raw memory; Invoke
        // calls a thunk
        (Pure, "TypeInfo.FullName TypeInfo.Name TypeInfo.Namespace TypeInfo.Flags TypeInfo.Flags2 TypeInfo.BaseId TypeInfo.ElementId "
            + "TypeInfo.AssemblyOf TypeInfo.GenericParam TypeInfo.GenericConstraints TypeInfo.GenericParamMethod TypeInfo.AssemblyNames "
            + "TypeInfo.ManifestResources TypeInfo.DeclaringId TypeInfo.Rank TypeInfo.Attributes TypeInfo.CorElementType TypeInfo.Size "
            + "TypeInfo.BoxOffset TypeInfo.EnumInfo TypeInfo.GenericArgs TypeInfo.ArrayOf TypeInfo.Meta TypeInfo.Interfaces "
            + "TypeInfo.NameTable TypeInfo.ProgramTypes TypeInfo.Nested TypeInfo.Instances"),
        (Raw, "TypeInfo.ResourceInit TypeInfo.ResourceUnpack"),
        (RunsCode, "TypeInfo.Invoke"),
        // ---- System.Delegate (1): the target object, retained
        (Pure, "Delegate.TargetOf"));

    /// <summary>Upstream methods the compiler replaces by name (Runtime2.cs's s_upstreamIntrinsics, keyed by the full
    /// type name), and Activation.cs's RuntimeType cases (they call a constructor: code).</summary>
    static readonly Dictionary<string, IntrinsicSignature> s_upstream = Table(
        (Pure, "System.Double.ConvertToIntegerNative System.Single.ConvertToIntegerNative System.Numerics.BitOperations.LeadingZeroCount "
            + "System.Numerics.BitOperations.Log2 System.Numerics.BitOperations.PopCount System.Numerics.BitOperations.RotateLeft "
            + "System.Numerics.BitOperations.RotateRight System.Numerics.BitOperations.TrailingZeroCount "
            + "System.Runtime.CompilerServices.RuntimeHelpers.GetRawData System.Runtime.CompilerServices.RuntimeHelpers.IsBitwiseEquatable "
            + "System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences System.Runtime.CompilerServices.RuntimeHelpers.ObjectHasComponentSize "
            + "System.Runtime.CompilerServices.Unsafe.AddByteOffset System.Runtime.CompilerServices.Unsafe.AreSame System.Runtime.CompilerServices.Unsafe.As "
            + "System.Runtime.CompilerServices.Unsafe.AsPointer System.Runtime.CompilerServices.Unsafe.AsRef System.Runtime.CompilerServices.Unsafe.ByteOffset "
            + "System.Runtime.CompilerServices.Unsafe.IsAddressGreaterThan System.Runtime.CompilerServices.Unsafe.IsAddressLessThan "
            + "System.Runtime.CompilerServices.Unsafe.SizeOf System.Runtime.CompilerServices.Unsafe.SkipInit System.Runtime.CompilerServices.Unsafe.SubtractByteOffset "
            + "System.Runtime.CompilerServices.Unsafe.Unbox System.Threading.Interlocked.MemoryBarrier System.Threading.Volatile.ReadBarrier "
            + "System.Threading.Volatile.WriteBarrier"),
        // (Unsafe.Copy: through a ref T destination, else through a void*: SlotOrRaw below)
        (Raw, "System.Runtime.CompilerServices.Unsafe.Copy System.Runtime.CompilerServices.Unsafe.CopyBlock System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned "
            + "System.Runtime.CompilerServices.Unsafe.InitBlock System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned "
            + "System.SpanHelpers.Memmove System.SpanHelpers.memmove System.SpanHelpers.memset"),
        (new IntrinsicSignature(IntrinsicWrites.NoReferences), "System.SpanHelpers.ClearWithoutReferences"),
        // (the narrow overloads are intrinsic, through ref byte/ushort; the others run their IL, which is not an intrinsic)
        (RefSlot, "System.Threading.Interlocked.CompareExchange System.Threading.Interlocked.Exchange"),
        // (Object.GetType's lowering calls Type.FromId, whose RuntimeType.FromId is [DeclaredEffects]: it fills the
        // runtime's table of type objects, Private(RuntimeType))
        (new IntrinsicSignature(IntrinsicWrites.TypeTable), "System.Object.GetType"),
        (RunsCode, "System.RuntimeType.CreateInstanceOfT System.RuntimeType.CreateInstanceMono System.RuntimeType.CallDefaultStructConstructor"));

    /// <summary>The number of hand-written entries (for the report).</summary>
    public static int Count => s_il2wasm.Count + s_upstream.Count;

    /// <summary>The table's key of an intrinsic: "Type.Method" for il2wasm's own, the full type name for upstream's.</summary>
    static (string Key, bool Own) KeyOf(MethodDesc m)
    {
        var ot = (MetadataType)m.OwningType;
        bool own = m.GetTypicalMethodDefinition() is EcmaMethod em && em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute");
        if (own) return (ot.GetName() + "." + m.GetName(), true);
        return ((ot.GetNamespace().Length > 0 ? ot.GetNamespace() + "." : "") + ot.GetName() + "." + m.GetName(), false);
    }

    /// <summary>The signature of an intrinsic (a method lowering expands: IReachHost.LoweredInline); Unknown when the
    /// table has no entry (sound: Wild, not cheap).</summary>
    /// <summary>The intrinsic writes only through its first argument, a ref (RefSlot).</summary>
    public static bool WritesThroughFirstRef(MethodDesc m) => Of(m).Writes == IntrinsicWrites.RefSlot;

    public static IntrinsicSignature Of(MethodDesc m)
    {
        if (m.OwningType is not MetadataType) return IntrinsicSignature.Unknown;
        var (key, own) = KeyOf(m);
        return (own ? s_il2wasm : s_upstream).TryGetValue(key, out var s) ? s : IntrinsicSignature.Unknown;
    }

    /// <summary>The table has an entry for the method.</summary>
    public static bool Known(MethodDesc m)
    {
        if (m.OwningType is not MetadataType) return false;
        var (key, own) = KeyOf(m);
        return (own ? s_il2wasm : s_upstream).ContainsKey(key);
    }

    /// <summary>An intrinsic call is not a further call for the call rule (4.3): cheap, and its releases cannot run
    /// program code.</summary>
    public static bool IsCheap(MethodDesc m, bool releasesRunCode)
    {
        var s = Of(m);
        return s.Cheap && !(s.Releases && releasesRunCode);
    }

    /// <summary>The runtime's types that own private tables in memory of their own (no object, no program key), by full
    /// name. A raw store in one of their methods writes those tables (or a dying object's header), which no program
    /// load reads (each read from their sources):
    /// <list type="bullet">
    /// <item>the object marks and weak-handle chains (Boundary/ObjectMarks.cs: IntMap, ObjectMarks, WeakSlots);</item>
    /// <item>GCHandle's slots (GCHandle.Mono.cs: an 8-byte block of Rt.Alloc per handle, the target's address and the
    /// kind);</item>
    /// <item>Monitor's table of sync entries (Monitor.Mono.cs: s_table, a block of Rt.Alloc).</item>
    /// </list></summary>
    static readonly HashSet<string> s_privateTypes =
    [
        "Il2Wasm.Runtime.IntMap", "Il2Wasm.Runtime.ObjectMarks", "Il2Wasm.Runtime.WeakSlots",
        "System.Runtime.InteropServices.GCHandle", "System.Threading.Monitor",
    ];

    /// <summary>The private key a raw store of the caller writes, or null: the caller is a method of a private-table
    /// type (Rt's allocator is not one: it is part of Rt, whose other methods write program objects; its entry points
    /// are [DeclaredEffects] instead). The key is <see cref="MemKey.Private"/> of the outermost type.</summary>
    static MemKey? PrivateKey(MethodDesc? caller)
    {
        if (caller?.OwningType is not MetadataType t || t.Module != t.Context.SystemModule) return null;
        var outer = t;
        while (outer.ContainingType is MetadataType ct) outer = ct;
        return s_privateTypes.Contains(outer.GetNamespace() + "." + outer.GetName()) ? MemKey.Private(outer) : null;
    }

    /// <summary>The keys an intrinsic call may write, as dense keys of the solution (the IntrinsicEffects hook):
    /// its own writes, joined with every key when it releases and a release can run program code.</summary>
    public static KeySet Effects(ProgramSolution solution, MethodDesc m, bool releasesRunCode) => Effects(solution, null, m, releasesRunCode);

    static readonly bool s_noClearNoRefs = System.Environment.GetEnvironmentVariable("IL2WASM_NO_CLEAR_NO_REFS") == "1";
    static readonly bool s_noReleaseNodes = System.Environment.GetEnvironmentVariable("IL2WASM_NO_RELEASE_NODES_FOR_INTRINSICS") == "1";

    /// <summary>Rt.Release and Rt.ReleaseElements, whose releases the solve's release nodes count.</summary>
    static bool CountedByReleaseNodes(MethodDesc m) => m.GetName() is "Release" or "ReleaseElements"
        && m.OwningType is MetadataType t && t.GetName() == "Rt" && t.GetNamespace() == "Il2Wasm.Runtime";

    /// <summary>As <see cref="Effects(ProgramSolution, MethodDesc, bool)"/>, for a call in <paramref name="caller"/>:
    /// a raw Mem store, copy or fill there writes the caller type's private key (<see cref="PrivateKey"/>).</summary>
    public static KeySet Effects(ProgramSolution solution, MethodDesc? caller, MethodDesc m, bool releasesRunCode)
    {
        var s = Of(m);
        if (s.Writes == IntrinsicWrites.Wild && !s.Releases && m.OwningType is MetadataType { } mt && mt.GetName() == "Mem" && PrivateKey(caller) is { } pk)
            return new KeySet(solution.Pool.Of(solution.Keys.Intern(pk)), false);
        // (Rt.Release(o) and Rt.ReleaseElements<T>: the solve's release nodes already count what they release, by the
        // operand's static type and by T (Solver.ReleasedTypes, MEMORY-IR 3.6), so their own effects add nothing.
        // That holds while every such operand is a counted value of a released type, not an object made from an address
        // with a narrower type: ProgramSolution.ReleaseViolations checks it in every solve with IL2WASM_VERIFY=1, and
        // SolverChecks on every body of its programs. IL2WASM_NO_RELEASE_NODES_FOR_INTRINSICS=1: All, as before)
        if (s.Releases && releasesRunCode && !(CountedByReleaseNodes(m) && !s_noReleaseNodes)) return KeySet.All;
        switch (s.Writes)
        {
            case IntrinsicWrites.None: return KeySet.Empty;
            case IntrinsicWrites.NoReferences: return s_noClearNoRefs ? KeySet.All : KeySet.Empty;
            case IntrinsicWrites.RefSlot:
                // (the location is the first parameter: ref T)
                if (m.Signature.Length > 0 && m.Signature[0] is ByRefType br) return new KeySet(solution.Pool.Of(solution.Keys.Intern(MemKey.Slot(br.ParameterType))), false);
                return KeySet.All;
            case IntrinsicWrites.TypeTable:
                return m.Context.SystemModule.GetType("System"u8, "RuntimeType"u8, throwIfNotFound: false) is MetadataType rt
                    ? new KeySet(solution.Pool.Of(solution.Keys.Intern(MemKey.Private(rt))), false) : KeySet.All;
            case IntrinsicWrites.Chars:
            {
                var ch = m.Context.GetWellKnownType(WellKnownType.Char);
                return new KeySet(solution.Pool.Of([solution.Keys.Intern(MemKey.Elem(ch)), solution.Keys.Intern(MemKey.Slot(ch))]), false);
            }
            default:
                // (Unsafe.Copy<T>(ref T, void*): through the ref T only)
                if (m.GetName() == "Copy" && m.Signature.Length > 0 && m.Signature[0] is ByRefType cbr && !Il2Wasm.IL.Ir.Kinds.Counted(cbr.ParameterType))
                    return new KeySet(solution.Pool.Of(solution.Keys.Intern(MemKey.Slot(cbr.ParameterType))), false);
                return KeySet.All;
        }
    }

    /// <summary>A method marked [Il2Wasm.Runtime.DeclaredEffects] (Boundary/Runtime.cs: the heap's entry points): the
    /// solver takes its effects as declared and does not solve its body. It writes only its type's private memory
    /// (<see cref="MemKey.Private"/> of the outermost type), may throw a fresh exception (control flow, not a program
    /// write), runs no program code and releases nothing. Null: not declared.</summary>
    public static KeySet? DeclaredEffects(ProgramSolution solution, MethodDesc m)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em || !em.HasCustomAttribute("Il2Wasm.Runtime", "DeclaredEffectsAttribute")) return null;
        var outer = (MetadataType)m.OwningType;
        while (outer.ContainingType is MetadataType ct) outer = ct;
        return new KeySet(solution.Pool.Of(solution.Keys.Intern(MemKey.Private(outer))), false);
    }

    /// <summary>Every [Il2Wasm.Runtime.Intrinsic] method of the system module that the table has no entry for (a
    /// check: IL2WASM_MEM_TRACE prints them).</summary>
    public static IEnumerable<string> Missing(TypeSystemContext ctx)
    {
        if (ctx.SystemModule is not EcmaModule sm) yield break;
        foreach (var t in sm.GetAllTypes())
            foreach (var m in t.GetMethods())
                if (m is EcmaMethod em && em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute") && !Known(m)) yield return t.GetName() + "." + m.GetName();
    }
}

