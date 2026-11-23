// Entry points of the program with threads (tools/frames-threads.mjs).
using System.Runtime.InteropServices;
static class WasmMainThreads
{
    [UnmanagedCallersOnly(EntryPoint = "idle_churn")] static void IChurn() => Threaded.Idle("churn");
    [UnmanagedCallersOnly(EntryPoint = "idle_collections")] static void ICollections() => Threaded.Idle("collections");
    [UnmanagedCallersOnly(EntryPoint = "idle_strings")] static void IStrings() => Threaded.Idle("strings");
    [UnmanagedCallersOnly(EntryPoint = "idle_tree")] static void ITree() => Threaded.Idle("tree");
    [UnmanagedCallersOnly(EntryPoint = "idle_bigdrop")] static void IBigDrop() => Threaded.Idle("bigdrop");
    [UnmanagedCallersOnly(EntryPoint = "pchurn1")] static void PChurn1() => Threaded.Private("pchurn1", false, 1);
    [UnmanagedCallersOnly(EntryPoint = "pchurn2")] static void PChurn2() => Threaded.Private("pchurn2", false, 2);
    [UnmanagedCallersOnly(EntryPoint = "pchurn4")] static void PChurn4() => Threaded.Private("pchurn4", false, 4);
    [UnmanagedCallersOnly(EntryPoint = "pchurn8")] static void PChurn8() => Threaded.Private("pchurn8", false, 8);
    [UnmanagedCallersOnly(EntryPoint = "ptree1")] static void PTree1() => Threaded.Private("ptree1", true, 1);
    [UnmanagedCallersOnly(EntryPoint = "ptree2")] static void PTree2() => Threaded.Private("ptree2", true, 2);
    [UnmanagedCallersOnly(EntryPoint = "ptree4")] static void PTree4() => Threaded.Private("ptree4", true, 4);
    [UnmanagedCallersOnly(EntryPoint = "ptree8")] static void PTree8() => Threaded.Private("ptree8", true, 8);
    [UnmanagedCallersOnly(EntryPoint = "parts1")] static void Parts1() => Threaded.OnWorker(() => Parts.Run("parts1"));
    [UnmanagedCallersOnly(EntryPoint = "shared1")] static void Shared1() => Threaded.Shared("shared1", 1);
    [UnmanagedCallersOnly(EntryPoint = "shared2")] static void Shared2() => Threaded.Shared("shared2", 2);
    [UnmanagedCallersOnly(EntryPoint = "shared4")] static void Shared4() => Threaded.Shared("shared4", 4);
    [UnmanagedCallersOnly(EntryPoint = "shared8")] static void Shared8() => Threaded.Shared("shared8", 8);
}
