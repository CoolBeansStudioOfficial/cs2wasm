using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Locals that borrow from a field (section 4.1: "a variable that is never reassigned borrows from its source for its
// scope"). `var x = this.f;` (or `p.f` for a parameter never assigned) would retain the field's object and release it
// when x goes out of scope. The object is already kept alive by the field, as long as nothing writes the field
// meanwhile: so when no code the function can run (itself and everything it calls, through dispatch stubs and the
// function table too) stores to that field, its address is never taken, and no other thread can run, x holds no
// count. Decided once the whole program is compiled: the declaration stores through a placeholder that becomes the
// retain, or nothing; on success the local's releases (scope exit, unwinding) go too.
sealed partial class FunctionCompiler
{
    /// <summary>CS2WASM_NO_GETTER_BORROWS=1: a local does not borrow a borrowed getter's value, as before.</summary>
    static readonly bool s_noGetterBorrows = Environment.GetEnvironmentVariable("CS2WASM_NO_GETTER_BORROWS") == "1";
    /// <summary>The field a declaration can borrow from (key), when the local and its source qualify.</summary>
    string? FieldBorrowCandidate(ILocalSymbol l, IOperation init)
    {
        var r = FieldBorrowCandidateCore(l, init);
        if (Driver.BorrowDebug && r == null) Console.Error.WriteLine($"borrow? {M.Name} {l.Name}: none for {init.Kind} {init.Syntax}");
        return r;
    }

    string? FieldBorrowCandidateCore(ILocalSymbol l, IOperation init)
    {
        if (l.IsRef || IsCaptured(l) || WrittenLocals().Contains(l) || _closure != null || _machine != null)
        {
            if (Driver.BorrowDebug) Console.Error.WriteLine($"borrow? {M.Name} {l.Name}: no (ref {l.IsRef}, captured {IsCaptured(l)}, written {WrittenLocals().Contains(l)}, closure {_closure != null}, machine {_machine != null})");
            return null;
        }
        while (init is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) init = c.Operand;
        // a copy of a reference that stays put for the whole method: the object outlives the local ("": always)
        if (IsStableBase(init)) return "";
        // (threads: only from a place no other thread stores, Sharing.PriorThreadLocal)
        bool tm = D.ThreadMode;
        // an element of an array that stays put (a stable reference, or a field of one): no element store may run
        if (init is IArrayElementReferenceOperation { Indices.Length: 1 } ae && Types.IsRef(T.Resolve(ae.Type!, Map)))
        {
            if (tm && !UnsharedPlace(ae)) return null;
            var ar = ae.ArrayReference;
            while (ar is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } ac) ar = ac.Operand;
            if (tm && ar is IFieldReferenceOperation { Instance: { } fi } && !UnsharedContainer(TypeOf(fi))) return null;
            if (IsStableBase(ar)) return Driver.ElementsKey;
            // (an array a static holds, `var e = s_cache[i]`: the static's key too; no other thread can store it)
            if (StaticArrayKey(ar) is { } sak) return sak + "\n" + Driver.ElementsKey;
            if (ar is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } ai } afr && IsStableBase(ai) && afr.Field.ContainingType.TypeKind == TypeKind.Class
                && !(ai is IParameterReferenceOperation && TypeOf(ai).IsValueType))
            {
                var ak = Driver.FieldKey(Owner(afr.Field), StorageField(afr.Field));
                D.NoteBorrowField(ak, StorageField(afr.Field), T.Resolve(afr.Field.Type, Map));
                return ak + "\n" + Driver.ElementsKey;
            }
            return null;
        }
        // a getter that returns a field's value or an element borrowed (the indexer of List), of a receiver that stays
        // put: the fields the value came through (Driver.ReturnPathKeys), and the receiver's own place (no threads)
        if (!tm && !s_noGetterBorrows && init is IPropertyReferenceOperation { Property.GetMethod: { IsStatic: false } gm, Instance: { } gi } gpr
            && Types.IsRef(T.Resolve(gpr.Type!, Map)) && !TypeOf(gi).IsValueType && !IsVirtualCall(gi, gm)
            && D.Method(SubstMethod(gm)).BorrowedReturn is { BorrowKeys: { } gk, BorrowedKind: Own.Unstable })
        {
            var recv = gi;
            while (recv is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } rc) recv = rc.Operand;
            if (IsStableBase(recv)) return gk;
            if (StaticArrayKey(recv) is { } rsk) return rsk + "\n" + gk;
            if (recv is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } ri } rfr && IsStableBase(ri) && rfr.Field.ContainingType.TypeKind == TypeKind.Class
                && !(ri is IParameterReferenceOperation && TypeOf(ri).IsValueType))
            {
                var rk = Driver.FieldKey(Owner(rfr.Field), StorageField(rfr.Field));
                D.NoteBorrowField(rk, StorageField(rfr.Field), T.Resolve(rfr.Field.Type, Map));
                return rk + "\n" + gk;
            }
            return null;
        }
        // a static's value: as a field's, with the static's own key
        if (tm && init is IFieldReferenceOperation { Field.IsStatic: true }) return null;
        if (init is IFieldReferenceOperation { Field: { IsStatic: true, IsVolatile: false } stf } sfr && Types.IsRef(T.Resolve(stf.Type, Map)) && !IsThreadStatic(StorageField(stf)))
        {
            var sk = Driver.FieldKey(Owner(sfr.Field), StorageField(sfr.Field));
            D.NoteBorrowField(sk, StorageField(sfr.Field), T.Resolve(stf.Type, Map));
            return sk;
        }
        if (init is not IFieldReferenceOperation { Field.IsStatic: false, Instance: { } inst } fr) return null;
        if (!Types.IsRef(T.Resolve(fr.Field.Type, Map)) || fr.Field.ContainingType.TypeKind != TypeKind.Class) return null;
        if (tm && !UnsharedPlace(fr)) return null;
        switch (inst)
        {
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } when M.ContainingType.TypeKind == TypeKind.Class: break;
            case IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } p } when !IsCaptured(p) && !_assignedParams.Contains(p) && !_assignedParams.Contains(p.OriginalDefinition) && !TypeOf(inst).IsValueType: break;
            default: return null;
        }
        var key = Driver.FieldKey(Owner(fr.Field), StorageField(fr.Field));
        D.NoteBorrowField(key, StorageField(fr.Field), T.Resolve(fr.Field.Type, Map));
        return key;
    }

    /// <summary>The key of the static an array is read from, when its elements can be borrowed like a stable array's
    /// (no threads: a static is any thread's to store), else null.</summary>
    string? StaticArrayKey(IOperation ar)
    {
        if (ar is not IFieldReferenceOperation { Field: { IsStatic: true, IsVolatile: false } sf } sfr
            || !Types.IsRef(T.Resolve(sf.Type, Map)) || IsThreadStatic(StorageField(sf))) return null;
        // (threads: a static only the engine thread's code names)
        if (D.ThreadMode && !EngineStatic(sf)) return null;
        var key = Driver.FieldKey(Owner(sfr.Field), StorageField(sfr.Field));
        D.NoteBorrowField(key, StorageField(sfr.Field), T.Resolve(sf.Type, Map));
        return key;
    }

    /// <summary>The declaration of a borrowing candidate: the field's value goes to the local through the placeholder.</summary>
    void EmitFieldBorrow(Var v, IOperation init, string key, ILocalSymbol sym)
    {
        var o = EmitValue(init);
        ConvertTop(TypeOf(init), v.Type, ref o);
        // (an owned value: no placeholder, none in the module either: a checked build's validator would find it empty)
        if (o == Own.Owned) { E.Set(v.Local); return; }
        var marker = D.Module.AddFunction("borrow? " + key, new FuncType([ValType.I32], [ValType.I32]));
        E.Call(marker);
        E.Set(v.Local);
        if (D.ThreadMode) D.ThreadLocalBorrows.Add(marker);
        D.FieldBorrows.Add((E.Fn, v.Local, key, marker));
        if (SimpleArrayUses(sym)) D.SimpleBorrows.Add((E.Fn, v.Local));
    }

    /// <summary>Every use of the local is its array's length, or one of its elements read (a number, nothing that
    /// holds a reference) or stored, with an index and a value that are variables, constants or arithmetic on them:
    /// nothing derived from the local outlives the use, and no call runs between reading the local and using it.
    /// A store of the field it borrows from then matters only if such a use can still follow it
    /// (Driver.StoredWhileInScope): `T[] array = _items; if (size &lt; array.Length) array[size] = item; else Grow();`
    /// borrows, though Grow stores _items.</summary>
    bool SimpleArrayUses(ILocalSymbol l)
    {
        if (T.Resolve(l.Type, Map) is not IArrayTypeSymbol { Rank: 1 } at) return false;
        IOperation? body = null;
        foreach (var r in M.OriginalDefinition.DeclaringSyntaxReferences)
            if (D.ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is { } b) { body = b; break; }
        if (body == null) return false;
        // (a `fixed` statement's pointer into the array is none of the uses looked at below)
        if (body.Syntax.DescendantNodes().Any(n => n is FixedStatementSyntax)) return false;
        bool elementsHoldReferences = T.NeedsRc(T.Resolve(at.ElementType, Map));
        static bool Trivial(IOperation? o) => o switch
        {
            null => true,
            ILocalReferenceOperation or IParameterReferenceOperation or ILiteralOperation or IInstanceReferenceOperation => true,
            IParenthesizedOperation p => Trivial(p.Operand),
            IConversionOperation { OperatorMethod: null } c => (c.Conversion.IsIdentity || c.Conversion.IsNumeric || c.Conversion.IsReference && c.Conversion.IsImplicit) && Trivial(c.Operand),
            IUnaryOperation { OperatorMethod: null } u => Trivial(u.Operand),
            IBinaryOperation { OperatorMethod: null, OperatorKind: not (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) } bin => Trivial(bin.LeftOperand) && Trivial(bin.RightOperand),
            _ => o.ConstantValue.HasValue,
        };
        foreach (var op in body.DescendantsAndSelf())
        {
            if (op is not ILocalReferenceOperation lr || !SymbolEqualityComparer.Default.Equals(lr.Local, l) || lr.IsDeclaration) continue;
            if (InLambda(lr, body)) return false;
            IOperation use = lr;
            while (use.Parent is IConversionOperation { OperatorMethod: null, Conversion.IsIdentity: true } or IParenthesizedOperation) use = use.Parent;
            switch (use.Parent)
            {
                case IPropertyReferenceOperation { Property.Name: "Length", Arguments.Length: 0 } pr when ReferenceEquals(pr.Instance, use):
                    break;
                case IArrayElementReferenceOperation ae when ReferenceEquals(ae.ArrayReference, use) && ae.Indices.All(Trivial):
                    if (ae.Parent is ISimpleAssignmentOperation { IsRef: false } sa && ReferenceEquals(sa.Target, ae))
                    {
                        if (!Trivial(sa.Value)) return false;
                    }
                    else
                    {
                        // a read: of a number only, and as a value (not `ref a[i]`, `a[i]++`, `a[i] += x`)
                        if (elementsHoldReferences || Types.IsStruct(T.Resolve(at.ElementType, Map))) return false;
                        if (ae.Parent is IArgumentOperation { Parameter.RefKind: not RefKind.None } or IAddressOfOperation or IIncrementOrDecrementOperation or ICompoundAssignmentOperation
                            or ISimpleAssignmentOperation { IsRef: true } or IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.IsRef: true } }
                            or IDeconstructionAssignmentOperation or ITupleOperation or ICoalesceAssignmentOperation) return false;
                    }
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}

