using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace MdReader.Droid;

/// <summary>
/// The Android head. Besides being the launcher it accepts <c>VIEW</c> and <c>SEND</c> intents for
/// text documents, so a Markdown file opened from a file manager or shared from another app lands
/// in MD Reader as a tab.
/// </summary>
[Activity(
    Label = "MD Reader",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "file", "content" },
    DataMimeTypes = new[] { "text/plain", "text/markdown", "text/x-markdown" })]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeTypes = new[] { "text/plain", "text/markdown", "text/x-markdown" })]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The library folder is picked through the storage access framework; asking Android to
        // keep the grant means the folder still opens in the next session.
        RemoteAccess.PersistFolder = Persist;
        Deliver(Intent);
    }

    private void Persist(System.Uri uri)
    {
        try
        {
            var androidUri = Android.Net.Uri.Parse(uri.ToString());
            if (androidUri != null && ContentResolver != null)
            {
                ContentResolver.TakePersistableUriPermission(androidUri, ActivityFlags.GrantReadUriPermission);
            }
        }
        catch (Exception)
        {
            // A provider that does not grant persistable access keeps the old behaviour.
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Deliver(intent);
    }

#pragma warning disable CS0672, CS0618 // Activity.OnBackPressed is deprecated on API 33, but it is
    // still the entry point behind the dispatcher and the one every supported Android version has.
    /// <summary>Back leaves the app, so it asks about unsaved edits first: the same card as the
    /// window's close button, and the activity only finishes when the view agrees. Avalonia's own
    /// activity declares this member public, so the override has to match.</summary>
    public override void OnBackPressed()
    {
        var view = MdReader.App.ActiveView;
        if (view != null && view.HasUnsavedTabs)
        {
            _ = ConfirmExitAsync(view);
            return;
        }

        base.OnBackPressed();
    }
#pragma warning restore CS0672, CS0618

    private async Task ConfirmExitAsync(MdReader.MainView view)
    {
        if (await view.ConfirmExitAsync())
        {
            RunOnUiThread(Finish);
        }
    }

    /// <summary>Reads the shared document and hands the bytes to the shared view.</summary>
    private void Deliver(Intent? intent)
    {
        var uri = intent?.Data;
        if (uri == null)
        {
            return;
        }

        try
        {
            using var stream = ContentResolver?.OpenInputStream(uri);
            if (stream == null)
            {
                return;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            ExternalOpen.Deliver(uri.LastPathSegment ?? uri.ToString() ?? "document.md", buffer.ToArray(), uri.ToString());
        }
        catch (Exception)
        {
            // An unreadable document falls back to the empty state instead of crashing.
        }
    }
}
