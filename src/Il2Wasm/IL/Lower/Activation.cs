#nullable enable
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// Activator.CreateInstance<T>() and CreateInstance(Type[, nonPublic]) (SPEC 6.6) for the types the module keeps: the
// IL CoreLib's RuntimeType.CreateInstanceOfT, CreateInstanceMono and CallDefaultStructConstructor (which need
// reflection's constructor records) are served by generated functions, rt.create_instance(type id, nonPublic, wrap)
// and rt.struct_ctor(type id, address, wrap), switches over the types the program can name at run time: those it
// constructs and those it takes a Type object of (typeof; CreateInstance<T> is typeof(T) inside). Each case does what
// CoreCLR does: a class's parameterless constructor (a non-public one only with nonPublic), a struct's (or its zero
// value), null for a Nullable; MissingMethodException with CoreCLR's messages for an abstract class, an interface,
// string, a class without one (ThrowHelpers.CannotCreate); a constructor's exception wrapped in
// TargetInvocationException unless the caller asks it not to be (wrap).
public sealed partial class IlCompiler
{
    readonly HashSet<TypeDesc> _typeOfs = [];
    readonly List<TypeDesc> _typeOfOrder = [];
    readonly HashSet<TypeDesc> _activationSeen = [];
    readonly List<TypeDesc> _activationOrder = [];
    WasmFunction? _createInstance, _structCtor;

    /// <summary>The program takes the Type object of t (typeof): Activator can be asked for one.</summary>
    internal void NoteTypeOf(TypeDesc t) { if (_typeOfs.Add(t)) _typeOfOrder.Add(t); }

    /// <summary>rt.create_instance(type id, nonPublic, wrap) -> the new object (its body when the program is known).</summary>
    WasmFunction CreateInstanceFn => _createInstance ??= Module.AddFunction("rt.create_instance", new FuncType([ValType.I32, ValType.I32, ValType.I32], [ValType.I32]));
    /// <summary>rt.struct_ctor(type id, address, wrap): a struct's parameterless constructor on the value at address.</summary>
    WasmFunction StructCtorFn => _structCtor ??= Module.AddFunction("rt.struct_ctor", new FuncType([ValType.I32, ValType.I32, ValType.I32], []));

    /// <summary>RuntimeType.CreateInstanceOfT (this), CreateInstanceMono (this, nonPublic, wrap) and
    /// CallDefaultStructConstructor (this, ref value).</summary>
    internal bool TryActivation(Emitter e, MethodDesc m, Node n, FunctionLowering fl)
    {
        if (m.OwningType is not MetadataType rtt || rtt.Module != _ctx.SystemModule || rtt.GetName() != "RuntimeType" || rtt.GetNamespace() != "System") return false;
        var name = m.GetName();
        if (!(name == "CreateInstanceOfT" && m.Signature.Length == 0 || name == "CreateInstanceMono" && m.Signature.Length == 2
              || name == "CallDefaultStructConstructor" && m.Signature.Length == 1)) return false;
        var typeId = rtt.GetMethods().First(x => x.GetName() == "get_TypeId");
        fl.PushArgs(n);
        int a = e.AddLocal(ValType.I32), b = e.AddLocal(ValType.I32);
        _ = CreateInstanceFn; _ = StructCtorFn;
        switch (name)
        {
            case "CreateInstanceOfT": e.Call(FunctionFor(typeId)); e.I32(0); e.I32(1); e.Call(CreateInstanceFn); break;
            case "CreateInstanceMono": e.Set(b); e.Set(a); e.Call(FunctionFor(typeId)); e.Get(a); e.Get(b); e.Call(CreateInstanceFn); break;
            default: e.Set(a); e.Call(FunctionFor(typeId)); e.Get(a); e.I32(1); e.Call(StructCtorFn); break;
        }
        fl.SetResult(n);
        return true;
    }

    static bool IsPublic(MethodDesc m) => m.GetTypicalMethodDefinition() is EcmaMethod em && (em.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) == System.Reflection.MethodAttributes.Public;

    /// <summary>A type's parameterless instance constructor, public or not (the type system's GetDefaultConstructor
    /// finds only a public one).</summary>
    static MethodDesc? ParameterlessCtor(DefType t)
    {
        foreach (var m in t.GetMethods())
            if (m.IsConstructor && !m.Signature.IsStatic && m.Signature.Length == 0) return m;
        return null;
    }

    /// <summary>What CreateInstance does for t: 0 a constructor (or a struct's zero value), 1 abstract, 2 interface,
    /// 3 a non-public constructor, 4 string, 5 null (a Nullable), 6 no parameterless constructor.</summary>
    static (int how, MethodDesc? ctor) ActivationOf(TypeDesc t)
    {
        if (t.IsString) return (4, null);
        if (t.IsInterface) return (2, null);
        if (t.IsNullable) return (5, null);
        if (t is not DefType dt || t.IsArray || t.IsPointer || t.IsByRef || t.IsFunctionPointer || t.IsGenericDefinition || t.ContainsSignatureVariables()) return (6, null);
        if (t.IsByRefLike) return (6, null);
        var ctor = ParameterlessCtor(dt);
        if (t.IsValueType) return (0, ctor);
        if (dt is MetadataType { IsAbstract: true }) return (1, null);
        if (t.IsDelegate || ctor == null) return (6, null);
        return (!IsPublic(ctor) ? 3 : 0, ctor);
    }

