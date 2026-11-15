// Assembly-level custom attributes (as GodotSharp finds a game's scripts: [assembly: AssemblyHasScripts(new Type[] { ... })]
// and [ScriptPath] on each), with array arguments of typeof constants.
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: HasScripts(new Type[] { typeof(Mover), typeof(Other) })]

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class HasScriptsAttribute : Attribute
{
    public Type[] ScriptTypes { get; }
    public HasScriptsAttribute(Type[] scriptTypes) { ScriptTypes = scriptTypes; }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class PathAttribute : Attribute { public string Path { get; } public PathAttribute(string p) { Path = p; } }

[Path("res://Mover.cs")] public class Mover { }
public class Other { }

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var asm = typeof(Program).Assembly;
        var attrs = asm.GetCustomAttributes(inherit: false);
        Console.WriteLine(attrs.Length > 0);
        var hs = attrs.OfType<HasScriptsAttribute>().FirstOrDefault();
        Console.WriteLine(hs == null ? "none" : string.Join(",", hs.ScriptTypes.Select(t => t.Name)));
        foreach (var t in hs?.ScriptTypes ?? [])
            Console.WriteLine(t.Name + " " + (t.GetCustomAttributes(false).OfType<PathAttribute>().FirstOrDefault()?.Path ?? "-"));
    }
}
