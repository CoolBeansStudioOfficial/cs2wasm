using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

internal sealed partial class Driver
{
    /// <summary>Runs the embedder's plugin passes after the program is lowered (section 7.1).</summary>
    void RunPlugins()
    {
        if (Options.Plugins.Count == 0) return;
        var reachable = AllMethods.Where(m => m.Impl is MethodImpl.Body or MethodImpl.Synthesized && m.Sym.MethodKind is not MethodKind.AnonymousFunction)
            .Select(m => m.Sym).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
        var graph = new Dictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var m in AllMethods)
        {
            if (m.Impl is not (MethodImpl.Body or MethodImpl.Synthesized)) continue;
            var callees = m.Callees.Select(c => c.Sym).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
            if (graph.TryGetValue(m.Sym, out var existing)) callees = existing.Concat(callees).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
            graph[m.Sym] = callees;
        }
        foreach (var p in Options.Plugins)
        {
            if (p.InterfaceVersion != PluginContext.InterfaceVersion)
            {
                Report(Diags.Plugin, null, $"plugin '{p.Name}' targets interface version {p.InterfaceVersion}; this compiler provides {PluginContext.InterfaceVersion}");
                continue;
            }
            var ctx = new PluginContext(reachable, graph, Main);
            try { p.Run(ctx); }
            catch (Exception ex) { Report(Diags.Plugin, null, $"plugin '{p.Name}' failed: {ex.Message}"); continue; }
            foreach (var (name, data) in ctx.Sections) Module.CustomSections.Add(new CustomSection(name, data));
            Diagnostics.AddRange(ctx.Diagnostics);
        }
    }
}
