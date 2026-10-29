using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>Looks up localized strings from the .resw files for code-behind text.</summary>
public static class Loc
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>Returns the string for the key, or <c>!key</c> if it is missing.</summary>
    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? "!" + key : value;
    }

    /// <summary>Returns the string for the key formatted with the current culture.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
