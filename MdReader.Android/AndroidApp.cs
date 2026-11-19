using System;
using Android.Runtime;
using Avalonia.Android;

namespace MdReader.Droid;

// Avalonia 12 moved app initialization from the activity to the Android Application class.
[Android.App.Application]
public class AndroidApp : AvaloniaAndroidApplication<MdReader.App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
