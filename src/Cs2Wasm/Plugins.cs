using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Cs2Wasm;

/// <summary>
/// A whole-program pass run by the compiler (section 7.1), after lowering and the whole-program analyses and before
/// optimization. Interface version 3 (versions 1 and 2 still run): the values that depend on thread scheduling
/// (section 7.3); reachability and the call graph; member reads and
/// writes with the conditions they run under; control-flow graphs, def-use chains and closed forms of loop-carried
/// and per-call updates per method; field targets and aliasing; instrumentation of writes, reads and statements;
/// generated C# added to the program (and exported); clones of a method with chosen imports suppressed; custom
/// sections and diagnostics. Everything is in Roslyn's terms (operations, symbols, locations), so a plugin reports in
/// the developer's. Version 4: a hook that receives the value of any one expression and gives the value the program
/// goes on with (InterceptValue), write hooks at one write (InstrumentWrite), which accesses a hook runs at
/// (MemberAccess.Instrumentable), and generated code that names the program's private members. Version 5: collection
/// entries as accesses (MemberAccess.IsEntry) with hooks at them (InstrumentEntryWrites, InstrumentEntryReads); a hook
/// after a statement (InstrumentAfter); conditions with early returns and across calls (CallersOf, PathConditions);
/// aliasing by where references come from and which new objects a method owns alone (MayAlias, AllocationSites,
/// IsOwned).
/// </summary>
public interface ICompilerPlugin
{
    /// <summary>The plugin interface version the plugin was written against (1 to 5).</summary>
    int InterfaceVersion { get; }
    string Name { get; }
    void Run(PluginContext context);
}

/// <summary>(Version 5.) A plugin that also runs before the program's first compile (audit A4.9). What it adds there
/// (hooks, sources, clones, suppressed imports) is compiled once, with every analysis knowing the hooks; a hook added
/// in Run instead makes the compiler compile again what it instruments and what relied on an answer it changes. Before
/// the compile the program's reach is not known: <see cref="PluginContext.ReachableMethods"/> and
/// <see cref="PluginContext.Accesses"/> cover every method of the program's source with a body, the call graph is
/// the source's direct calls (by definition), and the types an alias or a field may have are every type of the
/// source, any library type taken as possible. A program that starts threads is compiled again from the start, so
/// RunBeforeCompile can run more than once in one build. Run is still called after the compile.</summary>
public interface ICompilerPluginBeforeCompile : ICompilerPlugin
{
    void RunBeforeCompile(PluginContext context);
}

public enum AccessKind { Read, Write, ReadWrite }

/// <summary>A branch an operation runs under: <see cref="Test"/> evaluated to <see cref="WhenTrue"/>. For a switch
/// section, Test is the case clause.</summary>
public sealed record BranchCondition(IOperation Test, bool WhenTrue);

/// <summary>A read or write of a field, property or event in a reachable method (lambdas count as their method).</summary>
public sealed record MemberAccess(ISymbol Member, IMethodSymbol Method, IOperation Operation, AccessKind Kind, IReadOnlyList<BranchCondition> Conditions)
{
    public Location Location => Operation.Syntax.GetLocation();
    /// <summary>Whether a hook set with InstrumentWrites or InstrumentReads runs at this access. False for a write the
    /// compiler does not make where it is written: the member passed as a `ref` or `out` argument (the callee
    /// writes, any time until it returns, or later through a reference it kept), a deconstruction's target, an
    /// event's handlers added or removed. A plugin that must see every write treats such a member as one it cannot
    /// follow.</summary>
    public bool Instrumentable { get; init; } = true;
    /// <summary>(Version 5.) An access to the entries of the collection the member holds, not to the member: an
    /// element read or written (`Scores[p] = 1`, `Cells[i].X = 3`), a library method that changes it (`Add`, `Remove`,
    /// `Clear`, `Sort`, `Array.Fill`: a write) or reads it (`TryGetValue`, `Count`, `Contains`, LINQ, a `foreach`: a
    /// read), on the member itself, on an element of it (`Grid[x][y] = 1` is entry x of Grid), or on a local whose
    /// every reaching definition is the member (the collection the member held when the local was set).
    /// <see cref="MemberAccess.Operation"/> is the assignment, the call, the element, the loop.</summary>
    public bool IsEntry { get; init; }
    /// <summary>(Version 5.) For an entry access, the one entry it names: the index or key (a set's item); null for
    /// the whole collection or an entry it does not name (an append, a clear, a sort, a count, an iteration).</summary>
    public IOperation? Key { get; init; }
}

