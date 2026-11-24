#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Il2Wasm.IL.Memory;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Ir;

/// <summary>The IR inliner (docs/MEMORY-IR.md 3.7), after the solve, on planned bodies. S1 agent d's file (with
/// Memory/Canon.cs).
///
/// How a step works:
///   - the caller's plan is taken out of its body (Strip: the Retain and Release nodes go, Node.Moves is cleared);
///   - the callee's body (imported afresh through the frozen callback, its plan stripped the same way) is spliced in
///     at every chosen site of one level: the call's block is split, the callee's arguments become the caller's
///     values, its returns branch to the rest of the block (a phi joins several);
///   - Canon.Run plans the caller again. So the result is OSSA by construction, and the counts the callee spent on
///     its own arguments (the entry retain and the release at its last use) are gone: the planner sees the caller's
///     value, which the caller keeps across the inlined code.
/// One Step inlines one level of sites (block and node order). The calls the inlined code brings are the next
/// level's candidates, up to MaxLevels. The InlineSite returned is the level's first site.
///
/// What keeps behaviour identical:
///   - Stack traces (SPEC 6.3). They are frames from the PDB (Lower/DebugInfo.cs), and lowering has no per-node
///     inline scope yet. So a callee is inlined only when a trace can never show its frame, or when no exception can
///     leave it and nothing in it can take a trace. The first case is a hidden frame: [StackTraceHidden] on it or a
///     type around it, or a type of Il2Wasm.Runtime. The second case means it makes no call that may throw, and has
///     no throwing operation (a dereference of anything but this, an index, a cast, a division, an overflow check, a
///     throw, a static of a type with a .cctor). Every inlined node takes the call's IL offset, so its line is the
///     call's line.
///   - callvirt's null check: .NET throws at the call, before the callee runs. The site is inlined only when the
///     receiver is known not null (made right there), or when the callee's first effect is a dereference of this.
///     That dereference throws the same NullReferenceException at the same position.
///   - Static constructors: a callee whose call would trigger one (ECMA-335 II.10.5.3.1, Statics.CctorCheckForCall)
///     is not inlined. Static field accesses keep their own checks in lowering.
///   - Exception regions, filters, struct arguments, byrefs and pinned locals:
///     - a callee with regions of its own, variables (pinned locals and locals in the frame are variables), localloc,
///       a struct argument (passed as the address of a copy) or a struct result is not inlined;
///     - a call site inside a filter is not inlined;
///     - an inlined body inside a try, a catch or a finally becomes part of that region, so its exceptions go to the
///       caller's handlers as the callee's frame would have passed them on;
///     - byref values are ordinary SSA values: the re-plan finds their bases (Ownership.FindBases).
///   - [MethodImpl(NoInlining)], synchronized methods, methods that observe lifetimes, intrinsics lowering expands,
///     and recursion are never inlined.
///
/// Devirtualization: a virtual or interface call whose receiver is an object made in the same body has exactly one
/// target (ProgramSolution.CallTargets). Wider target sets are not used: the solution's constructed types come from
/// the IR only, and lowering's late imports can construct more.
///
/// Size (deterministic): a callee of at most Budget nodes after its plan is stripped (arguments and branches not
/// counted); twice that for [AggressiveInlining]. Per caller: at most MaxLevels levels, and at most
/// max(MinGrowth, the caller's own size) nodes added in all.
///
/// Switches:
///   - IL2WASM_NO_IR_INLINE=1: none (Pipeline.Inline);
///   - IL2WASM_NO_INLINE_HIDDEN=1: no hidden-frame callees that may throw;
///   - IL2WASM_NO_INLINE_DEVIRT=1: no devirtualized sites;
///   - IL2WASM_INLINE_BUDGET=n: the size budget;
///   - IL2WASM_INLINE_TRACE=1: each inlined site on stderr ("inline: caller <- callee").
/// Break twins, for the tests:
///   - IL2WASM_INLINE_TRACE_BREAK=1: inlines visible callees that may throw (a trace loses their frame);
///   - IL2WASM_INLINE_NULL_BREAK=1: ignores callvirt's null rule (a call on null runs the callee's first effects).</summary>
public static class Inline
{
    /// <summary>The size budget in IR nodes (the old front end's figure: FINISH.md stream K); a target.</summary>
    public const int Budget = 24;

