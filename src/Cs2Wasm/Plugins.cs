using Microsoft.CodeAnalysis;

namespace Cs2Wasm;

/// <summary>
/// A whole-program pass run by the compiler (section 7.1). Version 1 of the interface exposes reachability,
/// the call graph and source mapping, and lets a plugin add custom sections and diagnostics.
/// </summary>
public interface ICompilerPlugin
{
    /// <summary>The plugin interface version the plugin was written against.</summary>
    int InterfaceVersion { get; }
    string Name { get; }
    void Run(PluginContext context);
}

public sealed class PluginContext
{
    public const int InterfaceVersion = 1;
    readonly List<(string, byte[])> _sections = [];
    readonly List<Diagnostic> _diagnostics = [];

    internal PluginContext(IReadOnlyList<IMethodSymbol> reachable, IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> callGraph, Compilation main)
    {
        ReachableMethods = reachable;
        CallGraph = callGraph;
        Compilation = main;
    }

    /// <summary>The program's main compilation (the embedder may read the same one).</summary>
    public Compilation Compilation { get; }
    /// <summary>Every method instance that the roots reach, as concrete (constructed) symbols.</summary>
    public IReadOnlyList<IMethodSymbol> ReachableMethods { get; }
    /// <summary>Direct calls between reachable methods (virtual calls appear as calls to every implementation reached).</summary>
    public IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> CallGraph { get; }

    public void AddCustomSection(string name, byte[] data) => _sections.Add((name, data));
    public void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    internal IReadOnlyList<(string, byte[])> Sections => _sections;
    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
}
