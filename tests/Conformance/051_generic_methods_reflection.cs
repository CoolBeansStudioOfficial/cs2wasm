using System;
using System.Reflection;
using System.Runtime.InteropServices;
class Util { public static string Show<T>(T v) => typeof(T).Name + ":" + v; public T Pick<T>(T a, T b) => b; public int F; public int P { get; set; } public void A() { } }
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    var def = typeof(Util).GetMethod("Show")!;
    Console.WriteLine(def.IsGenericMethodDefinition + " " + def.IsGenericMethod + " " + def.GetGenericArguments()[0].Name);
    var mi = def.MakeGenericMethod(typeof(int));
    Console.WriteLine(mi.IsGenericMethodDefinition + " " + mi.GetGenericArguments()[0].Name + " " + mi.Invoke(null, new object[] { 42 }));
    Console.WriteLine(mi.GetGenericMethodDefinition() == def);
    var p = typeof(Util).GetMethod("Pick")!.MakeGenericMethod(typeof(string));
    Console.WriteLine(p.Invoke(new Util(), new object[] { "a", "b" }));
    Console.WriteLine((typeof(Util).GetMethod("A") == typeof(Util).GetMethod("A")) + " " + (typeof(Util).GetField("F") == typeof(Util).GetField("F")) + " " + (typeof(Util).GetProperty("P") == typeof(Util).GetProperty("P")));
    Console.WriteLine(typeof(Util).GetMethod("A")!.Equals(typeof(Util).GetMethod("Pick")));
  }
}
