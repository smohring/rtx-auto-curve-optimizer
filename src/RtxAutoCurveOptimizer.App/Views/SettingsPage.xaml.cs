using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App.Views;

/// <summary>Settings page: demo mode switch and a few paths.</summary>
public sealed partial class SettingsPage : Page
{
    private bool updating;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var session = GpuSession.Current;
        updating = true;
        DemoSwitch.IsOn = session.DemoMode;
        updating = false;
        BusyBar.IsOpen = false;
        VersionText.Text = Loc.Format("Settings_Version", typeof(SettingsPage).Assembly.GetName().Version);
        SessionsText.Text = Loc.Format("Settings_SessionsFolder", JsonSessionStore.DefaultDirectory);
        ProfilesText.Text = Loc.Format("Settings_ProfilesFolder", session.ProfileStore.ProfilesDirectory);
    }

    /// <summary>Switches between the real and the simulated GPU; refused while an operation runs.</summary>
    private void DemoSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (updating) return;
        var session = GpuSession.Current;
        if (session.IsBusy)
        {
            updating = true;
            DemoSwitch.IsOn = session.DemoMode;
            updating = false;
            BusyBar.IsOpen = true;
            return;
        }

        session.Open(DemoSwitch.IsOn);
        ProfilesText.Text = Loc.Format("Settings_ProfilesFolder", session.ProfileStore.ProfilesDirectory);
    }
}
