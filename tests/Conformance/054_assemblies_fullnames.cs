using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
class X<T> { public class In { } }
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    Console.WriteLine(typeof(List<int>).FullName);
    Console.WriteLine(typeof(X<string>).FullName);
    Console.WriteLine(typeof(Dictionary<string, X<int>>).FullName);
    Console.WriteLine(typeof(X<int>).AssemblyQualifiedName);
    Console.WriteLine(typeof(X<int>.In).FullName);
    Console.WriteLine(typeof(List<int>).ToString() + " " + typeof(List<>).FullName);
    Console.WriteLine(typeof(int[]).FullName + " " + typeof(List<int>[]).FullName);
    Console.WriteLine(typeof(int).MakeByRefType().FullName + " " + typeof(int).AssemblyQualifiedName);
    ReadOnlySpan<char> sp = "1.23"; Range r = 2..4; Console.WriteLine(sp[r].ToString());
    var a = typeof(Program).Assembly;
    Console.WriteLine(a.FullName + " | " + a.GetName().Name + " " + a.GetName().Version + " | " + a);
    Console.WriteLine((a == Assembly.GetExecutingAssembly()) + " " + (Assembly.GetEntryAssembly() != null) + " " + (typeof(int).Assembly == typeof(string).Assembly) + " " + (a == typeof(int).Assembly));
    Console.WriteLine(typeof(object).Assembly.GetName().Name + " " + typeof(Enumerable).Assembly.GetName().Name + " " + typeof(int[]).Assembly.GetName().Name + " " + typeof(X<int>).Assembly.GetName().Name);
  }
}
