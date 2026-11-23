// NativeAOT's program without a thread: the scene named on the command line.
static class AotMainSingle
{
    static void Main(string[] args)
    {
        string s = args[0];
        if (s == "pchurn0") Single.Private(s, false);
        else if (s == "ptree0") Single.Private(s, true);
        else if (s == "shared0") Single.Shared(s);
        else if (s == "parts0") Parts.Run(s);
        else if (s == "tparts0") TreeParts.Run(s);
        else Frames.Run(s);
    }
}
