// NativeAOT's program with threads: the scene named on the command line (idle_<scene>, pchurnN, ptreeN, sharedN).
static class AotMainThreads
{
    static void Main(string[] args)
    {
        string s = args[0];
        if (s.StartsWith("idle_")) Threaded.Idle(s.Substring(5));
        else if (s == "parts1") Threaded.OnWorker(() => Parts.Run(s));
        else if (s.StartsWith("pchurn")) Threaded.Private(s, false, int.Parse(s.Substring(6)));
        else if (s.StartsWith("ptree")) Threaded.Private(s, true, int.Parse(s.Substring(5)));
        else Threaded.Shared(s, int.Parse(s.Substring(6)));
    }
}
