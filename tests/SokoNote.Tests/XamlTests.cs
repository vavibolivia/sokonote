using System.IO;
using System.Xml.Linq;

namespace SokoNote.Tests;

public class XamlTests
{
    private static IEnumerable<string> XamlFiles()
    {
        return Directory.EnumerateFiles(SourcePaths.AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"));
    }

    [Fact]
    public void No_event_handlers_inside_style_setter_values()
    {
        // regression: Click handlers in a ContextMenu inside <Setter.Value> got wired to the wrong element by the
        // XAML compiler (InvalidCastException "Button → Grid" in a loop as soon as list items were created)
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var setterValue in doc.Descendants().Where(e => e.Name.LocalName == "Setter.Value"))
                foreach (var el in setterValue.Descendants())
                    if (el.Attributes().Any(a => a.Name.LocalName is "Click" or "Checked" or "Unchecked" or "SelectionChanged"
                            or "MouseDoubleClick" or "KeyDown" or "Loaded"))
                        offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)el).LineNumber} <{el.Name.LocalName}>");
        }
        Assert.Empty(offenders);
    }
}
