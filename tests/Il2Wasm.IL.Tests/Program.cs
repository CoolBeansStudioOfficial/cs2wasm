// The IL front end's tests: the type system (NativeAOT's, for wasm32), the importer's IL shapes, and end-to-end
// programs compiled from IL through the existing back end and run under host/run.mjs, with .NET as the oracle.
// usage: dotnet run --project tests/Il2Wasm.IL.Tests [-- filter]
using Il2Wasm.IL.Tests;

if (args.Length == 3 && args[0] == "compile")
{
    // (a program by name to a module, for debugging: compile 03_objects out.wasm)
    File.WriteAllBytes(args[2], EndToEnd.Compile(args[1], File.ReadAllText(Build.Here("Programs/" + args[1] + ".cs"))));
    return 0;
}
if (args.Length == 4 && args[0] == "framework")
{
    // (a program against a framework directory, the IL CoreLib and the stock libraries: framework <dir> <program.cs> out.wasm)
    var dir = args[1];
    var refs = Directory.GetFiles(dir, "*.dll");
    var dll = Build.Compile(Path.GetFileNameWithoutExtension(args[2]), [File.ReadAllText(args[2])], refs.Where(r => !Path.GetFileName(r).StartsWith("System.Private.CoreLib")).Append(Path.Combine(dir, "System.Private.CoreLib.dll")).ToArray());
    var ctx = new Il2Wasm.IL.Types.IlContext();
    ctx.AddAssemblies(refs.Append(dll));
    ctx.SetCoreLib();
    var entry = ctx.Load(Path.GetFileNameWithoutExtension(args[2])).EntryPoint!;
    var comp = new Il2Wasm.IL.Lower.IlCompiler(ctx) { Checked = Environment.GetEnvironmentVariable("IL2WASM_IL_CHECKED") == "1" };
    if (File.Exists(Path.Combine(dir, "il2wasm.natives.txt"))) comp.LoadNatives(Path.Combine(dir, "il2wasm.natives.txt"));
    File.WriteAllBytes(args[3], comp.Compile(entry));
    foreach (var w in comp.Warnings.Distinct().OrderBy(w => w)) Console.Error.WriteLine("warning: " + w);
    return 0;
}
var filter = args.Length > 0 ? args[0] : "";
var c = new Checks(filter);
try
{
    if (c.Wants("types")) TypeSystemChecks.Run(c);
    if (c.Wants("import")) ImporterChecks.Run(c);
    if (c.Wants("sigs")) SignatureChecks.Run(c);
    if (c.Wants("e2e")) EndToEnd.Run(c);
    if (c.Wants("fw")) EndToEnd.RunFramework(c);
    if (RuntimePrograms.Wants(filter)) RuntimePrograms.Run(c, filter);
}
finally { try { Directory.Delete(Build.Dir, true); } catch { } }
Console.WriteLine($"{c.Passed} passed, {c.Failed} failed");
return c.Failed == 0 ? 0 : 1;

sealed class Checks(string filter)
{
    public int Passed, Failed;
    string _section = "";
    public bool Wants(string group) => filter.Length == 0 || group.Contains(filter) || filter.Contains(group) || group == "e2e" && char.IsDigit(filter[0]) || group == "fw" && filter.StartsWith("f");
    /// <summary>A program by name: all, or those the filter (a number prefix: "03") names.</summary>
    public bool WantsProgram(string name) => filter.Length == 0 || filter is "e2e" or "fw" || name.StartsWith(filter);
    public void Section(string s) { _section = s; Console.WriteLine("== " + s); }
    public void Info(string s) => Console.WriteLine("   " + s);
    public void True(string what, bool ok) => Report(what, ok, "");
    public void Eq<T>(string what, T actual, T expected) => Report(what, EqualityComparer<T>.Default.Equals(actual, expected), $" (got {actual}, expected {expected})");
    void Report(string what, bool ok, string detail)
    {
        if (ok) { Passed++; Console.WriteLine("   ok   " + what); }
        else { Failed++; Console.WriteLine("   FAIL " + what + detail + "   [" + _section + "]"); }
    }
}
