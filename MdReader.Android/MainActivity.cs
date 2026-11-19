using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace MdReader.Droid;

[Activity(
    Label = "MD Reader",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<MdReader.App>
{
}
