using System.Runtime.InteropServices;
static class WasmMain
{
    [UnmanagedCallersOnly(EntryPoint = "churn")] static void Churn() => Frames.Run("churn");
    [UnmanagedCallersOnly(EntryPoint = "collections")] static void Collections() => Frames.Run("collections");
    [UnmanagedCallersOnly(EntryPoint = "strings")] static void Strings() => Frames.Run("strings");
    [UnmanagedCallersOnly(EntryPoint = "tree")] static void Tree() => Frames.Run("tree");
    [UnmanagedCallersOnly(EntryPoint = "bigdrop")] static void BigDrop() => Frames.Run("bigdrop");
}