    IEnumerable<TypeDesc> ActivationCandidates() => _typeOfOrder.Concat(_constructedOrder).Where(t => !t.IsCanonicalSubtype(CanonicalFormKind.Any)).Distinct();

    /// <summary>Once Activator is reached: each candidate's constructor compiled (ResolveDispatch's fixed point).</summary>
    bool RequestActivation()
    {
        if (_createInstance == null) return false;
        bool any = false;
        foreach (var t in ActivationCandidates().ToList())
        {
            if (!_activationSeen.Add(t)) continue;
            _activationOrder.Add(t);
            var (how, ctor) = ActivationOf(t);
            if (how is 0 or 3)
            {
                if (!_constructed.Contains(t)) { Constructed(t); any = true; }
                if (ctor != null && !_functions.ContainsKey(ctor)) { FunctionFor(ctor); any = true; }
            }
        }
        if (TryCoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "CannotCreate") is { } cc && !_functions.ContainsKey(cc)) { FunctionFor(cc); any = true; }
        if (TryCoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "InvocationFailed") is { } inv && !_functions.ContainsKey(inv)) { FunctionFor(inv); any = true; }
        return any;
    }

    void EmitCreateInstance(System.Func<TypeDesc, int> final)
    {
        if (_createInstance == null) return;
        var cannot = FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "CannotCreate"));
        var invocation = FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "InvocationFailed"));
        {
            var e = new Emitter(_createInstance);
            int o = e.AddLocal(ValType.I32), ex = e.AddLocal(ValType.I32);
            foreach (var t in _activationOrder)
            {
                var (how, ctor) = ActivationOf(t);
                if (how == 6) continue;
                e.Get(0); e.I32(final(t)); e.Op(WOp.I32Eq); e.If();
                if (how is 1 or 2 or 4) { e.Get(0); e.I32(how); e.Call(cannot); e.Op(WOp.Unreachable); e.End(); continue; }
                if (how == 5) { e.I32(0); e.Op(WOp.Return); e.End(); continue; }
                // (a non-public constructor: only with nonPublic)
                if (ctor != null && !IsPublic(ctor)) { e.Get(1); e.Op(WOp.I32Eqz); e.If(); e.Get(0); e.I32(3); e.Call(cannot); e.End(); }
                int size = t.IsValueType ? IlContext.ObjectHeader + System.Math.Max(4, Kinds.SizeOf(t)) : IlContext.InstanceSize((DefType)t);
                e.I32(size); e.Tid(TypeId(t)); e.Call(Rt.AllocObject); e.Set(o);
                if (ctor != null && t.IsValueType) { e.Get(0); e.Get(o); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Get(2); e.Call(StructCtorFn); }
                else if (ctor != null) EmitCtorCall(e, ctor, o, ex, invocation, () => { e.Get(o); e.Call(Rt.Release); });
                e.Get(o); e.Op(WOp.Return);
                e.End();
            }
            e.Get(0); e.I32(6); e.Call(cannot);
            e.Op(WOp.Unreachable);
        }
        {
            var e = new Emitter(StructCtorFn);
            int ex = e.AddLocal(ValType.I32);
            foreach (var t in _activationOrder)
            {
                if (!t.IsValueType || ActivationOf(t) is not (0, { } ctor)) continue;
                e.Get(0); e.I32(final(t)); e.Op(WOp.I32Eq); e.If();
                EmitCtorCall(e, ctor, 1, ex, invocation, null);
                e.Op(WOp.Return);
                e.End();
            }
        }
    }

    /// <summary>The constructor on `self` (a local: the object, or the struct's address); its exception, after `fail`
    /// (the object let go), wrapped in TargetInvocationException when local 2 (wrap) is set, and thrown.</summary>
    void EmitCtorCall(Emitter e, MethodDesc ctor, int self, int ex, WasmFunction invocation, System.Action? fail)
    {
        var done = e.Block();
        var caught = e.Block(ValType.I32);
        e.TryTable(null, (0, ExceptionTag, caught));
        e.Get(self);
        e.Call(FunctionFor(ctor));
        e.Br(done);
        e.End();
        e.Op(WOp.Unreachable);
        e.End();
        e.Set(ex);
        fail?.Invoke();
        e.Get(2);
        e.If(); e.Get(ex); e.Call(invocation); e.Get(ex); e.Call(Rt.Release); e.Set(ex); e.End();
        e.Get(ex); EmitThrow(e);
        e.End();
    }
}
