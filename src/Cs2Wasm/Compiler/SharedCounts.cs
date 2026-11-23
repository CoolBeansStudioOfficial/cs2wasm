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
    WasmFunction? _pubInto, _pubAt, _pubAlways, _share, _shareVisit, _storeRef;
    /// <summary>(address, value) -> old value: a reference stored into memory that is not the frame. With threads an
    /// atomic exchange: a plain read and write would race with another thread's store or compare-and-swap of the same
    /// slot and release the old value twice (racy code stays memory-safe, section 4.5); without, the read and the write.</summary>
    public WasmFunction StoreRef => _storeRef ??= M.AddFunction("rt.store_ref", FT([I, I], [I]));
    public WasmFunction? StoreRefIfMade => _storeRef;
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

    WasmFunction? _canon64, _canon32;
    /// <summary>(x) -> x, or .NET's NaN when x is a NaN (section 7.3: a NaN's payload and sign, which differ between
    /// machines, never reach memory or anything that reads a float's bits).</summary>
    public WasmFunction CanonF64 => _canon64 ??= MakeCanon(true);
    public WasmFunction CanonF32 => _canon32 ??= MakeCanon(false);
    WasmFunction MakeCanon(bool d)
    {
        var t = d ? ValType.F64 : ValType.F32;
        var f = M.AddFunction(d ? "rt.canon_f64" : "rt.canon_f32", FT([t], [t]));
        var e = new Emitter(f);
        e.Get(0);
        if (d) e.Emit(Op.F64Const, unchecked((long)0xFFF8000000000000)); else e.Emit(Op.F32Const, unchecked((int)0xFFC00000));
        e.Get(0); e.Get(0); e.Op(d ? Op.F64Eq : Op.F32Eq);
        e.Op(Op.Select);
        return f;
    }

    WasmFunction? _finHold, _finUnhold;
    /// <summary>Around a finalizer's call in a destroy function: with threads, the thread keeps its epoch meanwhile
    /// (Limbo.Hold); without, nothing.</summary>
    public WasmFunction FinHold => _finHold ??= M.AddFunction("rt.fin_hold", FT([], []));
    public WasmFunction FinUnhold => _finUnhold ??= M.AddFunction("rt.fin_unhold", FT([], []));
    public WasmFunction? FinHoldIfMade => _finHold;
    WasmFunction? _beforeFin;
    /// <summary>Called before a finalizer runs from a destroy function (before rt.fin_hold): Rt.ArenaBeforeFinalizer in a
    /// program whose groups are let go in two passes (RuntimeGen.EmitArenaSupport), nothing in any other.</summary>
    public WasmFunction BeforeFin => _beforeFin ??= M.AddFunction("rt.before_fin", FT([], []));
    public void EmitFinalizerHold(bool threads)
    {
        if (!threads) return;
        // (nothing to hold: an object is destroyed when its count reaches zero, on every thread)
    }

    readonly List<(WasmFunction fn, ITypeSymbol type)> _structCopies = [];
    internal void NoteStructCopy(WasmFunction f, ITypeSymbol t) => _structCopies.Add((f, t));
}

/// <summary>A lambda made into a delegate at one place of the text, in one compilation of its method (Sharing.cs:
/// a delegate value's source names the place, so the closure that is reached with the delegate is that lambda's, not
/// every closure a delegate of the type was ever made with). Unsure: emitted more than once with another closure, or
/// never emitted: such a delegate is any of its type.</summary>
internal sealed class LambdaSite
{
    public WasmFunction? Fn;
    public ITypeSymbol? Type;
    public Display? Env;
    public bool Unsure;
}

internal sealed partial class Driver
{
    internal readonly List<LambdaSite> LambdaSites = [];
    /// <summary>For a function delegates were made with (FunctionCompiler.NewDelegate): each delegate type, and the
    /// lambda site (-1: a method group's or a local function's, which code can also name otherwise).</summary>
    internal readonly Dictionary<WasmFunction, List<(ITypeSymbol dt, int site)>> DelegatesMade = [];
    internal int NewLambdaSite() { lock (LambdaSites) { LambdaSites.Add(new LambdaSite()); return LambdaSites.Count - 1; } }
    internal void FillLambdaSite(int site, WasmFunction fn, ITypeSymbol dt, Display? env)
    {
        lock (LambdaSites)
        {
            var ls = LambdaSites[site];
            if (ls.Fn == null) { ls.Fn = fn; ls.Type = dt; ls.Env = env; }
            else if (!ReferenceEquals(ls.Fn, fn) || !ReferenceEquals(ls.Env, env) || Types.Key(ls.Type!) != Types.Key(dt)) ls.Unsure = true;
        }
    }
    internal void NoteDelegateMade(WasmFunction fn, ITypeSymbol dt, int site)
    {
        lock (LambdaSites)
        {
            if (!DelegatesMade.TryGetValue(fn, out var l)) DelegatesMade[fn] = l = [];
            l.Add((dt, site));
        }
    }

    public WasmGlobal? StackLo, StackHi;
    /// <summary>How deep the destroy functions are nested on this thread, and the objects whose destruction waits for
    /// the outermost one to finish (RuntimeGen.EmitDestroyDispatch).</summary>
    public WasmGlobal DestroyDepth = null!, DestroyPending = null!;
    bool? _mayUseThreads;
    /// <summary>Publication code is emitted for every program: any code may start threads, the program's or a
    /// library's (Roslyn's Parallel.For); it is removed again at the end if no thread is actually reachable.</summary>
    public bool MayUseThreads => _mayUseThreads ??= true;

    /// <summary>Fills in the sharing functions (threads) or removes their calls (no threads).</summary>
    internal void EmitSharing(bool threads, IEnumerable<(int id, ITypeSymbol type)> types)
    {
        var rt = Rt;
        var fns = new[] { rt.PublishIntoIfMade, rt.PublishAtIfMade, rt.PublishAlwaysIfMade, rt.ShareIfMade };
        if (rt.StoreRefIfMade is { } sr)
        {
            var e = new Emitter(sr);
            // (threads: under the place's lock, so that a read that counts, rt.load_retain, sees the old value still
            // counted or the new one)
            int s = threads ? e.AddLocal(ValType.I32) : -1, old = e.AddLocal(ValType.I32);
            if (threads) rt.EmitRefLock(e, 0, s);
            e.Get(0); e.Load(Op.I32Load); e.Set(old); e.Get(0); e.Get(1); e.Store(Op.I32Store);
            if (threads) rt.EmitRefUnlock(e, s);
            e.Get(old);
        }
        // (no object is marked shared: counts are atomic in a program with threads, plain in one without)
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
