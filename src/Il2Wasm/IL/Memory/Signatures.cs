#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Memory;

/// <summary>Ownership signatures, dispatch-group signatures, the call rule and the specialization policy
/// (docs/MEMORY-IR.md section 4.1 to 4.5). S1 agent a's file. Nothing reads it unless IL2WASM_MEM_PLAN=1.
/// <para><b>Entries and variants</b> (agreed with rows c and f, 2026-11-24). Every method's own function, the
/// <see cref="Variant.Entry"/>, has the all-Lend convention: every parameter is <c>Guaranteed(Arg(i))</c> inside and
/// the result is owned. That is what every caller that is not a planned IR call site assumes (generated code:
/// rt.destroy's finalizer calls, dispatch stubs, a delegate's Invoke, the start code, activation; and the late
/// imports), so no HandOver is ever seen by a caller that cannot hand over. A HandOver convention lives only in an
/// owned variant (<c>|own&lt;mask&gt;</c>, bit i = parameter i, this = 0), and a variant is only ever called from a
/// planned direct call site (a direct Call, or a NewObj of a class that is not a string or a delegate). Virtual and
/// interface sites use the Entry in S1: a group join needs per-group dispatch stubs, which S1 does not build. So 4.4's
/// "one entry with the owned convention" for large methods is the owned variant every planned direct caller uses;
/// the Entry exists only if some other caller asks for it.</para>
/// <para><b>Demand</b> (<see cref="Demand"/>, stored in <c>Facts(m).Signature</c>): parameter i is HandOver when the
/// callee takes its count at its last use on some path (reason Stores: a store, a return, a throw, a box, or a pass to
/// a callee's HandOver parameter at a direct site), or keeps running after its own last use (reason KeepsRunning,
/// 4.3). It is a least fixed point over the call graph: demand only grows.</para></summary>
public static class Signatures
{
    /// <summary>IL2WASM_NO_HANDOVER=1: the call rule never hands over (every parameter Lend; no owned variant).</summary>
    static readonly bool s_noHandOver = Environment.GetEnvironmentVariable("IL2WASM_NO_HANDOVER") == "1";
    /// <summary>IL2WASM_NO_OWNED_ENTRY=1: no owned variant (every call at the Entry, so nothing is handed over either:
    /// in S1 a HandOver exists only through a variant).</summary>
    static readonly bool s_noOwnedEntry = Environment.GetEnvironmentVariable("IL2WASM_NO_OWNED_ENTRY") == "1";
    /// <summary>IL2WASM_NO_BORROW_RETURN=1: no borrowed-return variant.</summary>
    static readonly bool s_noBorrowReturn = Environment.GetEnvironmentVariable("IL2WASM_NO_BORROW_RETURN") == "1";
    /// <summary>IL2WASM_HANDOVER_BREAK=1 (the rule's test): the call rule hands a last-use argument over, but the call
    /// goes to the Entry, which lends: the count handed over is never released (a leak the live-object check shows).</summary>
    static readonly bool s_handOverBreak = Environment.GetEnvironmentVariable("IL2WASM_HANDOVER_BREAK") == "1";
    /// <summary>IL2WASM_BORROW_RETURN_BREAK=1 (the rule's test): a method whose result is read from a parameter's path
    /// returns it borrowed even when the method writes or calls before it returns.</summary>
    static readonly bool s_borrowReturnBreak = Environment.GetEnvironmentVariable("IL2WASM_BORROW_RETURN_BREAK") == "1";
    /// <summary>IL2WASM_SIG_STATS=1 (or IL2WASM_MEM_TRACE=1): the measurements of Compute on stderr.</summary>
    static readonly bool s_stats = Environment.GetEnvironmentVariable("IL2WASM_SIG_STATS") is "1" or "2" || Environment.GetEnvironmentVariable("IL2WASM_MEM_TRACE") == "1";
    /// <summary>IL2WASM_SIG_DUMP=name: every analyzed method whose name contains it, with its demand signature.</summary>
    static readonly string? s_dump = Environment.GetEnvironmentVariable("IL2WASM_SIG_DUMP") is { Length: > 0 } d ? d : null;

    /// <summary>The size caps of 4.4 (targets, Q9: size decides): at most <see cref="SmallMethod"/> IR nodes, an owned
    /// variant for every mask its last-use sites need; up to <see cref="LargeMethod"/>, the lend entry and the all-HandOver
    /// variant; above, one owned variant every planned caller uses.</summary>
    public const int SmallMethod = 25, LargeMethod = 400;
    /// <summary>A small method's masks are enumerated only up to this many HandOver parameters (2^4 - 1 = 15 variants);
    /// past it, the full mask and the single-parameter masks.</summary>
    const int MaxEnumerated = 4;
    /// <summary>Parameters past this index are always Lend (the mask is a uint).</summary>
    const int MaxParams = 32;

    // ------------------------------------------------------------------ per-solution state