/// <summary>Reaching definitions over a method's control-flow graph: for each read of a local or parameter, the
/// operations that may have written the value it sees (an empty list: the parameter's incoming value or a local's
/// default).</summary>
public sealed class DefUse
{
    public required ControlFlowGraph Graph { get; init; }
    public required IReadOnlyDictionary<IOperation, IReadOnlyList<IOperation>> ReachingDefinitions { get; init; }
    public required IReadOnlyDictionary<ISymbol, IReadOnlyList<IOperation>> Definitions { get; init; }
    public required IReadOnlyDictionary<ISymbol, IReadOnlyList<IOperation>> Uses { get; init; }
}

public enum UpdateKind { Add, Multiply, Affine }

/// <summary>A value updated by a fixed rule, x' = Factor·x + Step, once per loop iteration (<see cref="Loop"/> is the
/// loop's first block in the method's graph) or once per call of the method (Loop null: per-tick code). The factor
/// and step do not change while the rule applies, so x after n steps has a closed form.</summary>
public sealed record UpdateRule(ISymbol Target, IMethodSymbol Method, IOperation Update, UpdateKind Kind, IOperation? Factor, IOperation? Step, BasicBlock? Loop)
{
    /// <summary>-1 when the step is subtracted (x -= s, x--); a null Step with Add means 1.</summary>
    public int StepSign { get; init; } = 1;
    public string Formula => Kind switch
    {
        UpdateKind.Add => $"{Target.Name}(n) = {Target.Name}(0) {(StepSign < 0 ? "-" : "+")} n * ({Step?.Syntax.ToString() ?? "1"})",
        UpdateKind.Multiply => $"{Target.Name}(n) = {Target.Name}(0) * ({Factor!.Syntax})^n",
        _ => $"{Target.Name}(n) = a^n * {Target.Name}(0) + b * (a^n - 1) / (a - 1), a = {Factor!.Syntax}, b = {Step!.Syntax}",
    };
}

public enum InstrumentPoint { Before, After }

/// <summary>Where a value that depends on thread scheduling comes from (section 7.3): a member (or captured variable)
/// and the write that makes it so, or an operation whose result names the winner of a race (Symbol null).</summary>
public sealed record SchedulingMark(ISymbol? Symbol, IOperation Operation, string Reason)
{
    public Location Location => Operation.Syntax.GetLocation();
}

/// <summary>The values that can depend on how threads interleave (section 7.3): a networking plugin treats a write of
/// such a value as one the server computes and sends. Order-independent parallel work is not marked.</summary>
public sealed class SchedulingInfo
{
    readonly Func<IOperation, bool> _dependent;
    internal SchedulingInfo(IReadOnlyList<SchedulingMark> marks, IReadOnlyCollection<ISymbol> members, Func<IOperation, bool> dependent)
    {
        Marks = marks; Members = members; _dependent = dependent;
    }
    /// <summary>Every place scheduling enters a value: racy writes, accumulations, changed shared collections, race results.</summary>
    public IReadOnlyList<SchedulingMark> Marks { get; }
    /// <summary>The fields, properties and captured variables (definitions) whose values can depend on scheduling.</summary>
    public IReadOnlyCollection<ISymbol> Members { get; }
    /// <summary>Whether the value of an operation (of a reachable source method's body) can depend on scheduling.</summary>
    public bool IsSchedulingDependent(IOperation operation) => _dependent(operation);
}

public sealed class PluginContext
{
    public const int InterfaceVersion = 5;
    readonly List<(string, byte[])> _sections = [];
    readonly List<Diagnostic> _diagnostics = [];
    readonly Compiler.PluginHost _host;
    readonly int _version;

    internal PluginContext(Compiler.PluginHost host, int version = InterfaceVersion) { _host = host; _version = version; }

    /// <summary>(Version 5.) Whether this is ICompilerPluginBeforeCompile.RunBeforeCompile, before the first compile.</summary>
    public bool BeforeCompile => _host.BeforeCompile;
    /// <summary>The program's main compilation (the embedder may read the same one).</summary>
    public Compilation Compilation => _host.Main;
    /// <summary>Every method instance that the roots reach, as concrete (constructed) symbols.</summary>
    public IReadOnlyList<IMethodSymbol> ReachableMethods => _host.Reachable;
    /// <summary>Direct calls between reachable methods (virtual calls appear as calls to every implementation reached).</summary>
    public IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> CallGraph => _host.CallGraph;

