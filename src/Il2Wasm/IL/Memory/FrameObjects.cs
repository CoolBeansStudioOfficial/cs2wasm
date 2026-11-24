#nullable enable
using System;
using System.Collections.Generic;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>Objects that live in their frame (docs/MEMORY-IR.md 2.3's FrameOwned; the old front end's
/// EscapeAnalysis.ParamEscape on SSA, in its simplest form): a <c>newobj</c> of a class whose constructor keeps
/// <c>this</c> in (InitStores' rule), with no finalizer, at most 256 bytes, used by this body only as the base of its
/// field loads and stores, in comparisons, branches and its own counts, never stored, passed, returned, thrown, cast or
/// carried by a phi. Nothing but this frame can reach it, so lowering makes it in the frame (Rt.InitFrameObject, an
/// immortal header) and the plan's release of it at its last use releases its children (SPEC 4.3: never later than
/// the object's last use). IL2WASM_NO_FRAME_OBJECTS=1 turns it off.</summary>
public static class FrameObjects
{
    static readonly bool s_off = Environment.GetEnvironmentVariable("IL2WASM_NO_FRAME_OBJECTS") == "1";
    const int MaxSize = 256;

    /// <summary>Frame objects found (all plans of the process), for IL2WASM_PLAN_STATS.</summary>
    public static int Found;

    public static List<Node> Find(PlanContext ctx)
    {
        var r = new List<Node>();
        if (s_off || !HasFrameRuntime(ctx.Method.Context)) return r;
        var body = ctx.Body;
        Dictionary<Node, bool>? ok = null;
        foreach (var b in body.Blocks)
            foreach (var n in b.Code)
                if (n.Op == Opc.NewObj && n.Kind == VK.Ref && n.X is MethodDesc ctor && Candidate(ctx.Solution, ctor))
                    (ok ??= new())[n] = true;
        if (ok == null) return r;
        foreach (var b in body.Blocks)
        {
            foreach (var p in b.Phis) foreach (var a in p.A) if (ok.ContainsKey(a)) ok[a] = false;
            foreach (var n in b.Code)
                for (int j = 0; j < n.A.Length; j++)
                {
                    var o = n.A[j];
                    if (!ok.ContainsKey(o)) continue;
                    bool fine = n.Op switch
                    {
                        Opc.LdFld => j == 0,
                        Opc.StFld => j == 0 && n.A[1] != o,
                        Opc.Cmp or Opc.BrIf or Opc.Retain or Opc.Release => true,
                        _ => false,
                    };
                    if (!fine) ok[o] = false;
                }
        }
        foreach (var (n, y) in ok) if (y) r.Add(n);
        r.Sort((x, y) => x.Id.CompareTo(y.Id));
        Found += r.Count;
        return r;
    }

    /// <summary>The CoreLib has Rt.InitFrameObject (the stub CoreLib of the IL tests does not).</summary>
    static bool HasFrameRuntime(TypeSystemContext c)
    {
        lock (s_frameRuntime)
        {
            if (s_frameRuntime.TryGetValue(c, out bool r)) return r;
            r = c.SystemModule.GetType("Il2Wasm.Runtime"u8, "Rt"u8, throwIfNotFound: false) is MetadataType rt
                && System.Linq.Enumerable.Any(rt.GetMethods(), m => m.GetName() == "InitFrameObject");
            s_frameRuntime.Add(c, r);
            return r;
        }
    }
    static readonly Dictionary<TypeSystemContext, bool> s_frameRuntime = new();

    static bool Candidate(ProgramSolution sol, MethodDesc ctor)
    {
        if (ctor.OwningType is not DefType { IsValueType: false, IsString: false, IsDelegate: false } t || t is ArrayType) return false;
        if (!ctor.IsConstructor || ctor.Signature.IsStatic) return false;
        if (Il2Wasm.IL.Lower.IlCompiler.FinalizerOf(t) != null) return false;
        int size;
        try { size = IlContext.InstanceSize(t); } catch (Exception) { return false; }
        if (size > MaxSize) return false;
        return InitStores.KeepsThis(sol, ctor);
    }
}
