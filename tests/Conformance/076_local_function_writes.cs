using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public class QFT { public bool F; public IEnumerable<string> Items() { yield return "x"; } }
public static class QF { [UnmanagedCallersOnly(EntryPoint="main")] static void main()=>Run();
 static void Go(object o){
  List<string>? observed = null; QFT? first = null;
  void Handle(QFT t) { if (t.F) { (observed ??= new()).AddRange(t.Items()); } else { first ??= t; } }
  if (o is List<QFT> list) { foreach (QFT t in list) Handle(t); } else { Handle((QFT)o); }
  if (observed != null) Console.WriteLine("observed " + observed.Count); else if (first != null) Console.WriteLine("first"); else Console.WriteLine("NOTHING");
 }
 public static void Run(){
  Go(new QFT { F = true }); Go(new QFT()); Go(new List<QFT> { new QFT { F = true }, new QFT { F = true } });
  int n = 0; string? s = null; void Inc() { n++; s ??= "set"; } Inc(); Inc(); Console.WriteLine(n + " " + s);
 } }
