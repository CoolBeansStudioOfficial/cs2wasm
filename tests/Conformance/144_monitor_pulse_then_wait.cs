// Two threads taking turns on one lock, each pulsing the other and then waiting: a pulse goes to a thread that was
// waiting when it was given, never to the one that gives it and waits next (a count of pending pulses let a thread
// take its own, and both waited forever, as PLINQ's ordered merge did). Also a wait whose time runs out takes no
// pulse meant for another.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class Program
{
    static readonly object s_lock = new object();
    static int s_turn;

    static void Player(int me, int rounds)
    {
        lock (s_lock)
        {
            for (int i = 0; i < rounds; i++)
            {
                while (s_turn != me) Monitor.Wait(s_lock);
                s_turn = 1 - me;
                Monitor.Pulse(s_lock);
            }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        const int Rounds = 20000;
        var a = new Thread(() => Player(0, Rounds));
        var b = new Thread(() => Player(1, Rounds));
        a.Start(); b.Start();
        a.Join(); b.Join();
        Console.WriteLine("turns " + s_turn);

        // a timed wait that runs out, then a pulse for the one that waits on
        var gate = new object();
        bool woke = false;
        var waiter = new Thread(() => { lock (gate) { while (!woke) Monitor.Wait(gate); } });
        waiter.Start();
        Thread.Sleep(20);
        lock (gate) { Monitor.Wait(gate, 1); }
        lock (gate) { woke = true; Monitor.Pulse(gate); }
        waiter.Join();
        Console.WriteLine("woke " + woke);
    }
}
