using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SokoNote.Tests;

public class LocalizationTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string SourceDir() => SourcePaths.AppDir;

    private static Dictionary<string, string> Load(string lang) =>
        XDocument.Load(Path.Combine(SourceDir(), "Resources", $"Strings.{lang}.xaml")).Root!
            .Elements().ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value);

    private static string[] Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(v => v).ToArray();

    [Fact]
    public void Korean_and_english_have_the_same_keys()
    {
        var ko = Load("ko");
        var en = Load("en");
        Assert.Empty(ko.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(ko.Keys));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        var ko = Load("ko");
        var en = Load("en");
        var mismatched = ko.Keys.Where(k => en.ContainsKey(k) && !Placeholders(ko[k]).SequenceEqual(Placeholders(en[k]))).ToList();
        Assert.Empty(mismatched);
    }

    [Fact]
    public void App_name_is_soko_work_diary()
    {
        Assert.Equal("Soko Work Diary", Load("ko")["S.AppName"]);
        Assert.Equal("Soko Work Diary", Load("en")["S.AppName"]);
    }

    [Fact]
    public void Every_key_used_in_code_and_xaml_exists()
    {
        var keys = Load("ko").Keys.ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SourceDir(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\Resources\")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(?:Loc\.[TF]\(""|DynamicResource\s+|StaticResource\s+)(S\.[A-Za-z0-9_.]+)"))
                used.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(used);
        // keys built as prefix + enum value are checked in Enum_values_have_labels
        Assert.Empty(used.Where(k => !k.EndsWith('.') && !keys.Contains(k)).OrderBy(k => k));
    }

    [Fact]
    public void Enum_values_have_labels()
    {
        foreach (var lang in new[] { "ko", "en" })
        {
            var keys = Load(lang).Keys.ToHashSet();
            Assert.All(Enum.GetNames<Models.EntryStatus>(), n => Assert.Contains("S.Status." + n, keys));
            Assert.All(Enum.GetNames<Services.ClaudeScope>(), n => Assert.Contains("S.Claude.Scope." + n, keys));
        }
    }
}