    const int MaxLevels = 3;
    const int MinGrowth = 64;

    static readonly int s_budget = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_INLINE_BUDGET"), out var b) ? b : Budget;
    static readonly bool s_noHidden = Environment.GetEnvironmentVariable("IL2WASM_NO_INLINE_HIDDEN") == "1";
    static readonly bool s_noDevirt = Environment.GetEnvironmentVariable("IL2WASM_NO_INLINE_DEVIRT") == "1";
    static readonly bool s_trace = Environment.GetEnvironmentVariable("IL2WASM_INLINE_TRACE") == "1";
    static readonly bool s_traceBreak = Environment.GetEnvironmentVariable("IL2WASM_INLINE_TRACE_BREAK") == "1";
    static readonly int s_limit = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_INLINE_LIMIT"), out var lim) ? lim : int.MaxValue;
    static readonly bool s_nullBreak = Environment.GetEnvironmentVariable("IL2WASM_INLINE_NULL_BREAK") == "1";

    /// <summary>Sites inlined since the process started (all compilations).</summary>
    public static int SitesInlined;

    /// <summary>The facts the callee's own plan had for a node, carried to its copy in the caller (null: a node the
    /// splice made, or one of the caller's own). Plan.Build can start from them: they hold inside the callee
    /// whatever the caller is.</summary>
    public sealed record SourceFact(OwnFact Own, AllocKind Alloc, LoadKind Load, StoreKind Store, Memory.AccessKind Access, ArgConv[]? Args, Variant Callee);

    static readonly ConditionalWeakTable<Body, Dictionary<Node, SourceFact>> s_sourceFacts = new();

    /// <summary>The callee plan's facts for an inlined node of this body (null when none).</summary>
    public static SourceFact? SourceFactOf(Body body, Node n) =>
        s_sourceFacts.TryGetValue(body, out var d) && d.TryGetValue(n, out var f) ? f : null;

    /// <summary>Per caller plan: the levels done, the nodes each inlined node came with (its inline chain), growth.</summary>
    sealed class State
    {
        public int Level;
        public int Grown;
        public int Original;
        public readonly Dictionary<Node, (int Level, MethodDesc[] Chain)> Origin = new();
        /// <summary>Every method inlined into the caller, in first-inlined order.</summary>
        public readonly List<MethodDesc> Inlined = [];
        public readonly HashSet<MethodDesc> InlinedSet = [];
    }

    /// <summary>The methods inlined into this plan's body (each once, in the order first inlined). Their facts are
    /// the caller's now: lowering records each one's summary (ProgramFacts.Merge) as its own lowering would have, so
    /// the next round's facts still see their stores, calls and constants.</summary>
    public static IReadOnlyList<MethodDesc> InlinedInto(MethodPlan plan) => s_state.TryGetValue(plan, out var st) ? st.Inlined : [];

    static readonly ConditionalWeakTable<MethodPlan, State> s_state = new();

    /// <summary>A callee as it is spliced: its body with the plan stripped (copied per site), and what decides a site.</summary>
    sealed class Proto
    {
        public required Body Body;
        public required MethodPlan Plan;
        public int Size;
        /// <summary>A trace never shows its frame.</summary>
        public bool Hidden;
        /// <summary>Its first effect on every path is a dereference of this (a reference): it raises callvirt's
        /// NullReferenceException where the call would.</summary>
        public bool FirstEffectDerefsThis;
        /// <summary>It dereferences this (a reference) somewhere: only safe when this is known not null.</summary>
        public bool DerefsThis;
        /// <summary>It may throw or take a trace other than by dereferencing this.</summary>
        public bool MayThrow;
    }

    static readonly ConditionalWeakTable<ProgramSolution, Dictionary<MethodDesc, Proto?>> s_protos = new();