    /// <summary>One method's local facts, from one pass over its body.</summary>
    sealed class Local
    {
        public int Size;
        /// <summary>Parameters that hold counts (references, structs with references) and are analyzed.</summary>
        public uint Counted;
        /// <summary>Parameters stored, returned, thrown or boxed at a last use.</summary>
        public uint StoresAtLastUse;
        /// <summary>Per parameter: a loop header where it is dead.</summary>
        public uint DeadLoop;
        /// <summary>Per parameter: calls where it is dead (null: a call that always counts: indirect, virtual, not in the
        /// solution, an intrinsic that is not cheap).</summary>
        public List<MethodDesc?>[] DeadCalls = [];
        /// <summary>Last-use passes to a direct site: (parameter, callee, callee parameter).</summary>
        public readonly List<(int Param, MethodDesc Callee, int CalleeParam)> PassOn = [];
        /// <summary>The body has a loop, or a call that always counts (for a caller that inlines this method).</summary>
        public bool HasLoop;
        public readonly List<MethodDesc?> Calls = [];
        public BorrowPath? ReturnBorrow;
    }

    sealed class State
    {
        public readonly Dictionary<MethodDesc, Local> Locals = new();
        public readonly HashSet<MethodDesc> Long = [];
        public bool ReleasesRunCode;
        public bool Computed;
    }

    static readonly ConditionalWeakTable<ProgramSolution, State> s_states = new();

    static State StateOf(ProgramSolution sol) => s_states.GetValue(sol, NewState);

    static State NewState(ProgramSolution sol)
    {
        var st = new State();
        // (3.6: a release can run program code when some constructed type has a finalizer, or a weak-handle table can
        // be written by a destroy)
        foreach (var t in sol.Constructed) if (Il2Wasm.IL.Lower.IlCompiler.FinalizerOf(t) != null) { st.ReleasesRunCode = true; break; }
        if (!st.ReleasesRunCode)
            for (int i = 0; i < sol.Methods.Count; i++)
                if (sol.Methods[i] is { } m && m.GetName() == "SetAt" && m.OwningType is MetadataType om && om.GetName() == "ObjectMarks") { st.ReleasesRunCode = true; break; }
        return st;
    }

    // ------------------------------------------------------------------ Compute

