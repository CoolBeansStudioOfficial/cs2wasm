using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Cfg { static class Top { public static readonly int V = int.Parse("x"); } public static class Outer { public static class Inner { public static readonly int V = int.Parse("y"); } } }

static class Program
{
    static class Broken
    {
        public static readonly int Value = Compute();
        static int Compute() => throw new InvalidOperationException("no config");
    }

    static class Fine
    {
        public static int Count;
        static Fine() { Count = 5; Console.WriteLine("Fine initialized"); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        for (int i = 0; i < 2; i++)
        {
            try { Console.WriteLine(Broken.Value); }
            catch (TypeInitializationException e) { Console.WriteLine(e.Message + " | " + e.TypeName + " | " + e.InnerException.GetType().Name + ": " + e.InnerException.Message); }
        }
        try { Console.WriteLine(Cfg.Top.V); } catch (TypeInitializationException e) { Console.WriteLine(e.TypeName); }
        try { Console.WriteLine(Cfg.Outer.Inner.V); } catch (TypeInitializationException e) { Console.WriteLine(e.TypeName); }
        Console.WriteLine(Fine.Count + Fine.Count);
        Console.WriteLine(Task.Run(() => 1).Result);
    }
}