    /// <summary>One inlining step on the caller's planned body: one level of call sites (in block and node order) is
    /// replaced by the callees' bodies, which <paramref name="callee"/> gives for the entry each site uses (null: not
    /// inlined). Returns the level's first site, or null when nothing more is inlined (the body is then unchanged).
    /// After a non-null result the body holds no plan: Canon.Run plans it again.</summary>
    public static InlineSite? Step(PlanContext ctx, MethodPlan plan, Func<MethodDesc, Variant, (Body Body, MethodPlan Plan)?> callee)
    {
        // (a late import is outside the solution: nothing is known of its calls)
        if (ctx.Summary is null) return null;
        var body = ctx.Body;
        var st = s_state.GetValue(plan, _ => new State { Original = Size(body) });
        if (st.Level >= MaxLevels) return null;
        int level = st.Level++;
        var sites = new List<(Node Call, MethodDesc Target, Proto P, MethodDesc[] Chain)>();
        int growth = st.Grown, cap = Math.Max(MinGrowth, st.Original);
        foreach (var blk in body.Blocks)
        {
            if (InFilter(blk.Region)) continue;
            foreach (var n in blk.Code)
            {
                if (n.Op != Opc.Call || n.X is not MethodDesc m) continue;
                var (nl, chain) = st.Origin.TryGetValue(n, out var o) ? o : (0, []);
                if (nl != level) continue;
                bool knownNonNull = n.A.Length > 0 && IsKnownNonNull(n.A[0]);
                var kind = (CallKind)n.Sub;
                if (kind != CallKind.Direct)
                {
                    // (devirtualized: the receiver is made right here, its type exact)
                    if (s_noDevirt || n.A.Length == 0 || n.A[0].Op != Opc.NewObj) continue;
                    var targets = ctx.Solution.CallTargets(n);
                    if (targets.Count != 1) continue;
                    m = targets[0];
                }
                if (m == ctx.Method || chain.Contains(m)) continue;
                var origin = chain.Length > 0 ? chain[^1] : ctx.Method;
                if (ctx.Solution.Summary(origin) is not { } os || os.Intrinsics.Contains(m)) continue;
                if (ProtoOf(ctx.Solution, m, callee) is not { } p) continue;
                if (!SiteFits(n, p, knownNonNull)) continue;
                if (growth + p.Size > cap) continue;
                if (SitesInlined + sites.Count >= s_limit) continue;
                growth += p.Size;
                sites.Add((n, m, p, chain));
            }
        }
        if (sites.Count == 0) return null;
        if (s_trace) Console.Error.WriteLine($"strip: {ctx.Method} counts {body.Blocks.Sum(x => x.Code.Count(n => n.Op is Opc.Retain or Opc.Release))}");
        Strip(body);
        var facts = s_sourceFacts.GetValue(body, _ => new());
        (Node Call, MethodDesc Target, Block Head, Node TailTerm)? first = null;
        var touched = new List<Block>();
        foreach (var (call, target, p, chain) in sites)
        {
            var site = Splice(body, call, target, p, [.. chain, target], level + 1, st, facts, touched);
            first ??= site;
            if (st.InlinedSet.Add(target)) st.Inlined.Add(target);
            System.Threading.Interlocked.Increment(ref SitesInlined);
            if (s_trace) Console.Error.WriteLine($"inline: {ctx.Method} <- {target}");
        }
        st.Grown = growth;
        // (straight chains the splices made are one block again: the call's block with the callee's entry, a lone
        // return's block with the rest of the call's block)
        var gone = new HashSet<Block>();
        foreach (var x in touched) if (!gone.Contains(x)) MergeChain(body, x, gone);
        body.Blocks.RemoveAll(gone.Contains);
        for (int i = 0; i < body.Blocks.Count; i++) body.Blocks[i].Id = i;
        var f = first!.Value;
        return new InlineSite(f.Call, f.Target, f.Head, f.TailTerm.Block);
    }

    /// <summary>Takes a plan out of a body: its Retain and Release nodes, and the Moves bits.</summary>
    internal static void Strip(Body body)
    {
        foreach (var blk in body.Blocks)
        {
            blk.Code.RemoveAll(n => n.Op is Opc.Retain or Opc.Release);
            foreach (var n in blk.Phis) n.Moves = 0;
            foreach (var n in blk.Code) n.Moves = 0;
        }
    }

    static int Size(Body body)
    {
        int s = 0;
        foreach (var blk in body.Blocks)
        {
            s += blk.Phis.Count;
            foreach (var n in blk.Code) if (n.Op is not (Opc.Arg or Opc.Br or Opc.Retain or Opc.Release)) s++;
        }
        return s;
    }

