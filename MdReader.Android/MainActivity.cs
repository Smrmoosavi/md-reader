using System;
using System.IO;
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
        Deliver(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Deliver(intent);
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
            ExternalOpen.Deliver(uri.LastPathSegment ?? uri.ToString() ?? "document.md", buffer.ToArray());
        }
        catch (Exception)
        {
            // An unreadable document falls back to the empty state instead of crashing.
        }
    }
}
