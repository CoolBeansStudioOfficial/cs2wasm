using System; using System.Collections.Generic; using System.Linq; using System.Runtime.InteropServices; using System.Threading; using System.Threading.Tasks;
using System.Runtime.CompilerServices;
public struct P08Aw : ICriticalNotifyCompletion { public bool IsCompleted=>false; public int GetResult()=>5;
  public void OnCompleted(Action c){ Console.WriteLine("OnCompleted"); c(); } public void UnsafeOnCompleted(Action c){ Console.WriteLine("UnsafeOnCompleted"); c(); } public P08Aw GetAwaiter()=>this; }
public static class P08 { [UnmanagedCallersOnly(EntryPoint="main")] static void main()=>Run();
 static async ValueTask<int> V(int x){ if(x>0) await Task.Delay(1); return x*2; }
 static async IAsyncEnumerable<int> Gen([EnumeratorCancellation] CancellationToken ct=default){ for(int i=0;i<3;i++){ await Task.Yield(); yield return i; } }
 static async Task Main2(){
  Console.WriteLine(await new P08Aw()); Console.WriteLine(await V(0)+await V(4));
  await foreach(var x in Gen()) Console.Write(x); Console.WriteLine();
  var tcs=new TaskCompletionSource<int>(); _=Task.Run(()=>tcs.SetResult(9)); Console.WriteLine(await tcs.Task);
  var r=await Task.WhenAll(Enumerable.Range(0,5).Select(async i=>{ await Task.Delay(5-i); return i*i; })); Console.WriteLine(string.Join(",",r));
  try { await Task.WhenAll(Task.FromException(new ArgumentException("a")),Task.FromException(new FormatException("b"))); } catch(Exception e){ Console.WriteLine(e.GetType().Name); }
  var any=await Task.WhenAny(Task.Delay(2000),Task.Delay(1)); Console.WriteLine(any.IsCompleted);
  await using(var d=new D()){ Console.WriteLine("in"); }
  using var cts=new CancellationTokenSource(20); try { await Task.Delay(5000,cts.Token); } catch(OperationCanceledException){ Console.WriteLine("cancelled"); }
  Console.WriteLine(await Task.Run(()=>Task.Factory.StartNew(()=>3,TaskCreationOptions.LongRunning)).ConfigureAwait(false)); }
 class D:IAsyncDisposable{ public async ValueTask DisposeAsync(){ await Task.Yield(); Console.WriteLine("disposed"); } }
 public static void Run(){ Main2().GetAwaiter().GetResult(); } }