    /// <summary>After the solve: every reached method's demand signature (4.1, 4.3: HandOver by store or by
    /// KeepsRunningAfter) and borrowed return (4.4, Q4), into <c>Solution.Facts(m)</c>, to the least fixed point.</summary>
    public static void Compute(ProgramSolution solution)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var st = StateOf(solution);
        if (st.Computed) return;
        st.Computed = true;
        // the intrinsics' effects from the hand table (the hook is the solution's, read when facts are solved)
        ProgramSolution.IntrinsicEffects = (s, caller, m) => IntrinsicSignatures.Effects(s, caller, m, StateOf(s).ReleasesRunCode);
        ProgramSolution.DeclaredEffects = IntrinsicSignatures.DeclaredEffects;
        int n = solution.Methods.Count;
        // local facts, one pass per body (in MethodId order: deterministic)
        for (int id = 0; id < n; id++)
        {
            var m = solution.Methods[id];
            if (solution.BodyOf(m) is not { } body) continue;
            st.Locals[m] = Analyze(solution, st, m, body);
        }
        // the methods that run long when inlined (a loop, or a call that counts): least fixed point
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int id = 0; id < n; id++)
            {
                var m = solution.Methods[id];
                if (st.Long.Contains(m) || !st.Locals.TryGetValue(m, out var l)) continue;
                if (l.HasLoop || l.Calls.Any(c => c == null || c == m || !NotFurther(solution, st, c))) { st.Long.Add(m); changed = true; }
            }
        }
        // the demand: KeepsRunning is local (given Long); Stores grows over the call graph
        var demand = new Dictionary<MethodDesc, OwnershipSignature>();
        for (int id = 0; id < n; id++)
        {
            var m = solution.Methods[id];
            var sig = OwnershipSignature.Bottom(m);
            demand[m] = sig;
            if (s_noHandOver || s_noOwnedEntry || !st.Locals.TryGetValue(m, out var l)) continue;
            for (int i = 0; i < sig.Params.Length && i < MaxParams; i++)
            {
                uint bit = 1u << i;
                if ((l.Counted & bit) == 0) continue;
                var r = HandOverReason.None;
                if ((l.StoresAtLastUse & bit) != 0) r |= HandOverReason.Stores;
                if ((l.DeadLoop & bit) != 0 || l.DeadCalls[i].Any(c => c == null || !NotFurther(solution, st, c))) r |= HandOverReason.KeepsRunning;
                if (r != 0) { sig.Params[i] = ParamConv.HandOver; sig.Reasons[i] = r; }
            }
        }
        int rounds = 0;
        for (bool changed = true; changed; rounds++)
        {
            changed = false;
            for (int id = 0; id < n; id++)
            {
                var m = solution.Methods[id];
                if (!st.Locals.TryGetValue(m, out var l)) continue;
                var sig = demand[m];
                foreach (var (p, callee, cp) in l.PassOn)
                {
                    if (!demand.TryGetValue(callee, out var cs) || cp >= cs.Params.Length || cs.Params[cp] != ParamConv.HandOver) continue;
                    var r = sig.Reasons[p] | (cs.Reasons[cp] & (HandOverReason.Stores | HandOverReason.KeepsRunning));
                    if (sig.Params[p] == ParamConv.HandOver && r == sig.Reasons[p]) continue;
                    sig.Params[p] = ParamConv.HandOver; sig.Reasons[p] = r; changed = true;
                }
            }
        }
        // (IL2WASM_HANDOVER_ONLY=lo-hi, a bisect aid: only a method with a HandOver parameter whose name hashes into [lo, hi)
        // of 65536 keeps its demand; IL2WASM_HANDOVER_LIST=1 lists them)
        if (Environment.GetEnvironmentVariable("IL2WASM_HANDOVER_ONLY") is { Length: > 0 } only)
        {
            var parts = only.Split('-');
            int lo = int.Parse(parts[0]), hi = int.Parse(parts[1]), j = 0;
            for (int id = 0; id < n; id++)
            {
                var m = solution.Methods[id];
                if (!demand[m].Params.Contains(ParamConv.HandOver)) continue;
                uint h = 2166136261; foreach (char ch in m.ToString()!) h = (h ^ ch) * 16777619;
                int b = (int)(h % 65536);
                if (Environment.GetEnvironmentVariable("IL2WASM_HANDOVER_LIST") == "1" && b >= lo && b < hi) Console.Error.WriteLine($"handover: {b} {m} {demand[m]}");
                if (b < lo || b >= hi) demand[m] = OwnershipSignature.Bottom(m);
                j++;
            }
            Console.Error.WriteLine($"handover: {j} methods hand over");
        }
        for (int id = 0; id < n; id++)
        {
            var m = solution.Methods[id];
            var f = solution.Facts(m);
            var sig = demand[m];
            if (!s_noBorrowReturn && st.Locals.TryGetValue(m, out var l) && l.ReturnBorrow != null) { sig.ReturnBorrow = l.ReturnBorrow; f.BorrowReturn = l.ReturnBorrow; }
            f.Signature = sig;
            if (s_dump != null && m.ToString()!.Contains(s_dump)) Console.Error.WriteLine($"sig: {m} {sig} reasons({string.Join(",", sig.Reasons)}) size {(st.Locals.TryGetValue(m, out var dl) ? dl.Size : -1)} variants [{string.Join(",", VariantsOf(solution, m).Select(v => v.Suffix))}]");
        }
        if (s_stats) Report(solution, st, rounds, clock.ElapsedMilliseconds);
        Observed?.Invoke(solution);
    }

    /// <summary>Called with each solution Compute finishes (the tests read the signatures through it).</summary>
    public static Action<ProgramSolution>? Observed;

    /// <summary>A call that is not a further call for the call rule (Q8): one inlined there that runs nothing long, or
    /// a throw helper (a callee that never returns normally and has no loop: the path it is on ends by an exception
    /// at once, it does not keep running).</summary>
    static bool NotFurther(ProgramSolution sol, State st, MethodDesc c) =>
        ShortInline(sol, st, c) || sol.Summary(c) is { MayReturn: false } && st.Locals.TryGetValue(c, out var l) && !l.HasLoop;

    /// <summary>The callee is inlined at a direct site and runs nothing long (3.7's policy: the budget, or
    /// AggressiveInlining up to the large cap): then the call is not a further call (Q8).</summary>
    static bool ShortInline(ProgramSolution sol, State st, MethodDesc c)
    {
        if (!Pipeline.Inline || !st.Locals.TryGetValue(c, out var l) || st.Long.Contains(c)) return false;
        return l.Size <= Inline.Budget || c.GetTypicalMethodDefinition() is EcmaMethod { IsAggressiveInlining: true } && l.Size <= LargeMethod;
    }

    // ------------------------------------------------------------------ the local pass

    /// <summary>A counted parameter: a reference, or a struct with references (Q8: the rule covers this and structs);
    /// a byref, a pointer and a value type's this are addresses, never counted.</summary>
    static bool CountedParam(MethodDesc m, int i)
    {
        bool inst = !m.Signature.IsStatic;
        if (inst && i == 0) return !m.OwningType.IsValueType;
        var t = m.Signature[i - (inst ? 1 : 0)];
        // (a reference only: a struct parameter arrives as an address into the caller's copy, which the callee's plan
        // does not count, so an owned variant would never release a struct handed over to it)
        return Kinds.Of(t) == VK.Ref && Kinds.Counted(t);
    }

    /// <summary>The parameter (this = 0) a call operand is passed to: the operand index for a call, one more for a
    /// constructor (its this is the new object), -1 for no parameter (a function pointer's).</summary>
    public static int ParamOf(Node call, int operand) => call.Op switch
    {
        Opc.NewObj => operand + 1,
        Opc.Call => operand,
        _ => -1,
    };

    /// <summary>A call site that may go to a variant (row f: only direct Call and NewObj sites): its callee, or null.</summary>
    static MethodDesc? DirectSite(Node call)
    {
        if (call.X is not MethodDesc m) return null;
        if (call.Op == Opc.Call) return (CallKind)call.Sub == CallKind.Direct ? m : null;
        if (call.Op == Opc.NewObj)
        {
            var t = m.OwningType;
            if (t.IsString || t.IsDelegate || t is ArrayType) return null;
            return m;
        }
        return null;
    }

    static bool Forwards(Node n) => n.Op is Opc.CastClass or Opc.IsInst
        || n.Op == Opc.Call && n.Kind == VK.Ref && n.A.Length == 1 && n.A[0].Kind == VK.Ref && n.X is MethodDesc m && m.GetName() == "As"
            && m.OwningType is MetadataType ut && ut.GetName() == "Unsafe";

    /// <summary>An address or a by-ref-like value made from its operands (Ownership's Derives): it keeps them alive.</summary>
    static bool Derived(Node n) => n.Kind == VK.Ptr && n.Op is Opc.LdFldA or Opc.LdElemA or Opc.Unbox or Opc.Bin or Opc.Conv or Opc.Call or Opc.CallI or Opc.RefAnyVal
        || n.Kind == VK.Struct && n.Type is DefType { IsByRefLike: true } && n.Op is Opc.Call or Opc.CallI or Opc.NewObj or Opc.LdObj or Opc.MkRefAny;

    /// <summary>The finally and fault ends, followed by their try's leave targets (as Ownership's liveness).</summary>
    static Dictionary<Block, List<Block>> FinallyEnds(Body body)
    {
        var d = new Dictionary<Block, List<Block>>();
        foreach (var r in body.Regions)
        {
            if (r.Kind is not (RegionKind.Finally or RegionKind.Fault)) continue;
            var inside = Ownership.BlocksWithin(r.Try!).ToHashSet();
            var targets = inside.SelectMany(b => b.Succs).Where(t => !inside.Contains(t)).Distinct().ToList();
            foreach (var b in Ownership.BlocksWithin(r)) if (b.Term?.Op == Opc.EndFinally) d[b] = targets;
        }
        return d;
    }

    static Local Analyze(ProgramSolution sol, State st, MethodDesc m, Body body)
    {
        var l = new Local();
        int np = Math.Min(body.Args.Length, MaxParams);
        l.DeadCalls = new List<MethodDesc?>[np];
        for (int i = 0; i < np; i++) { l.DeadCalls[i] = []; if (CountedParam(m, i)) l.Counted |= 1u << i; }
        foreach (var b in body.Blocks) l.Size += b.Phis.Count + b.Code.Count;
        var intrinsics = sol.Summary(m)?.Intrinsics.ToHashSet() ?? [];
        // which nodes are a parameter (or forward it, or are an address into it): a bit per parameter
        var mask = new uint[body.ValueCount + 1];
        var slot = new uint[body.Vars.Count];
        for (int s = 0; s < body.Vars.Count; s++) if (body.Vars[s].ArgIndex is int ai and >= 0 and < MaxParams) slot[s] |= 1u << ai;
        for (int i = 0; i < np; i++) mask[body.Args[i].Id] = 1u << i;
        uint Bits(Node x) => x.Id < mask.Length ? mask[x.Id] : 0;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var b in body.Blocks)
                foreach (var x in b.Phis.Concat(b.Code))
                {
                    uint v = 0;
                    if (x.IsPhi || Forwards(x) || Derived(x)) foreach (var a in x.A) if (a != null) v |= Bits(a);
                    if (x.Op is Opc.VarLd or Opc.LocA && x.I < slot.Length) v |= slot[(int)x.I];
                    if (x.Op == Opc.VarSt && x.A.Length > 0 && x.I < slot.Length && (slot[(int)x.I] | Bits(x.A[0])) != slot[(int)x.I]) { slot[(int)x.I] |= Bits(x.A[0]); changed = true; }
                    if (x.Id < mask.Length && (mask[x.Id] | v) != mask[x.Id]) { mask[x.Id] |= v; changed = true; }
                }
        }
        // the parameters a node uses (an operand that is one, or a read of a variable that holds one)
        uint Uses(Node x)
        {
            uint u = 0;
            foreach (var a in x.A) if (a != null) u |= Bits(a);
            if (x.Op is Opc.VarLd or Opc.LocA && x.I < slot.Length) u |= slot[(int)x.I];
            return u;
        }
        // liveness of the parameters per block (handlers are successors of their try's blocks; a finally's end is
        // followed by its try's leave targets; a phi's operand is used at the end of its predecessor)
        var ends = FinallyEnds(body);
        var idx = new Dictionary<Block, int>();
        for (int i = 0; i < body.Blocks.Count; i++) idx[body.Blocks[i]] = i;
        int nb = body.Blocks.Count;
        var useIn = new uint[nb]; var edgeUse = new uint[nb]; var liveIn = new uint[nb]; var liveOut = new uint[nb];
        var succs = new List<Block>[nb];
        for (int bi = 0; bi < nb; bi++)
        {
            var b = body.Blocks[bi];
            foreach (var x in b.Code) useIn[bi] |= Uses(x);
            foreach (var s in b.Succs)
            {
                int pi = s.Preds.IndexOf(b);
                if (pi >= 0) foreach (var phi in s.Phis) if (pi < phi.A.Length && phi.A[pi] != null) edgeUse[bi] |= Bits(phi.A[pi]);
            }
            succs[bi] = b.Succs.Concat(ends.GetValueOrDefault(b) ?? []).Concat(Ownership.ExceptionSuccs(b)).Distinct().ToList();
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int bi = nb - 1; bi >= 0; bi--)
            {
                uint o = edgeUse[bi];
                foreach (var s in succs[bi]) o |= liveIn[idx[s]];
                uint i = o | useIn[bi];
                if (o != liveOut[bi] || i != liveIn[bi]) { liveOut[bi] = o; liveIn[bi] = i; changed = true; }
            }
        }
        // loop headers (targets of retreating edges, depth-first from the entry)
        var headers = new HashSet<Block>();
        {
            var state = new byte[nb];
            var stack = new Stack<(Block b, int k)>();
            stack.Push((body.Blocks[0], 0)); state[0] = 1;
            while (stack.Count > 0)
            {
                var (b, k) = stack.Pop();
                if (k < b.Succs.Count)
                {
                    stack.Push((b, k + 1));
                    var s = b.Succs[k]; int si = idx[s];
                    if (state[si] == 1) headers.Add(s);
                    else if (state[si] == 0) { state[si] = 1; stack.Push((s, 0)); }
                }
                else state[idx[b]] = 2;
            }
        }
        foreach (var h in headers) l.DeadLoop |= l.Counted & ~liveIn[idx[h]];
        if (headers.Count > 0) l.HasLoop = true;
        // last uses and the points where a parameter is dead, backwards through each block
        for (int bi = 0; bi < nb; bi++)
        {
            var b = body.Blocks[bi];
            uint live = liveOut[bi];
            for (int k = b.Code.Count - 1; k >= 0; k--)
            {
                var x = b.Code[k];
                uint u = Uses(x) & l.Counted;
                MethodDesc? callee = null;
                bool isCall = x.Op is Opc.Call or Opc.NewObj or Opc.CallI;
                bool further = false;
                if (isCall)
                {
                    var target = x.X as MethodDesc;
                    if (x.Op == Opc.Call && target != null && intrinsics.Contains(target))
                        further = !IntrinsicSignatures.IsCheap(target, st.ReleasesRunCode);
                    else
                    {
                        further = true;
                        callee = DirectSite(x) is { } dc && sol.Covers(dc) ? dc : null;
                    }
                    if (further) l.Calls.Add(callee);
                }
                // a further call where a parameter is dead (not used here or after)
                if (further) { uint dead = l.Counted & ~live & ~u; for (int i = 0; i < np; i++) if ((dead & (1u << i)) != 0) l.DeadCalls[i].Add(callee); }
                uint last = u & ~live;
                if (last != 0)
                {
                    var direct = DirectSite(x);
                    for (int a = 0; a < x.A.Length; a++)
                    {
                        var op = x.A[a];
                        if (op == null || op.Kind is not (VK.Ref or VK.Struct)) continue;
                        uint bits = Bits(op) & last;
                        if (bits == 0) continue;
                        if (x.Op != Opc.VarSt && Ownership.Consumes(x, a)) l.StoresAtLastUse |= bits;
                        else if (direct != null && sol.Covers(direct) && !intrinsics.Contains(direct) && ParamOf(x, a) is int cp and >= 0 and < MaxParams)
                            for (int i = 0; i < np; i++) if ((bits & (1u << i)) != 0) l.PassOn.Add((i, direct, cp));
                    }
                }
                live |= u;
            }
        }
        for (int i = 0; i < np; i++) l.DeadCalls[i] = l.DeadCalls[i].Distinct().ToList();
        l.ReturnBorrow = ReturnPath(sol, m, body);
        return l;
    }

    /// <summary>The borrowed return of 4.4 (getters, <c>return this</c>, an element of a field's array): every return
    /// gives the same path from one reference parameter, at most three field or element steps, and nothing in the
    /// method writes or calls (so the path holds from the load to the return; the caller's proofs take it from there).</summary>
    static BorrowPath? ReturnPath(ProgramSolution sol, MethodDesc m, Body body)
    {
        if (m.Signature.ReturnType is var rt && (rt.IsVoid || Kinds.Of(rt) != VK.Ref)) return null;
        BorrowPath? path = null;
        bool any = false;
        foreach (var b in body.Blocks)
            foreach (var x in b.Code)
            {
                switch (x.Op)
                {
                    case Opc.Ret:
                    {
                        var p = x.A.Length == 1 ? PathOf(sol, body, x.A[0]) : null;
                        if (p == null || any && !BorrowPath.Same(p, path)) return null;
                        path = p; any = true;
                        break;
                    }
                    case Opc.Call or Opc.CallI or Opc.NewObj or Opc.NewArr or Opc.Box or Opc.Throw or Opc.Rethrow or Opc.LocAlloc:
                        if (!s_borrowReturnBreak) return null;
                        break;
                    case Opc.StFld or Opc.StSFld or Opc.StElem or Opc.StInd or Opc.CpBlk or Opc.InitBlk or Opc.InitObj:
                        if (!s_borrowReturnBreak) return null;
                        break;
                }
            }
        return path;
    }

    static BorrowPath? PathOf(ProgramSolution sol, Body body, Node v)
    {
        var steps = new List<PathStep>();
        for (int guard = 0; guard < 16; guard++)
        {
            if (Forwards(v)) { v = v.A[0]; continue; }
            switch (v.Op)
            {
                case Opc.Arg when v.Kind == VK.Ref:
                    if (steps.Count > BorrowPath.MaxSteps) return null;
                    steps.Reverse();
                    return new BorrowPath(RootKind.Arg, (int)v.I, steps.ToArray());
                case Opc.LdFld or Opc.LdFldA when v.X is FieldDesc { IsStatic: false } f && v.A.Length == 1:
                    steps.Add(new PathStep(sol.Keys.Intern(MemKey.Field(f)), false));
                    v = v.A[0];
                    continue;
                case Opc.LdElem or Opc.LdElemA when v.A.Length >= 1 && v.A[0].Type is ArrayType { IsSzArray: true } at:
                    steps.Add(new PathStep(sol.Keys.Intern(MemKey.Elem(at.ElementType)), true));
                    v = v.A[0];
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ the frozen interface

    /// <summary>A method's Entry convention: every parameter Lend, the result owned (for every method, in the solution or
    /// not: what every caller that is not a planned direct site assumes; an uncovered method is the naive convention).</summary>
    public static OwnershipSignature Of(ProgramSolution solution, MethodDesc m) => OwnershipSignature.Bottom(m);

    /// <summary>The computed demand signature (HandOver with reasons, the borrowed-return path): what the owned and
    /// borrowed-return variants are made from. The bottom (all Lend) for a method outside the solution, or before
    /// <see cref="Compute"/>.</summary>
    public static OwnershipSignature Demand(ProgramSolution solution, MethodDesc m)
    {
        if (!solution.Covers(m)) return OwnershipSignature.Bottom(m);
        return solution.Facts(m).Signature ?? OwnershipSignature.Bottom(m);
    }

    /// <summary>The convention a plan of entry <paramref name="v"/> of m uses (row c): the Entry's (all Lend); an owned
    /// variant's HandOver exactly at its mask's parameters; a borrowed-return variant's all Lend with the result
    /// borrowed from the demand's path.</summary>
    public static OwnershipSignature ConventionOf(ProgramSolution solution, MethodDesc m, Variant v)
    {
        var sig = OwnershipSignature.Bottom(m);
        switch (v.Kind)
        {
            case VariantKind.Owned:
            {
                var d = Demand(solution, m);
                for (int i = 0; i < sig.Params.Length && i < MaxParams; i++)
                    if ((v.Mask & (1u << i)) != 0) { sig.Params[i] = ParamConv.HandOver; sig.Reasons[i] = i < d.Reasons.Length ? d.Reasons[i] : HandOverReason.None; }
                break;
            }
            case VariantKind.BorrowReturn:
                sig.ReturnBorrow = Demand(solution, m).ReturnBorrow;
                break;
        }
        return sig;
    }

    /// <summary>Entry v takes parameter i owned (bit i of an owned variant's mask; this = 0).</summary>
    public static bool Owns(Variant v, int parameter) => v.Kind == VariantKind.Owned && parameter is >= 0 and < MaxParams && (v.Mask & (1u << parameter)) != 0;

    /// <summary>A call site's group signature: the join over its targets' Entry conventions (4.5). In S1 every
    /// virtual and interface site calls the Entry, so this is all Lend; <see cref="GroupDemand"/> is the join of the
    /// targets' demand (reported, for S2's per-group stubs).</summary>
    public static OwnershipSignature GroupOf(ProgramSolution solution, Node call)
    {
        var m = (MethodDesc)call.X!;
        var g = OwnershipSignature.Bottom(m);
        foreach (var t in solution.CallTargets(call)) g.JoinFrom(Of(solution, t));
        return g;
    }

    /// <summary>The join of the demand over a call site's reachable targets only (4.5: receiver types, not every
    /// override of the slot); a target in the group that lends where the join hands over adds reason Group.</summary>
    public static OwnershipSignature GroupDemand(ProgramSolution solution, Node call)
    {
        var m = (MethodDesc)call.X!;
        var g = OwnershipSignature.Bottom(m);
        var targets = solution.CallTargets(call);
        foreach (var t in targets) g.JoinFrom(Demand(solution, t));
        if (targets.Count > 1)
            for (int i = 0; i < g.Params.Length; i++)
                if (g.Params[i] == ParamConv.HandOver && targets.Any(t => Demand(solution, t) is var d && i < d.Params.Length && d.Params[i] == ParamConv.Lend)) g.Reasons[i] |= HandOverReason.Group;
        return g;
    }

    /// <summary>The call rule (4.3) for one counted operand (an index into call.A): HandOver at its last use to a
    /// parameter the callee's demand hands over, at a direct site that can call an owned variant; else Lend. For a
    /// large callee (one owned variant every planned caller uses) a live-on operand of a HandOver parameter is
    /// KeepAlive. The final convention is decided by the variant ChooseVariant picks: operand k is owned by the call
    /// iff <c>Owns(v, ParamOf(call, k))</c> (HandOver at a last use, KeepAlive otherwise), else Lend.</summary>
    public static ArgConv CallRule(PlanContext ctx, Node call, int arg, bool lastUse)
    {
        if (s_noHandOver || s_noOwnedEntry || DirectSite(call) is not { } callee || !ctx.Solution.Covers(callee)) return ArgConv.Lend;
        int p = ParamOf(call, arg);
        var d = Demand(ctx.Solution, callee);
        if (p < 0 || p >= d.Params.Length || p >= MaxParams || d.Params[p] != ParamConv.HandOver) return ArgConv.Lend;
        if (lastUse) return ArgConv.HandOver;
        return SizeOf(ctx.Solution, callee) > LargeMethod ? ArgConv.KeepAlive : ArgConv.Lend;
    }

    /// <summary>KeepsRunningAfter(m, i) (4.3, Q8): a path from a last use of parameter i to an exit passes a loop header
    /// or a call that is not inlined there (or is inlined but runs long itself) and is not a cheap intrinsic;
    /// exceptional exits count (liveness runs through the handlers).</summary>
    public static bool KeepsRunningAfter(ProgramSolution solution, Body body, int parameter)
    {
        var st = StateOf(solution);
        if (parameter < 0 || parameter >= MaxParams || parameter >= body.Args.Length) return false;
        var l = st.Locals.TryGetValue(body.Method, out var known) ? known : Analyze(solution, st, body.Method, body);
        if ((l.Counted & (1u << parameter)) == 0) return false;
        return (l.DeadLoop & (1u << parameter)) != 0 || l.DeadCalls[parameter].Any(c => c == null || !NotFurther(solution, st, c));
    }

    static int SizeOf(ProgramSolution sol, MethodDesc m) => StateOf(sol).Locals.TryGetValue(m, out var l) ? l.Size : int.MaxValue;

    /// <summary>The callee entry a call uses (4.4), for the parameters handed over at their last use
    /// (<paramref name="handedOverMask"/>, bit i = parameter i): a small method's owned variant for exactly that mask;
    /// a medium one's all-HandOver variant when any is handed over; a large one's all-HandOver variant always (when it
    /// has HandOver parameters); else the borrowed-return variant when the method has one; else the Entry. Only direct
    /// sites (row f).</summary>
    public static Variant ChooseVariant(PlanContext ctx, Node call, uint handedOverMask)
    {
        if (DirectSite(call) is not { } callee || !ctx.Solution.Covers(callee)) return Variant.Entry;
        var vs = VariantsOf(ctx.Solution, callee);
        if (vs.Count == 0) return Variant.Entry;
        uint all = HandOverMask(Demand(ctx.Solution, callee));
        uint mask = handedOverMask & all;
        if (mask != 0 && s_handOverBreak) return Variant.Entry;
        int size = SizeOf(ctx.Solution, callee);
        // (a NewObj's this is the new object: the caller holds its only count and no operand passes it, so no count
        // could be handed over for it; a variant that owns it would release the caller's count. Such a site takes a
        // variant without this, or the Entry)
        bool thisFree = call.Op == Opc.NewObj;
        if (mask != 0 || size > LargeMethod && all != 0)
        {
            var want = new Variant(VariantKind.Owned, size <= SmallMethod && mask != 0 ? mask : all);
            if (vs.Contains(want) && !(thisFree && Owns(want, 0))) return want;
            var full = new Variant(VariantKind.Owned, all);
            if (vs.Contains(full) && !(thisFree && Owns(full, 0))) return full;
        }
        var br = new Variant(VariantKind.BorrowReturn);
        return mask == 0 && vs.Contains(br) ? br : Variant.Entry;
    }

    static uint HandOverMask(OwnershipSignature d)
    {
        uint all = 0;
        for (int i = 0; i < d.Params.Length && i < MaxParams; i++) if (d.Params[i] == ParamConv.HandOver) all |= 1u << i;
        return all;
    }

    /// <summary>The variants a method may have (each lowered with its own plan when a call site picks it), in stable key
    /// order: owned variants by mask (4.4's caps by size), then the borrowed-return variant. Every variant
    /// ChooseVariant can return is listed (row f's verifier check).</summary>
    public static IReadOnlyList<Variant> VariantsOf(ProgramSolution solution, MethodDesc m)
    {
        if (!solution.Covers(m)) return [];
        var r = new List<Variant>();
        var d = Demand(solution, m);
        uint all = s_noHandOver || s_noOwnedEntry ? 0 : HandOverMask(d);
        if (all != 0)
        {
            if (SizeOf(solution, m) <= SmallMethod)
            {
                if (BitOperations.PopCount(all) <= MaxEnumerated)
                {
                    for (uint s = all; s != 0; s = (s - 1) & all) r.Add(new Variant(VariantKind.Owned, s));
                }
                else
                {
                    r.Add(new Variant(VariantKind.Owned, all));
                    for (int i = 0; i < MaxParams; i++) if ((all & (1u << i)) != 0) r.Add(new Variant(VariantKind.Owned, 1u << i));
                }
                r.Sort((a, b) => a.Mask.CompareTo(b.Mask));
            }
            else r.Add(new Variant(VariantKind.Owned, all));
        }
        if (!s_noBorrowReturn && d.ReturnBorrow != null) r.Add(new Variant(VariantKind.BorrowReturn));
        return r;
    }

    // ------------------------------------------------------------------ the report

    /// <summary>IL2WASM_SIG_ROOTS=1 (with IL2WASM_SIG_STATS=2): each method whose own intrinsic call makes it Wild.</summary>
    static readonly bool s_rootsTrace = Environment.GetEnvironmentVariable("IL2WASM_SIG_ROOTS") == "1";

    static void Report(ProgramSolution sol, State st, int rounds, long ms)
    {
        int n = sol.Methods.Count, analyzed = 0, nonTrivial = 0, stores = 0, keeps = 0, borrowRet = 0, counted = 0, hoParams = 0;
        int small = 0, medium = 0, large = 0, smallVariants = 0, mediumVariants = 0, largeVariants = 0, maxPerMethod = 0;
        int core = 0, coreNotWild = 0, all = 0, allNotWild = 0;
        var system = sol.Methods.Items.FirstOrDefault()?.Context.SystemModule;
        for (int id = 0; id < n; id++)
        {
            var m = sol.Methods[id];
            var f = sol.Facts(m);
            all++; if (!f.Mods.Wild) allNotWild++;
            if (m.OwningType is MetadataType { Module: var mod } && mod == system) { core++; if (!f.Mods.Wild) coreNotWild++; }
            if (!st.Locals.TryGetValue(m, out var l)) continue;
            analyzed++;
            counted += BitOperations.PopCount(l.Counted);
            var d = f.Signature!;
            bool any = false;
            for (int i = 0; i < d.Params.Length; i++)
            {
                if (d.Params[i] != ParamConv.HandOver) continue;
                any = true; hoParams++;
                if ((d.Reasons[i] & HandOverReason.Stores) != 0) stores++;
                if ((d.Reasons[i] & HandOverReason.KeepsRunning) != 0) keeps++;
            }
            if (d.ReturnBorrow != null) borrowRet++;
            if (any || d.ReturnBorrow != null) nonTrivial++;
            int vc = VariantsOf(sol, m).Count(v => v.Kind == VariantKind.Owned);
            if (vc == 0) continue;
            maxPerMethod = Math.Max(maxPerMethod, vc);
            if (l.Size <= SmallMethod) { small++; smallVariants += vc; }
            else if (l.Size <= LargeMethod) { medium++; mediumVariants += vc; }
            else { large++; largeVariants += vc; }
        }
        Console.Error.WriteLine($"sig: {analyzed} of {n} methods analyzed, {nonTrivial} with a non-trivial signature ({hoParams} HandOver of {counted} counted parameters: {stores} by store, {keeps} keep running; {borrowRet} borrowed returns); {rounds} rounds, releases {(st.ReleasesRunCode ? "can" : "cannot")} run code; {ms} ms");
        Console.Error.WriteLine($"sig: owned variants allowed: small (<= {SmallMethod} nodes) {small} methods / {smallVariants} variants, medium {medium} / {mediumVariants}, large (> {LargeMethod}) {large} / {largeVariants}; at most {maxPerMethod} per method");
        Console.Error.WriteLine($"sig: Mods not Wild: CoreLib {coreNotWild} of {core} ({(core > 0 ? 100.0 * coreNotWild / core : 0):F1}%), all {allNotWild} of {all} ({(all > 0 ? 100.0 * allNotWild / all : 0):F1}%); intrinsic table {IntrinsicSignatures.Count} entries");
        if (system != null) foreach (var miss in IntrinsicSignatures.Missing(system.Context)) Console.Error.WriteLine("sig: intrinsic without a table entry: " + miss);
        if (Environment.GetEnvironmentVariable("IL2WASM_SIG_STATS") != "2") return;
        // where Wild comes from: the methods whose own body makes Mods Wild, by cause (a method may have several)
        var causes = new Dictionary<string, int>();
        void Note(string c) => causes[c] = causes.GetValueOrDefault(c) + 1;
        int roots = 0;
        for (int id = 0; id < n; id++)
        {
            var m = sol.Methods[id];
            if (sol.Summary(m) is not { } s || IntrinsicSignatures.DeclaredEffects(sol, m) != null) continue;
            bool root = false;
            if (s.WildStores) { Note("raw store / block copy"); root = true; }
            if (s.CallsUnknown) { Note("calli / delegate invoke"); root = true; }
            foreach (var c in s.Calls.Concat(s.Slots.SelectMany(x => sol.Targets(x)))) if (!sol.Covers(c)) { Note("uncovered callee " + c.OwningType + "::" + c.GetName()); root = true; }
            foreach (var im in s.Intrinsics) if (IntrinsicSignatures.Effects(sol, m, im, st.ReleasesRunCode).Wild) { Note("intrinsic " + im.OwningType.ToString()!.Split('.').Last() + "." + im.GetName()); root = true; if (s_rootsTrace) Console.Error.WriteLine("sig: root " + m.OwningType + "::" + m.GetName() + " via " + im.OwningType.ToString()!.Split('.').Last() + "." + im.GetName()); }
            if (root) roots++;
        }
        foreach (var t in sol.Constructed)
            if (Il2Wasm.IL.Lower.IlCompiler.FinalizerOf(t) is { } fin)
                Console.Error.WriteLine($"sig: finalizer of {t}: {fin} Mods {(sol.Covers(fin) ? sol.Facts(fin).Mods.ToString() : "uncovered")}");
        Console.Error.WriteLine($"sig: {roots} methods are Wild by their own body; causes:");
        foreach (var (c, k) in causes.OrderByDescending(x => x.Value).Take(40)) Console.Error.WriteLine($"sig:   {k,6} {c}");
    }
}
