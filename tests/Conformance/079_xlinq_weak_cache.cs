using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

// System.Xml.Linq keeps its namespaces in caches of weak references, written for a collector; cs2wasm's manifest makes
// the code that creates a namespace keep it while it hands it out (section 7.4).
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
    }
}
