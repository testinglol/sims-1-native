using SimsEngine.Formats;

/// <summary>The files of an installed game folder, loose or inside its FAR archives, for the offline pack compilers.</summary>
internal static class InstalledFiles
{
    internal sealed record Entry(string Path, Func<byte[]> Read)
    {
        /// <summary>The file name without folders, for archive entries too.</summary>
        public string Name => System.IO.Path.GetFileName(Path[(Path.LastIndexOf("::", StringComparison.Ordinal) + 1)..].Replace('\\', '/').TrimStart(':'));
    }

    /// <summary>Visits every file whose name <paramref name="wanted"/> accepts. Archive entries are named
    /// <c>archive.far::entry</c>; their contents are read only when the visitor asks for them.</summary>
    internal static void Visit(string game, Func<string, bool> wanted, Action<Entry> visit)
    {
        if (!Directory.Exists(game)) throw new InvalidDataException($"No installed game at {game}");
        foreach (var path in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
            {
                var archive = FarArchive.Open(path);
                foreach (var entry in archive.Entries)
                    if (wanted(System.IO.Path.GetFileName(entry.Name.Replace('\\', '/'))))
                    {
                        var captured = entry;
                        visit(new Entry(path + "::" + captured.Name, () => archive.Read(captured).ToArray()));
                    }
            }
            else if (wanted(System.IO.Path.GetFileName(path)) && new FileInfo(path).Length < 64 * 1024 * 1024)
                visit(new Entry(path, () => File.ReadAllBytes(path)));
        }
    }
}