    static bool InFilter(Region? r)
    {
        for (; r != null; r = r.Parent) if (r.Kind == RegionKind.Filter) return true;
        return false;
    }

    /// <summary>A reference that cannot be null: an object made here (newobj, a box, a string literal).</summary>
    static bool IsKnownNonNull(Node v) => v.Kind == VK.Ref && v.Op is Opc.NewObj or Opc.LdStr or Opc.Box
        && !(v.Op == Opc.Box && v.X is TypeDesc { IsNullable: true });

    static bool SiteFits(Node call, Proto p, bool knownNonNull)
    {
        // (struct values: a struct argument is a copy at an address; a struct result is written to the caller's)
        if (call.Kind == VK.Struct) return false;
        // (a call's address result keeps the objects of its arguments to the result's last use, by Ownership's
        // rules for calls: a returned byref or pointer, and the runtime's integer addresses Rt.AddressOf, ArrayData and
        // StringData. Inlined, only the derivations the body shows would keep them: an object could be freed under
        // its address)
        if (call.Kind == VK.Ptr || IsAddressOf(p.Body.Method)) return false;
        var args = p.Body.Args;
        if (args.Length != call.A.Length) return false;
        for (int i = 0; i < args.Length; i++) if (call.A[i].Kind != args[i].Kind || call.A[i].Kind == VK.Struct) return false;
        bool refThis = args.Length > 0 && !p.Body.Method.Signature.IsStatic && args[0].Kind == VK.Ref;
        if (refThis && call.Ovf && !knownNonNull && !p.FirstEffectDerefsThis && !s_nullBreak) return false;
        if (p.Hidden && !s_noHidden) return true;
        if (p.MayThrow && !s_traceBreak) return false;
        // (a `call` of an instance method can pass null: the callee's dereference would throw in its own frame)
        if (p.DerefsThis && !(knownNonNull || call.Ovf) && !s_traceBreak) return false;
        return true;
    }

    /// <summary>Ownership.IsAddressOf's methods: an int result that keeps its object as an address does.</summary>
    static bool IsAddressOf(MethodDesc m) => m.OwningType is MetadataType t && t.GetName() == "Rt" && t.GetNamespace() == "Il2Wasm.Runtime"
        && m.GetName() is "AddressOf" or "ArrayData" or "StringData";

    // ------------------------------------------------------------------ the callee

    static Proto? ProtoOf(ProgramSolution sol, MethodDesc m, Func<MethodDesc, Variant, (Body Body, MethodPlan Plan)?> callee)
    {
        var cache = s_protos.GetValue(sol, _ => new());
        if (cache.TryGetValue(m, out var p)) return p;
        p = MakeProto(sol, m, callee);
        cache[m] = p;
        return p;
    }