internal sealed partial class Driver
{
    /// <summary>The borrowing locals (function, local) whose uses are all plain array accesses (FunctionCompiler.SimpleArrayUses).</summary>
    internal readonly HashSet<(WasmFunction, int)> SimpleBorrows = [];
    public static string FieldKey(ITypeSymbol owner, ISymbol storage) => Types.Key(owner) + "::" + storage.Name;
    /// <summary>The pseudo-field of every reference element (and of memory written through an address).</summary>
    public const string ElementsKey = "[]";
    /// <summary>The pseudo-field a thread's safe point stores (threads: ResolveFieldBorrows).</summary>
    const string SafePointKey = "<safe point>";
    static readonly bool s_borrowDebug = Environment.GetEnvironmentVariable("CS2WASM_BORROW") == "1";
    /// <summary>CS2WASM_NO_SELF_STORES=1: an element store of a borrowing local's own value breaks its borrow, as before.</summary>
    static readonly bool s_noSelfStores = Environment.GetEnvironmentVariable("CS2WASM_NO_SELF_STORES") == "1";
    internal static bool BorrowDebug => s_borrowDebug;

    public static bool WritesMemory(Op op) => op is Op.I32Store or Op.I32Store8 or Op.I32Store16 or Op.I64Store or Op.I64Store8 or Op.I64Store16 or Op.I64Store32
        or Op.MemoryCopy or Op.MemoryFill || (int)op >= 0xFE00 && op is not (Op.I32AtomicLoad or Op.I64AtomicLoad);
    /// <summary>The instance fields each function stores to (by key).</summary>
    public readonly Dictionary<WasmFunction, HashSet<string>> FieldWrites = [];
    /// <summary>The stores a borrow must not overlap: FieldWrites without the stores that only replace a null.</summary>
    public readonly Dictionary<WasmFunction, HashSet<string>> BorrowWrites = [];
    /// <summary>Set while a store that runs only when the place holds null is emitted (EmitCoalesceAssign).</summary>
    [ThreadStatic] internal static WasmFunction? NullOnlyStoreIn;
    /// <summary>CS2WASM_NO_NULL_ONLY=1: such stores break borrows as any store does (as before).</summary>
    static readonly bool s_noNullOnly = Environment.GetEnvironmentVariable("CS2WASM_NO_NULL_ONLY") == "1";
    /// <summary>Fields whose address code takes (ref, out, Interlocked): written by whoever holds the address.</summary>
    public readonly HashSet<string> FieldsAddressTaken = [];
    public readonly List<(WasmFunction fn, int local, string field, WasmFunction marker)> FieldBorrows = [];
    /// <summary>Temps holding a field of a stable object for a call: the retain and the release placeholders.</summary>
    readonly List<(WasmFunction fn, string field, WasmFunction retain, WasmFunction release)> _tempBorrows = [];

    public (WasmFunction retain, WasmFunction release) NewFieldBorrow(WasmFunction fn, string key)
    {
        var r = Module.AddFunction("borrow? " + key, new FuncType([ValType.I32], [ValType.I32]));
        var d = Module.AddFunction("unborrow? " + key, new FuncType([ValType.I32], []));
        _tempBorrows.Add((fn, key, r, d));
        return (r, d);
    }

    /// <summary>A store whose place in the function is not marked (the runtime's own functions): anywhere in it.</summary>
    public void NoteFieldWrite(WasmFunction fn, string key)
    {
        if (!FieldWrites.TryGetValue(fn, out var s)) FieldWrites[fn] = s = [];
        s.Add(key);
        if (!BorrowWrites.TryGetValue(fn, out var bw)) BorrowWrites[fn] = bw = [];
        bw.Add(key);
        if (!_unplacedWrites.TryGetValue(fn, out var u)) _unplacedWrites[fn] = u = [];
        u.Add(key);
    }

    /// <summary>Where a function stores a field (a no-op instruction carrying the key, beside the store): a borrow is
    /// broken only by a store that runs while the borrowing local is live (section 4.1, retroactive owners). Taken out
    /// again once the borrows are decided.</summary>
    internal class WriteMark(string key) { public readonly string Key = key; }
    /// <summary>The mark of an element store whose value is a register local read as it is (the instruction names the
    /// local: its index and one): the store counts the new value before it lets go of the old one, so the object the
    /// local names stays held by a slot, and a borrow of that local goes on; not for a slot of a cycle (its site).</summary>
    internal sealed class SelfStoreMark(string key, Arenas.Site? site, int param) : WriteMark(key)
    {
        public readonly Arenas.Site? Site = site;
        /// <summary>The ordinal of the parameter the stored local is, or -1.</summary>
        public readonly int Param = param;
    }
    /// <summary>Right after a setter's call: the caller's register local that was the argument of the parameter with
    /// this ordinal, read as it is. A callee whose only element store is of that parameter (the indexer of List) cannot
    /// let go of the object the local names (SelfStoreParam).</summary>
    internal sealed class ArgLocalMark(int param, int local) : WriteMark("<argument>") { public readonly int Param = param, Local = local; }

    readonly Dictionary<WasmFunction, int> _selfStoreParam = [];
    /// <summary>The ordinal of the parameter whose value is the only thing the function stores into elements, itself
    /// (every element store's mark names it, in no slot of a cycle) and nothing it calls storing any; else -1.</summary>
    int SelfStoreParam(WasmFunction g, StoreSummary summary)
    {
        if (_selfStoreParam.TryGetValue(g, out var known)) return known;
        int param = -2;
        var b = g.Body;
        if (g.IsImport || _unplacedWrites.TryGetValue(g, out var u) && u.Contains(ElementsKey)) param = -1;
        for (int i = 0; i < b.Count && param != -1; i++)
        {
            var ins = b[i];
            if (ins.Op == Op.Nop)
            {
                if (ins.Ref is WriteMark wm && wm.Key == ElementsKey)
                    param = wm is SelfStoreMark { Param: >= 0 } sm && sm.Site is not { Cyclic: true } && (param == -2 || param == sm.Param) ? sm.Param : -1;
            }
            else if (ins.Op == Op.Call && StoresOf(b, i) is { } h) { if (summary.Has(h, ElementsKey)) param = -1; }
            else if (ins.Op == Op.CallIndirect) { if (summary.HasIndirect(ElementsKey, ins)) param = -1; }
        }
        return _selfStoreParam[g] = param >= 0 ? param : -1;
    }
    /// <summary>The ArgLocalMark right after the call at `i`, if any.</summary>
    static ArgLocalMark? ArgMarkAfter(List<Instr> b, int i)
    {
        for (int k = i + 1; k < b.Count && b[k].Op == Op.Nop; k++) if (b[k].Ref is ArgLocalMark am) return am;
        return null;
    }

    /// <summary>Right after a call that can only throw, by a path known at compile time (a List's MoveNext on a list
    /// that was changed: EmitForEach): the stores the call can make are those of the function that throws, `Throws`.
    /// A no-op instruction, taken out with the write marks.</summary>
    internal sealed class ThrowPath(WasmFunction throws) { public readonly WasmFunction Throws = throws; }

