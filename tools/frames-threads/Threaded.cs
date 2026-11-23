// The scenes of tools/frames-threads.mjs that start threads:
//   idle <scene>     tools/frames/Frames.cs's scene on the main thread while one worker waits on a monitor: what a
//                    program pays for having a thread at all
//   pchurnN, ptreeN  N threads, each with a Worker of its own made on that thread (Work.cs): scaling of private work
//   sharedN          the main thread posts 64 work items a frame into a queue, N workers take them, work, and put
//                    results into a second queue the main thread empties: objects made on one thread, freed on another
using System;
using System.Threading;

static class Threaded
{
    // (the same with the work handed over as an Action, one delegate type for the main thread's work and the
    // workers': aidle_<scene> and aparts1. Which lambda a worker can run is told by the delegates it can reach,
    // not by their type: Sharing.LambdaTag)
    public static void IdleBy(Action work)
    {
        var idle = new IdleWorker();
        work();
        idle.Stop();
    }
    public static void OnWorkerBy(Action work)
    {
        var t = new Thread(() => work());
        t.Start();
        t.Join();
    }

    public static void Idle(string scene)
    {
        var idle = new IdleWorker();
        Frames.Run(scene);
        idle.Stop();
    }

    /// <summary>The churn's or the tree's parts on the calling thread while one worker waits on a monitor.</summary>
    public static void PartsWithIdle(bool tree)
    {
        var idle = new IdleWorker();
        if (tree) TreeParts.Run("tparts_main"); else Parts.Run("parts_main");
        idle.Stop();
    }

    /// <summary>The churn's or the tree's parts on a worker.</summary>
    public static void PartsOnWorker(bool tree)
    {
        var t = new Thread(() => { if (tree) TreeParts.Run("tparts1"); else Parts.Run("parts1"); });
        t.Start();
        t.Join();
    }

    /// <summary>A worker that waits on a monitor until it is stopped.</summary>
    sealed class IdleWorker
    {
        readonly object _gate = new();
        bool _done;
        readonly Thread _thread;
        public IdleWorker()
        {
            _thread = new Thread(() => { lock (_gate) { while (!_done) Monitor.Wait(_gate); } });
            _thread.Start();
        }
        public void Stop()
        {
            lock (_gate) { _done = true; Monitor.PulseAll(_gate); }
            _thread.Join();
        }
    }

    public static void Private(string scene, bool tree, int n)
    {
        var times = new long[n][];
        var checks = new long[n];
        var threads = new Thread[n];
        long t0 = Report.Now();
        for (int i = 0; i < n; i++)
        {
            int id = i;
            threads[i] = new Thread(() =>
            {
                var w = new Worker(id);
                w.Run(tree, Single.Warm, Single.Frames);
                checks[id] = w.Check;
                times[id] = w.Times;
            });
            threads[i].Start();
        }
        long check = 0;
        for (int i = 0; i < n; i++) { threads[i].Join(); check += checks[i]; }
        Report.Print(scene, times, check, Report.Now() - t0);
    }

    /// <summary>N threads of arithmetic alone (no object, no memory beyond a few locals): what the machine itself
    /// gives N threads, to read the other scenes' scaling against.</summary>
    public static void Spin(string scene, int n)
    {
        var times = new long[n][];
        var checks = new long[n];
        var threads = new Thread[n];
        long t0 = Report.Now();
        for (int i = 0; i < n; i++)
        {
            int id = i;
            threads[i] = new Thread(() =>
            {
                var mine = new long[600];
                uint x = 12345 + (uint)id;
                long sum = 0;
                for (int f = 0; f < 700; f++)
                {
                    long a = Report.Now();
                    for (int k = 0; k < 400000; k++) { x = x * 1664525u + 1013904223u; sum += x >> 24; }
                    if (f >= 100) mine[f - 100] = Report.Now() - a;
                }
                checks[id] = sum;
                times[id] = mine;
            });
            threads[i].Start();
        }
        long check = 0;
        for (int i = 0; i < n; i++) { threads[i].Join(); check += checks[i]; }
        Report.Print(scene, times, check, Report.Now() - t0);
    }

    public static void Shared(string scene, int n)
    {
        var threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            threads[i] = new Thread(() =>
            {
                while (true)
                {
                    var j = Jobs.In.Take();
                    if (j.Id < 0) break;
                    Jobs.Out.Put(Jobs.Process(j));
                }
            });
            threads[i].Start();
        }
        long t0 = Report.Now(), check = 0;
        var times = new long[Single.Frames];
        for (int f = 0; f < Single.Warm + Single.Frames; f++)
        {
            long a = Report.Now();
            Jobs.Post(f);
            check += Jobs.Collect(true);
            if (f >= Single.Warm) times[f - Single.Warm] = Report.Now() - a;
        }
        long wall = Report.Now() - t0;
        for (int i = 0; i < n; i++) Jobs.In.Put(new Job { Id = -1 });
        for (int i = 0; i < n; i++) threads[i].Join();
        Report.Print(scene, new[] { times }, check, wall);
    }
}
