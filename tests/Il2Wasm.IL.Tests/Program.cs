// The IL front end's tests: the type system (NativeAOT's, for wasm32), the importer's IL shapes, and end-to-end
// programs compiled from IL through the existing back end and run under host/run.mjs, with .NET as the oracle.
// usage: dotnet run --project tests/Il2Wasm.IL.Tests [-- filter]
using Il2Wasm.IL.Tests;

var filter = args.Length > 0 ? args[0] : "";
var c = new Checks(filter);
try
{
    if (c.Wants("types")) TypeSystemChecks.Run(c);
}
finally { try { Directory.Delete(Build.Dir, true); } catch { } }
Console.WriteLine($"{c.Passed} passed, {c.Failed} failed");
return c.Failed == 0 ? 0 : 1;

sealed class Checks(string filter)
{
    public int Passed, Failed;
    string _section = "";
    public bool Wants(string group) => filter.Length == 0 || group.Contains(filter) || filter.Contains(group);
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
