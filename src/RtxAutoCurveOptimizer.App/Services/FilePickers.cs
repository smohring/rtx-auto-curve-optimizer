using Microsoft.Windows.Storage.Pickers;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>Save-file picker from the Windows App SDK (the classic picker fails in elevated unpackaged apps).</summary>
public static class FilePickers
{
    /// <summary>Asks for a file name; returns null if the user cancelled. Throws if the picker cannot be shown.</summary>
    public static async Task<string?> PickSaveAsync(string suggestedName, string extension, string description)
    {
        var picker = new FileSavePicker(Dialogs.WindowId)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(description, [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    /// <summary>Folder used when the picker is unavailable: Documents\RtxAutoCurveOptimizer.</summary>
    public static string FallbackDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RtxAutoCurveOptimizer");
}
