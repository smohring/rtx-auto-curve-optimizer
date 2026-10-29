using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.App.Services;

namespace RtxAutoCurveOptimizer.App.Views;

/// <summary>One selectable Afterburner profile slot.</summary>
public sealed record SlotOption(int Slot, string Text);

/// <summary>Dialog that asks for the Afterburner profile slot to write; refuses while Afterburner runs.</summary>
public sealed partial class SaveProfileDialog : ContentDialog
{
    private readonly bool profileFound;

    /// <param name="profilePath">Profile file of the GPU, or null if none was found.</param>
    /// <param name="profile">The parsed profile, used to show which slots are empty.</param>
    public SaveProfileDialog(string? profilePath, AfterburnerProfile? profile)
    {
        InitializeComponent();
        profileFound = profilePath is not null && profile is not null;
        MissingBar.IsOpen = !profileFound;
        PathText.Text = profilePath ?? string.Empty;

        var slots = Enumerable.Range(1, AfterburnerProfile.SlotCount)
            .Select(n => new SlotOption(n, Loc.Format(
                profile is null || profile.IsSlotEmpty(n) ? "SaveProfile_SlotEmpty" : "SaveProfile_SlotUsed", n)))
            .ToList();
        SlotBox.ItemsSource = slots;
        var first = profile?.EmptySlots.FirstOrDefault() ?? 0;
        SlotBox.SelectedIndex = first > 0 ? first - 1 : 0;
        UpdateState();
    }

    /// <summary>The chosen slot, 1 to 5.</summary>
    public int SelectedSlot => (SlotBox.SelectedItem as SlotOption)?.Slot ?? 1;

    private void UpdateState()
    {
        RunningBar.IsOpen = AfterburnerProcess.IsRunning();
        IsPrimaryButtonEnabled = profileFound && !RunningBar.IsOpen;
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => UpdateState();
}
