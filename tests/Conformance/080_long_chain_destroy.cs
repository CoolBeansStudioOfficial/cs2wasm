using System; using System.Runtime.InteropServices;
// a chain through a type that cannot form a cycle with itself (object slot holding arrays): one long nest of destructions
sealed class Box { public object[] Inner; public int V; }
public static class Chain { [UnmanagedCallersOnly(EntryPoint="main")] static void main()=>Run();
 public static void Run(){
  object[] head = null;
  for (int i = 0; i < 300000; i++) head = new object[] { head, i };
  int depth = 0; for (var p = head; p != null; p = (object[])p[0]) depth++;
  head = null;
  Console.WriteLine("freed a chain of " + depth); } }
