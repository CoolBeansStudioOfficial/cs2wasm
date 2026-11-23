// Entry points of the program without a thread (tools/frames-threads.mjs).
using System.Runtime.InteropServices;
static class WasmMainSingle
{
    [UnmanagedCallersOnly(EntryPoint = "churn")] static void Churn() => Frames.Run("churn");
    [UnmanagedCallersOnly(EntryPoint = "collections")] static void Collections() => Frames.Run("collections");
    [UnmanagedCallersOnly(EntryPoint = "strings")] static void Strings() => Frames.Run("strings");
    [UnmanagedCallersOnly(EntryPoint = "tree")] static void Tree() => Frames.Run("tree");
    [UnmanagedCallersOnly(EntryPoint = "bigdrop")] static void BigDrop() => Frames.Run("bigdrop");
    [UnmanagedCallersOnly(EntryPoint = "pchurn0")] static void PChurn() => Single.Private("pchurn0", false);
    [UnmanagedCallersOnly(EntryPoint = "ptree0")] static void PTree() => Single.Private("ptree0", true);
    [UnmanagedCallersOnly(EntryPoint = "shared0")] static void Shared() => Single.Shared("shared0");
}
