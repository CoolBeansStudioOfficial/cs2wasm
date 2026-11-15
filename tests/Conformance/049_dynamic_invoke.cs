using System;
using System.Runtime.InteropServices;
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    Func<int, int, int> add = (a, b) => a + b;
    Console.WriteLine(add.DynamicInvoke(2, 40));
    Action<string> say = s => Console.WriteLine("say " + s);
    Console.WriteLine(say.DynamicInvoke("hi") == null);
    try { add.DynamicInvoke(1); } catch (System.Reflection.TargetParameterCountException e) { Console.WriteLine(e.Message); }
    Func<int> boom = () => throw new InvalidOperationException("x");
    try { boom.DynamicInvoke(); } catch (System.Reflection.TargetInvocationException e) { Console.WriteLine(e.InnerException!.Message); }
  }
}
