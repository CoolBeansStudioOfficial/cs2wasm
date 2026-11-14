using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Cs2Wasm;

/// <summary>
/// A whole-program pass run by the compiler (section 7.1), after lowering and the whole-program analyses and before
/// optimization. Interface version 2 (version 1 plugins still run): reachability and the call graph; member reads and
/// writes with the conditions they run under; control-flow graphs, def-use chains and closed forms of loop-carried
/// and per-call updates per method; field targets and aliasing; instrumentation of writes, reads and statements;
/// generated C# added to the program (and exported); clones of a method with chosen imports suppressed; custom
/// sections and diagnostics. Everything is in Roslyn's terms (operations, symbols, locations), so a plugin reports in
/// the developer's.
/// </summary>
public interface ICompilerPlugin
{
    /// <summary>The plugin interface version the plugin was written against (1 or 2).</summary>
    int InterfaceVersion { get; }
    string Name { get; }
    void Run(PluginContext context);
}

public enum AccessKind { Read, Write, ReadWrite }

/// <summary>A branch an operation runs under: <see cref="Test"/> evaluated to <see cref="WhenTrue"/>. For a switch
/// section, Test is the case clause.</summary>
public sealed record BranchCondition(IOperation Test, bool WhenTrue);

/// <summary>A read or write of a field, property or event in a reachable method (lambdas count as their method).</summary>
public sealed record MemberAccess(ISymbol Member, IMethodSymbol Method, IOperation Operation, AccessKind Kind, IReadOnlyList<BranchCondition> Conditions)
{
    public Location Location => Operation.Syntax.GetLocation();
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

public sealed class PluginContext
{
    public const int InterfaceVersion = 2;
    readonly List<(string, byte[])> _sections = [];
    readonly List<Diagnostic> _diagnostics = [];
    readonly Compiler.PluginHost _host;

    internal PluginContext(Compiler.PluginHost host) { _host = host; }

    /// <summary>The program's main compilation (the embedder may read the same one).</summary>
    public Compilation Compilation => _host.Main;
    /// <summary>Every method instance that the roots reach, as concrete (constructed) symbols.</summary>
    public IReadOnlyList<IMethodSymbol> ReachableMethods => _host.Reachable;
    /// <summary>Direct calls between reachable methods (virtual calls appear as calls to every implementation reached).</summary>
    public IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> CallGraph => _host.CallGraph;

    // ---------------- analyses ----------------
    /// <summary>Every read and write of fields, properties and events in reachable source methods.</summary>
    public IReadOnlyList<MemberAccess> Accesses => _host.Accesses;
    /// <summary>The reads and writes of one member (its definition: generic instantiations included).</summary>
    public IEnumerable<MemberAccess> AccessesOf(ISymbol member) => _host.AccessesOf(member);
    /// <summary>The method's control-flow graph (Roslyn's lowering of its body), or null without a body in source.</summary>
    public ControlFlowGraph? GetControlFlowGraph(IMethodSymbol method) => _host.Graph(method);
    /// <summary>Reaching definitions of the method's locals and parameters over its control-flow graph.</summary>
    public DefUse? GetDefUse(IMethodSymbol method) => _host.DefUse(method);
    /// <summary>Loop-carried and per-call update rules with closed forms (induction variables).</summary>
    public IReadOnlyList<UpdateRule> GetUpdateRules(IMethodSymbol method) => _host.UpdateRules(method);
    /// <summary>The types of the objects a field can refer to: the static types stored into it and their constructed subtypes.</summary>
    public IReadOnlyCollection<ITypeSymbol> PointsTo(IFieldSymbol field) => _host.PointsTo(field);
    /// <summary>Whether two reference-typed operations can name the same object (false only when it is certain).</summary>
    public bool MayAlias(IOperation a, IOperation b) => _host.MayAlias(a, b);

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
    /// <summary>Calls `hook` (a static method taking () or (int tag)) before a statement runs.</summary>
    public void InstrumentBefore(IOperation statement, IMethodSymbol hook, int tag = 0) => _host.AddStatementHook(statement, hook, tag);
    /// <summary>Compiles a copy of `method`, and of what it calls directly, in which calls to the given imports
    /// (boundary methods) do nothing and return defaults; the copy is exported as `exportName`.</summary>
    public void CloneWithSuppressedImports(IMethodSymbol method, IEnumerable<IMethodSymbol> suppressedImports, string exportName) => _host.Clone(method, suppressedImports, exportName);

    public void AddCustomSection(string name, byte[] data) => _sections.Add((name, data));
    public void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    internal IReadOnlyList<(string, byte[])> Sections => _sections;
    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
}
