using System.Globalization;
using System.Windows;

namespace AerolinkManager.App;

public static class LocalizationService
{
    private static readonly HashSet<string> Supported = ["en", "uk", "ru"];
    public static string CurrentLanguage { get; private set; } = "en";

    public static void Apply(string? language)
    {
        var selected = Supported.Contains(language ?? string.Empty) ? language! : FromSystemCulture();
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(item => item.Source?.OriginalString.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null) dictionaries.Remove(existing);
        dictionaries.Insert(0, new ResourceDictionary { Source = new Uri($"Resources/Strings.{selected}.xaml", UriKind.Relative) });
        CurrentLanguage = selected;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(selected == "uk" ? "uk-UA" : selected == "ru" ? "ru-RU" : "en-US");
    }

    public static string Text(string key)
    {
        return System.Windows.Application.Current.TryFindResource(key) as string ?? key;
    }

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);

    private static string FromSystemCulture()
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Supported.Contains(language) ? language : "en";
    }
}
