using CommunityToolkit.Mvvm.ComponentModel;

namespace RtxAutoCurveOptimizer.App.ViewModels;

/// <summary>A labelled live value shown as a card (label, large value, small detail line).</summary>
public sealed partial class TileViewModel : ObservableObject
{
    public TileViewModel(string label)
    {
        Label = label;
        Value = Placeholder;
        Detail = string.Empty;
    }

    /// <summary>Shown while no value is available.</summary>
    public const string Placeholder = "–";

    public string Label { get; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; }

    /// <summary>Optional tooltip with the full text behind a shortened value; null shows no tooltip.</summary>
    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    /// <summary>Resets value and detail to the placeholder.</summary>
    public void Clear()
    {
        Value = Placeholder;
        Detail = string.Empty;
        ToolTip = null;
    }
}
