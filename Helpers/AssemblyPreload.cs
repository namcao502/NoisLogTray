using System.Reflection;

namespace NoisLogTray;

// The published exe is a single-file bundle, and the runtime reads an assembly out of it on first use, by the exe's path.
// scripts/publish.ps1 swaps the exe of a running app, so a later first use read the new bundle and failed (Clean up
// worktrees: "System.Linq.Parallel" not found). Loading every referenced assembly at startup leaves nothing to read later.
internal static class AssemblyPreload
{
    internal static void Start(Assembly root) => Task.Run(() => LoadClosure(root));

    // The names of root and every assembly it references, directly or not; one that cannot load is logged and skipped.
    internal static IReadOnlyList<string> LoadClosure(Assembly root)
    {
        var loadedNames = new HashSet<string>(StringComparer.Ordinal) { root.GetName().Name ?? string.Empty };
        var pending = new Queue<Assembly>();
        pending.Enqueue(root);
        while (pending.Count != 0)
        {
            foreach (var reference in pending.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name == null || !loadedNames.Add(reference.Name)) continue;
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    AppLogger.Info($"[startup] {reference.Name} not preloaded: {e.Message}");
                }
            }
        }
        return loadedNames.ToList();
    }
}
