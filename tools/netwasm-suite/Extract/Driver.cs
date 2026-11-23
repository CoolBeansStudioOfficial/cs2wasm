// The driver each case project gets: a Main that calls the case's entry point over its inputs, in order, in one
// process (as NetWasm's own oracle host does), and prints every observation as one line of ASCII: the value returned
// or the exception that escaped (its type, its message, and its inner exceptions'), then the case's trace if it has
// one. Lines end in "\n" on every platform and every character outside printable ASCII is escaped, so the .NET run and
// the module's can be compared byte for byte.
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Il2Wasm.NetWasmSuite;

sealed record Signature(string? Problem, string TypeName = "", string ReturnType = "", string ArgKind = "", bool Trace = false, bool TypedTrace = false);

static class Driver
{
    // bumped whenever the generated driver changes, so oracle results recorded with an older one are redone
    public const string Version = "2";

    public static Signature Analyze(CaseInfo c)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var entryPoints = new List<(string Ns, ClassDeclarationSyntax Type)>();
        foreach (var (_, text) in c.Sources)
        {
            var root = CSharpSyntaxTree.ParseText(text, parse).GetRoot();
            if (root.DescendantNodes().OfType<GlobalStatementSyntax>().Any()) return new("the source has top-level statements");
            foreach (var t in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t => t.Identifier.ValueText == "EntryPoint" && t.Parent is not TypeDeclarationSyntax))
            {
                var ns = string.Join(".", t.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
                entryPoints.Add((ns, t));
            }
        }
        var want = c.Namespace;
        var pick = entryPoints.Where(e => e.Ns == want).Select(e => e.Type).ToList();
        if (pick.Count == 0 && entryPoints.Count == 1) pick = [entryPoints[0].Type];
        if (pick.Count == 0) return new($"no EntryPoint class in namespace {want}");
        var ns2 = entryPoints.First(e => pick.Contains(e.Type)).Ns;
        var typeName = "global::" + (ns2.Length > 0 ? ns2 + "." : "") + "EntryPoint";
        var methods = pick.SelectMany(t => t.Members.OfType<MethodDeclarationSyntax>()).ToList();
        bool IsStatic(MethodDeclarationSyntax m) => m.Modifiers.Any(SyntaxKind.StaticKeyword) || pick.Any(t => t.Modifiers.Any(SyntaxKind.StaticKeyword));
        bool Public(MethodDeclarationSyntax m) => !m.Modifiers.Any(SyntaxKind.PrivateKeyword) && !m.Modifiers.Any(SyntaxKind.ProtectedKeyword) && (m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.InternalKeyword));
        var entry = methods.Where(m => m.Identifier.ValueText == c.Entry && IsStatic(m)).ToList();
        if (entry.Count == 0) return new($"no static {c.Entry} in {typeName}");
        var e = entry.FirstOrDefault(m => m.ParameterList.Parameters.Count == 1) ?? entry[0];
        if (!Public(e)) return new($"{c.Entry} is not accessible");
        var argKind = e.ParameterList.Parameters.Count switch
        {
            0 => "none",
            1 => e.ParameterList.Parameters[0].Type?.ToString() switch { "int" or "Int32" or "System.Int32" => "int", "string[]" => "args", var t => "unsupported:" + t },
            _ => "unsupported:" + e.ParameterList.Parameters.Count + " parameters",
        };
        if (argKind.StartsWith("unsupported:", StringComparison.Ordinal)) return new($"entry parameters: {argKind[12..]}");
        if (e.TypeParameterList is not null) return new("generic entry");
        bool Has(string name, int argc) => methods.Any(m => m.Identifier.ValueText == name && IsStatic(m) && Public(m) && m.ParameterList.Parameters.Count == argc);
        var typed = Has("TraceCount", 0) && Has("TraceKind", 1) && Has("TraceEventId", 1) && Has("TracePayloadLow", 1) && Has("TracePayloadHigh", 1);
        return new(null, typeName, e.ReturnType.ToString(), argKind, Has("Trace", 0), typed);
    }

    public static string Generate(CaseInfo c, Signature s)
    {
        var call = $"{s.TypeName}.{c.Entry}({s.ArgKind switch { "int" => "input", "args" => "new string[] { input.ToString(global::System.Globalization.CultureInfo.InvariantCulture) }", _ => "" }})";
        var rt = s.ReturnType.Replace("System.Threading.Tasks.", "");
        var value = rt switch
        {
            "void" => $"{call}; line = \"done\";",
            "Task" or "ValueTask" => $"{call}.GetAwaiter().GetResult(); line = \"done\";",
            _ when rt.StartsWith("Task<", StringComparison.Ordinal) || rt.StartsWith("ValueTask<", StringComparison.Ordinal) => $"line = \"value \" + Show({call}.GetAwaiter().GetResult());",
            _ => $"line = \"value \" + Show({call});",
        };
        var inputs = c.Inputs.Count == 0 && s.ArgKind == "none" ? "0" : string.Join(", ", c.Inputs);
        var sb = new StringBuilder();
        sb.Append($$"""
            // <auto-generated/> by il2wasm's tools/netwasm-suite: the driver of {{c.Id.Replace("*/", "")}}
            #nullable disable
            namespace Il2WasmSuite
            {
                internal static class CaseMain
                {
                    private static readonly int[] Inputs = new int[] { {{inputs}} };

                    private static int current;

                    private static int Main()
                    {
                        // an exception that escapes on another thread (or a finalizer): recorded as the observation of
                        // the input that was running, then the process ends with a fixed code
                        global::System.AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                        {
                            Write(current, "unhandled " + (e.ExceptionObject is global::System.Exception x ? Describe(x) : "" + e.ExceptionObject));
                            global::System.Console.Out.Flush();
                            global::System.Environment.Exit(197);
                        };
                        foreach (var input in Inputs)
                        {
                            string line;
                            current = input;
                            try { {{value}} }
                            catch (global::System.Exception e) { line = "throw " + Describe(e); }
                            Write(input, line);

            """);
        if (s.Trace)
            sb.Append($$"""
                            try { Write(input, "trace " + Show({{s.TypeName}}.Trace())); }
                            catch (global::System.Exception e) { Write(input, "trace throw " + Describe(e)); }

            """);
        if (s.TypedTrace)
            sb.Append($$"""
                            try
                            {
                                var count = {{s.TypeName}}.TraceCount();
                                Write(input, "trace count " + Show(count));
                                for (var i = 0; i < count; i++)
                                    Write(input, "trace " + Show(i) + ": " + Show({{s.TypeName}}.TraceKind(i)) + " " + Show({{s.TypeName}}.TraceEventId(i)) + " " + Show({{s.TypeName}}.TracePayloadLow(i)) + " " + Show({{s.TypeName}}.TracePayloadHigh(i)));
                            }
                            catch (global::System.Exception e) { Write(input, "trace throw " + Describe(e)); }

            """);
        sb.Append("""
                        }
                        global::System.Console.Out.Flush();
                        return 0;
                    }

                    private static string Show<T>(T value)
                    {
                        if (value is null) return "null";
                        if (value is global::System.IFormattable f) return f.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture);
                        return value.ToString();
                    }

                    private static string Describe(global::System.Exception e)
                    {
                        var text = e.GetType().FullName + ": " + e.Message;
                        var depth = 0;
                        for (var inner = e.InnerException; inner is not null && depth < 4; inner = inner.InnerException, depth++)
                            text += " ---> " + inner.GetType().FullName + ": " + inner.Message;
                        return text;
                    }

                    private static void Write(int input, string line)
                    {
                        var b = new global::System.Text.StringBuilder();
                        b.Append('[').Append(input.ToString(global::System.Globalization.CultureInfo.InvariantCulture)).Append("] ");
                        foreach (var ch in line)
                        {
                            if (ch == '\\') b.Append("\\\\");
                            else if (ch >= ' ' && ch <= '~') b.Append(ch);
                            else b.Append("\\u").Append(((int)ch).ToString("x4", global::System.Globalization.CultureInfo.InvariantCulture));
                        }
                        b.Append('\n');
                        global::System.Console.Out.Write(b.ToString());
                    }
                }
            }

            """);
        return sb.ToString();
    }

    // A library suite's driver: each [Test] method of each concrete class, in source order, on a new instance (disposed
    // after), once per [Arguments] row; one line each, PASS or FAIL with the exception. Returns null and a reason when
    // the suite has no test the driver can call.
    public static (string? Text, int Tests, string? Problem) GenerateLibrary(CaseInfo c)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var calls = new StringBuilder();
        var tests = 0;
        foreach (var (_, text) in c.Sources)
        {
            var root = CSharpSyntaxTree.ParseText(text, parse).GetRoot();
            foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (type.Modifiers.Any(SyntaxKind.AbstractKeyword) || type.TypeParameterList is not null) continue;
                if (type.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.TypeParameterList is not null)) continue;
                var names = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText);
                var ns = string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
                var full = "global::" + (ns.Length > 0 ? ns + "." : "") + string.Join(".", names);
                var display = (ns.Length > 0 ? ns + "." : "") + string.Join(".", names);
                foreach (var m in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var attrs = m.AttributeLists.SelectMany(a => a.Attributes).ToList();
                    if (!attrs.Any(a => a.Name.ToString() is "Test" or "TUnit.Core.Test" or "TestAttribute")) continue;
                    var rows = attrs.Where(a => a.Name.ToString() is "Arguments" or "TUnit.Core.Arguments").Select(a => a.ArgumentList?.Arguments.ToString() ?? "").ToList();
                    if (rows.Count == 0) rows.Add("");
                    var isStatic = m.Modifiers.Any(SyntaxKind.StaticKeyword);
                    var rt = m.ReturnType.ToString();
                    var awaits = rt is "Task" or "ValueTask" || rt.StartsWith("Task<", StringComparison.Ordinal) || rt.StartsWith("ValueTask<", StringComparison.Ordinal) || rt.EndsWith(".Task", StringComparison.Ordinal) || rt.EndsWith(".ValueTask", StringComparison.Ordinal);
                    foreach (var row in rows)
                    {
                        tests++;
                        var label = $"{display}.{m.Identifier.ValueText}({row})".Replace("\\", "\\\\").Replace("\"", "\\\"");
                        var target = isStatic ? full : "t";
                        var call = $"{(awaits ? "await " : "")}{target}.{m.Identifier.ValueText}({row});";
                        calls.Append("            await Test(\"").Append(label).Append("\", async () => { ");
                        if (isStatic) calls.Append(call);
                        else calls.Append($"var t = new {full}(); try {{ {call} }} finally {{ await Dispose(t); }}");
                        calls.Append(" });\n");
                    }
                }
            }
        }
        if (tests == 0) return (null, 0, "no [Test] method");
        var sb = new StringBuilder();
        sb.Append($$"""
            // <auto-generated/> by il2wasm's tools/netwasm-suite: the driver of {{c.Id.Replace("*/", "")}}
            #nullable disable
            #pragma warning disable CS1998
            namespace Il2WasmSuite
            {
                internal static class CaseMain
                {
                    private static int passed, failed;

                    private static string current = "";

                    private static async global::System.Threading.Tasks.Task<int> Main()
                    {
                        // an exception that escapes on another thread (or a finalizer): recorded against the test that
                        // was running, then the process ends with a fixed code
                        global::System.AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                        {
                            Write("UNHANDLED " + current + ": " + (e.ExceptionObject is global::System.Exception x ? Describe(x) : "" + e.ExceptionObject));
                            global::System.Console.Out.Flush();
                            global::System.Environment.Exit(197);
                        };

            """);
        sb.Append(calls);
        sb.Append("""
                        Write(passed.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + " passed, " + failed.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + " failed");
                        global::System.Console.Out.Flush();
                        return 0;
                    }

                    private static async global::System.Threading.Tasks.Task Test(string name, global::System.Func<global::System.Threading.Tasks.Task> body)
                    {
                        current = name;
                        try { await body(); passed++; Write("PASS " + name); }
                        catch (global::System.Exception e) { failed++; Write("FAIL " + name + ": " + Describe(e)); }
                    }

                    private static async global::System.Threading.Tasks.Task Dispose(object test)
                    {
                        if (test is global::System.IAsyncDisposable a) await a.DisposeAsync();
                        else if (test is global::System.IDisposable d) d.Dispose();
                    }

                    private static string Describe(global::System.Exception e)
                    {
                        var text = e.GetType().FullName + ": " + e.Message;
                        var depth = 0;
                        for (var inner = e.InnerException; inner is not null && depth < 4; inner = inner.InnerException, depth++)
                            text += " ---> " + inner.GetType().FullName + ": " + inner.Message;
                        return text;
                    }

                    private static void Write(string line)
                    {
                        var b = new global::System.Text.StringBuilder();
                        foreach (var ch in line)
                        {
                            if (ch == '\\') b.Append("\\\\");
                            else if (ch >= ' ' && ch <= '~') b.Append(ch);
                            else b.Append("\\u").Append(((int)ch).ToString("x4", global::System.Globalization.CultureInfo.InvariantCulture));
                        }
                        b.Append('\n');
                        global::System.Console.Out.Write(b.ToString());
                    }
                }
            }

            """);
        return (sb.ToString(), tests, null);
    }
}
