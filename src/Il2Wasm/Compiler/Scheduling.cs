using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>Scheduling dependence (section 7.3): the values that can depend on how threads interleave, for a plugin
/// that treats their writes as ones the server computes.
///
/// Concurrent code is the work handed to other threads: the delegates passed to <c>Task.Run</c>,
/// <c>TaskFactory.StartNew</c>, <c>ContinueWith</c>, <c>new Thread</c>, the thread pool's queueing methods,
/// <c>Parallel.For</c>/<c>ForEach</c>/<c>Invoke</c> and <c>Timer</c> callbacks, with what they call. Marked:
/// <list type="bullet">
/// <item>a field, property or captured variable that concurrent code writes and anything else can see, unless every
/// such write is order-independent: an integer <c>+=</c>/<c>-=</c>/<c>++</c>/<c>--</c> under a <c>lock</c>, or an
/// <c>Interlocked</c> add whose result is unused (a float sum is never: its rounding depends on the order);</item>
/// <item>a shared collection concurrent code changes (its order is whichever thread came first);</item>
/// <item>results that name the winner of a race: <c>Task.WhenAny</c>/<c>WaitAny</c>, the <c>Try</c> takes of the
/// concurrent collections, <c>Interlocked</c> results that are used, thread ids, <c>Task.IsCompleted</c>/<c>Status</c>,
/// <c>Monitor.TryEnter</c>;</item>
/// <item>and, to a fixed point, every member and local assigned a value computed from those.</item>
/// </list>
/// Parallel work that writes separate elements (<c>a[i]</c> in a <c>Parallel.For</c> body indexed by its own
/// parameter) and tasks joined by <c>await</c> in program order are deterministic and not marked.</summary>
internal sealed partial class PluginHost
{
    SchedulingInfo? _scheduling;
    public SchedulingInfo Scheduling => _scheduling ??= ComputeScheduling();

    static readonly HashSet<string> s_spawners = ["Run", "StartNew", "ContinueWith", "QueueUserWorkItem", "UnsafeQueueUserWorkItem", "For", "ForEach", "Invoke", "ForAsync", "ForEachAsync"];

    static bool IsSpawn(IMethodSymbol m)
    {
        var t = m.ContainingType?.OriginalDefinition;
        if (t == null) return false;
        var name = Types.FullName(t);
        return name switch
        {
            "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" => m.Name is "Run" or "ContinueWith",
            "System.Threading.Tasks.TaskFactory" or "System.Threading.Tasks.TaskFactory`1" => m.Name == "StartNew",
            "System.Threading.ThreadPool" => m.Name is "QueueUserWorkItem" or "UnsafeQueueUserWorkItem",
            "System.Threading.Tasks.Parallel" => s_spawners.Contains(m.Name),
            _ => false,
        };
    }

    static bool IsSpawnCreation(IObjectCreationOperation oc) =>
        oc.Type is INamedTypeSymbol t && Types.FullName(t.OriginalDefinition) is "System.Threading.Thread" or "System.Threading.Timer";

