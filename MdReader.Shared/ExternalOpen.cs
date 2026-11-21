using System;

namespace MdReader;

/// <summary>
/// Carries a file that the operating system handed to the app (Android <c>VIEW</c>/<c>SEND</c>
/// intents, where the document has no filesystem path) to the shared view. The Android head
/// reads the stream and publishes the bytes here; <see cref="MainView"/> picks them up.
/// </summary>
public static class ExternalOpen
{
    private static readonly object Gate = new();
    private static (string Name, byte[] Bytes)? _pending;

    /// <summary>Raised after <see cref="Deliver"/> stored a document.</summary>
    public static event EventHandler? PendingChanged;

    public static void Deliver(string name, byte[] bytes)
    {
        lock (Gate)
        {
            _pending = (name, bytes);
        }

        PendingChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Takes the pending document, if any, and clears it.</summary>
    public static (string Name, byte[] Bytes)? Take()
    {
        lock (Gate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }
}
