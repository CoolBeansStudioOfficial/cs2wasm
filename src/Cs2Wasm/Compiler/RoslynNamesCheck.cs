using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// CS2WASM_CHECK_NAMES=1 (a developer's check, never in a build otherwise): the program is emitted by Roslyn itself,
/// and the names of the methods and classes it made for lambdas, local functions and state machines are compared
/// with the ones RoslynNames.cs predicts; each difference goes to stderr as `names: ...`.
/// </summary>
internal sealed partial class Driver
{
    static readonly bool s_checkRelease = Environment.GetEnvironmentVariable("CS2WASM_CHECK_NAMES_RELEASE") == "1";

    internal void CheckGeneratedNames()
    {
        if (Environment.GetEnvironmentVariable("CS2WASM_CHECK_NAMES") is not ("1" or "only")) return;
        foreach (var comp0 in Inputs)
        {
            // (CS2WASM_CHECK_NAMES_RELEASE=1: as a release build would name them, environments merged)
            var comp = s_checkRelease ? comp0.WithOptions(comp0.Options.WithOptimizationLevel(OptimizationLevel.Release)) : comp0;
            using var ms = new MemoryStream();
            if (Cs2WasmCompiler.EmitForNamesCheck is not { } emit) { Console.Error.WriteLine("names: no Roslyn emit in this build"); return; }
            if (!emit(comp, ms)) { Console.Error.WriteLine("names: the program does not emit"); continue; }
            ms.Position = 0;
            using var pe = new System.Reflection.PortableExecutable.PEReader(ms);
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var actual = new HashSet<string>();
            string TypeName(System.Reflection.Metadata.TypeDefinitionHandle h)
            {
                var td = md.GetTypeDefinition(h);
                var name = md.GetString(td.Name);
                var decl = td.GetDeclaringType();
                if (!decl.IsNil) return TypeName(decl) + "+" + name;
                var ns = md.GetString(td.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }
            foreach (var h in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(h);
                var tn = TypeName(h);
                if (tn.StartsWith("<PrivateImplementationDetails>") || tn.Contains("<>f__AnonymousType") || tn == "<Module>" || tn.EndsWith("+<>O") || tn.Contains("<>o__") || tn.StartsWith("<>A") || tn.StartsWith("<>F") || tn.StartsWith("<>z__") || tn.Contains("+<G>$") || tn.Contains("<>f__AnonymousDelegate") || tn.Contains("+<>E__") || tn.StartsWith("Microsoft.CodeAnalysis.") || tn.StartsWith("System.Runtime.CompilerServices.")) continue;
                if (md.GetString(td.Name).StartsWith('<')) actual.Add("T " + tn);
                foreach (var mh in td.GetMethods())
                {
                    var mn = md.GetString(md.GetMethodDefinition(mh).Name);
                    if (mn.StartsWith('<') && (mn.Contains(">b__") || mn.Contains(">g__"))) actual.Add("M " + tn + "::" + mn);
                }
            }
            var predicted = new HashSet<string>();
            // (the symbols of the compilation the operation trees are of)
            foreach (var t in AllTypes(comp0.Assembly.GlobalNamespace))
                // (C# 14's extension blocks: their members' code is the enclosing class's, not checked)
                foreach (var m in t.TypeKind.ToString() == "Extension" ? [] : t.GetMembers().OfType<IMethodSymbol>())
                {
                    if (m.DeclaringSyntaxReferences.Length == 0 && m.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor)) continue;
                    var tn = ClrPath(t.OriginalDefinition);
                    if ((m.IsAsync || m.IsIterator) && StateMachineName(null!, m) is var (_, smFull)) predicted.Add("T " + smFull);
                    if (NamesOf(m) is not { } names) continue;
                    foreach (var f in names.Functions.Values)
                    {
                        var c = NestedContainerSuffix(names, f);
                        var ct = c.Length > 0 ? tn + "+" + c : tn;
                        if (c.Length > 0) predicted.Add("T " + ct);
                        if (f.InExpressionTree) continue;
                        predicted.Add("M " + ct + "::" + f.Name);
                        if (f.Sym.IsAsync || f.Sym.IsIterator) predicted.Add("T " + ct + "+<" + f.Name + ">d");
                    }
                    foreach (var e in names.Envs) predicted.Add("T " + tn + "+" + e.Name);
                }
            foreach (var x in actual.Except(predicted).Order()) Console.Error.WriteLine("names: Roslyn made, not predicted: " + x);
            foreach (var x in predicted.Except(actual).Order()) Console.Error.WriteLine("names: predicted, Roslyn did not make: " + x);
        }
    }
}
