using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>Page navigation requests raised by view models (tags: overview, optimize, results, settings).</summary>
public static class AppNavigation
{
    public static event Action<string>? Requested;

    public static void Go(string tag) => Requested?.Invoke(tag);
}

/// <summary>Shows ContentDialogs on the main window, one at a time.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Root of the main window content; set once the window is loaded.</summary>
    public static XamlRoot? Root { get; set; }

    /// <summary>Window id of the main window, needed by the file pickers.</summary>
    public static WindowId WindowId { get; set; }

    /// <summary>Shows a dialog with localized texts; <paramref name="secondary"/> may be null.</summary>
    public static async Task<ContentDialogResult> ShowAsync(
        string title, string message, string primary, string? secondary, string close,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = defaultButton,
        };
        if (secondary is not null) dialog.SecondaryButtonText = secondary;
        return await ShowAsync(dialog);
    }

    /// <summary>Shows a prepared dialog once no other dialog is open.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            dialog.XamlRoot = Root ?? throw new InvalidOperationException("The main window is not ready.");
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
