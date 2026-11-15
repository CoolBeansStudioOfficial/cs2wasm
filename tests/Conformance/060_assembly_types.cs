using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Plugins
{
    public interface IPlugin { string Run(); }
    public class Alpha : IPlugin { public string Run() => "alpha"; public class Inner { } }
    public sealed class Beta : IPlugin { public string Run() => "beta"; }
    public abstract class Base { }
    internal class Gamma : Base, IPlugin { public string Run() => "gamma"; }
    public struct Point { public int X; }
    public enum Mode { A, B }
    public class Box<T> { public T? Value; }
    public delegate int Op(int a);
}

namespace Other { public class Zeta { } }

public class TopLevel { }

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var asm = typeof(Program).Assembly;
        var types = asm.GetTypes().Where(t => !t.Name.Contains('<') && t.Namespace != "Microsoft.CodeAnalysis" && t.Namespace != "System.Runtime.CompilerServices").ToList();
        foreach (var t in types) Console.WriteLine(t.FullName + " " + t.IsPublic + " " + t.IsNested);
        var plugins = types.Where(t => typeof(Plugins.IPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .OrderBy(t => t.Name).Select(t => ((Plugins.IPlugin)Activator.CreateInstance(t)!).Run());
        Console.WriteLine(string.Join(",", plugins));
        Console.WriteLine(asm.GetType("Plugins.Alpha+Inner") + " " + (asm.GetType("Plugins.Nope") == null) + " " + asm.GetType("plugins.beta", false, true));
        try { asm.GetType("Plugins.Nope", true); } catch (TypeLoadException e) { Console.WriteLine(e.GetType().Name); }
        Console.WriteLine(asm.GetExportedTypes().Count(t => !t.Name.Contains('<')) + " " + asm.GetModules().Length + " " + (asm.ManifestModule.GetTypes().Length == asm.GetTypes().Length));
        Console.WriteLine(typeof(object).Assembly.GetType("System.Int32") + " " + (typeof(object).Assembly.GetType("Plugins.Alpha") == null));
    }
}