    SchedulingInfo ComputeScheduling()
    {
        var marks = new List<SchedulingMark>();
        var members = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var bodies = new List<(IMethodSymbol m, IOperation body)>();
        foreach (var m in SourceDefinitions()) if (Body(m) is { } b) bodies.Add((m, b));

        // ---- concurrent code: the delegates handed to other threads and what they call ----
        var regions = new List<(IOperation root, bool parallelFor, IParameterSymbol? index)>();
        var entryMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var (_, body) in bodies)
            foreach (var op in body.DescendantsAndSelf())
            {
                IEnumerable<IArgumentOperation>? args = op switch
                {
                    IInvocationOperation inv when IsSpawn(inv.TargetMethod) => inv.Arguments,
                    IObjectCreationOperation oc when IsSpawnCreation(oc) => oc.Arguments,
                    _ => null,
                };
                if (args == null) continue;
                bool pfor = op is IInvocationOperation { TargetMethod.Name: "For" or "ForEach" } pi && Types.FullName(pi.TargetMethod.ContainingType.OriginalDefinition) == "System.Threading.Tasks.Parallel";
                foreach (var a in args)
                {
                    var v = a.Value;
                    while (v is IConversionOperation c) v = c.Operand;
                    // Parallel.Invoke(params Action[]): each element
                    var items = v is IArrayCreationOperation { Initializer: { } ai } ? ai.ElementValues.ToList() : [v];
                    foreach (var item0 in items)
                    {
                        var item = item0;
                        while (item is IConversionOperation c2) item = c2.Operand;
                        if (item is IDelegateCreationOperation dc) item = dc.Target;
                        if (item is IAnonymousFunctionOperation af)
                            regions.Add((af.Body, pfor, pfor && af.Symbol.Parameters.Length > 0 ? af.Symbol.Parameters[0] : null));
                        else if (item is IMethodReferenceOperation mr) entryMethods.Add(mr.Method.OriginalDefinition);
                    }
                }
            }
        // methods reachable from the concurrent code
        var worker = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var work = new Stack<IMethodSymbol>();
        void Reach(IMethodSymbol m) { if (worker.Add(m.OriginalDefinition)) work.Push(m.OriginalDefinition); }
        foreach (var m in entryMethods) Reach(m);
        foreach (var (root, _, _) in regions)
            foreach (var op in root.DescendantsAndSelf())
                if (op is IInvocationOperation inv) Reach(inv.TargetMethod);
                else if (op is IObjectCreationOperation { Constructor: { } ctor }) Reach(ctor);
                else if (op is IPropertyReferenceOperation pr) { if (pr.Property.GetMethod is { } g) Reach(g); if (pr.Property.SetMethod is { } s) Reach(s); }
        var graphByDef = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var (k, vs) in CallGraph)
        {
            if (!graphByDef.TryGetValue(k.OriginalDefinition, out var l)) graphByDef[k.OriginalDefinition] = l = [];
            l.AddRange(vs);
        }
        while (work.Count > 0)
            if (graphByDef.TryGetValue(work.Pop(), out var cs)) foreach (var c in cs) Reach(c);

        bool InConcurrent(IOperation op, IMethodSymbol m, out (IOperation root, bool parallelFor, IParameterSymbol? index) region)
        {
            foreach (var r in regions)
                for (var p = op; p != null; p = p.Parent)
                    if (p == r.root) { region = r; return true; }
            region = default;
            return worker.Contains(m.OriginalDefinition);
        }

        void Mark(ISymbol? s, IOperation at, string reason)
        {
            if (s != null) members.Add(s.OriginalDefinition);
            marks.Add(new SchedulingMark(s?.OriginalDefinition, at, reason));
        }

        // ---- racy writes and changed shared collections in concurrent code ----
        foreach (var (m, body) in bodies)
            foreach (var op in body.DescendantsAndSelf())
            {
                if (!InConcurrent(op, m, out var region)) continue;
                ISymbol? target = null; IOperation? write = null;
                switch (op)
                {
                    case IAssignmentOperation a when SharedSymbol(a.Target, region.root) is { } s: target = s; write = a; break;
                    case IIncrementOrDecrementOperation inc when SharedSymbol(inc.Target, region.root) is { } s: target = s; write = inc; break;
                    case IInvocationOperation { Instance: { } recv } inv when !inv.TargetMethod.IsStatic && Mutates(inv.TargetMethod) && SharedSymbol(recv, region.root) is { } holder:
                        Mark(holder, inv, "a shared collection or object changed by concurrent code (" + inv.TargetMethod.Name + ")");
                        continue;
                    case IArgumentOperation { Parameter.RefKind: RefKind.Ref } arg when arg.Parent is IInvocationOperation ii && IsInterlocked(ii.TargetMethod) && SharedSymbol(arg.Value, region.root) is { } isym:
                        // an atomic add whose result nobody reads leaves an order-independent total; others do not
                        if (ii.TargetMethod.Name is "Increment" or "Decrement" or "Add" && ii.Parent is IExpressionStatementOperation && IsInteger(arg.Value.Type)) continue;
                        Mark(isym, ii, "Interlocked." + ii.TargetMethod.Name + " from concurrent code");
                        continue;
                }
                if (target == null || write == null) continue;
                // separate elements of a Parallel.For (a[i] with the body's own index) are deterministic
                if (write is IAssignmentOperation { Target: IArrayElementReferenceOperation ae } && region.index != null
                    && ae.Indices.Length == 1 && ae.Indices[0] is IParameterReferenceOperation ip && SymbolEqualityComparer.Default.Equals(ip.Parameter, region.index))
                    continue;
                if (OrderIndependent(write)) continue;
                Mark(target, write, IsFloat(TargetType(write)) && write is ICompoundAssignmentOperation ? "a floating-point value accumulated by several threads" : "written by concurrent code");
            }

        // ---- results that name the winner of a race ----
        var sources = new HashSet<IOperation>();
        foreach (var (_, body) in bodies)
            foreach (var op in body.DescendantsAndSelf())
                if (RaceResult(op) is { } why) { sources.Add(op); marks.Add(new SchedulingMark(null, op, why)); }

        // ---- what is computed from them, to a fixed point ----
        var tainted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        bool Dependent(IOperation? o)
        {
            if (o == null) return false;
            foreach (var x in o.DescendantsAndSelf())
            {
                if (sources.Contains(x)) return true;
                ISymbol? s = x switch
                {
                    IFieldReferenceOperation f => f.Field.OriginalDefinition,
                    IPropertyReferenceOperation p => p.Property.OriginalDefinition,
                    ILocalReferenceOperation l => l.Local,
                    _ => null,
                };
                if (s != null && (members.Contains(s) || tainted.Contains(s))) return true;
            }
            return false;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (_, body) in bodies)
                foreach (var op in body.DescendantsAndSelf())
                {
                    (ISymbol? sym, IOperation? value) = op switch
                    {
                        IVariableDeclaratorOperation { Initializer: { } init } d => ((ISymbol?)d.Symbol, (IOperation?)init.Value),
                        IAssignmentOperation a => (AssignedSymbol(a.Target), (IOperation?)a.Value),
                        IArgumentOperation { Parameter.RefKind: RefKind.Out } oa when oa.Parent is { } call && sources.Contains(call) => (AssignedSymbol(oa.Value), call),
                        _ => (null, null),
                    };
                    if (sym == null || !Dependent(value)) continue;
                    if (sym is ILocalSymbol) { if (tainted.Add(sym)) changed = true; continue; }
                    if (members.Add(sym.OriginalDefinition)) { marks.Add(new SchedulingMark(sym.OriginalDefinition, op, "assigned a value that depends on scheduling")); changed = true; }
                }
        }
        return new SchedulingInfo(marks, members, o => Dependent(o));
    }

    static ISymbol? AssignedSymbol(IOperation t)
    {
        while (t is IConversionOperation c) t = c.Operand;
        if (t is IDeclarationExpressionOperation de) t = de.Expression;
        return t switch
        {
            IFieldReferenceOperation f => f.Field,
            IPropertyReferenceOperation p => p.Property,
            ILocalReferenceOperation l => l.Local,
            IArrayElementReferenceOperation ae => AssignedSymbol(ae.ArrayReference),
            _ => null,
        };
    }

    /// <summary>The shared variable a write in concurrent code changes: a field, property or static (not of an object
    /// made in the region), an element of one, or a local captured from outside the region.</summary>
    static ISymbol? SharedSymbol(IOperation t, IOperation? region)
    {
        while (t is IConversionOperation c) t = c.Operand;
        switch (t)
        {
            case IFieldReferenceOperation f:
                if (f.Instance is ILocalReferenceOperation li && region != null && DeclaredIn(li.Local, region)) return null;
                return f.Field;
            case IPropertyReferenceOperation p:
                if (p.Instance is ILocalReferenceOperation lp && region != null && DeclaredIn(lp.Local, region)) return null;
                return p.Property;
            case IArrayElementReferenceOperation ae: return SharedSymbol(ae.ArrayReference, region);
            case ILocalReferenceOperation l: return region != null && !DeclaredIn(l.Local, region) ? l.Local : null;
            case IParameterReferenceOperation: return null;
            default: return null;
        }
    }

    static bool DeclaredIn(ILocalSymbol l, IOperation region) =>
        l.DeclaringSyntaxReferences.Any(r => region.Syntax.Span.Contains(r.Span) && r.SyntaxTree == region.Syntax.SyntaxTree);

    static bool OrderIndependent(IOperation write)
    {
        bool integer = IsInteger(TargetType(write));
        bool commutative = write switch
        {
            ICompoundAssignmentOperation ca => ca.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr,
            IIncrementOrDecrementOperation => true,
            _ => false,
        };
        if (!integer || !commutative) return false;
        for (var p = write.Parent; p != null; p = p.Parent) if (p is ILockOperation) return true;
        return false;
    }

    static ITypeSymbol? TargetType(IOperation write) => write switch
    {
        IAssignmentOperation a => a.Target.Type,
        IIncrementOrDecrementOperation i => i.Target.Type,
        _ => null,
    };

    static bool IsInteger(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_UInt32 or SpecialType.System_UInt64
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_SByte;

    static bool IsFloat(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    static bool IsInterlocked(IMethodSymbol m) => m.ContainingType is { Name: "Interlocked" } it && it.ContainingNamespace?.ToDisplayString() == "System.Threading";

    /// <summary>A method that changes the object it is called on (Add, Enqueue, Push, Remove, Clear, Insert, Append...).</summary>
    static bool Mutates(IMethodSymbol m) => m.Name is "Add" or "AddRange" or "Insert" or "InsertRange" or "Remove" or "RemoveAt" or "RemoveAll" or "RemoveRange" or "Clear"
        or "Enqueue" or "Dequeue" or "Push" or "Pop" or "TryAdd" or "TryRemove" or "TryUpdate" or "AddOrUpdate" or "GetOrAdd" or "TryDequeue" or "TryPop" or "TryTake"
        or "Append" or "AppendLine" or "Write" or "WriteLine" or "Sort" or "Reverse" or "Set" or "SetValue";

    static string? RaceResult(IOperation op)
    {
        switch (op)
        {
            case IInvocationOperation inv:
                {
                    var m = inv.TargetMethod; var t = m.ContainingType is { } ct ? Types.FullName(ct.OriginalDefinition) : "";
                    if (t is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" && m.Name is "WhenAny" or "WaitAny") return "which task finishes first (Task." + m.Name + ")";
                    if (t.StartsWith("System.Collections.Concurrent.") && m.Name is "TryTake" or "TryDequeue" or "TryPop" or "TryPeek" or "Take" or "TryTakeFromAny" or "TakeFromAny")
                        return "what a concurrent collection gives first (" + m.Name + ")";
                    if (IsInterlocked(m) && inv.Parent is not IExpressionStatementOperation) return "the value an Interlocked operation saw (" + m.Name + ")";
                    if (t == "System.Threading.Monitor" && m.Name == "TryEnter") return "whether another thread held the lock (Monitor.TryEnter)";
                    return null;
                }
            case IPropertyReferenceOperation pr:
                {
                    var p = pr.Property; var t = p.ContainingType is { } ct ? Types.FullName(ct.OriginalDefinition) : "";
                    if (t == "System.Threading.Thread" && p.Name == "ManagedThreadId" || t == "System.Environment" && p.Name == "CurrentManagedThreadId") return "the thread it runs on";
                    if (t is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" && p.Name is "IsCompleted" or "Status" or "IsCompletedSuccessfully") return "whether a task has finished yet (" + p.Name + ")";
                    return null;
                }
        }
        return null;
    }
}
