using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Q1 { [UnmanagedCallersOnly(EntryPoint="main")] static void main()=>Run();
 static bool IsNull<T>(T x) => x == null;
 static bool Eq<T>(T x, T y) => EqualityComparer<T>.Default.Equals(x, y);
 public static void Run(){
  int n = 0;
  for (int i = 0; i < 1000; i++) { if (IsNull(i)) n++; if (IsNull("a")) n++; }
  var d = new Dictionary<int, int>();
  for (int i = 0; i < 1000; i++) d[i] = i;
  for (int i = 0; i < 1000; i++) if (Eq(i, i)) n++;
  var ds = new Dictionary<string, string>();
  for (int i = 0; i < 1000; i++) ds["k" + (i % 10)] = "v";
  Console.WriteLine(n + d.Count + ds.Count); } }
