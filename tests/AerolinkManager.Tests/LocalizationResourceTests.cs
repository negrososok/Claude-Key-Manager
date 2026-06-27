using System.Xml.Linq;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class LocalizationResourceTests
{
    [TestMethod]
    public void EnglishUkrainianAndRussianDictionariesHaveIdenticalKeys()
    {
        var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App", "Resources"));
        var english = Keys(Path.Combine(resources, "Strings.en.xaml"));
        var ukrainian = Keys(Path.Combine(resources, "Strings.uk.xaml"));
        var russian = Keys(Path.Combine(resources, "Strings.ru.xaml"));

        CollectionAssert.AreEquivalent(english, ukrainian, "Ukrainian resources do not match English keys.");
        CollectionAssert.AreEquivalent(english, russian, "Russian resources do not match English keys.");
        Assert.IsTrue(english.Count >= 60, "Localization coverage unexpectedly shrank.");
    }

    [TestMethod]
    public void NoResourceDictionaryHasDuplicateKeys()
    {
        var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App", "Resources"));
        foreach (var file in Directory.GetFiles(resources, "Strings.*.xaml"))
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var elements = XDocument.Load(file).Root!.Elements()
                .Select(el => (string?)el.Attribute(x + "Key"))
                .Where(k => k is not null)
                .Cast<string>()
                .ToList();
            var duplicates = elements
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.AreEqual(0, duplicates.Count,
                $"Duplicate x:Key values in {Path.GetFileName(file)}: {string.Join(", ", duplicates)}. " +
                "WPF throws System.ArgumentException when loading a ResourceDictionary with duplicate keys.");
        }
    }

    [TestMethod]
    public void AppXamlAndResourcesDoNotContainMojibakeFragments()
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App"));
        var files = Directory.GetFiles(Path.Combine(appRoot, "Resources"), "Strings.*.xaml")
            .Append(Path.Combine(appRoot, "MainWindow.xaml"));
        var fragments = new[] { "вЂ", "рџ", "вљ", "в—", "в•", "Рџ" };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var fragment in fragments)
            {
                Assert.IsFalse(text.Contains(fragment, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} contains mojibake fragment '{fragment}'.");
            }
        }
    }

    [TestMethod]
    public void ResourceValuesDoNotExposeRawEnumNames()
    {
        var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App", "Resources"));
        var rawTerms = new[]
        {
            "PriorityThenLru",
            "LeastRecentlyUsed",
            "RespectUser",
            "PreferProfile",
            "ForceProfile",
            "ManualKey",
            "ProviderFallback"
        };

        foreach (var file in Directory.GetFiles(resources, "Strings.*.xaml"))
        {
            var values = XDocument.Load(file).Root!.Elements().Select(el => el.Value);
            foreach (var value in values)
            {
                foreach (var term in rawTerms)
                {
                    Assert.IsFalse(value.Contains(term, StringComparison.Ordinal),
                        $"{Path.GetFileName(file)} exposes raw enum term '{term}' in value '{value}'.");
                }
            }
        }
    }

    private static List<string> Keys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!.Elements()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => key is not null)
            .Cast<string>()
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }
}