    /// <summary>The function whose stores the call at `i` can make: what it calls, or the throwing function of a call
    /// marked ThrowPath.</summary>
    internal static WasmFunction? StoresOf(List<Instr> b, int i) =>
        b[i].Ref is not WasmFunction g ? null : i + 1 < b.Count && b[i + 1].Op == Op.Nop && b[i + 1].Ref is ThrowPath tp ? tp.Throws : g;
    readonly Dictionary<string, WriteMark> _writeMarks = [];
    readonly Dictionary<WasmFunction, HashSet<string>> _unplacedWrites = [];

    public void NoteFieldWrite(Emitter e, string key)
    {
        if (!FieldWrites.TryGetValue(e.Fn, out var s)) FieldWrites[e.Fn] = s = [];
        s.Add(key);
        // (a store that only runs when the field holds null, `f ??= v`: it lets go of nothing a borrow can hold, so
        // the borrows do not see it; what else may change the field meanwhile, v's own code, they see as before)
        if (NullOnlyStoreIn == e.Fn && !s_noNullOnly) return;
        if (!BorrowWrites.TryGetValue(e.Fn, out var bw)) BorrowWrites[e.Fn] = bw = [];
        bw.Add(key);
        if (!_writeMarks.TryGetValue(key, out var m)) _writeMarks[key] = m = new WriteMark(key);
        e.Emit(Op.Nop, 0, 0, m);
        e.Body[^1] = e.Body[^1] with { Pos = -1 };
    }

    void StripWriteMarks()
    {
        foreach (var f in Module.Functions)
        {
            if (f.IsImport) continue;
            f.Body.RemoveAll(x => x.Op == Op.Nop && x.Ref is WriteMark or ThrowPath or FieldStoreMark);
            // (and the indirect calls' buckets: bodies that differ only there are the same code)
            var b = f.Body;
            for (int i = 0; i < b.Count; i++) if (b[i].Op == Op.CallIndirect && b[i].Imm2 != 0) b[i] = b[i] with { Imm2 = 0 };
        }
    }

    // ---- what an indirect call can reach ----
    // An indirect call is a delegate's Invoke (the functions delegates of that type were made with), a reflection
    // thunk's call, an exception filter's test, or a function pointer's call (any function in the table). The call
    // carries its bucket (Imm2, taken out again with the marks); buckets list their functions.
    internal const string ThunkBucket = "thunk", FilterBucket = "filter";
    /// <summary>Delegates convert between instances of one generic delegate (variance): a bucket per definition.</summary>
    internal static string DelegateBucket(ITypeSymbol dt) => "d:" + Types.Key(dt.OriginalDefinition);
    readonly Dictionary<string, int> _bucketIds = [];
    readonly List<HashSet<WasmFunction>> _bucketFns = [[]];
    readonly List<string> _bucketKeys = [""];

    internal int IndirectBucket(string key)
    {
        lock (_bucketIds)
        {
            if (!_bucketIds.TryGetValue(key, out int id)) { _bucketIds[key] = id = _bucketFns.Count; _bucketFns.Add([]); _bucketKeys.Add(key); }
            return id;
        }
    }

    internal void NoteIndirectTarget(string key, WasmFunction f)
    {
        int id = IndirectBucket(key);
        lock (_bucketIds) _bucketFns[id].Add(f);
    }

    // A delegate's Invoke of one constructed type reaches the functions delegates of a type it can be converted from
    // were made with: the same type, or another instance of its definition whose type arguments variance allows
    // (`Action<object>` held as an `Action<string>`), not every instance of the definition (an `Action<Diagnostic>`'s
    // lambda is never run by `Action<AssemblyLoadContext>.Invoke`).
    static readonly bool s_noDelegateTypes = Environment.GetEnvironmentVariable("CS2WASM_NO_DELEGATE_TYPES") == "1";
    readonly Dictionary<string, List<(INamedTypeSymbol type, WasmFunction fn)>> _delegateTargets = [];
    readonly Dictionary<int, INamedTypeSymbol> _delegateBuckets = [];
    readonly Dictionary<int, List<WasmFunction>> _delegateBucketFns = [];

    /// <summary>A delegate of type dt made with fn (beside NoteIndirectTarget's bucket of its definition).</summary>
    internal void NoteDelegateTarget(ITypeSymbol dt, WasmFunction fn)
    {
        if (dt is not INamedTypeSymbol nt) return;
        lock (_bucketIds)
        {
            var key = Types.Key(nt.OriginalDefinition);
            if (!_delegateTargets.TryGetValue(key, out var l)) _delegateTargets[key] = l = [];
            l.Add((nt, fn));
        }
    }

    /// <summary>The bucket of an Invoke's indirect call on delegates of the constructed type dt.</summary>
    internal int DelegateCallBucket(ITypeSymbol dt)
    {
        if (s_noDelegateTypes || dt is not INamedTypeSymbol nt || !nt.IsGenericType) return IndirectBucket(DelegateBucket(dt));
        int id = IndirectBucket("dc:" + Types.Key(nt));
        lock (_bucketIds) _delegateBuckets[id] = nt;
        return id;
    }

    /// <summary>Whether a delegate made as `from` can be invoked through `to`'s Invoke: one definition, each type
    /// argument the same, or (a variant parameter's, reference types) convertible the way its variance allows; the
    /// containing types' arguments the same. Unsure: true.</summary>
    bool DelegateConvertible(INamedTypeSymbol from, INamedTypeSymbol to)
    {
        if (!SymbolEqualityComparer.Default.Equals(from.OriginalDefinition, to.OriginalDefinition)) return false;
        if (Types.Key(from) == Types.Key(to)) return true;
        for (INamedTypeSymbol? a = from.ContainingType, b = to.ContainingType; a != null || b != null; a = a?.ContainingType, b = b?.ContainingType)
            if (a == null || b == null || Types.Key(a) != Types.Key(b)) return false;
        var ps = from.OriginalDefinition.TypeParameters;
        if (ps.Length != from.TypeArguments.Length || ps.Length != to.TypeArguments.Length) return true;
        for (int i = 0; i < ps.Length; i++)
        {
            ITypeSymbol f = from.TypeArguments[i], t = to.TypeArguments[i];
            if (Types.Key(f) == Types.Key(t)) continue;
            if (f is ITypeParameterSymbol || t is ITypeParameterSymbol) return true;
            // (variance converts only between reference types)
            if (!f.IsReferenceType || !t.IsReferenceType) return false;
            switch (ps[i].Variance)
            {
                case VarianceKind.Out: if (!Types.IsSubtype(f, t)) return false; break;
                case VarianceKind.In: if (!Types.IsSubtype(t, f)) return false; break;
                default: return false;
            }
        }
        return true;
    }

    List<WasmFunction>? _tableFns;
    /// <summary>Every function some code can put in the table (an unbucketed indirect call reaches them all).</summary>
    List<WasmFunction> TableFns()
    {
        if (_tableFns != null) return _tableFns;
        var set = new HashSet<WasmFunction>();
        foreach (var t in Module.Functions)
        {
            if (t.InTable || t.TableRoot) set.Add(t);
            foreach (var i2 in t.Body) if (i2.Op == Op.I32Const && i2.Ref is WasmFunction tf) set.Add(tf);
        }
        return _tableFns = [.. set.OrderBy(f => f.Order)];
    }

    /// <summary>The functions an indirect call can reach. Functions the reflection tables refer to (TableRoot) can
    /// be any delegate's (Delegate.CreateDelegate) and are what a thunk call reaches.</summary>
    internal IEnumerable<WasmFunction> IndirectTargets(in Instr call)
    {
        // (a call_indirect runs only a function of exactly its type: any other traps before it runs)
        var any = IndirectTargetsAnyType(call);
        if (s_noIndirectTypes || call.Ref is not FuncType sig) return any;
        return any.Where(f => f.Type.Equals(sig));
    }

    /// <summary>CS2WASM_NO_INDIRECT_TYPES=1: an indirect call reaches its bucket's functions of any type (as before).</summary>
    static readonly bool s_noIndirectTypes = Environment.GetEnvironmentVariable("CS2WASM_NO_INDIRECT_TYPES") == "1";

    IEnumerable<WasmFunction> IndirectTargetsAnyType(in Instr call)
    {
        int b = (int)call.Imm2;
        if (b <= 0 || b >= _bucketFns.Count) return TableFns();
        var key = _bucketKeys[b];
        if (key == FilterBucket) return _bucketFns[b];
        IEnumerable<WasmFunction> own = _bucketFns[b];
        if (_delegateBuckets.TryGetValue(b, out var to))
        {
            if (!_delegateBucketFns.TryGetValue(b, out var fns))
            {
                fns = [];
                if (_delegateTargets.TryGetValue(Types.Key(to.OriginalDefinition), out var made))
                    foreach (var (from, fn) in made) if (DelegateConvertible(from, to)) fns.Add(fn);
                _delegateBucketFns[b] = fns;
            }
            own = fns;
        }
        return own.Concat(TableFns().Where(f => f.TableRoot)).Concat(_objectArrayThunks.Values.Select(t => t.Fn));
    }

