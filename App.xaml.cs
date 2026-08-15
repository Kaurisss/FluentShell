using Microsoft.UI.Xaml;
using Syncfusion.Licensing;

namespace FluentShell;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        var licenseKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(licenseKey))
            SyncfusionLicenseProvider.RegisterLicense(licenseKey);

        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
