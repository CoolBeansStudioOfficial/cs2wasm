using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Section 4.5: counts are atomic only where an object is shared. A shared object has bit 30 of its count word set;
/// its retains and releases are compare-and-swap loops, every other object's are plain. An object becomes shared, with
/// everything it reaches, when it is published: stored into a static field, into an object or array that is
/// already shared, handed to another thread (Thread.Start, the thread pool), or stored somewhere the compiler cannot
/// see is the current thread's own (an interior address that is not on its stack). That keeps the invariant: an
/// object another thread can reach is marked before it can. Without threads in the program, none of this is emitted.
/// </summary>
enum Publish { Unknown, None, Always, Container }

internal sealed partial class RuntimeFns
{
    public const int SharedBit = 0x40000000;
    /// <summary>With SharedBit: the object waits in limbo (its count reached zero; Limbo).</summary>
    public const int LimboBit = 0x20000000;
    WasmFunction? _pubInto, _pubAt, _pubAlways, _share, _shareVisit;
    /// <summary>(value, container) -> value: shares the value if the container is shared.</summary>
    public WasmFunction PublishInto => _pubInto ??= M.AddFunction("rt.publish_into", FT([I, I], [I]));
    /// <summary>(value, address) -> value: shares the value unless the address is on this thread's stack.</summary>
    public WasmFunction PublishAt => _pubAt ??= M.AddFunction("rt.publish_at", FT([I, I], [I]));
    /// <summary>(value) -> value: shares it (static fields).</summary>
    public WasmFunction PublishAlways => _pubAlways ??= M.AddFunction("rt.publish_always", FT([I], [I]));
    /// <summary>(object): shares it and what it reaches (hand-off to another thread).</summary>
    public WasmFunction Share => _share ??= M.AddFunction("rt.share", FT([I], []));
    /// <summary>(object): marks and queues what the object refers to (one level; Sharing.Share drains the queue).</summary>
    public WasmFunction ShareVisit => _shareVisit ??= M.AddFunction("rt.share_visit", FT([I], []));

    readonly List<(WasmFunction fn, ITypeSymbol type)> _structCopies = [];
    internal void NoteStructCopy(WasmFunction f, ITypeSymbol t) => _structCopies.Add((f, t));
}

internal sealed partial class Driver
{
    public WasmGlobal? StackLo, StackHi;
    bool? _mayUseThreads;
    /// <summary>Publication code is emitted for every program: any code may start threads, the program's or a
    /// library's (Roslyn's Parallel.For); it is removed again at the end if no thread is actually reachable.</summary>
    public bool MayUseThreads => _mayUseThreads ??= true;