    /// <summary>Whether a store of one of the keys may run while the local is in scope (section 4.1: a variable
    /// borrows from its source for its scope): from its definition (at `def`) to the end of its scope (the mark
    /// PopScope leaves; the function's end without one). Everything derived from the local (temporaries, references
    /// into its object, locals that borrow from its fields) lives inside that scope, so the lexical range is enough.
    /// Stores: the function's own (marks), and those of what it calls in the range.</summary>
    bool StoredWhileInScope(WasmFunction fn, int local, int def, string[] keys, StoreSummary summary)
    {
        var b = fn.Body;
        // (a local whose uses are all its array's length and plain element accesses, FunctionCompiler.SimpleArrayUses:
        // a store no such use can follow leaves nothing that looks at the array)
        bool simple = SimpleBorrows.Contains((fn, local));
        bool Why(int at, string what, string k)
        {
            // (not a cycle link's call: it sits between an element store's address and the store itself)
            if (simple && at >= 0 && !(b[at].Ref is WasmFunction lf && (lf.Name.StartsWith("Cs2Wasm.Runtime.Rt.Arena") || lf.Name.StartsWith("arena.")))
                && !UseCanFollow(b, at, local, def, Rt.Release)) return false;
            if (s_borrowDebug) Console.Error.WriteLine($"  refused {fn.Name} local {local}: {k} stored by {what}{(at >= 0 && b[at].Ref is WasmFunction wg ? StoreChain(wg, k) : "")}");
            return true;
        }
        if (_unplacedWrites.TryGetValue(fn, out var u) && keys.Any(u.Contains)) return Why(-1, "the function (unplaced)", "");
        for (int i = def; i < b.Count; i++)
        {
            var ins = b[i];
            if (ins.Op == Op.Nop)
            {
                // (the scope's end is a mark too: asked first)
                if (ReferenceEquals(ins.Ref, ScopeEnd)) { if (ins.Imm == local) return false; }
                // (an element store of the borrowing local's own value: Expressions.cs, MemLV.Store)
                else if (ins.Ref is SelfStoreMark sm && ins.Imm == local + 1 && sm.Site is not { Cyclic: true } && !s_noSelfStores) { }
                else if (ins.Ref is WriteMark wm && Array.IndexOf(keys, wm.Key) >= 0) { if (Why(i, "the function itself", wm.Key)) return true; }
            }
            else if (ins.Op == Op.Call && StoresOf(b, i) is { } g)
            {
                // (the check of a type one of whose statics the value was read from: that read came after the same
                // check, so the initializer has run, or is running further up this stack, and this call returns at
                // once, as StoreSummary says of the initializer's own stores)
                if (!s_noSelfStores && g.Name.StartsWith("static init ", StringComparison.Ordinal) && !g.Name.StartsWith(StaticInitBodyPrefix, StringComparison.Ordinal)
                    && keys.Any(k => IsStaticKey(k) && k.LastIndexOf("::", StringComparison.Ordinal) is > 0 and var c && g.Name.Length == 12 + c && string.CompareOrdinal(g.Name, 12, k, 0, c) == 0)) continue;
                foreach (var k in keys)
                    if (summary.Has(g, k))
                    {
                        // (a setter that stores only its value, called with the borrowing local's own value)
                        if (k == ElementsKey && !s_noSelfStores && ArgMarkAfter(b, i) is { } am && am.Local == local && SelfStoreParam(g, summary) == am.Param) continue;
                        if (Why(i, "a call of " + g.Name, k)) return true;
                        break;
                    }
            }
            else if (ins.Op == Op.CallIndirect) { foreach (var k in keys) if (summary.HasIndirect(k, ins)) { if (Why(i, "an indirect call", k)) return true; break; } }
        }
        return false;
    }

    /// <summary>CS2WASM_BORROW=1 (diagnosis): the shortest chain of direct calls from the function to one that stores
    /// the key itself, as the reason a borrow was refused.</summary>
    string StoreChain(WasmFunction from, string key)
    {
        var parent = new Dictionary<WasmFunction, WasmFunction?> { [from] = null };
        var work = new Queue<WasmFunction>(); work.Enqueue(from);
        while (work.Count > 0)
        {
            var f = work.Dequeue();
            if (BorrowWrites.TryGetValue(f, out var w) && w.Contains(key))
            {
                var chain = new List<string>();
                for (WasmFunction? x = f; x != null; x = parent[x]) chain.Add(x.Name);
                chain.Reverse();
                return "\n     chain: " + string.Join("\n       -> ", chain);
            }
            for (int i = 0; i < f.Body.Count; i++)
                if (f.Body[i].Op == Op.Call && StoresOf(f.Body, i) is { } g && !StoreSummary.Internal(g) && !(key == ElementsKey && g.Name.StartsWith("Cs2Wasm.Runtime.Rt.OnThrow(", StringComparison.Ordinal)) && parent.TryAdd(g, f)) work.Enqueue(g);
        }
        return "\n     chain: (through an indirect call)";
    }