    // ---------------- analyses ----------------
    /// <summary>Every read and write of fields, properties and events in reachable source methods; from version 5 also
    /// of the entries of the collections they hold (<see cref="MemberAccess.IsEntry"/>).</summary>
    public IReadOnlyList<MemberAccess> Accesses => _version >= 5 ? _host.Accesses : _host.MemberAccesses;
    /// <summary>The reads and writes of one member (its definition: generic instantiations included), from version 5
    /// with those of its collection's entries.</summary>
    public IEnumerable<MemberAccess> AccessesOf(ISymbol member) => _host.AccessesOf(member).Where(a => _version >= 5 || !a.IsEntry);
    /// <summary>The method's control-flow graph (Roslyn's lowering of its body), or null without a body in source.</summary>
    public ControlFlowGraph? GetControlFlowGraph(IMethodSymbol method) => _host.Graph(method);
    /// <summary>Reaching definitions of the method's locals and parameters over its control-flow graph.</summary>
    public DefUse? GetDefUse(IMethodSymbol method) => _host.DefUse(method);
    /// <summary>Loop-carried and per-call update rules with closed forms (induction variables).</summary>
    public IReadOnlyList<UpdateRule> GetUpdateRules(IMethodSymbol method) => _host.UpdateRules(method);
    /// <summary>The types of the objects a field can refer to: the static types stored into it and their constructed subtypes.</summary>
    public IReadOnlyCollection<ITypeSymbol> PointsTo(IFieldSymbol field) => _host.PointsTo(field);
    /// <summary>Whether two reference-typed operations can name the same object (false only when it is certain). From
    /// version 5 (every version gets the answer) by where each comes from, through locals: two different `new`s, a
    /// `new` and a parameter's incoming value in its method, or a `new` its method owns alone (<see cref="IsOwned"/>)
    /// and anything from outside it, do not; then by type.</summary>
    public bool MayAlias(IOperation a, IOperation b) => _host.MayAlias(a, b);
    /// <summary>(Version 5.) The `new` expressions whose objects a reference can name (each as an operation, to
    /// compare by its Syntax), through locals by the method's def-use chains; null when it can name an object made
    /// elsewhere (a field's, a parameter's, a call's).</summary>
    public IReadOnlyCollection<IOperation>? AllocationSites(IOperation reference) => _host.AllocationSites(reference);
    /// <summary>(Version 5.) Whether the object a `new` makes stays its method's own (section 4's single owner): it is
    /// not stored, captured, returned or handed to code that keeps or returns it (the compiler's escape analysis), so
    /// no reference from outside the method names it. <see cref="MayAlias"/> uses it.</summary>
    public bool IsOwned(IOperation creation) => _host.IsOwned(creation);
    /// <summary>(Version 5.) The calls of a method in reachable source code, each with the conditions it runs under
    /// (a call of a virtual or interface method counts for every override or implementation).</summary>
    public IReadOnlyList<CallSite> CallersOf(IMethodSymbol method) => _host.CallersOf(method);
    /// <summary>(Version 5.) The full condition an operation runs under, across calls: every path of calls from a
    /// method no source code calls (or from a delegate's invocation, a recursion, or the cut), with the conditions
    /// along it. The operation runs only when, for some path, all of that path's conditions hold.</summary>
    public IReadOnlyList<ConditionPath> PathConditions(IOperation operation, int maxDepth = 16, int maxPaths = 1024) => _host.PathConditions(operation, maxDepth, maxPaths);
    /// <summary>(Version 3.) The values that can depend on thread scheduling: marks, members, and a query.</summary>
    public SchedulingInfo Scheduling => _host.Scheduling;

