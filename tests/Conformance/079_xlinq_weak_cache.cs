using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

// System.Xml.Linq keeps its namespaces and names in caches of weak references: an object only a weak reference refers
// to must live until a collection, as under .NET (section 4.4).
static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var doc = new XElement("root", new XAttribute("k", 1),
            new XElement("item", new XAttribute("id", "a"), "first"),
            new XElement("item", new XAttribute("id", "b"), "second"));
        Console.WriteLine(doc);
        Console.WriteLine(string.Join(",", doc.Elements("item").Select(e => (string)e.Attribute("id") + "=" + e.Value)));
        XNamespace ns = "urn:x";
        var q = new XElement(ns + "q", new XElement(ns + "r", 5));
        Console.WriteLine(q.Element(ns + "r").Value + " " + q.Name.NamespaceName + " " + (XName.Get("q", "urn:x") == q.Name));
        var w = new WeakReference<object>(new object());
        Console.WriteLine(w.TryGetTarget(out _));
    }
}
