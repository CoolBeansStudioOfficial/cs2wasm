using System; using System.Collections.Generic; using System.Linq; using System.Runtime.InteropServices; using System.Threading; using System.Threading.Tasks;
using System.Collections.Concurrent; using System.Threading.Channels;
public static class P06 { [UnmanagedCallersOnly(EntryPoint="main")] static void main()=>Run();
 [ThreadStatic] static int ts;
 public static void Run(){
  var cd=new ConcurrentDictionary<string,int>(); foreach(var k in new[]{"zeta","alpha","mid","beta","omega","k1","k2","k3"}) cd[k]=k.Length;
  Console.WriteLine(string.Join(",",cd.Keys));
  long total=0; Parallel.For(0,1000,i=>Interlocked.Add(ref total,i)); Console.WriteLine(total);
  var ch=Channel.CreateUnbounded<int>(); var prod=Task.Run(async()=>{ for(int i=0;i<100;i++) await ch.Writer.WriteAsync(i); ch.Writer.Complete(); });
  int sum=0; var cons=Task.Run(async()=>{ await foreach(var x in ch.Reader.ReadAllAsync()) sum+=x; }); Task.WaitAll(prod,cons); Console.WriteLine(sum);
  var rw=new ReaderWriterLockSlim(); var bc=new BlockingCollection<int>(4); var bar=new Barrier(2); int got=0;
  var t=new Thread(()=>{ ts=5; bar.SignalAndWait(); foreach(var x in bc.GetConsumingEnumerable()){ rw.EnterWriteLock(); got+=x; rw.ExitWriteLock(); } }); t.Start();
  bar.SignalAndWait(); for(int i=1;i<=50;i++) bc.Add(i); bc.CompleteAdding(); t.Join(); Console.WriteLine(got+" "+ts);
  var tl=new ThreadLocal<int>(()=>Environment.CurrentManagedThreadId); var al=new AsyncLocal<string>(); al.Value="a";
  Task.Run(()=>Console.WriteLine(al.Value+" "+(tl.Value!=0))).Wait();
  var sem=new SemaphoreSlim(0); var w=Task.Run(async()=>{ await sem.WaitAsync(); return 42; }); sem.Release(); Console.WriteLine(w.Result);
  var cts=new CancellationTokenSource(); cts.Cancel(); try { Task.Delay(1000,cts.Token).Wait(); } catch(AggregateException e){ Console.WriteLine(e.InnerException!.GetType().Name); }
  var lz=new Lazy<int>(()=>7,LazyThreadSafetyMode.ExecutionAndPublication); Console.WriteLine(lz.Value);
  var mre=new ManualResetEvent(false); ThreadPool.QueueUserWorkItem(_=>mre.Set()); Console.WriteLine(mre.WaitOne(5000));
  var q=new ConcurrentQueue<int>(); Parallel.ForEach(Enumerable.Range(0,200),i=>q.Enqueue(i)); Console.WriteLine(q.Sum());
  Console.WriteLine(Enumerable.Range(0,1000).AsParallel().Where(i=>i%3==0).Sum()); } }