    // ---------------- changing the program ----------------
    /// <summary>Adds generated C# to the program, compiled against it; its `[UnmanagedCallersOnly(EntryPoint)]`
    /// methods are exported. Returns the compilation, to find the generated methods (as hooks, say).</summary>
    public Compilation AddSource(string code, string path = "plugin.g.cs") => _host.AddSource(code, path);
    /// <summary>Calls `hook` at every write of `member` (simple, compound, ++/--, ??=, initializers). The hook is a
    /// static method taking (), (object receiver) or (object receiver, int tag); the receiver is null for static
    /// members and struct receivers, and with <see cref="InstrumentPoint.Before"/> also when it is not a local,
    /// parameter or `this`.</summary>
    public void InstrumentWrites(ISymbol member, IMethodSymbol hook, int tag = 0, InstrumentPoint point = InstrumentPoint.After) => _host.AddMemberHook(member, hook, tag, point, write: true);
    /// <summary>Calls `hook` after every read of `member`, with the same parameter forms as <see cref="InstrumentWrites"/>.</summary>
    public void InstrumentReads(ISymbol member, IMethodSymbol hook, int tag = 0) => _host.AddMemberHook(member, hook, tag, InstrumentPoint.After, write: false);
    /// <summary>(Version 5.) Calls `hook` after every entry write of the collection `member` holds (the accesses with
    /// <see cref="MemberAccess.IsEntry"/>, a write or read-write, and <see cref="MemberAccess.Instrumentable"/>): an
    /// element stored, a library method that changes it. The hook is a static method taking (), (int tag), (object
    /// receiver), (object receiver, int tag), or (object receiver, TKey key, bool keyed, int tag): the receiver is the
    /// object whose member holds the collection (null for a static member, a struct, or a collection reached through a
    /// local), the key the entry the access names (<see cref="MemberAccess.Key"/>, computed once, as the program
    /// computes it) and keyed false where it names none (the key is then the default). TKey is the key's type at every
    /// keyed site, a reference type or a primitive (a struct key is refused).</summary>
    public void InstrumentEntryWrites(ISymbol member, IMethodSymbol hook, int tag = 0) => _host.AddEntryHook(member, hook, tag, write: true);
    /// <summary>(Version 5.) Calls `hook` after every entry read of the collection `member` holds (as a `foreach`
    /// begins, before the collection is computed), with the forms of <see cref="InstrumentEntryWrites"/>.</summary>
    public void InstrumentEntryReads(ISymbol member, IMethodSymbol hook, int tag = 0) => _host.AddEntryHook(member, hook, tag, write: false);
    /// <summary>(Version 4.) Calls `hook` at one write (an assignment, a compound assignment, `++`/`--`, `??=`, of a
    /// field or property), with the parameter forms and points of <see cref="InstrumentWrites"/>: a plugin that
    /// classifies each write marks the ones it means and no others.</summary>
    public void InstrumentWrite(IOperation write, IMethodSymbol hook, int tag = 0, InstrumentPoint point = InstrumentPoint.After) => _host.AddSiteWriteHook(write, hook, tag, point);
    /// <summary>(Version 4.) The value of one expression goes through `hook` each time it is computed: a static method
    /// `T Hook(T value)` or `T Hook(T value, int tag)`, T the expression's type; what it returns is the value the
    /// program goes on with. A branch's condition (the outcome is seen, or replaced by one that was sent), a call's
    /// result (a random draw, an awaited answer), the value an assignment stores, an argument. Several hooks on one
    /// expression run in the order they were added, each on the result of the one before. An expression the
    /// compiler does not compute as a value where it stands (the target of an assignment, a constant folded into
    /// its use) is refused with an error when the program is built.</summary>
    public void InterceptValue(IOperation expression, IMethodSymbol hook, int tag = 0) => _host.AddValueHook(expression, hook, tag);
    /// <summary>Calls `hook` (a static method taking () or (int tag)) before a statement runs.</summary>
    public void InstrumentBefore(IOperation statement, IMethodSymbol hook, int tag = 0) => _host.AddStatementHook(statement, hook, tag);
    /// <summary>(Version 5.) Calls `hook` (a static method taking () or (int tag)) after a statement, each time it
    /// completes normally: control reaches its end (a loop's after the loop ends, by its condition or a `break`; an
    /// `if` whose branch returned does not). Not when it is left by a `return`, a `throw`, an exception or a jump out,
    /// so a jump statement itself (`return`, `throw`, `break`, `continue`, `goto`, `yield break`) is refused. An
    /// `await` or `yield return` inside is not an end: the hook runs when the statement finishes after it resumes.</summary>
    public void InstrumentAfter(IOperation statement, IMethodSymbol hook, int tag = 0) => _host.AddStatementHook(statement, hook, tag, after: true);
    /// <summary>Compiles a copy of `method`, and of what it calls directly, in which calls to the given imports
    /// (boundary methods) do nothing and return defaults; the copy is exported as `exportName`.</summary>
    public void CloneWithSuppressedImports(IMethodSymbol method, IEnumerable<IMethodSymbol> suppressedImports, string exportName) => _host.Clone(method, suppressedImports, exportName);

    /// <summary>(Version 4.) Makes the imports (boundary methods) suppressible and returns their bit (1, 2, 4, ...; at
    /// most 31 groups). While generated code has the bit set in <c>Cs2Wasm.Effects.Suppressed</c> (an int property
    /// every source added with AddSource can name), a call to one of them does nothing and returns its type's
    /// default, wherever the call is made from: a virtual method, a delegate, a lambda, the library. A run of the
    /// program's own code with its effects left out (a re-simulation, a partial tick for display) is that code
    /// called between setting the bit and restoring it; nothing is cloned. A worker thread has its own value.</summary>
    public int SuppressibleImports(IEnumerable<IMethodSymbol> imports) => _host.Suppressible(imports);

    public void AddCustomSection(string name, byte[] data) => _sections.Add((name, data));
    public void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    internal IReadOnlyList<(string, byte[])> Sections => _sections;
    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
}