    /// <summary>Whether a read of the local can run after the instruction at `at`, in the local's scope (it began at
    /// `def`): one later in the function, unless it is in the other arm of an `if` that `at` is in the first arm of;
    /// or any one inside a loop that began after `def` and that `at` is in (the way round). The releases of the
    /// local at its scope's ends are no reads (they go with the count).</summary>
    static bool UseCanFollow(List<Instr> b, int at, int local, int def, WasmFunction release)
    {
        bool IsUse(int k) => b[k].Op == Op.LocalGet && b[k].Imm == local && !(k + 1 < b.Count && b[k + 1].Op == Op.Call && ReferenceEquals(b[k + 1].Ref, release));
        var open = new List<(int start, Op kind)>();
        for (int k = 0; k < at; k++)
            switch (b[k].Op)
            {
                case Op.Block or Op.Loop or Op.If or Op.TryTable: open.Add((k, b[k].Op)); break;
                case Op.End: if (open.Count > 0) open.RemoveAt(open.Count - 1); break;
            }
        // a loop of the scope that is open here: everything in it can run again
        int loop = -1;
        foreach (var (start, kind) in open) if (kind == Op.Loop && start > def) { loop = start; break; }
        if (loop >= 0)
        {
            int depth = 0;
            for (int k = loop; k < b.Count; k++)
            {
                if (b[k].Op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
                else if (b[k].Op == Op.End && --depth == 0) break;
                if (IsUse(k)) return true;
            }
        }
        // what follows: `outer` of the blocks open at `at` are still open, `inner` opened since; `skip`: in the other
        // arm of an `if` that `at` was in the first arm of, until that `if` ends
        int outer = open.Count, inner = 0, skip = -1;
        for (int k = at + 1; k < b.Count; k++)
        {
            var ins = b[k];
            switch (ins.Op)
            {
                case Op.Block or Op.Loop or Op.If or Op.TryTable: inner++; break;
                case Op.Else: if (inner == 0 && skip < 0) skip = outer; break;
                case Op.End:
                    if (inner > 0) inner--;
                    else { if (skip == outer) skip = -1; outer--; if (outer < 0) return false; }
                    break;
                case Op.Nop when ReferenceEquals(ins.Ref, ScopeEnd) && ins.Imm == local: return false;
            }
            if (skip < 0 && IsUse(k)) return true;
        }
        return false;
    }

    /// <summary>The end of a local's scope (a no-op instruction: Imm is the local), for StoredWhileInScope.</summary>
    internal static readonly WriteMark ScopeEnd = new("scope end");
    /// <summary>Where a function's handler for an exception on its way out begins (it releases what the function
    /// holds and throws on): a temporary is not read again past it.</summary>
    internal static readonly WriteMark HandlerStart = new("handler");

    /// <summary>Resolves the placeholders (RuntimeGen.Finish, when every function exists).</summary>
    /// <summary>A release: the atomic one or (threads) the plain one, TypedCounts.cs.</summary>
    bool IsRelease(object? f) => ReferenceEquals(f, Rt.Release) || f != null && ReferenceEquals(f, Rt.ReleasePlainIfMade);

    /// <summary>CS2WASM_COUNT_RC: why a temporary's borrow was not proven, as its counts' cause.</summary>
    static readonly bool s_countRcWhy = Environment.GetEnvironmentVariable("CS2WASM_COUNT_RC") == "1";

    static string ShortKey(string k) => k.Length > 90 ? k[^90..] : k;

    readonly Dictionary<(WasmFunction, string), string> _chainKinds = [];

    /// <summary>CS2WASM_COUNT_RC: by what way the call reaches a store of the key: the shortest chain of calls from it
    /// to a function that stores the key, through a destruction (a release) or not, and the function that stores.</summary>
    string ChainKind(WasmFunction root, string key)
    {
        if (_chainKinds.TryGetValue((root, key), out var known)) return known;
        var parent = new Dictionary<WasmFunction, WasmFunction?> { [root] = null };
        var work = new Queue<WasmFunction>(); work.Enqueue(root);
        string r = " (no chain found)";
        int steps = 0;
        while (work.Count > 0 && steps++ < 20000)
        {
            var f = work.Dequeue();
            if (BorrowWrites.TryGetValue(f, out var fw) && fw.Contains(key))
            {
                var chain = new List<WasmFunction>();
                for (WasmFunction? x = f; x != null; x = parent[x]) chain.Add(x);
                chain.Reverse();
                int d = chain.FindIndex(x => x.Name == "rt.destroy" || x.Name.StartsWith("rt.release") || x.Name.StartsWith("rt.destroy"));
                var after = d >= 0 ? chain.Skip(d + 1).FirstOrDefault(x => !x.Name.StartsWith("rt.") && !x.Name.StartsWith("destroy")) : null;
                r = d >= 0 ? $" via a destruction, {(after?.Name ?? "?")}" : $" by {f.Name}";
                if (r.Length > 160) r = r[..160];
                break;
            }
            void Add(WasmFunction g) { if (!StoreSummary.Internal(g) && parent.TryAdd(g, f)) work.Enqueue(g); }
            if (f.ForwardTo != null) Add(f.ForwardTo);
            for (int j = 0; j < f.Body.Count; j++)
            {
                var ins = f.Body[j];
                if (ins.Op == Op.Call && StoresOf(f.Body, j) is { } g) Add(g);
                else if (ins.Op == Op.CallIndirect) foreach (var t in IndirectTargets(ins)) Add(t);
            }
        }
        return _chainKinds[(root, key)] = r;
    }

    public void ResolveFieldBorrows()
    {
        PrintReachWhy();
        // (the statics a loop can read once: StaticLoads.cs)
        var staticLoads = StaticLoadCandidates();
        // (the fields a loop can read once: FieldLoads.cs)
        bool fieldLoads = PrepareFieldLoads();
        if (FieldBorrows.Count == 0 && _tempBorrows.Count == 0 && staticLoads.Count == 0 && _hoistCount == 0) { FinishFieldLoads(null); StripWriteMarks(); return; }
        var byFn = FieldBorrows.GroupBy(b => b.fn).ToDictionary(g => g.Key, g => g.ToList());
        bool threads = UsesThreads, safePoints = false;
        // what each function may store, with everything it can call (an indirect call: any function in the table)
        StoreSummary? summary = null;
        {
            var queried = new HashSet<string>();
            foreach (var b in FieldBorrows) foreach (var k in b.field.Split('\n')) if (k != "") queried.Add(k);
            foreach (var b in _tempBorrows) foreach (var k in b.field.Split('\n')) queried.Add(k);
            foreach (var l in staticLoads.Values) foreach (var s in l) queried.Add(AllStoresPrefix + _staticRefSlots[s].key);
            summary = new StoreSummary(this, queried);
            // (threads: the safe point is a pseudo-field the function that moves a thread's epoch stores)
            if (threads && AllMethods.FirstOrDefault(m => m.Compiled && m.Sym.Name == "SetEpoch" && m.Sym.ContainingType?.Name == "Limbo") is { } setEpoch)
            {
                safePoints = true;
                var (fns, buckets, table) = ReachesSafePoint(setEpoch.Fn);
                summary.Safe = fns; summary.SafeBuckets = buckets; summary.SafeTable = table;
            }
        }
        // Threads (section 4.5), decided at compile time: the code a worker thread can run is what its start export
        // reaches. A field that only code no worker can run stores is never changed by another thread, so code no
        // worker can run borrows from it exactly as in a program without threads. Everything else keeps its counts.
        HashSet<WasmFunction>? worker = threads ? WorkerReachable() : null;
        var confined = new Dictionary<string, bool>();
        bool Confined(WasmFunction fn, string k)
        {
            if (worker == null) return true;
            if (worker.Contains(fn)) return false;
            if (!confined.TryGetValue(k, out var c))
            {
                c = true;
                foreach (var (w, keys) in FieldWrites) if (keys.Contains(k) && worker.Contains(w)) { c = false; break; }
                confined[k] = c;
            }
            return c;
        }
        if (s_borrowDebug && worker != null) Console.Error.WriteLine($"threads: {worker.Count} of {Module.Functions.Count} functions can run on a worker");
        // Any other code, and any other field: another thread may store the field while it is borrowed, and release the
        // object it held. Such an object is shared (what a shared object refers to is shared), so its last release
        // puts it in limbo, and it is destroyed only after every thread has passed a safe point (Limbo, section 4.5).
        // So the borrow holds while the borrowing thread passes no safe point: with the stores this thread itself
        // may run, no safe point may be reachable in the borrow's range. Decided here, at compile time. Not for an
        // object that may be an arena's member (its arena frees it at once).
        var arenaMember = new Dictionary<string, bool>();
        bool MayBeArenaMember(string k)
        {
            if (!Arenas.Enabled) return false;
            if (!arenaMember.TryGetValue(k, out var r))
            {
                r = !_borrowFieldTypes.TryGetValue(k, out var ft) || Arenas.AnyCycleTypeSubtypeOf(ft);
                arenaMember[k] = r;
            }
            return r;
        }
        // null: no borrow; else the keys no store may reach in the range (the safe point's among them when it matters)
        string[]? ThreadKeys(WasmFunction fn, string[] keys, WasmFunction? marker = null)
        {
            if (worker == null || marker != null && ThreadLocalBorrows.Contains(marker) || keys.All(k => Confined(fn, k))) return keys;
            if (s_borrowDebug) Console.Error.WriteLine($"  refused {fn.Name}: threads");
            return null;
        }
        var reflected = ReflectedFieldKeys();
        foreach (var (fn, list) in byFn)
        {
            var b = fn.Body;
            // Where each local is set and read, and where each marker is called: the instructions below are replaced
            // in place (none moves until the RemoveAll at the end), and only sets, reads and markers are looked at, so
            // each borrow visits its own positions instead of the whole body (a function with many borrows was
            // gone through once per borrow, three times). Each position is still asked about its current instruction,
            // in the body's order, as the scans over the whole body did.
            var setsOf = new Dictionary<long, List<int>>();
            var getsOf = new Dictionary<long, List<int>>();
            var markerAt = new Dictionary<WasmFunction, List<int>>();
            {
                var markerSet = new HashSet<WasmFunction>();
                foreach (var it in list) markerSet.Add(it.marker);
                for (int i = 0; i < b.Count; i++)
                {
                    var x = b[i];
                    List<int>? l;
                    if (x.Op is Op.LocalSet or Op.LocalTee) { if (!setsOf.TryGetValue(x.Imm, out l)) setsOf[x.Imm] = l = []; l.Add(i); }
                    else if (x.Op == Op.LocalGet) { if (!getsOf.TryGetValue(x.Imm, out l)) getsOf[x.Imm] = l = []; l.Add(i); }
                    else if (x.Op == Op.Call && x.Ref is WasmFunction mw && markerSet.Contains(mw)) { if (!markerAt.TryGetValue(mw, out l)) markerAt[mw] = l = []; l.Add(i); }
                }
            }
            foreach (var (_, local, field, marker) in list)
            {
                var markerPos = markerAt.TryGetValue(marker, out var mp) ? mp : [];
                // (an init-only field never changes once its object is constructed: no code, on no thread, writes it)
                bool ok = field == "" || field.Split('\n').All(k => !FieldsAddressTaken.Contains(k) && !reflected.Contains(k)
                    );
                if (ok && field != "" && summary != null)
                {
                    var keys = ThreadKeys(fn, field.Split('\n').Where(k => !InitOnlyKey(k)).ToArray(), marker);
                    int def = -1;
                    foreach (var p in markerPos) if (b[p].Op == Op.Call && b[p].Ref == marker) { def = p; break; }
                    if (keys == null || keys.Length > 0 && (def < 0 || StoredWhileInScope(fn, local, def, keys, summary))) ok = false;
                }
                if (s_borrowDebug) Console.Error.WriteLine($"borrow {fn.Name} local {local} {field.Replace('\n', '|')}: {ok}"
                    + (summary != null ? " own=" + (FieldWrites.TryGetValue(fn, out var ow) && ow.Contains(field) ? "y" : "n") + " via "
                        + string.Join(",", fn.Body.Where(x => x.Op == Op.Call && x.Ref is WasmFunction g && summary.Has(g, field)).Select(x => ((WasmFunction)x.Ref!).Name).Distinct().Take(5)) : ""));
                // the local's other writes are the releases at scope exit (get; release; 0; set): nothing moves its value
                if (ok && setsOf.TryGetValue(local, out var sets))
                    foreach (var i in sets)
                    {
                        if (!ok) break;
                        if (b[i].Op is Op.LocalSet or Op.LocalTee && b[i].Imm == local && !(i > 0 && b[i - 1].Op == Op.Call && b[i - 1].Ref == marker))
                            ok = i >= 3 && b[i - 1].Op == Op.I32Const && b[i - 1].Imm == 0 && b[i - 1].Ref == null
                                 && b[i - 2].Op == Op.Call && IsRelease(b[i - 2].Ref) && b[i - 3].Op == Op.LocalGet && b[i - 3].Imm == local
                                 // (an assignment of a local that is assigned again, LocalBorrows.cs: the value through the
                                 // placeholder, the old value released, then the store)
                                 || i >= 5 && b[i - 1].Op == Op.LocalGet && b[i - 2].Op == Op.Call && IsRelease(b[i - 2].Ref)
                                    && b[i - 3].Op == Op.LocalGet && b[i - 3].Imm == local && b[i - 4].Op == Op.LocalSet && b[i - 4].Imm == b[i - 1].Imm
                                    && b[i - 5].Op == Op.Call && b[i - 5].Ref == marker;
                    }
                // (the marker's calls, and with a borrow the local's reads, in the body's order)
                IEnumerable<int> at = markerPos;
                if (ok && getsOf.TryGetValue(local, out var gets)) at = MergeAscending(markerPos, gets);
                foreach (var i in at)
                {
                    if (b[i].Op == Op.Call && b[i].Ref == marker)
                        b[i] = ok ? b[i] with { Op = Op.Nop, Ref = null } : b[i] with { Ref = Rt.Retain };
                    else if (ok && b[i].Op == Op.LocalGet && b[i].Imm == local && i + 1 < b.Count && b[i + 1].Op == Op.Call && IsRelease(b[i + 1].Ref))
                    {
                        b[i] = b[i] with { Op = Op.Nop };
                        b[i + 1] = b[i + 1] with { Op = Op.Nop, Ref = null };
                    }
                }
            }
            fn.Body.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
        }
        // the temps: the retain and the release go together
        var markers = new Dictionary<WasmFunction, WasmFunction?>();
        // (per function, once: where the temps' retains and releases are called, and its last handler start. The
        // bodies do not change in this loop; each temp was a search of its whole function, three times)
        var tempFns = new HashSet<WasmFunction>();
        foreach (var t in _tempBorrows) { tempFns.Add(t.retain); tempFns.Add(t.release); }
        var tempIndex = new Dictionary<WasmFunction, (Dictionary<WasmFunction, List<int>> calls, int handler)>();
        (Dictionary<WasmFunction, List<int>> calls, int handler) TempIndex(WasmFunction fn)
        {
            if (tempIndex.TryGetValue(fn, out var ix)) return ix;
            var calls = new Dictionary<WasmFunction, List<int>>();
            int handler = -1;
            var fb = fn.Body;
            for (int i = 0; i < fb.Count; i++)
            {
                if (fb[i].Op == Op.Call && fb[i].Ref is WasmFunction cw && tempFns.Contains(cw)) { if (!calls.TryGetValue(cw, out var l)) calls[cw] = l = []; l.Add(i); }
                else if (fb[i].Op == Op.Nop && ReferenceEquals(fb[i].Ref, HandlerStart)) handler = i;
            }
            return tempIndex[fn] = (calls, handler);
        }
        var whys = new Dictionary<WasmFunction, string>();
        foreach (var (fn, field, retain, release) in _tempBorrows)
        {
            // (several fields: the value came through each of them, a getter's path: Driver.ReturnPathKeys)
            var all = field.Split('\n');
            bool addr = all.Any(k => FieldsAddressTaken.Contains(k) || reflected.Contains(k));
            // (an init-only field never changes: nothing to look for)
            var live = all.Where(k => !InitOnlyKey(k)).ToArray();
            var tkeys = addr ? null : live.Length == 0 ? live : ThreadKeys(fn, live, retain);
            bool ok = tkeys != null;
            string? why = s_countRcWhy ? (addr ? "address taken" : ok ? null : "threads") : null;
            // the borrow lasts from the retain to the statement's release: only what runs in between may not store the
            // fields (the function's own stores anywhere: the whole function's summary decides)
            // fields, the function's own stores: those marked in between, and those without a place)
            if (ok && live.Length > 0 && _unplacedWrites.TryGetValue(fn, out var unplaced) && live.Any(unplaced.Contains)) { ok = false; why = "the function's own unplaced store"; }
            if (ok && (live.Length > 0 || tkeys!.Length > 0))
            {
                var b = fn.Body;
                var (calls, h) = TempIndex(fn);
                int r = calls.TryGetValue(retain, out var rs) ? rs[0] : -1;
                // (to the last release on a way that goes on; the releases of the handler for an exception on its way
                // out come after everything, and past them the value is not looked at)
                int e = -1;
                if (r >= 0 && calls.TryGetValue(release, out var es))
                {
                    int limit = h < 0 ? b.Count - 1 : h;
                    for (int q = es.Count - 1; q >= 0; q--) if (es[q] <= limit) { e = es[q]; break; }
                }
                if (r < 0 || e < r) { ok = !tkeys!.Any(k => summary!.Has(fn, k)) && !(BorrowWrites.TryGetValue(fn, out var own) && live.Any(own.Contains)); if (!ok) why = "no range: the whole function stores"; }
                else
                    for (int k = r + 1; k < e && ok; k++)
                    {
                        var ins = b[k];
                        if (ins.Op == Op.Nop) { if (ins.Ref is WriteMark wm && !ReferenceEquals(wm, ScopeEnd) && Array.IndexOf(live, wm.Key) >= 0) { ok = false; why = "own store in range of " + ShortKey(wm.Key); } }
                        else if (ins.Op == Op.Call && StoresOf(b, k) is { } g && g != retain) { foreach (var key in tkeys!) if (summary!.Has(g, key)) { ok = false; why = s_countRcWhy ? (IsRelease(g) ? "a release" : g.Name.StartsWith("rt.") ? g.Name : "a call") + " storing " + ShortKey(key) + ChainKind(g, key) : null; break; } }
                        else if (ins.Op == Op.CallIndirect) { foreach (var key in tkeys!) if (summary!.HasIndirect(key, ins)) { ok = false; why = "an indirect call storing " + ShortKey(key); break; } }
                    }
            }
            markers[retain] = ok ? null : Rt.Retain;
            markers[release] = ok ? null : Rt.Release;
            if (why != null && !ok) { whys[retain] = why; whys[release] = why; }
        }
        if (markers.Count > 0)
            foreach (var f in Module.Functions)
            {
                var b = f.Body;
                bool any = false;
                for (int i = 0; i < b.Count; i++)
                    if (b[i].Op == Op.Call && b[i].Ref is WasmFunction g && markers.TryGetValue(g, out var to))
                    {
                        any = true;
                        // (retain → the value stays; release → its operand is dropped)
                        if (to != null) b[i] = b[i] with { Ref = to, Cause = whys.TryGetValue(g, out var wy) ? Emitter.CauseOf("unproven temp borrow (" + wy + ")") : b[i].Cause };
                        else b[i] = g.Type.Results.Length == 1 ? b[i] with { Op = Op.Nop, Ref = null } : b[i] with { Op = Op.Drop, Ref = null };
                    }
                if (any) b.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
            }
        var dead = markers.Keys.Concat(FieldBorrows.Select(b => b.marker)).ToHashSet();
        Module.Functions.RemoveAll(dead.Contains);
        ResolveStaticLoads(staticLoads, summary!);
        if (fieldLoads) FinishFieldLoads(summary);
        StripWriteMarks();
    }

    /// <summary>Two ascending lists of positions as one, ascending.</summary>
    static IEnumerable<int> MergeAscending(List<int> a, List<int> b)
    {
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
            yield return j >= b.Count || i < a.Count && a[i] <= b[j] ? a[i++] : b[j++];
    }

    /// <summary>Threads: the functions from which a call chain reaches the function that moves a thread's epoch (a
    /// safe point), not counting a finalizer's call from a destroy function (between rt.fin_hold and rt.fin_unhold the
    /// thread keeps its epoch: Limbo.Hold). With the buckets of indirect calls that can, and whether one without a
    /// bucket can.</summary>
    (HashSet<WasmFunction> fns, HashSet<int> buckets, bool table) ReachesSafePoint(WasmFunction setEpoch)
    {
        var hold = Rt.FinHoldIfMade;
        var callers = new Dictionary<WasmFunction, List<WasmFunction>>();
        var bucketCallers = new Dictionary<int, List<WasmFunction>>();
        var tableCallers = new List<WasmFunction>();
        void Edge(WasmFunction from, WasmFunction to)
        {
            if (!callers.TryGetValue(to, out var l)) callers[to] = l = [];
            l.Add(from);
        }
        foreach (var f in Module.Functions)
        {
            if (f.ForwardTo != null) Edge(f, f.ForwardTo);
            bool held = false;
            foreach (var ins in f.Body)
            {
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g)
                {
                    if (g == hold) held = true;
                    else if (held) held = g != Rt.FinUnhold; // (the finalizer's call, then the unhold)
                    else Edge(f, g);
                }
                else if (ins.Op == Op.CallIndirect && !held)
                {
                    if (ins.Imm2 > 0 && ins.Imm2 < _bucketFns.Count) { if (!bucketCallers.TryGetValue((int)ins.Imm2, out var l)) bucketCallers[(int)ins.Imm2] = l = []; l.Add(f); }
                    else tableCallers.Add(f);
                }
            }
        }
        var bucketsOf = new Dictionary<WasmFunction, List<int>>();
        for (int bk = 1; bk < _bucketFns.Count; bk++)
            foreach (var t in IndirectTargets(new Instr { Op = Op.CallIndirect, Imm2 = bk }))
            {
                if (!bucketsOf.TryGetValue(t, out var l)) bucketsOf[t] = l = [];
                l.Add(bk);
            }
        var inTable = TableFns().ToHashSet();
        var seen = new HashSet<WasmFunction> { setEpoch };
        var buckets = new HashSet<int>();
        bool table = false;
        var work = new Stack<WasmFunction>(); work.Push(setEpoch);
        void Add(WasmFunction f) { if (seen.Add(f)) work.Push(f); }
        while (work.Count > 0)
        {
            var f = work.Pop();
            if (callers.TryGetValue(f, out var cs)) foreach (var c in cs) Add(c);
            if (bucketsOf.TryGetValue(f, out var bs))
                foreach (var bk in bs)
                    if (buckets.Add(bk) && bucketCallers.TryGetValue(bk, out var bc)) foreach (var c in bc) Add(c);
            if (!table && inTable.Contains(f)) { table = true; foreach (var c in tableCallers) Add(c); }
        }
        if (s_borrowDebug) Console.Error.WriteLine($"threads: {seen.Count} of {Module.Functions.Count} functions can reach a safe point");
        return (seen, buckets, table);
    }

