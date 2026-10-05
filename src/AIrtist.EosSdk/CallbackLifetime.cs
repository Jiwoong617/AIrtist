using System;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AIrtist.Eos")]
[assembly: InternalsVisibleTo("AIrtist.EosChecks")]

namespace Epic.OnlineServices;

// AIrtist-owned addition, separate from the downloaded, pinned SDK sources.
public sealed partial class Helper
{
    // Call only after releasing the owner's native Platform on its Tick thread.
    // Platform.Release can discard pending completions; the C# wrapper otherwise retains their delegates/client data.
    internal static void ReleaseCallbacksAfterPlatformRelease(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        IntPtr[] keys;
        lock (s_ClientDatas)
            keys = s_ClientDatas.Where(pair => ReferenceEquals(pair.Value, owner)).Select(pair => pair.Key).ToArray();
        foreach (var key in keys) RemoveCallback(key);
    }
}
