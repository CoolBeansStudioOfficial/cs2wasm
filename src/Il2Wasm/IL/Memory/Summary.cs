#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Reach;
using Internal.IL;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>A method as the reach phase left it for lowering (section 1.2): its IL, its body (the import after the
/// constant propagation's facts), its local summary, or why there is none: no IL (it traps), an ILLink substitution
/// (the stub is its body), or the import's exception, rethrown where lowering reaches the method (so a failure is
/// reported exactly where and as it was before the phase split).</summary>
public sealed class ImportedMethod
{
    public readonly MethodDesc Method;
    public MethodIL? Il;
    public Body? Body;
    public MethodSummary? Summary;
    /// <summary>An ILLink substitution's constant (or a void stub): lowering emits it, nothing is imported.</summary>
    public bool Substituted;
    public ExceptionDispatchInfo? Error;
    /// <summary>The error came before the import (reading the method's IL): lowering rethrows it before anything
    /// else, as it was thrown before.</summary>
    public bool ErrorBeforeImport;
    /// <summary>The constant propagation's decisions the body was imported with (null: none, or it is off): with the
    /// IL they give the same body again (import is deterministic), so the reach phase can let a body go and lowering
    /// import it again (IL2WASM_MEM_REIMPORT=1).</summary>
    public Il2Wasm.IL.Ir.ImportFacts? Facts;
    public ImportedMethod(MethodDesc m) { Method = m; }

    /// <summary>The body for lowering: the one kept, or the same one imported again from the IL and the decisions.</summary>
    public Body? TakeBody()
    {
        var b = Body ?? (Il != null && Error == null && !Substituted ? Importer.Import(Method, Il, Facts) : null);
        Body = null;
        return b;
    }
}

/// <summary>What the reach phase asks of the compiler: whether lowering expands a call inline (an intrinsic: its IL
/// is not what runs, so it is no edge), the method a string constructor maps to, and a type's extra edges (its
/// finalizer).</summary>
public interface IReachHost
{
    /// <summary>The method, imported (or why not); null when lowering makes its function without IL (an import, a
    /// P/Invoke, an array's method, a delegate's Invoke): nothing to import.</summary>
    ImportedMethod? Import(MethodDesc m);
    /// <summary>Lowering expands a call to this method itself (an intrinsic of il2wasm's or upstream's).</summary>
    bool LoweredInline(MethodDesc m);
    /// <summary>The method a constructor call becomes (string's static Ctor), or the constructor itself.</summary>
    MethodDesc ConstructorTarget(MethodDesc ctor);
    /// <summary>The methods a constructed type reaches by itself (its finalizer).</summary>
    IEnumerable<MethodDesc> TypeEdges(TypeDesc constructed);
    /// <summary>The implementation of a dispatch slot on an exact type (the compiler's dispatch).</summary>
    MethodDesc? Resolve(TypeDesc exact, MethodDesc slot);
}

/// <summary>A method's local transfer summary (section 2.8, the local part; section 3.2's transfer functions): built
/// once from one body, with no whole-program facts and no shared state, so summaries can be built in any order or in
/// parallel and the solver sees the same input. Three parts:
/// <list type="bullet">
/// <item>the constant propagation's facts today's rounds record (ProgramFacts.Merge applies them, in node order, when
/// lowering reaches the method: the same order as before the phase split);</item>
/// <item>the reach edges of the body lowering lowers (calls, dispatch slots, constructed types, initializers);</item>
/// <item>the memory facts: the keys stored, the calls whose effects are not known here, throw and return.</item>
/// </list></summary>
public sealed class MethodSummary
{
    public readonly MethodDesc Method;
    public MethodSummary(MethodDesc m) { Method = m; }

    // ---- the constant propagation's facts (built from the first import and its Sccp, as ProgramFacts.Record did)
    /// <summary>The direct callees the constant propagation saw (ProgramFacts.IsDirectCallee).</summary>
    public readonly HashSet<MethodDesc> Direct = [];
    /// <summary>Each direct call's or constructor's argument values, in node order.</summary>
    public readonly List<(MethodDesc Callee, Val[] Args)> ArgMeets = [];
    /// <summary>Instance field stores (value) and field addresses (Bottom, Address true), in node order.</summary>
    public readonly List<(FieldDesc Field, Val Value, bool Address)> FieldStores = [];
    /// <summary>The value at every return that can run (null: void or opaque, nothing recorded).</summary>
    public Val? Return;
    public readonly List<FieldDesc> StaticsWritten = [];
    public readonly List<TypeDesc> TypeObjects = [];
    public readonly List<MethodDesc> FunctionPointers = [];
    public readonly List<TypeDesc> ArrayElementTypes = [];
    public readonly List<TypeDesc> Aliased = [];

    // ---- reach edges (from the body lowering lowers)
    /// <summary>Direct calls, constructors and function pointers lowering makes a function for.</summary>
    public readonly List<MethodDesc> Calls = [];
    /// <summary>Virtual and interface slots called (each a dispatch over the constructed types).</summary>
    public readonly List<MethodDesc> Slots = [];
    /// <summary>Each virtual or interface call's slot with its receiver's static type (null: not a reference, or none),
    /// in node order: the targets are the slot's implementations over the constructed types the receiver admits.</summary>
    public readonly List<(MethodDesc Slot, TypeDesc? Receiver, int NodeId)> SlotCalls = [];
    public readonly List<TypeDesc> Constructs = [];
    /// <summary>Types whose static constructor a use may start.</summary>
    public readonly List<TypeDesc> Initializes = [];

    // ---- memory (the local half of Mods, 2.8; the solver closes it over the call graph)
    /// <summary>The keys the body stores to directly (fields, elements, statics, slots through a ref; never its own
    /// frame variables).</summary>
    public readonly List<MemKey> Stores = [];
    /// <summary>A store through a raw pointer, a block copy or fill: any key.</summary>
    public bool WildStores;
    /// <summary>A call whose target is not known here (calli, a delegate's Invoke, an import, a P/Invoke).</summary>
    public bool CallsUnknown;
    /// <summary>Calls lowering expands itself (intrinsics): their effects come from the intrinsic table.</summary>
    public readonly List<MethodDesc> Intrinsics = [];
    /// <summary>The intrinsics that write only through their first argument, a ref (Interlocked.Exchange,
    /// CompareExchange, Atomic.*: IntrinsicSignatures' RefSlot), every call of which here passes the address of a known
    /// field, static or element: the keys they write are in <see cref="Stores"/>, not any slot of the type.</summary>
    public readonly HashSet<MethodDesc> IntrinsicsByAddress = [];
    public bool MayThrow, MayReturn, ObservesLifetime;

    /// <summary>Builds the summary of a method. <paramref name="first"/> and <paramref name="sccp"/> are the first
    /// import and its constant propagation (null when it is off: no constant facts); <paramref name="body"/> is the
    /// body lowering lowers.</summary>
    public static MethodSummary Build(MethodDesc m, Body? first, Sccp? sccp, Body body, IReachHost host)
    {
        var s = FromConstants(m, first, sccp);
        s.AddEdges(body, host);
        return s;
    }

    /// <summary>The first part alone (the import after it may still fail: what the constant propagation saw is
    /// recorded all the same, as before the phase split).</summary>
    public static MethodSummary FromConstants(MethodDesc m, Body? first, Sccp? sccp)
    {
        var s = new MethodSummary(m);
        if (first != null && sccp != null) s.ConstantFacts(first, sccp);
        return s;
    }

    /// <summary>The reach edges and memory facts of the body lowering lowers.</summary>
    public void AddEdges(Body body, IReachHost host) => Edges(body, host);

    void ConstantFacts(Body body, Sccp s)
    {
        var ret = Val.Top;
        foreach (var b in s.ExecutableBlocks)
            foreach (var n in b.Code)
                switch (n.Op)
                {
                    case Opc.StFld when n.X is FieldDesc sf:
                        FieldStores.Add((sf, s.ValueOf(n.A[1]), false));
                        break;
                    case Opc.LdFldA when n.X is FieldDesc af:
                        FieldStores.Add((af, Val.Bottom, true));
                        break;
                    case Opc.Ret when n.A.Length > 0:
                        ret = Val.Meet(ret, s.ValueOf(n.A[0]));
                        break;
                    case Opc.StSFld: case Opc.LdSFldA:
                        if (n.X is FieldDesc f) StaticsWritten.Add(f);
                        break;
                    case Opc.TypeOf: case Opc.LdToken: case Opc.MkRefAny: // (__reftype gives a TypedReference's type object)
                        if (n.X is TypeDesc tt) TypeObjects.Add(tt);
                        break;
                    case Opc.LdFtn: case Opc.LdVirtFtn:
                        if (n.X is MethodDesc fm) FunctionPointers.Add(fm);
                        break;
                    case Opc.Call when n.X is MethodDesc am && am.GetName() is "CreateInstance" or "MakeArrayType" && n.A.Length > 0 && n.A[0].Op == Opc.TypeOf && n.A[0].X is TypeDesc et
                        && am.OwningType is MetadataType { Name: var _ } amt && amt.GetNamespace() == "System" && amt.GetName() is "Array" or "Type" or "RuntimeType" or "Activator":
                        // (an array made at run time of a type the code names: Array.CreateInstance(typeof(X), n), typeof(X).MakeArrayType())
                        ArrayElementTypes.Add(et);
                        if ((CallKind)n.Sub == CallKind.Direct) { Direct.Add(am); ArgMeets.Add((am, n.A.Select(s.ValueOf).ToArray())); }
                        break;
                    case Opc.Call when (CallKind)n.Sub == CallKind.Direct && n.X is MethodDesc cm:
                        // (a removed call is never reached, but its arguments only weaken the meet)
                        Direct.Add(cm);
                        ArgMeets.Add((cm, n.A.Select(s.ValueOf).ToArray()));
                        if (cm.HasInstantiation && cm.GetName() == "As" && cm.OwningType is MetadataType { Name: var _ } ut && ut.GetName() == "Unsafe")
                            for (int k = 0; k < cm.Instantiation.Length; k++) Aliased.Add(cm.Instantiation[k]);
                        break;
                    case Opc.NewObj when n.X is MethodDesc ctor:
                        Direct.Add(ctor);
                        ArgMeets.Add((ctor, [Val.NonNull, .. n.A.Select(s.ValueOf)]));
                        break;
                }
        if (!body.Method.Signature.ReturnType.IsVoid && !ProgramFacts.Opaque(body.Method)) Return = ret;
    }

    void Edges(Body body, IReachHost host)
    {
        var owner = body.Method.OwningType;
        foreach (var b in body.Blocks)
            foreach (var n in b.Code)
                switch (n.Op)
                {
                    case Opc.Call when n.X is MethodDesc m:
                        if (host.LoweredInline(m))
                        {
                            Intrinsics.Add(m); MayThrow = true;
                            if (IntrinsicSignatures.WritesThroughFirstRef(m))
                            {
                                if (n.A.Length > 0 && AddressKey(n.A[0]) is { } ak) { Stores.Add(ak); if (!_unaddressed.Contains(m)) IntrinsicsByAddress.Add(m); }
                                else { _unaddressed.Add(m); IntrinsicsByAddress.Remove(m); }
                            }
                            // (Generics.New<T..>("Name`n"): lowering allocates Name<T..> and calls its default
                            // constructor, IlCompiler.cs: a constructed type and a call edge like a newobj's)
                            if (NewByName(m, n) is { } made) { Constructs.Add(made.OwningType); Calls.Add(made); }
                            break;
                        }
                        MayThrow = true;
                        if (Il2Wasm.IL.Lower.IlCompiler.IsDelegateInvoke(m) && m.GetTypicalMethodDefinition() is Internal.TypeSystem.Ecma.EcmaMethod { IsRuntimeImplemented: true }) { CallsUnknown = true; break; }
                        if ((CallKind)n.Sub == CallKind.Direct)
                        {
                            Calls.Add(m);
                            if (!m.IsStaticConstructor && (m.Signature.IsStatic || m.IsConstructor || m.OwningType.IsValueType) && m.OwningType is MetadataType { IsBeforeFieldInit: false } it) Initializes.Add(it);
                        }
                        else { Slots.Add(m); SlotCalls.Add((m, n.A.Length > 0 && n.A[0].Kind == VK.Ref ? n.A[0].Type : null, n.Id)); }
                        if (m.OwningType is MetadataType gc && gc.GetName() == "GC" && gc.GetNamespace() == "System" && m.GetName() == "KeepAlive") ObservesLifetime = true;
                        break;
                    case Opc.CallI:
                        CallsUnknown = true; MayThrow = true;
                        break;
                    case Opc.NewObj when n.X is MethodDesc ctor:
                    {
                        MayThrow = true;
                        if (ctor.OwningType is ArrayType) { Constructs.Add(ctor.OwningType); break; }
                        var t = ctor.OwningType;
                        if (t.IsDelegate && ctor.Signature.Length == 2 && n.A[1].Op is Opc.LdFtn or Opc.LdVirtFtn) { Constructs.Add(t); break; }
                        if (!t.IsValueType) Constructs.Add(t);
                        var target = host.ConstructorTarget(ctor);
                        Calls.Add(target);
                        if (t is MetadataType { IsBeforeFieldInit: false } ct && !t.IsString) Initializes.Add(ct);
                        break;
                    }
                    case Opc.LdFtn when n.X is MethodDesc fm:
                        Calls.Add(fm);
                        break;
                    case Opc.LdVirtFtn when n.X is MethodDesc vm:
                        if (vm.IsVirtual && !vm.IsFinal) { Slots.Add(vm); SlotCalls.Add((vm, n.A.Length > 0 && n.A[0].Kind == VK.Ref ? n.A[0].Type : null, -1)); } else Calls.Add(vm);
                        break;
                    case Opc.NewArr when n.Type is ArrayType at:
                        Constructs.Add(at); MayThrow = true;
                        break;
                    case Opc.Box when n.X is TypeDesc bt:
                        Constructs.Add(bt.IsNullable ? bt.Instantiation[0] : bt);
                        break;
                    case Opc.LdSFld or Opc.LdSFldA when n.X is FieldDesc sf:
                        if (sf.OwningType != owner) Initializes.Add(sf.OwningType);
                        break;
                    case Opc.StSFld when n.X is FieldDesc sf:
                        Stores.Add(MemKey.Static(sf));
                        if (sf.OwningType != owner) Initializes.Add(sf.OwningType);
                        break;
                    case Opc.StFld when n.X is FieldDesc f:
                        Stores.Add(f.IsStatic ? MemKey.Static(f) : MemKey.Field(f));
                        break;
                    case Opc.StElem:
                        Stores.Add(n.A[0].Type is ArrayType sat ? MemKey.Elem(sat.ElementType) : MemKey.Slot(n.A[2].Type ?? body.Method.Context.GetWellKnownType(WellKnownType.Object)));
                        MayThrow = true;
                        break;
                    case Opc.StInd: case Opc.InitObj:
                        AddressStore(body, n.A[0], n.X as TypeDesc ?? n.A.ElementAtOrDefault(1)?.Type);
                        break;
                    case Opc.CpBlk: case Opc.InitBlk:
                        WildStores = true;
                        break;
                    case Opc.Throw: case Opc.Rethrow:
                        MayThrow = true;
                        break;
                    case Opc.Ret:
                        MayReturn = true;
                        break;
                    case Opc.CastClass: case Opc.UnboxAny: case Opc.Unbox: case Opc.LdElem: case Opc.LdElemA: case Opc.LdLen: case Opc.LdFld: case Opc.CheckFinite:
                        MayThrow = true;
                        break;
                }
    }

    /// <summary>The default constructor Generics.New or NewOfNullableArg calls (the type named by its literal over the
    /// call's type arguments, as IlCompiler's lowering makes it), or null (no literal, no such type or constructor).</summary>
    /// <summary>A Generics.New or NewOfNullableArg call that lowering makes a trap (IlCompiler.cs: NewOfNullableArg of a
    /// type argument that is not a Nullable, a type with no default constructor): it never produces a value.</summary>
    internal static bool NewTraps(MethodDesc m, Node n) => IsNew(m) && NewByName(m, n) == null && n.A.Length > 0 && n.A[0].Op == Opc.LdStr
        && (m.GetName() == "NewOfNullableArg" && !m.Instantiation[0].IsNullable || NewDefinition(m, n) is { } gd && gd.Instantiation.Length == (m.GetName() == "NewOfNullableArg" ? 1 : m.Instantiation.Length));

    static bool IsNew(MethodDesc m) => m.GetName() is "New" or "NewOfNullableArg" && m.OwningType is MetadataType g && g.GetName() == "Generics" && g.GetNamespace() == "Il2Wasm.Runtime";

    static MetadataType? NewDefinition(MethodDesc m, Node n)
    {
        if (n.A.Length == 0 || n.A[0].Op != Opc.LdStr || n.A[0].X is not string name || name.LastIndexOf('.') is not (> 0 and var dot)) return null;
        return m.Context.SystemModule.GetType(System.Text.Encoding.UTF8.GetBytes(name[..dot]), System.Text.Encoding.UTF8.GetBytes(name[(dot + 1)..]), throwIfNotFound: false) as MetadataType;
    }

    internal static MethodDesc? NewByName(MethodDesc m, Node n)
    {
        if (m.GetName() is not ("New" or "NewOfNullableArg") || m.OwningType is not MetadataType g || g.GetName() != "Generics" || g.GetNamespace() != "Il2Wasm.Runtime") return null;
        if (n.A.Length == 0 || n.A[0].Op != Opc.LdStr || n.A[0].X is not string name || name.LastIndexOf('.') is not (> 0 and var dot)) return null;
        if (m.Context.SystemModule.GetType(System.Text.Encoding.UTF8.GetBytes(name[..dot]), System.Text.Encoding.UTF8.GetBytes(name[(dot + 1)..]), throwIfNotFound: false) is not MetadataType gd) return null;
        var args = Enumerable.Range(0, m.Instantiation.Length).Select(i => m.Instantiation[i]).ToArray();
        if (m.GetName() == "NewOfNullableArg") { if (!args[0].IsNullable) return null; args = [args[0].Instantiation[0]]; }
        if (args.Length != gd.Instantiation.Length) return null;
        try { return ((DefType)gd.MakeInstantiatedType(args)).GetDefaultConstructor(); }
        catch (Exception) { return null; }
    }

    readonly HashSet<MethodDesc> _unaddressed = [];

    /// <summary>The key an address names when it is a field's, a static's or an element's (RefStores' rule), or null.</summary>
    internal static MemKey? AddressKey(Node addr) => addr.Op switch
    {
        Opc.LdFldA when addr.X is FieldDesc f => f.IsStatic ? MemKey.Static(f) : MemKey.Field(f),
        Opc.LdSFldA when addr.X is FieldDesc sf => MemKey.Static(sf),
        Opc.LdElemA when addr.A.Length > 0 && addr.A[0].Type is ArrayType at => MemKey.Elem(at.ElementType),
        _ => null,
    };

    /// <summary>A store through an address: the key it names (RefStores' rule: a field's, an element's or a static's
    /// address names that key; any other ref T names every slot of type T; a raw pointer names anything).</summary>
    void AddressStore(Body body, Node addr, TypeDesc? stored)
    {
        switch (addr.Op)
        {
            case Opc.LdFldA when addr.X is FieldDesc f: Stores.Add(f.IsStatic ? MemKey.Static(f) : MemKey.Field(f)); return;
            case Opc.LdSFldA when addr.X is FieldDesc sf: Stores.Add(MemKey.Static(sf)); return;
            case Opc.LdElemA when addr.A.Length > 0 && addr.A[0].Type is ArrayType at: Stores.Add(MemKey.Elem(at.ElementType)); return;
            case Opc.LocA: return; // (the method's own frame variable)
        }
        if (addr.Type is ByRefType br) { Stores.Add(MemKey.Slot(br.ParameterType)); return; }
        if (stored != null && addr.Kind == VK.Ptr && addr.Type is not PointerType && addr.Type?.IsPointer != true && addr.Op is Opc.Arg) { Stores.Add(MemKey.Slot(stored)); return; }
        WildStores = true;
    }
}
