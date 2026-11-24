#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
// The depth's prologue is in rt.destroy itself, before its dispatch on the type, and each type's case ends with a call
// of rt.destroy_done (the epilogue) and a return (the old front end's were a small rt.destroy around rt.destroy_now):
//   - a small rt.destroy is inlined by V8 into rt.release, which is then too large for V8 to inline where it is called
//     (ListSort 13-22% slower, Linq 16-20%, Dictionary 12-17%; the same with --no-wasm-inlining as without the queue);
//   - the cases leaving by a branch to one epilogue at the end were slower still (homelab, naive: ListSort +15%,
//     Dictionary +12%, with nothing else changed; the same bytes of dead code there cost nothing).
// A queued object is taken from the list by a call of rt.destroy itself at depth 0, which empties what is left of the
// list before it returns.
// The dispatch on the type id is a br_table, as the old front end's (RuntimeFns2.IdSwitch): the chain of tests it
// replaces, one a constructed type, was rt.destroy's own time (15% of Linq's under naive counting, desk profile), and
// anything added to that function showed in the kernels that let go of many objects. A leaf (a destruction that
// releases nothing and runs no finalizer: a string, an array of numbers) is dispatched to before the depth count and
// returns without it, as the old front end's leaf types did (Linq under naive counting and the plan, homelab: +3-6%
// with every destruction counted). IL2WASM_NO_DESTROY_SWITCH=1: the chain of tests, every case counted.
public sealed partial class IlCompiler
{
    const int MaxDestroyNesting = 48;
    static readonly bool s_noSealedTail = Environment.GetEnvironmentVariable("IL2WASM_NO_SEALED_TAIL") == "1";
    WasmGlobal? _destroyDepth, _destroyPending;

    /// <summary>rt.destroy's start: an object too deep goes on the list (and the function returns); otherwise the
    /// depth goes up. Each type's case then leaves by EmitDestroyLeave.</summary>
    void EmitDestroyPrologue(Emitter w)
    {
        _destroyDepth = new WasmGlobal("rt.destroy.depth", ValType.I32, true, 0);
        _destroyPending = new WasmGlobal("rt.destroy.pending", ValType.I32, true, 0);
        Module.Globals.Add(_destroyDepth); Module.Globals.Add(_destroyPending);
        // (too deep: on the list, through its count word)
        w.GGet(_destroyDepth); w.I32(MaxDestroyNesting); w.Op(WOp.I32GeU);
        w.If();
        w.Get(0); w.GGet(_destroyPending); w.Store(WOp.I32Store);
        w.Get(0); w.GSet(_destroyPending);
        w.Op(WOp.Return);
        w.End();
        w.GGet(_destroyDepth); w.I32(1); w.Op(WOp.I32Add); w.GSet(_destroyDepth);
        // rt.destroy_done: the depth goes down, and the outermost destruction destroys what waits, one by one, each
        // with its count word put back to 0
        _destroyDone = Module.AddFunction("rt.destroy_done", new FuncType([], []));
        var d = new Emitter(_destroyDone);
        int q = d.AddLocal(ValType.I32);
        d.GGet(_destroyDepth); d.I32(1); d.Op(WOp.I32Sub); d.GSet(_destroyDepth);
        d.GGet(_destroyDepth); d.Op(WOp.I32Eqz);
        d.If();
        var exit = d.Block(); var top = d.Loop();
        d.GGet(_destroyPending); d.Tee(q); d.Op(WOp.I32Eqz); d.BrIf(exit);
        d.Get(q); d.Load(WOp.I32Load); d.GSet(_destroyPending);
        d.Get(q); d.I32(0); d.Store(WOp.I32Store);
        d.Get(q); d.Call(Rt.Destroy);
        d.Br(top);
        d.End(); d.End();
        d.End();
    }

    WasmFunction? _destroyDone;
    static readonly bool s_noDestroySwitch = Environment.GetEnvironmentVariable("IL2WASM_NO_DESTROY_SWITCH") == "1";

    /// <summary>A destruction that releases nothing and runs no finalizer (the weak handles' OnDestroy aside, as the
    /// old front end's leaves, RuntimeGen.IsLeafDestroy): it nests nothing, so it needs no depth count.</summary>
    static bool IsLeafDestroy(TypeDesc x)
    {
        if (x.IsString) return true;
        if (x is ArrayType at) return !Kinds.Counted(at.ElementType);
        if (x.IsValueType) return !Kinds.Counted(x);
        if (FinalizerOf(x) != null) return false;
        for (TypeDesc? c = x; c != null; c = c.BaseType)
            foreach (var fd in c.GetFields())
                if (!fd.IsStatic && Kinds.Counted(fd.FieldType)) return false;
        return true;
    }

    /// <summary>rt.destroy's dispatch: a br_table on the type id to the leaves' cases (each returns) and to the depth
    /// count's prologue for the others, then a second br_table to their cases (each leaves by EmitDestroyLeave). An
    /// id with no case falls through, past the prologue.</summary>
    void EmitDestroySwitch(Emitter e, int tid, List<(int id, bool leaf, Action body)> cases, int idCount)
    {
        if (s_noDestroySwitch)
        {
            EmitDestroyPrologue(e);
            foreach (var (id, _, body) in cases) { e.Get(tid); e.I32(id); e.Op(WOp.I32Eq); e.If(); body(); e.End(); }
            return;
        }
        var leaves = cases.Where(c => c.leaf).ToList();
        var others = cases.Where(c => !c.leaf).ToList();
        void Table(List<(int id, bool leaf, Action body)> cs, Label? rest)
        {
            var dflt = e.Block();
            var labels = new Label[cs.Count];
            for (int k = cs.Count - 1; k >= 0; k--) labels[k] = e.Block();
            var targets = new Label[idCount];
            for (int k = 0; k < targets.Length; k++) targets[k] = dflt;
            if (rest != null) foreach (var c in others) targets[c.id] = rest;
            for (int k = 0; k < cs.Count; k++) targets[cs[k].id] = labels[k];
            e.Get(tid); e.BrTable(targets, dflt);
            foreach (var (_, _, body) in cs) { e.End(); body(); }
            e.End();
        }
        // (the leaves, and a type with no case: no depth count)
        var counted = e.Block();
        Table(leaves, counted);
        e.Op(WOp.Return);
        e.End();
        EmitDestroyPrologue(e);
        Table(others, null);
    }

    /// <summary>A case of rt.destroy is done: the epilogue, and return.</summary>
    void EmitDestroyLeave(Emitter e) { e.Call(_destroyDone!); e.Op(WOp.Return); }

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

    /// <summary>x's case of rt.destroy as a loop over its tail field (SealedTail).</summary>
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
        EmitDestroyLeave(e);
    }
}
