// language: C#, file: src/Core/Instance.cs, target: Windows 10/11, .NET 8
// Single-instance plumbing: a named mutex for ownership and a named event so a second
// launch (e.g. double-clicking the exe while the tray copy runs) raises the existing
// window instead of dead-ending in a message box.
using System.Diagnostics;
using System.Threading;

namespace RoutRunner.Core;

public static class Instance
{
    public const string MutexName = @"Local\RoutRunner.SingleInstance";
    public const string ShowEventName = @"Local\RoutRunner.ShowWindow";

    /// <summary>Asks the resident instance to surface its window. Silent when nobody is listening.</summary>
    public static void RequestShow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch
        {
            // no listener, or the object's ACL refused the open — nothing to do
        }
    }
}