    /// <summary>Fills in the sharing functions (threads) or removes their calls (no threads).</summary>
    internal void EmitSharing(bool threads, IEnumerable<(int id, ITypeSymbol type)> types)
    {
        var rt = Rt;
        var fns = new[] { rt.PublishIntoIfMade, rt.PublishAtIfMade, rt.PublishAlwaysIfMade, rt.ShareIfMade };
        if (!threads)
        {
            foreach (var fn in Module.Functions)
            {
                var b = fn.Body;
                for (int i = 0; i < b.Count; i++)
                {
                    if (b[i].Op != Op.Call || b[i].Ref is not WasmFunction g) continue;
                    if (g == rt.PublishIntoIfMade || g == rt.PublishAtIfMade) b[i] = b[i] with { Op = Op.Drop, Ref = null };
                    else if (g == rt.PublishAlwaysIfMade) b[i] = b[i] with { Op = Op.Nop, Ref = null };
                    else if (g == rt.ShareIfMade) b[i] = b[i] with { Op = Op.Drop, Ref = null };
                }
            }
            if (rt.PublishIntoIfMade is { } f1) { var e = new Emitter(f1); e.Get(0); }
            if (rt.PublishAtIfMade is { } f2) { var e = new Emitter(f2); e.Get(0); }
            if (rt.PublishAlwaysIfMade is { } f3) { var e = new Emitter(f3); e.Get(0); }
            return;
        }
        var share = LibMethod("Cs2Wasm.Runtime.Sharing", "Share", 1).Fn;
        var markPush = LibMethod("Cs2Wasm.Runtime.Sharing", "MarkPush", 1).Fn;
        { var e = new Emitter(rt.Share); e.Get(0); e.Call(share); }
        {
            var e = new Emitter(rt.PublishAlways);
            e.Get(0); e.Call(share); e.Get(0);
        }
        {
            var e = new Emitter(rt.PublishInto);
            e.Get(1);
            e.If(); e.Get(1); e.Load(Op.I32Load); e.I32(RuntimeFns.SharedBit); e.Op(Op.I32And); e.If(); e.Get(0); e.Call(share); e.End(); e.End();
            e.Get(0);
        }
        {
            var e = new Emitter(rt.PublishAt);
            e.Get(1); e.GGet(StackLo!); e.Op(Op.I32GeU); e.Get(1); e.GGet(StackHi!); e.Op(Op.I32LtU); e.Op(Op.I32And);
            e.If(); e.Get(0); e.Op(Op.Return); e.End();
            e.Get(0); e.Call(share); e.Get(0);
        }
        // the visitor: one level of references per type
        {
            var e = new Emitter(rt.ShareVisit);
            void Refs(int baseLocal, int off, ITypeSymbol t)
            {
                if (Types.IsRef(t)) { e.Get(baseLocal); e.Load(Op.I32Load, off); e.Call(markPush); return; }
                if (!Types.IsStruct(t) || !Types.LayoutOf(t).HasRefs) return;
                foreach (var f in Types.LayoutOf(t).Fields) Refs(baseLocal, off + f.Offset, f.Type);
            }
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            // one case per type, reached through a br_table on the type id (a chain of comparisons would cost every
            // visit a walk over the types)
            var cases = types.Select(x => (ext: ExtId(x.id), x.id, x.type)).Where(x => x.ext >= 0).GroupBy(x => x.ext).Select(g => g.First()).OrderBy(x => x.ext).ToList();
            var dflt = e.Block();
            var labels = new Label[cases.Count];
            for (int i = cases.Count - 1; i >= 0; i--) labels[i] = e.Block();
            var targets = new Label[TypeCount];
            for (int i = 0; i < targets.Length; i++) targets[i] = dflt;
            for (int i = 0; i < cases.Count; i++) if (cases[i].ext < targets.Length) targets[cases[i].ext] = labels[i];
            e.Get(tid); e.BrTable(targets, dflt);
            foreach (var (_, id, type) in cases)
            {
                e.End();
                if (DisplayById.TryGetValue(id, out var disp))
                {
                    e.Get(0); e.Load(Op.I32Load, 12); e.Call(markPush);
                    foreach (var (off, t) in disp.Vars.Select(v => disp.Slots[v]).Concat(disp.ExtraSlots.Values)) Refs(0, off, t);
                }
                else if (type is IArrayTypeSymbol at)
                {
                    var et = Types.Canonical(at.ElementType);
                    if (Types.NeedsRc(et))
                    {
                        int size = Types.StorageOf(et).size, p = e.AddLocal(ValType.I32), end = e.AddLocal(ValType.I32);
                        e.Get(0); e.I32(Types.ArrayDataOffset(at.Rank)); e.Op(Op.I32Add); e.Set(p);
                        e.Get(p); e.Get(0); e.Load(Op.I32Load, 12); e.I32(size); e.Op(Op.I32Mul); e.Op(Op.I32Add); e.Set(end);
                        var brk = e.Block(); var top = e.Loop();
                        e.Get(p); e.Get(end); e.Op(Op.I32GeU); e.BrIf(brk);
                        Refs(p, 0, et);
                        e.Get(p); e.I32(size); e.Op(Op.I32Add); e.Set(p);
                        e.Br(top); e.End(); e.End();
                    }
                }
                else if (id != 1 && type is INamedTypeSymbol nt)
                {
                    if (nt.IsValueType) Refs(0, FunctionCompilerHelpers.BoxDataOffset(Types, nt), nt);
                    else foreach (var f in Types.LayoutOf(nt).Fields) Refs(0, f.Offset, f.Type);
                }
                e.Op(Op.Return);
            }
            e.End();
        }
        // struct copies into memory the compiler cannot place: share what they bring
        foreach (var (fn, t) in rt.StructCopies)
        {
            var e = new Emitter(fn);
            void Pub(int off, ITypeSymbol ft)
            {
                if (Types.IsRef(ft)) { e.Get(0); e.Load(Op.I32Load, off); e.Get(0); e.Call(rt.PublishAt); e.Op(Op.Drop); return; }
                if (!Types.IsStruct(ft) || !Types.LayoutOf(ft).HasRefs) return;
                foreach (var f in Types.LayoutOf(ft).Fields) Pub(off + f.Offset, f.Type);
            }
            foreach (var f in Types.LayoutOf(t).Fields) Pub(f.Offset, f.Type);
        }
        // Array.Copy into a shared array: its new elements become shared
        if (rt.ArrayCopyIfMade is { } copy)
        {
            var e = new Emitter(copy);
            e.Get(2); e.Call(LibMethod("Cs2Wasm.Runtime.Sharing", "RevisitIfShared", 1).Fn);
        }
    }
}

internal sealed partial class RuntimeFns
{
    public WasmFunction? PublishIntoIfMade => _pubInto;
    public WasmFunction? PublishAtIfMade => _pubAt;
    public WasmFunction? PublishAlwaysIfMade => _pubAlways;
    public WasmFunction? ShareIfMade => _share;
    public IReadOnlyList<(WasmFunction fn, ITypeSymbol type)> StructCopies => _structCopies;
}

sealed partial class FunctionCompiler
{
    /// <summary>The value (in `val`) is about to be stored: publish it as the destination requires.</summary>
    void EmitPublish(int val, Publish pub, Action? container, Action pushAddr)
    {
        if (!D.MayUseThreads) return;
        switch (pub)
        {
            case Publish.None: return;
            case Publish.Always: E.Get(val); E.Call(Rt.PublishAlways); E.Set(val); return;
            case Publish.Container: E.Get(val); container!(); E.Call(Rt.PublishInto); E.Set(val); return;
            default: E.Get(val); pushAddr(); E.Call(Rt.PublishAt); E.Set(val); return;
        }
    }
}
