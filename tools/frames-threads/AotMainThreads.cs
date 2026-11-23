// NativeAOT's program with threads: the scene named on the command line (idle_<scene>, pchurnN, ptreeN, sharedN).
static class AotMainThreads
{
    static void Main(string[] args)
    {
        string s = args[0];
        if (s.StartsWith("idle_")) Threaded.Idle(s.Substring(5));
        else if (s.StartsWith("aidle_")) Threaded.IdleBy(() => Frames.Run(s.Substring(6)));
        else if (s == "aparts1") Threaded.OnWorkerBy(() => Parts.Run("parts1"));
        else if (s == "parts1") Threaded.PartsOnWorker(false);
        else if (s == "tparts1") Threaded.PartsOnWorker(true);
        else if (s == "parts_main") Threaded.PartsWithIdle(false);
        else if (s == "tparts_main") Threaded.PartsWithIdle(true);
        else if (s.StartsWith("pchurn")) Threaded.Private(s, false, int.Parse(s.Substring(6)));
        else if (s.StartsWith("ptree")) Threaded.Private(s, true, int.Parse(s.Substring(5)));
        else Threaded.Shared(s, int.Parse(s.Substring(6)));
    }
}
