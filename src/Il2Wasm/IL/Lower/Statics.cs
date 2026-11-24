#nullable enable
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// Static constructors with .NET's rules (SPEC 3), as the operation-tree front end made them (Driver.StaticInit):
//   - triggers: a type without beforefieldinit runs its .cctor before the first access to a static field, the first
//     call of a static method, of a constructor (newobj, and a derived class's call of its base's), or of a value
//     type's instance method; a beforefieldinit type before the first access to a static field;
//   - each type has a state word: 0 not started, -1 started or done (a check made by the initializer's own thread
//     while it runs passes, as in .NET: the statics as they are so far), -2 failed;
//   - an exception out of the .cctor becomes a TypeInitializationException (ThrowHelpers.TypeInitFailed), thrown
//     now and again, the same object, at every later access, as CoreCLR does.
// The check at an access is the back end's shape (i32.const flag; i32.load; i32.const -1; i32.ne; if; call
// "static init T"; end) so Wasm/InitChecks.cs removes the ones that follow another.
// Threads: the IL front end compiles one thread today (Rt.ThreadMode 0). The threaded protocol (the word holds the
// running thread's id, other threads wait, a wait that would close a cycle goes on: RuntimeFns2.EmitCctorSupport)
// comes with threads; it is not here yet.
public sealed partial class IlCompiler
{
    WasmFunction? _cctorFailed, _cctorCheck;

    /// <summary>The type's static constructor runs before this access, once.</summary>
    internal void CctorCheck(Emitter e, TypeDesc t)
    {
        if (t.GetStaticConstructor() is not { } cctor) return;
        if (!_init.TryGetValue(t, out var f))
        {
            f = Module.AddFunction("static init " + t, new FuncType([], []));
            _init[t] = f;
            EmitInit(new Emitter(f), t, cctor);
        }
        e.I32Addr(InitFlag(t)); e.Load(WOp.I32Load); e.I32(-1); e.Op(WOp.I32Ne); e.If(); e.Call(f); e.End();
    }

    /// <summary>A call that triggers the initializer of a type without beforefieldinit: a static method, a constructor,
    /// a value type's instance method (ECMA-335 II.10.5.3.1).</summary>
    internal void CctorCheckForCall(Emitter e, MethodDesc m)
    {
        if (m.IsStaticConstructor) return;
        if (!(m.Signature.IsStatic || m.IsConstructor || m.OwningType.IsValueType)) return;
        if (m.OwningType is MetadataType { IsBeforeFieldInit: false } t) CctorCheck(e, t);
    }

    /// <summary>The name TypeInitializationException's message gives (a nested type by its own name, as the
    /// operation-tree front end measured against .NET).</summary>
    static string InitName(TypeDesc t)
    {
        var def = (MetadataType)t.GetTypeDefinition();
        if (def.ContainingType != null) return def.GetName();
        return def.GetNamespace().Length > 0 ? def.GetNamespace() + "." + def.GetName() : def.GetName();
    }

    void EmitInit(Emitter e, TypeDesc t, MethodDesc cctor)
    {
        var flag = InitFlag(t);
        var saved = NamedStatic("tie:" + t, 4, 4);
        if (_cctorFailed == null)
        {
            // (flag, saved, name, inner exception: owned): wraps it, keeps the wrapper for later accesses, throws it
            _cctorFailed = Module.AddFunction("rt.cctor_failed", new FuncType([ValType.I32, ValType.I32, ValType.I32, ValType.I32], []));
            var f = new Emitter(_cctorFailed);
            // (a core library without the wrapper, the tests' stub: the inner exception itself)
            if (TryCoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "TypeInitFailed") is { } wrap)
            {
                f.Get(1); f.Get(2); f.Get(3);
                f.Call(FunctionFor(wrap));
                f.Store(WOp.I32Store);
                f.Get(3); f.Call(Rt.Release);
            }
            else { f.Get(1); f.Get(3); f.Store(WOp.I32Store); }
            f.Get(0); f.I32(-2); f.Store(WOp.I32Store);
            f.Get(1); f.Load(WOp.I32Load); f.Call(Rt.Retain);
            EmitThrow(f);
            // (flag, saved): the type's initializer failed before: its exception again
            _cctorCheck = Module.AddFunction("rt.cctor_check", new FuncType([ValType.I32, ValType.I32], []));
            var c = new Emitter(_cctorCheck);
            c.Get(0); c.Load(WOp.I32Load); c.I32(-2); c.Op(WOp.I32Eq);
            c.If(); c.Get(1); c.Load(WOp.I32Load); c.Call(Rt.Retain); EmitThrow(c); c.End();
        }
        e.I32Addr(flag); e.Load(WOp.I32Load); e.Op(WOp.I32Eqz);
        e.If();
        e.I32Addr(flag); e.I32(-1); e.Store(WOp.I32Store);
        var caught = e.Block(ValType.I32);
        e.TryTable(null, (0, ExceptionTag, caught));
        e.Call(FunctionFor(cctor));
        e.End();
        e.Op(WOp.Return);
        e.End();
        // (the inner exception on the stack, owned: wrapped and thrown)
        int inner = e.AddLocal(ValType.I32);
        e.Set(inner);
        e.I32Addr(flag); e.I32Addr(saved); e.I32Addr(StringLiteral(InitName(t))); e.Get(inner);
        e.Call(_cctorFailed);
        e.Op(WOp.Unreachable);
        e.Else();
        e.I32Addr(flag); e.I32Addr(saved); e.Call(_cctorCheck!);
        e.End();
    }

    /// <summary>Throws the exception object on the stack (it holds the count the exception in flight keeps).</summary>
    internal void EmitThrow(Emitter e) => e.Emit(WOp.Throw, 0, 0, ExceptionTag);
}
