using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace BvrMp4Converter;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = Loc.F("about_ver", VersionString);
    }

    /// <summary>A csproj &lt;Version&gt; értéke (pl. 1.0.1).</summary>
    public static string VersionString
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // a "+commit" utótag levágása
            return info?.Split('+')[0] ?? "1.0.1";
        }
    }

    private void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* nincs alapértelmezett böngésző */ }
        e.Handled = true;
    }
}
