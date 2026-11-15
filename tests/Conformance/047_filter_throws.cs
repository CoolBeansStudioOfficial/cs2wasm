using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
static class Program {
  static bool Bad(Exception e) { Console.WriteLine("filter " + e.Message); throw new InvalidOperationException("in filter"); }
  static IEnumerable<int> It() { yield return 1; try { throw new Exception("a"); } catch (Exception e) when (Bad(e)) { Console.WriteLine("never"); } catch (Exception e) { Console.WriteLine("second " + e.Message); } yield return 2; }
  static async Task<int> As() { await Task.Yield(); try { throw new Exception("b"); } catch (Exception e) when (Bad(e)) { return 0; } catch (Exception e) { Console.WriteLine("second " + e.Message); } return 7; }
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() { foreach (var x in It()) Console.WriteLine(x); Console.WriteLine(As().GetAwaiter().GetResult()); }
}
