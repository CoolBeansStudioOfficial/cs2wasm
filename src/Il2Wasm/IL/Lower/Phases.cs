#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Memory;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Lower;

// The phase split of docs/MEMORY-IR.md 1.2 (IlCompiler.Reach: ReachPhase, SolvePhase, LowerPhase): what the reach
// phase asks of the compiler (IReachHost), a method's import with no effect on the module, and a method's plan with
// the S1 hooks (Memory/Plan.cs, Ir/Inline.cs, Memory/Canon.cs, Memory/Verify.cs) between the solve and lowering.
public sealed partial class IlCompiler : IReachHost
{
    /// <summary>A method's IL imported (the importer, the constant propagation, the import with its facts) and
    /// summarized, with nothing written to the module or to this round's facts: the reach phase does it in an order of
    /// its own. A failure is kept for lowering to rethrow where the method is lowered.</summary>
    ImportedMethod ImportMethod(MethodDesc m)
    {
        var u = new ImportedMethod(m);
        try
        {
            var typical = m.GetTypicalMethodDefinition() as EcmaMethod;
            MethodIL? il = typical != null ? EcmaMethodIL.Create(typical) : null;
            if (il != null && m != typical) il = new InstantiatedMethodIL(m, il);
            // ([UnsafeAccessor]: a body made for the instantiation)
            il ??= UnsafeAccessors.Body(m);
            u.Il = il;
        }
        catch (System.Exception e) { u.Error = ExceptionDispatchInfo.Capture(e); u.ErrorBeforeImport = true; return u; }
        if (u.Il == null) return u;
        // (an ILLink substitution: the stub is the body, nothing is imported)
        if (Facts.Substitutions.Method(m) is not null) { u.Substituted = true; return u; }
        try { u.Body = Facts.Import(m, u.Il, this, out u.Summary, out u.Facts); }
        catch (System.Exception e) { u.Error = ExceptionDispatchInfo.Capture(e); }
        return u;
    }

    /// <summary>IL2WASM_MEM_REIMPORT=1: the reach phase keeps only each method's summary and its import decisions, and
    /// lowering imports the body again (less memory held between the phases, one more import per method).</summary>
    static readonly bool s_reimport = System.Environment.GetEnvironmentVariable("IL2WASM_MEM_REIMPORT") == "1";

    ImportedMethod? IReachHost.Import(MethodDesc m)
    {
        // (the functions FunctionFor makes without IL: an array's method, a delegate's Invoke, an import; a P/Invoke
        // or an internal call is the runtime's method that serves it, or, when none does, imported late)
        if (m is ArrayMethod) return null;
        var typical = m.GetTypicalMethodDefinition();
        if (IsDelegateInvoke(m) && typical is EcmaMethod { IsRuntimeImplemented: true }) return null;
        if (typical is EcmaMethod em && (em.IsPInvoke || em.IsInternalCall || em.HasCustomAttribute("Il2Wasm.Runtime", "ImportAttribute"))) return null;
        var u = ImportMethod(m);
        if (s_reimport) u.Body = null;
        return u;
    }

    bool IReachHost.LoweredInline(MethodDesc m)
    {
        if (_inline.TryGetValue(m, out bool r)) return r;
        return _inline[m] = LoweredInline(m);
    }

    readonly Dictionary<MethodDesc, bool> _inline = new();

    bool LoweredInline(MethodDesc m)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em) return false;
        if (em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute")) return true;
        if (m.OwningType is not MetadataType ot || ot.Module != _ctx.SystemModule) return false;
        if (IsUpstreamIntrinsic(m)) return true;
        // (Activation.cs's RuntimeType cases)
        return ot.GetName() == "RuntimeType" && ot.GetNamespace() == "System" && m.GetName() is "CreateInstanceOfT" or "CreateInstanceMono" or "CallDefaultStructConstructor";
    }

    MethodDesc IReachHost.ConstructorTarget(MethodDesc ctor)
    {
        if (ctor.OwningType is not DefType { IsString: true } t) return ctor;
        // (string's constructors are its static Ctor methods, as NewObj lowers them)
        return t.GetMethods().FirstOrDefault(x => x.GetName() == "Ctor" && x.Signature.IsStatic && x.Signature.Length == ctor.Signature.Length
            && Enumerable.Range(0, x.Signature.Length).All(i => x.Signature[i] == ctor.Signature[i])) ?? ctor;
    }

    IEnumerable<MethodDesc> IReachHost.TypeEdges(TypeDesc constructed) => FinalizerOf(constructed) is { } fin ? [fin] : [];

    MethodDesc? IReachHost.Resolve(TypeDesc exact, MethodDesc slot) => Implementation(exact, slot);

    /// <summary>A method's plan, between the solve and lowering (section 1.2's per-method phase). The naive plan
    /// (Ir/Ownership.cs) unless IL2WASM_MEM_PLAN=1 turns the planner on; IL2WASM_MEM_NAIVE=1 always means the naive
    /// plan (the baseline the removal share is measured against).</summary>
    MethodPlan PlanMethod(MethodDesc m, Body body)
    {
        if (!Pipeline.Planned || Solution == null) return MethodPlan.Naive(body, Ownership.Run(body));
        var ctx = new PlanContext(m, body, Solution, Variant.Entry);
        var plan = Plan.Build(ctx);
        if (Pipeline.Inline)
        {
            for (int step = 0; Inline.Step(ctx, plan, CalleeForInline) is { } site; step++)
            {
                Canon.Run(ctx, plan, site);
                if (Pipeline.Verify) Verify.Check(ctx, plan);
            }
            // (an inlined callee's facts are the caller's now: recorded as its own lowering would have recorded them)
            foreach (var c in Inline.InlinedInto(plan))
                if (Solution.Summary(c) is { } s) { Facts.Merge(s); Facts.AddDirectCallees(m, s.Direct); }
        }
        if (Pipeline.Verify) Verify.Check(ctx, plan);
        return plan;
    }

    /// <summary>A callee's planned body for the IR inliner (Ir/Inline.cs): imported afresh (import is deterministic)
    /// and planned for the convention the call site uses; null when it is not to be inlined (no IL, a substitution, a
    /// failure).</summary>
    (Body Body, MethodPlan Plan)? CalleeForInline(MethodDesc callee, Variant variant)
    {
        if (Solution == null || !Solution.Covers(callee)) return null;
        var u = ImportMethod(callee);
        if (u.Body == null || u.Error != null) return null;
        var ctx = new PlanContext(callee, u.Body, Solution, variant);
        return (u.Body, Plan.Build(ctx));
    }

    /// <summary>The [UnmanagedCallersOnly(EntryPoint = ...)] methods of the module, with their export names.</summary>
    static IEnumerable<(string Name, MethodDesc Method)> ExportMethods(EcmaModule module)
    {
        foreach (var t in module.GetAllTypes())
        {
            if (t.HasInstantiation) continue;
            foreach (var m in t.GetMethods())
            {
                if (m is not EcmaMethod em || !m.Signature.IsStatic || m.HasInstantiation) continue;
                if (em.GetDecodedCustomAttribute("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute") is not { } a) continue;
                if (a.NamedArguments.FirstOrDefault(x => x.Name == "EntryPoint").Value is not string name) continue;
                yield return (name, m);
            }
        }
    }
}
