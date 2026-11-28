using System;

namespace MdReader;

/// <summary>
/// Platform hook for documents and folders that live behind a storage URI. Android's storage
/// access framework grants access to a picked folder only for the current process unless the app
/// asks for a persistable permission; the Android head installs the callback that does so.
/// </summary>
public static class RemoteAccess
{
    /// <summary>Asks the platform to remember read access to <paramref name="folder"/> after the
    /// app closes. Null on platforms that hand out real paths.</summary>
    public static Action<Uri>? PersistFolder { get; set; }
}
