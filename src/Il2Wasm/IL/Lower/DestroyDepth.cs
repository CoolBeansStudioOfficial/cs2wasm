#nullable enable
using System;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// How deep a destruction nests (the old front end's answer, RuntimeGen.EmitDestroyDispatch and EmitDestroy, ported).
// An object's destruction releases what it refers to, which may destroy that, and so on: a long chain (a linked list
// let go at its head) nested rt.release -> rt.destroy as deep as it is long, and ran out of stack. Two parts:
//   - the depth queue: rt.destroy counts how deep it is; past MaxDestroyNesting an object (dead: its count is 0) is
//     put on a list by its count word instead, and the outermost destruction, when it is done, takes them from the
//     list one by one, within the same release (SPEC 4.3: nothing freed later than the statement that made it
//     unreachable). The same order of events as far as a program can tell: a finalizer runs when the count is gone,
//     its weak handles are cleared at its own destruction (SPEC 4.4), in some order, as under .NET;
//   - the sealed tail: a sealed class without a finalizer whose field holds its own type (a list's node and its
//     next) holds null or an object of exactly that class there, so when this object's reference is the last, its
//     destruction is this case again: a loop, not a call. That field is let go of last, after the memory is freed
//     (any order C# allows). IL2WASM_NO_SEALED_TAIL=1: off (the queue alone).
public sealed partial class IlCompiler
{
    const int MaxDestroyNesting = 48;
    static readonly bool s_noSealedTail = Environment.GetEnvironmentVariable("IL2WASM_NO_SEALED_TAIL") == "1";
    WasmGlobal? _destroyDepth, _destroyPending;

    /// <summary>rt.destroy as the depth queue around rt.destroy_now (the dispatch on the type, returned for the
    /// caller to write).</summary>
    WasmFunction EmitDestroyQueue()
    {
        var now = Module.AddFunction("rt.destroy_now", new FuncType([ValType.I32], []));
        _destroyDepth = new WasmGlobal("rt.destroy.depth", ValType.I32, true, 0);
        _destroyPending = new WasmGlobal("rt.destroy.pending", ValType.I32, true, 0);
        Module.Globals.Add(_destroyDepth); Module.Globals.Add(_destroyPending);
        var w = new Emitter(Rt.Destroy);
        int q = w.AddLocal(ValType.I32);
        // (too deep: on the list, through its count word)
        w.GGet(_destroyDepth); w.I32(MaxDestroyNesting); w.Op(WOp.I32GeU);
        w.If();
        w.Get(0); w.GGet(_destroyPending); w.Store(WOp.I32Store);
        w.Get(0); w.GSet(_destroyPending);
        w.Op(WOp.Return);
        w.End();
        w.GGet(_destroyDepth); w.I32(1); w.Op(WOp.I32Add); w.GSet(_destroyDepth);
        w.Get(0); w.Call(now);
        w.GGet(_destroyDepth); w.I32(1); w.Op(WOp.I32Sub); w.GSet(_destroyDepth);
        // (the outermost: what waits, one by one, each with its count word put back to 0)
        w.GGet(_destroyDepth); w.Op(WOp.I32Eqz);
        w.If();
        var exit = w.Block(); var top = w.Loop();
        w.GGet(_destroyPending); w.Tee(q); w.Op(WOp.I32Eqz); w.BrIf(exit);
        w.Get(q); w.Load(WOp.I32Load); w.GSet(_destroyPending);
        w.Get(q); w.I32(0); w.Store(WOp.I32Store);
        w.I32(1); w.GSet(_destroyDepth);
        w.Get(q); w.Call(now);
        w.I32(0); w.GSet(_destroyDepth);
        w.Br(top);
        w.End(); w.End();
        w.End();
        return now;
    }

    /// <summary>The field a destruction of x loops on (the last instance field of x's own type), or null: x a sealed
    /// class without a finalizer.</summary>
    FieldDesc? SealedTail(TypeDesc x)
    {
        // (IL2WASM_IL_WATCH: every release of the watched object goes through rt.release)
        if (s_noSealedTail || s_watch != null) return null;
        if (x is not MetadataType { IsValueType: false, IsSealed: true, IsInterface: false } || x.IsArray || x.IsString || FinalizerOf(x) != null) return null;
        FieldDesc? tail = null;
        for (TypeDesc? c = x; c != null; c = c.BaseType)
            foreach (var fd in c.GetFields())
                if (!fd.IsStatic && Kinds.Of(fd.FieldType) == VK.Ref && fd.FieldType == x) tail = fd;
        return tail;
    }

    /// <summary>x's case of rt.destroy_now as a loop over its tail field (SealedTail).</summary>
    void EmitSealedTailDestroy(Emitter e, TypeDesc x, FieldDesc tail)
    {
        int next = e.AddLocal(ValType.I32), c = e.AddLocal(ValType.I32);
        var top = e.Loop();
        EmitFinalizeStep(e, x); // (no finalizer: the weak handles only)
        for (TypeDesc? t = x; t != null; t = t.BaseType)
            foreach (var fd in t.GetFields())
            {
                if (fd.IsStatic || fd == tail) continue;
                if (Kinds.Of(fd.FieldType) == VK.Ref) { e.Get(0); e.Load(WOp.I32Load, IlContext.FieldOffset(fd)); e.Call(Rt.Release); }
                else if (Kinds.Of(fd.FieldType) == VK.Struct && Kinds.Counted(fd.FieldType)) { e.Get(0); e.I32(IlContext.FieldOffset(fd)); e.Op(WOp.I32Add); e.Call(Rt.ReleaseStruct(fd.FieldType)); }
            }
        e.Get(0); e.Load(WOp.I32Load, IlContext.FieldOffset(tail)); e.Set(next);
        e.Get(0); e.I32(IlContext.InstanceSize((DefType)x)); e.Call(Rt.Free);
        e.Get(next);
        e.If();
        e.Get(next); e.Load(WOp.I32Load); e.Tee(c); e.I32(1); e.Op(WOp.I32GtS);
        e.If();
        e.Get(next); e.Get(c); e.I32(1); e.Op(WOp.I32Sub); e.Store(WOp.I32Store);
        e.Else();
        // (the last reference: this case again, on the next)
        e.Get(c); e.I32(1); e.Op(WOp.I32Eq);
        e.If();
        e.Get(next); e.I32(0); e.Store(WOp.I32Store);
        e.Get(next); e.Set(0);
        e.Br(top);
        e.End();
        // (an immortal object, or a release too many: as any release, whose checked build traps on it)
        e.Get(next); e.Call(Rt.Release);
        e.End();
        e.End();
        e.End();
        e.Op(WOp.Return);
    }
}
