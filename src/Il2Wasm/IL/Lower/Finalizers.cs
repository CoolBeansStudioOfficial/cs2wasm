#nullable enable
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// Finalizers and weak references (SPEC 4.4), through the runtime the operation-tree front end drove
// (Boundary/ObjectMarks.cs, GC.Mono.cs, GCHandle.Mono.cs, DependentHandle.cs): the compiler's part is the destroy
// path. An object of a class with a finalizer, at its last release:
//   - ObjectMarks.OnFinalize clears the short weak handles to it (before the finalizer, as .NET clears them) and
//     says whether GC.SuppressFinalize skipped the finalizer;
//   - unless it was finalized before (Rt.FinalizedBit: an object its finalizer brought back is not finalized again
//     unless GC.ReRegisterForFinalize asks), the finalizer runs with a temporary count of 1; if the count is more
//     afterwards, the finalizer stored the object somewhere and it lives on (the temporary count goes like any);
//   - ObjectMarks.OnDestroy clears what is left (handles that track resurrection, a ConditionalWeakTable's entries);
//   - then its references are released and its memory freed.
// The marks are compiled in only when the program sets one (ObjectMarks.SetAt is reached); without them only the
// finalizer's part is there. In IL a C# destructor already calls its base class's (try { body } finally
// { base.Finalize(); }), so the most derived override is the one call (the operation-tree front end called the chain
// itself: Roslyn's operations leave the base call out).
public sealed partial class IlCompiler
{
    readonly HashSet<TypeDesc> _finRequested = [];
    bool _marks;
    MethodDesc? _onDestroy, _onFinalize;

    /// <summary>The finalizer an object of the type runs (the most derived override of Object.Finalize), or null.</summary>
    internal static MethodDesc? FinalizerOf(TypeDesc x) =>
        x is DefType { IsValueType: false, IsInterface: false } && !x.IsArray && !x.IsString && !x.IsObject ? x.GetFinalizer() : null;

    /// <summary>Each constructed type's finalizer is compiled; the marks' destroy hooks once a mark can be set. True
    /// when anything new was queued (ResolveDispatch's fixed point).</summary>
    bool RequestFinalizers()
    {
        bool any = false;
        foreach (var x in _constructedOrder.ToList())
        {
            if (!_finRequested.Add(x)) continue;
            if (FinalizerOf(x) is { } fin && !_functions.ContainsKey(fin)) { FunctionFor(fin); any = true; }
        }
        if (!_marks && TryCoreMethod("Il2Wasm.Runtime", "ObjectMarks", "SetAt") is { } set && _functions.ContainsKey(set))
        {
            _marks = true;
            _onDestroy = CoreMethod("Il2Wasm.Runtime", "ObjectMarks", "OnDestroy");
            _onFinalize = CoreMethod("Il2Wasm.Runtime", "ObjectMarks", "OnFinalize");
            FunctionFor(_onDestroy); FunctionFor(_onFinalize);
            any = true;
        }
        return any;
    }

    /// <summary>In a destroy function (the object in local 0): the finalizer and the weak handles, before the object's
    /// references are released. Leaves the case (done) when the finalizer brought the object back.</summary>
    void EmitFinalizeStep(Emitter e, TypeDesc x, Label done)
    {
        var fin = FinalizerOf(x);
        if (fin == null)
        {
            if (_marks) { e.Get(0); e.Call(FunctionFor(_onDestroy!)); e.Op(WOp.Drop); }
            return;
        }
        const int finalizedBit = 0x20000000;
        if (_marks) { e.Get(0); e.Call(FunctionFor(_onFinalize!)); } else e.I32(1);
        e.If();
        e.Get(0); e.Load(WOp.I32Load, 8); e.I32(finalizedBit); e.Op(WOp.I32And); e.Op(WOp.I32Eqz);
        e.If();
        e.Get(0); e.Get(0); e.Load(WOp.I32Load, 8); e.I32(finalizedBit); e.Op(WOp.I32Or); e.Store(WOp.I32Store, 8);
        // (the finalizer runs with a temporary count: its own uses of the object count on it as on any)
        e.Get(0); e.I32(1); e.Store(WOp.I32Store);
        e.Get(0); e.Call(FunctionFor(fin));
        // (brought back: it lives on, and the temporary count goes like any other)
        e.Get(0); e.Load(WOp.I32Load); e.I32(1); e.Op(WOp.I32Ne);
        e.If(); e.Get(0); e.Call(Rt.Release); e.Br(done); e.End();
        e.Get(0); e.I32(0); e.Store(WOp.I32Store);
        e.End();
        e.End();
        if (_marks) { e.Get(0); e.Call(FunctionFor(_onDestroy!)); e.Op(WOp.Drop); }
    }
}