    /// <summary>CS2WASM_REACH_WHY="from>>to" (diagnosis): the shortest chain of calls from a function whose name
    /// contains `from` to one whose name contains `to`.</summary>
    void PrintReachWhy()
    {
        if (Environment.GetEnvironmentVariable("CS2WASM_REACH_WHY") is not { } spec || !spec.Contains(">>")) return;
        var parts = spec.Split(">>");
        bool store = parts[1].StartsWith("store:");
        int found = 0;
        var root = Module.Functions.FirstOrDefault(f => f.Name.Contains(parts[0]));
        if (root == null) { Console.Error.WriteLine("reach: no function " + parts[0]); return; }
        var parent = new Dictionary<WasmFunction, WasmFunction?> { [root] = null };
        var work = new Queue<WasmFunction>(); work.Enqueue(root);
        while (work.Count > 0)
        {
            var f = work.Dequeue();
            // ("store:key": the first function that stores the key)
            if (f != root && (parts[1].StartsWith("store:") ? BorrowWrites.TryGetValue(f, out var fw) && fw.Contains(parts[1][6..]) : f.Name.Contains(parts[1])))
            {
                var chain = new List<string>();
                for (WasmFunction? x = f; x != null; x = parent[x]) chain.Add(x.Name);
                chain.Reverse();
                Console.Error.WriteLine("reach: " + string.Join("\n   -> ", chain));
                // (a store: every storing function the root reaches, not past one)
                if (!store || ++found >= 25) return;
                continue;
            }
            void Add(WasmFunction g) { if (!(store && StoreSummary.Internal(g)) && parent.TryAdd(g, f)) work.Enqueue(g); }
            if (f.ForwardTo != null) Add(f.ForwardTo);
            bool held = false; // (a finalizer's call from a destroy function is left out, as ReachesSafePoint does)
            foreach (var ins in f.Body)
            {
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g)
                {
                    if (g == Rt.FinHoldIfMade) held = true;
                    else if (held) held = g != Rt.FinUnhold;
                    else Add(g);
                }
                else if (ins.Op == Op.CallIndirect && !held) foreach (var t in IndirectTargets(ins)) Add(t);
            }
        }
        Console.Error.WriteLine($"reach: {parts[0]} does not reach {parts[1]}");
    }

    /// <summary>The static slots that code a worker thread can run names (reads, writes or takes the address of: every
    /// use of a static's storage is an instruction that refers to its slot). Called before the counts are resolved.</summary>
    internal HashSet<object> WorkerStaticSlots(Func<WasmFunction, bool>? lambdaRuns = null)
    {
        var r = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var f in WorkerReachable(lambdaRuns))
            foreach (var ins in f.Body)
                if (ins.Ref is StaticSlot s) r.Add(s);
        // (the table's functions are listed again when the borrows ask: functions may be made in between)
        _tableFns = null;
        _delegateBucketFns.Clear();
        return r;
    }
    /// <summary>The slot of a static field by its key (StaticKey); null when it has none.</summary>
    internal object? StaticSlotOf(string key) => _statics.TryGetValue("F:" + key, out var s) ? s : null;

    /// <summary>The functions a worker thread can run: what its start export (__thread_entry: the only export a worker
    /// instance is entered through) reaches by calls; an indirect call reaches every function in the table.
    /// With `lambdaRuns` (Sharing.Analyze): a function that only lambdas' delegates were made with (DelegatesMade,
    /// every site a lambda's) is reached by an indirect call only when it says so (a delegate of one of its sites, or
    /// any delegate of its type, can be reached by another thread); and a function whose address the code of a
    /// reached function takes is reached (the code that makes a delegate may call it, or hand it to what does).</summary>
    HashSet<WasmFunction> WorkerReachable(Func<WasmFunction, bool>? lambdaRuns = null)
    {
        var seen = new HashSet<WasmFunction>();
        var root = Module.Functions.FirstOrDefault(f => f.Exports.Contains("__thread_entry"));
        if (root == null) return seen;
        var buckets = new HashSet<int>();
        bool tableDone = false;
        // (CS2WASM_WORKER_WHY=name: the chain of calls by which a worker reaches the function)
        var why = Environment.GetEnvironmentVariable("CS2WASM_WORKER_WHY");
        var parent = why != null ? new Dictionary<WasmFunction, WasmFunction>() : null;
        var work = new Queue<WasmFunction>();
        WasmFunction cur = root;
        void Add(WasmFunction f) { if (seen.Add(f)) { work.Enqueue(f); parent?.TryAdd(f, cur); } }
        Add(root);
        while (work.Count > 0)
        {
            var f = cur = work.Dequeue();
            if (parent != null && f.Name.Contains(why!))
            {
                var chain = new List<string>();
                for (var x = f; x != root && chain.Count < 40; x = parent[x]) chain.Add(x.Name);
                Console.Error.WriteLine("worker reaches: " + string.Join("\n   <- ", chain));
            }
            if (f.ForwardTo != null) Add(f.ForwardTo);
            foreach (var ins in f.Body)
            {
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g) Add(g);
                else if (ins.Op == Op.CallIndirect)
                {
                    if (ins.Imm2 == 0) { if (tableDone) continue; tableDone = true; }
                    else if (!buckets.Add((int)ins.Imm2)) continue;
                    foreach (var t in IndirectTargets(ins))
                        if (lambdaRuns == null || t.TableRoot || !DelegatesMade.TryGetValue(t, out var made) || made.Any(x => x.site < 0) || lambdaRuns(t)) Add(t);
                }
                else if (lambdaRuns != null && ins.Op == Op.I32Const && ins.Ref is WasmFunction taken) Add(taken);
            }
        }
        return seen;
    }

    /// <summary>What each function may store, with everything it can call: bits over the fields some borrow asks about,
    /// unioned over the call graph's strongly connected components (callees first); an indirect call reaches every
    /// function in the table (one node stands for them).</summary>
    sealed class StoreSummary
    {
        // (Rt.StringOf, Rt.Prefix: a string they make, its characters)
        static readonly string[] s_internals = ["Cs2Wasm.Runtime.ThrowHelpers.", "Cs2Wasm.Runtime.Rt.StringOf(", "Cs2Wasm.Runtime.Rt.Prefix(", "Cs2Wasm.Runtime.Rt.Alloc", "Cs2Wasm.Runtime.Rt.Free(", "Cs2Wasm.Runtime.Rt.FreeObject(", "Cs2Wasm.Runtime.Rt.FreeObjectArena(", "Cs2Wasm.Runtime.Rt.FreeBlock(", "Cs2Wasm.Runtime.Rt.Arena", "Cs2Wasm.Runtime.ObjectMarks.",
            // (threads: the list of the thread's own that rt.destroy keeps the objects waiting for the outermost destruction in: memory from Rt.Alloc)
            "Cs2Wasm.Runtime.Rt.PendDestroy(", "Cs2Wasm.Runtime.Rt.NextPending(", "Cs2Wasm.Runtime.Rt.LocksHeld(",
            // (threads: the exchange of a store in its region, called by generated code where the store is, which notes the
            // write itself; besides that it takes the region's locks, words of the runtime's own)
            "Cs2Wasm.Runtime.Rt.RegionExchange("];
        public static bool Internal(WasmFunction f) => s_internals.Any(p => f.Name.StartsWith(p, StringComparison.Ordinal));

        readonly Dictionary<string, int> _key = [];
        readonly Dictionary<WasmFunction, int> _node = [];
        readonly ulong[][] _bits;

        public StoreSummary(Driver d, HashSet<string> queried)
        {
            foreach (var k in queried) _key[k] = _key.Count;
            // (the keys asked about with all their stores, by the field's own key)
            Dictionary<string, int>? all = null;
            foreach (var (k, ki) in _key) if (k.StartsWith(AllStoresPrefix, StringComparison.Ordinal)) (all ??= [])[k[AllStoresPrefix.Length..]] = ki;
            // (the fields a loop would read once, FieldLoads.cs: by number, after the keys)
            _hoistBase = _key.Count;
            int words = Math.Max(1, (_key.Count + d._hoistCount + 63) / 64);
            var fns = d.Module.Functions;
            for (int i = 0; i < fns.Count; i++) _node[fns[i]] = i;
            int table = fns.Count, n = fns.Count + 1 + d._bucketFns.Count;
            _table = table; _buckets = d._bucketFns.Count;
            // (an indirect call's node by its bucket and its type, past those: it reaches the functions of that type
            // only, IndirectTargets)
            if (!s_noIndirectTypes)
                foreach (var f in fns)
                    foreach (var ins in f.Body)
                        if (ins.Op == Op.CallIndirect && ins.Ref is FuncType sig)
                        {
                            int bk = ins.Imm2 > 0 && ins.Imm2 < d._bucketFns.Count ? (int)ins.Imm2 : 0;
                            if (!_typed.ContainsKey((bk, sig))) _typed[(bk, sig)] = n++;
                            // (FieldLoads.cs asks once the buckets are gone from the calls: by the type alone)
                            if (d._hoistCount > 0 && !_typed.ContainsKey((0, sig))) _typed[(0, sig)] = n++;
                        }
            // what writes only memory no program code can reach: the runtime's throw helpers (bounds, null and cast
            // checks: the exception they make), the allocator (fresh or freed blocks), the arenas' and the marks'
            // own tables (a finalizer a release runs still counts: it is compiled code the destroy path calls)
            var skip = new bool[n];
            for (int i = 0; i < fns.Count; i++) skip[i] = Internal(fns[i]);
            var edges = new List<int>[n];
            for (int i = 0; i < n; i++) edges[i] = [];
            // (a function that stores none of the keys shares one empty set, and a component whose stores are one
            // other's shares that one's: the sets are only read once made. Each function had a set of its own, twice,
            // nearly a gigabyte for the compiler compiling itself)
            var none = new ulong[words];
            _bits = new ulong[n][];
            for (int i = 0; i < n; i++) _bits[i] = none;
            for (int i = 0; i < fns.Count; i++)
            {
                var f = fns[i];
                for (int j = 0; j < f.Body.Count; j++)
                {
                    var ins = f.Body[j];
                    if (ins.Op == Op.Call && StoresOf(f.Body, j) is { } g && _node.TryGetValue(g, out int gi) && !skip[gi] && gi != i) edges[i].Add(gi);
                    else if (ins.Op == Op.CallIndirect) edges[i].Add(IndirectNode(ins, d._bucketFns.Count));
                    else if (ins.Op == Op.I32Const && ins.Ref is WasmFunction t && _node.TryGetValue(t, out int ti)) edges[table].Add(ti);
                }
                if (f.InTable || f.TableRoot) edges[table].Add(i);
                if (f.ForwardTo != null && _node.TryGetValue(f.ForwardTo, out int fi)) edges[i].Add(fi);
                // (a type's static initializer's own stores of the type's statics: no borrow of one of them sees them.
                // A borrow of a static began with a read of it, after the check that runs the initializer: from
                // then on the initializer has run, or is running further up this thread's stack (a check meanwhile
                // returns at once: Rt.CctorEnter), and whatever it stores next it stores after the borrow's scope.
                // Not what it calls: a helper that stores a static can be called again later.)
                string? ownStatics = f.Name.StartsWith(Driver.StaticInitBodyPrefix, StringComparison.Ordinal) ? f.Name[Driver.StaticInitBodyPrefix.Length..] + "::" : null;
                if (d.BorrowWrites.TryGetValue(f, out var w))
                    foreach (var k in w)
                        if (_key.TryGetValue(k, out int ki) && !(ownStatics != null && k.StartsWith(ownStatics, StringComparison.Ordinal) && d.IsStaticKey(k)))
                        {
                            if (ReferenceEquals(_bits[i], none)) _bits[i] = new ulong[words];
                            _bits[i][ki >> 6] |= 1UL << (ki & 63);
                        }
                if (d._hoistOwn != null && d._hoistOwn.TryGetValue(f, out var ho))
                    foreach (int id in ho)
                    {
                        int ki = _hoistBase + id - 1;
                        if (ReferenceEquals(_bits[i], none)) _bits[i] = new ulong[words];
                        _bits[i][ki >> 6] |= 1UL << (ki & 63);
                    }
                // (every store of a static a loop would read once, StaticLoads.cs: those that only replace a null too;
                // the initializer's own left out as above, the loop is asked about the type's initialization itself)
                if (all != null && d.FieldWrites.TryGetValue(f, out var fw))
                    foreach (var k in fw)
                        if (all.TryGetValue(k, out int ki) && !(ownStatics != null && k.StartsWith(ownStatics, StringComparison.Ordinal)))
                        {
                            if (ReferenceEquals(_bits[i], none)) _bits[i] = new ulong[words];
                            _bits[i][ki >> 6] |= 1UL << (ki & 63);
                        }
            }
            for (int bk = 1; bk < d._bucketFns.Count; bk++)
                foreach (var t in d.IndirectTargets(new Instr { Op = Op.CallIndirect, Imm2 = bk }))
                    if (_node.TryGetValue(t, out int ti)) edges[table + bk].Add(ti);
            foreach (var ((bk, sig), node) in _typed)
                foreach (var t in d.IndirectTargets(new Instr { Op = Op.CallIndirect, Imm2 = bk, Ref = sig }))
                    if (_node.TryGetValue(t, out int ti)) edges[node].Add(ti);
            // A throw's trace (Rt.OnThrow): it writes the exception's fields, and elements only of the buffer and the
            // string it makes, which nothing else refers to. So its elements key is left out: a borrow of an element
            // across a call that may throw survives it (a function's walk of its list, calling itself), and one of
            // the exception's trace fields does not. Its stores, with everything it calls, become its own.
            if (_key.TryGetValue(ElementsKey, out int ek))
                for (int i = 0; i < fns.Count; i++)
                {
                    if (!fns[i].Name.StartsWith("Cs2Wasm.Runtime.Rt.OnThrow(", StringComparison.Ordinal)) continue;
                    var seen = new HashSet<int> { i };
                    var work = new Stack<int>(); work.Push(i);
                    var acc = new ulong[words];
                    bool indirect = false;
                    while (work.Count > 0 && !indirect)
                    {
                        int v = work.Pop();
                        for (int k = 0; k < words; k++) acc[k] |= _bits[v][k];
                        foreach (var t in edges[v]) { if (t >= table) { indirect = true; break; } if (seen.Add(t)) work.Push(t); }
                    }
                    if (indirect) continue;
                    acc[ek >> 6] &= ~(1UL << (ek & 63));
                    _bits[i] = acc;
                    edges[i].Clear();
                }
            // Tarjan, iterative: components come out callees first
            var index = new int[n]; var low = new int[n]; var on = new bool[n];
            Array.Fill(index, -1);
            var stack = new Stack<int>(); int next = 0;
            var call = new Stack<(int v, int e)>();
            for (int s0 = 0; s0 < n; s0++)
            {
                if (index[s0] >= 0) continue;
                call.Push((s0, 0)); index[s0] = low[s0] = next++; stack.Push(s0); on[s0] = true;
                while (call.Count > 0)
                {
                    var (v, e) = call.Pop();
                    if (e < edges[v].Count)
                    {
                        call.Push((v, e + 1));
                        int w2 = edges[v][e];
                        if (index[w2] < 0) { index[w2] = low[w2] = next++; stack.Push(w2); on[w2] = true; call.Push((w2, 0)); }
                        else if (on[w2]) low[v] = Math.Min(low[v], index[w2]);
                        continue;
                    }
                    if (call.Count > 0) { var (p, _) = call.Peek(); low[p] = Math.Min(low[p], low[v]); }
                    if (low[v] != index[v]) continue;
                    var comp = new List<int>();
                    int x;
                    do { x = stack.Pop(); on[x] = false; comp.Add(x); } while (x != v);
                    // (the union of the members' sets and their callees': one of them as it is while it is the only one
                    // that is not empty, a new set once a second one adds to it)
                    ulong[]? acc = null; bool own = false;
                    void Union(ulong[] a)
                    {
                        if (ReferenceEquals(a, none) || ReferenceEquals(a, acc)) return;
                        if (acc == null) { acc = a; return; }
                        if (!own) { acc = (ulong[])acc.Clone(); own = true; }
                        for (int k = 0; k < words; k++) acc[k] |= a[k];
                    }
                    foreach (var c in comp)
                    {
                        Union(_bits[c]);
                        foreach (var t in edges[c]) if (!on[t]) Union(_bits[t]);
                    }
                    foreach (var c in comp) _bits[c] = acc ?? none;
                }
            }
        }

        /// <summary>Whether some function an indirect call can reach stores the field.</summary>
        /// <summary>Threads: the functions that can reach a safe point, the indirect-call buckets that can, and whether
        /// an indirect call without a bucket can (ReachesSafePoint); asked through SafePointKey.</summary>
        public HashSet<WasmFunction>? Safe;
        public HashSet<int>? SafeBuckets;
        public bool SafeTable;

        public bool HasIndirect(string key, in Instr call)
        {
            if (key == SafePointKey) return call.Imm2 > 0 ? SafeBuckets?.Contains((int)call.Imm2) == true : SafeTable;
            int node = IndirectNode(call, _buckets);
            return _key.TryGetValue(key, out int k) && (_bits[node][k >> 6] & (1UL << (k & 63))) != 0;
        }
        readonly int _table;
        int _buckets;
        readonly Dictionary<(int, FuncType), int> _typed = [];

        /// <summary>An indirect call's node: its bucket's and type's, else its bucket's, else the table's.</summary>
        int IndirectNode(in Instr call, int buckets)
        {
            int bk = call.Imm2 > 0 && call.Imm2 < buckets ? (int)call.Imm2 : 0;
            if (call.Ref is FuncType sig && _typed.TryGetValue((bk, sig), out int t)) return t;
            return bk > 0 ? _table + bk : _table;
        }

        readonly int _hoistBase;
        public bool Knows(WasmFunction f) => _node.ContainsKey(f);
        /// <summary>Whether f, with what it calls, stores the field with this number (FieldLoads.cs).</summary>
        public bool HasId(WasmFunction f, int id)
        {
            int k = _hoistBase + id - 1;
            return _node.TryGetValue(f, out int i) && (_bits[i][k >> 6] & (1UL << (k & 63))) != 0;
        }
        /// <summary>An indirect call of this type (null: any), whatever its bucket was.</summary>
        public bool HasIndirectId(FuncType? sig, int id)
        {
            int k = _hoistBase + id - 1;
            int node = sig != null && _typed.TryGetValue((0, sig), out int t) ? t : _table;
            return (_bits[node][k >> 6] & (1UL << (k & 63))) != 0;
        }

        public bool Has(WasmFunction f, string key) => key == SafePointKey ? Safe?.Contains(f) == true :
            _key.TryGetValue(key, out int k) && _node.TryGetValue(f, out int i) && (_bits[i][k >> 6] & (1UL << (k & 63))) != 0;
    }

    /// <summary>Fields reflection can set (kept for the program's reflection: FieldInfo.SetValue writes them).</summary>
    HashSet<string> ReflectedFieldKeys()
    {
        var r = new HashSet<string>();
        if (_reflect == null) return r;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
            foreach (var m in KeptMembers(t, all, names, ctors))
                if (m is IFieldSymbol f && !f.IsStatic) r.Add(FieldKey(Types.Canonical(f.ContainingType), f));
        return r;
    }
}
