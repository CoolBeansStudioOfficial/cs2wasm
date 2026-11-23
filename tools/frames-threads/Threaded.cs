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
    public static void Idle(string scene)
    {
        var gate = new object();
        var done = new bool[1];
        var t = new Thread(() => { lock (gate) { while (!done[0]) Monitor.Wait(gate); } });
        t.Start();
        Frames.Run(scene);
        lock (gate) { done[0] = true; Monitor.PulseAll(gate); }
        t.Join();
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