    static Proto? MakeProto(ProgramSolution sol, MethodDesc m, Func<MethodDesc, Variant, (Body Body, MethodPlan Plan)?> callee)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em) return null;
        if (em.IsNoInlining || em.IsSynchronized || em.IsPInvoke || em.IsInternalCall || em.IsRuntimeImplemented || em.IsAbstract) return null;
        if (TriggersCctor(m)) return null;
        if (sol.Summary(m) is not { } sum || sum.ObservesLifetime) return null;
        if (callee(m, Variant.Entry) is not { } c) return null;
        var body = c.Body;
        if (body.Regions.Count > 1 || body.Vars.Count > 0 || body.UsesLocAlloc || body.FiltersOnePass) return null;
        Strip(body);
        var entry = body.Blocks[0];
        if (entry.Preds.Count > 0 || entry.Phis.Count > 0) return null;
        if (!body.Blocks.Any(x => x.Term?.Op == Opc.Ret)) return null;
        var p = new Proto { Body = body, Plan = c.Plan, Size = Size(body), Hidden = IsHiddenFrame(m) };
        int limit = em.IsAggressiveInlining ? 2 * s_budget : s_budget;
        if (p.Size > limit) return null;
        var thisArg = !m.Signature.IsStatic && body.Args.Length > 0 && body.Args[0].Kind == VK.Ref ? body.Args[0] : null;
        foreach (var blk in body.Blocks)
            foreach (var n in blk.Phis.Concat(blk.Code))
            {
                if (n.Op is Opc.CatchVal or Opc.Leave or Opc.EndFinally or Opc.EndFilter or Opc.LocAlloc or Opc.Rethrow) return null;
                // (the returned value is the call's: of the return type's kind)
                if (n.Op == Opc.Ret && (n.A.Length > 0 ? n.A[0].Kind != Kinds.Of(m.Signature.ReturnType) : !m.Signature.ReturnType.IsVoid)) return null;
                switch (Throws(sol, n, thisArg))
                {
                    case Effect.DerefThis: p.DerefsThis = true; break;
                    case Effect.MayThrow: p.MayThrow = true; break;
                }
            }
        if (thisArg != null) p.FirstEffectDerefsThis = FirstEffectDerefsThis(body, thisArg, sol);
        return p;
    }

    /// <summary>A call of it runs a .cctor check (Statics.CctorCheckForCall).</summary>
    static bool TriggersCctor(MethodDesc m) =>
        !m.IsStaticConstructor && (m.Signature.IsStatic || m.IsConstructor || m.OwningType.IsValueType)
        && m.OwningType is MetadataType { IsBeforeFieldInit: false } t && t.GetStaticConstructor() != null
        || m.IsStaticConstructor;

    /// <summary>A frame no trace shows (DebugInfo.IsHiddenFrame).</summary>
    static bool IsHiddenFrame(MethodDesc m)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em) return true;
        if (m.OwningType.IsDelegate && m.GetName() == "Invoke") return true;
        if (em.HasCustomAttribute("System.Diagnostics", "StackTraceHiddenAttribute")) return true;
        for (var t = (MetadataType?)em.OwningType; t != null; t = t.ContainingType)
        {
            if (t is EcmaType et && et.HasCustomAttribute("System.Diagnostics", "StackTraceHiddenAttribute")) return true;
            if (t.ContainingType == null && t.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    enum Effect { None, DerefThis, MayThrow }

    /// <summary>Whether an operation can raise an exception or take a trace (lowering's checks: a reference's
    /// dereference checks null, an address's does not).</summary>
    static Effect Throws(ProgramSolution sol, Node n, Node? thisArg)
    {
        switch (n.Op)
        {
            case Opc.Const: case Opc.Arg: case Opc.LdNull: case Opc.LdStr: case Opc.Phi: case Opc.Cmp: case Opc.Un:
            case Opc.Br: case Opc.BrIf: case Opc.Switch: case Opc.Ret: case Opc.Retain: case Opc.Release:
            case Opc.IsInst: case Opc.Box: case Opc.SizeOf: case Opc.LdFtn: case Opc.LdInd: case Opc.StInd:
            case Opc.LdObj: case Opc.InitObj: case Opc.CpBlk: case Opc.InitBlk:
                return n.Op is Opc.LdObj or Opc.InitObj or Opc.LdInd or Opc.StInd && n.A.Length > 0 && n.A[0].Kind == VK.Ref ? Effect.MayThrow : Effect.None;
            case Opc.Bin:
                if (n.Ovf) return Effect.MayThrow;
                return (BinOp)n.Sub is BinOp.Div or BinOp.DivUn or BinOp.Rem or BinOp.RemUn && n.Kind is VK.I32 or VK.I64 or VK.Ptr ? Effect.MayThrow : Effect.None;
            case Opc.Conv:
                return n.Ovf ? Effect.MayThrow : Effect.None;
            case Opc.LdFld: case Opc.StFld: case Opc.LdFldA:
                if (n.A[0].Kind != VK.Ref) return Effect.None;
                return n.A[0] == thisArg ? Effect.DerefThis : Effect.MayThrow;
            case Opc.LdSFld: case Opc.StSFld: case Opc.LdSFldA:
                return n.X is FieldDesc f && f.OwningType.GetStaticConstructor() == null ? Effect.None : Effect.MayThrow;
            case Opc.Call when n.X is MethodDesc t && (CallKind)n.Sub == CallKind.Direct:
                if (TriggersCctor(t) || !sol.Covers(t) || sol.Facts(t).MayThrow || sol.Facts(t).ObservesLifetime) return Effect.MayThrow;
                if (n.Ovf && n.A.Length > 0 && n.A[0].Kind == VK.Ref) return n.A[0] == thisArg ? Effect.DerefThis : Effect.MayThrow;
                return Effect.None;
            default:
                return Effect.MayThrow;
        }
    }

    /// <summary>The first operation with an effect, on the straight path from the entry, is a dereference of this.</summary>
    static bool FirstEffectDerefsThis(Body body, Node thisArg, ProgramSolution sol)
    {
        var blk = body.Blocks[0];
        var seen = new HashSet<Block>();
        while (seen.Add(blk))
        {
            foreach (var n in blk.Code)
            {
                if (n.Op is Opc.LdFld or Opc.StFld or Opc.LdFldA && n.A[0] == thisArg) return true;
                if (n.Op == Opc.Call && (CallKind)n.Sub == CallKind.Direct && n.Ovf && n.A.Length > 0 && n.A[0] == thisArg) return true;
                if (n.Op == Opc.Br) break;
                if (n.Op is Opc.Const or Opc.Arg or Opc.LdNull or Opc.LdStr or Opc.Cmp or Opc.Un) continue;
                if (n.Op == Opc.Bin && Throws(sol, n, null) == Effect.None) continue;
                if (n.Op == Opc.Conv && !n.Ovf) continue;
                return false;
            }
            if (blk.Term?.Op != Opc.Br || blk.Succs.Count != 1) return false;
            blk = blk.Succs[0];
        }
        return false;
    }

    // ------------------------------------------------------------------ the splice

    static (Node, MethodDesc, Block, Node) Splice(Body body, Node call, MethodDesc target, Proto p, MethodDesc[] chain, int level, State st, Dictionary<Node, SourceFact> facts, List<Block> touched)
    {
        var (cb, map) = Clone(p.Body);
        foreach (var (src, copy) in map)
            facts[copy] = new SourceFact(p.Plan.Own[src], p.Plan.Allocs[src], p.Plan.Loads[src], p.Plan.Stores[src], p.Plan.Accesses[src], p.Plan.Args[src], p.Plan.Callees[src]);
        var blk = call.Block;
        int at = blk.Code.IndexOf(call);
        var region = blk.Region;
        // the rest of the call's block
        var tail = NewBlock(body, region);
        for (int i = at + 1; i < blk.Code.Count; i++) { blk.Code[i].Block = tail; tail.Code.Add(blk.Code[i]); }
        blk.Code.RemoveRange(at, blk.Code.Count - at);
        tail.Succs.AddRange(blk.Succs);
        foreach (var s in blk.Succs.Distinct()) for (int i = 0; i < s.Preds.Count; i++) if (s.Preds[i] == blk) s.Preds[i] = tail;
        blk.Succs.Clear();
        // the callee's blocks, in the call's region; its arguments are the call's operands
        var subst = new Dictionary<Node, Node>();
        for (int i = 0; i < cb.Args.Length; i++) subst[cb.Args[i]] = call.A[i];
        var rets = new List<(Block, Node?)>();
        foreach (var x in cb.Blocks)
        {
            x.Id = body.Blocks.Count; x.ILStart = -1; x.Region = region;
            region.Blocks.Add(x); body.Blocks.Add(x);
            x.Code.RemoveAll(n => n.Op == Opc.Arg);
            foreach (var n in x.Phis.Concat(x.Code))
            {
                n.Id = body.NextId(); n.ILOffset = call.ILOffset;
                for (int k = 0; k < n.A.Length; k++) if (subst.TryGetValue(n.A[k], out var r)) n.A[k] = r;
                st.Origin[n] = (level, chain);
            }
            if (x.Term is { Op: Opc.Ret } ret)
            {
                rets.Add((x, ret.A.Length > 0 ? ret.A[0] : null));
                x.Code[^1] = new Node { Op = Opc.Br, Kind = VK.Void, Block = x, Id = body.NextId(), ILOffset = call.ILOffset };
                x.Succs.Clear(); x.Succs.Add(tail); tail.Preds.Add(x);
            }
        }
        // the call's block branches into the callee's entry
        var entry = cb.Blocks[0];
        blk.Code.Add(new Node { Op = Opc.Br, Kind = VK.Void, Block = blk, Id = body.NextId(), ILOffset = call.ILOffset });
        blk.Succs.Add(entry); entry.Preds.Add(blk);
        touched.Add(blk);
        foreach (var (rb, _) in rets) touched.Add(rb);
        // the result: the one returned value, or a phi of them
        if (call.HasValue)
        {
            Node result;
            if (rets.Count == 1) result = rets[0].Item2!;
            else
            {
                result = new Node { Op = Opc.Phi, Kind = call.Kind, SType = call.SType, Type = call.Type, Block = tail, Id = body.NextId(), ILOffset = call.ILOffset, A = rets.Select(r => r.Item2!).ToArray() };
                tail.Phis.Add(result);
            }
            foreach (var x in body.Blocks)
            {
                foreach (var n in x.Phis) Replace(n, call, result);
                foreach (var n in x.Code) Replace(n, call, result);
            }
        }
        return (call, target, blk, tail.Code[^1]);
    }

    /// <summary>Joins a block and its successor while the block ends in a branch to a block that only it reaches, in
    /// the same region, with no phis and not a region's entry.</summary>
    static void MergeChain(Body body, Block a, HashSet<Block> gone)
    {
        while (a.Term is { Op: Opc.Br } && a.Succs.Count == 1)
        {
            var b = a.Succs[0];
            if (b == a || b.Preds.Count != 1 || b.Phis.Count > 0 || b.Region != a.Region || b == body.Blocks[0]) return;
            foreach (var r in body.Regions) if (r.Entry == b) return;
            a.Code.RemoveAt(a.Code.Count - 1);
            foreach (var n in b.Code) { n.Block = a; a.Code.Add(n); }
            a.Succs.Clear(); a.Succs.AddRange(b.Succs);
            foreach (var s in b.Succs.Distinct()) for (int i = 0; i < s.Preds.Count; i++) if (s.Preds[i] == b) s.Preds[i] = a;
            gone.Add(b); b.Region.Blocks.Remove(b);
        }
    }

    static void Replace(Node n, Node from, Node to)
    {
        for (int k = 0; k < n.A.Length; k++) if (n.A[k] == from) n.A[k] = to;
    }

    static Block NewBlock(Body body, Region region)
    {
        var x = new Block { Id = body.Blocks.Count, ILStart = -1, Region = region };
        region.Blocks.Add(x);
        body.Blocks.Add(x);
        return x;
    }

    /// <summary>A copy of a body with no regions or variables (blocks, nodes and arguments; the region is set by the
    /// splice), and the map from each node to its copy.</summary>
    static (Body, Dictionary<Node, Node>) Clone(Body src)
    {
        var dst = new Body { Method = src.Method, Root = src.Root };
        var bmap = new Dictionary<Block, Block>();
        foreach (var x in src.Blocks) bmap[x] = new Block { Id = x.Id, ILStart = x.ILStart, Region = x.Region };
        var nmap = new Dictionary<Node, Node>();
        foreach (var x in src.Blocks)
            foreach (var n in x.Phis.Concat(x.Code))
                nmap[n] = new Node
                {
                    Op = n.Op, Kind = n.Kind, SType = n.SType, Type = n.Type, X = n.X, I = n.I, D = n.D, Sub = n.Sub, Ovf = n.Ovf,
                    Unsigned = n.Unsigned, Small = n.Small, Block = bmap[x], Id = n.Id, ILOffset = n.ILOffset,
                };
        foreach (var x in src.Blocks)
        {
            var y = bmap[x];
            foreach (var n in x.Phis) y.Phis.Add(nmap[n]);
            foreach (var n in x.Code) y.Code.Add(nmap[n]);
            foreach (var s in x.Succs) y.Succs.Add(bmap[s]);
            foreach (var s in x.Preds) y.Preds.Add(bmap[s]);
            dst.Blocks.Add(y);
        }
        foreach (var (o, c) in nmap) c.A = o.A.Select(a => nmap.TryGetValue(a, out var m) ? m : a).ToArray();
        dst.Args = src.Args.Select(a => nmap.TryGetValue(a, out var m) ? m : a).ToArray();
        return (dst, nmap);
    }
}
