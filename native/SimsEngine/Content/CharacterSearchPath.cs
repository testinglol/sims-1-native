using System.Runtime.CompilerServices;
using SimsEngine.Formats;

namespace SimsEngine.Content;

public sealed record CharacterFile(string Name, string Folder, string Source, Func<ReadOnlyMemory<byte>> Read)
{
    public string Extension => Path.GetExtension(Name);
}

/// <summary>Resolves character asset files across base game, expansion pack, and download directories in
/// strict search precedence order. Discovers loose files and archive entries, matching filenames
/// case-insensitively with earlier search locations taking priority.</summary>
public sealed class CharacterSearchPath
{
    private readonly Dictionary<string, CharacterFile> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(string Folder, IReadOnlyList<string> Sources)> Folders { get; }
    public IReadOnlyList<CharacterFile> Files { get; }
    public IReadOnlyList<CharacterFile> Shadowed { get; }

    private CharacterSearchPath(List<(string, IReadOnlyList<string>)> folders, List<CharacterFile> files)
    {
        Folders = folders;
        Files = files;
        var shadowed = new List<CharacterFile>();
        foreach (var file in files)
            if (!_byName.TryAdd(file.Name, file)) shadowed.Add(file);
        Shadowed = shadowed;
    }

    public CharacterFile? Find(string name) => _byName.GetValueOrDefault(name);

    public IEnumerable<CharacterFile> OfExtension(string extension) =>
        _byName.Values.Where(file => string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase));

    // Expansion pack bitmasks and root folder names ordered from newest pack to oldest.
    private static readonly (int Bit, string[] Roots)[] Packs =
    [
        (0x80, ["ExpansionPack7"]),
        (0x40, ["ExpansionPack6"]),
        (0x20, ["ExpansionPack5"]),
        (0x10, ["Deluxe", "ExpansionPackGOLD"]),
        (0x08, ["ExpansionPack4"]),
        (0x04, ["ExpansionPack3"]),
        (0x02, ["ExpansionPack2"]),
    ];

    private static readonly string[] LivinLarge = ["ExpansionPack", "ExpansionPack1"];

    public static CharacterSearchPath Skins(string game, ObjectRegistry? registry = null)
    {
        var folders = new List<string> { @"GameData\Skins" };
        folders.Add(@"GameData\cmx");
        for (var index = 2; index < 11; index++) folders.Add($@"GameData\cmx{index}");
        folders.Add(@"GameData\cmxpreview");
        folders.AddRange([@"GameData\People1", @"GameData\People2", @"GameData\Accessories1", @"GameData\Accessories2"]);
        var gameEditionMask = registry?.GameEditionMask ?? DetectGameEditionMask(game);
        // Includes shared expansion directories only when pack edition mask exceeds 3.
        if (gameEditionMask > 3)
            folders.AddRange([@"ExpansionShared\Skins", @"ExpansionShared\SkinsBuy", @"ExpansionShared\Animations",
                @"ExpansionShared\Skeletons", "TemplateNPCs", @"ExpansionShared\Accessories"]);
        foreach (var (bit, roots) in Packs)
            if ((gameEditionMask & bit) != 0 && Root(game, roots) is { } root)
                folders.AddRange([$@"{root}\Skins", $@"{root}\Animations", $@"{root}\Accessories"]);
        if ((gameEditionMask & 0x01) != 0 && Root(game, LivinLarge) is { } livinLarge) folders.Add($@"{livinLarge}\ExpansionPackAssets");
        return Build(game, folders, registry, lastRegisteredFirst: true);
    }

    public static CharacterSearchPath Textures(string game, ObjectRegistry? registry = null)
    {
        var gameEditionMask = registry?.GameEditionMask ?? DetectGameEditionMask(game);
        var folders = new List<string>();
        if (gameEditionMask > 3)
            folders.AddRange([@"ExpansionShared\Accessories", @"ExpansionShared\Skins", @"ExpansionShared\SkinsBuy", "TemplateNPCs"]);
        foreach (var (bit, roots) in Packs)
            if ((gameEditionMask & bit) != 0 && Root(game, roots) is { } root)
                folders.AddRange([$@"{root}\Accessories", $@"{root}\Skins"]);
        if ((gameEditionMask & 0x01) != 0 && Root(game, LivinLarge) is { } livinLarge) folders.Add($@"{livinLarge}\ExpansionPackAssets");
        folders.AddRange([@"GameData\Skins", @"GameData\Textures2", @"GameData\Textures"]);
        return Build(game, folders, registry, lastRegisteredFirst: false);
    }

    public static CharacterSearchPath Animation(string game, ObjectRegistry? registry = null) =>
        Build(game, [@"GameData\Animation"], registry, lastRegisteredFirst: true);

    private static string? Root(string game, string[] names) =>
        names.FirstOrDefault(name => ObjectRegistry.Resolve(game, name) is not null);

    // Determines installed expansion mask from marker files rather than directories.
    private static byte DetectGameEditionMask(string game)
    {
        bool HasFile(string windowsPath)
        {
            var parts = windowsPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var folder = parts.Length == 1 ? game : ObjectRegistry.Resolve(game, string.Join('\\', parts[..^1]));
            return folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder)
                .Any(path => string.Equals(Path.GetFileName(path), parts[^1], StringComparison.OrdinalIgnoreCase));
        }

        byte mask = 0x80;
        if (HasFile(@"ExpansionPack\xpack.hot") && HasFile(@"GameData\Ranger.iff")) mask |= 0x01;
        if (HasFile(@"ExpansionShared\Sound\houseparty.hot") && HasFile(@"GameData\Ranger2.iff")) mask |= 0x02;
        if (HasFile(@"ExpansionShared\Sound\HotDate.hot") && HasFile(@"GameData\Ranger3.iff")) mask |= 0x04;
        if (HasFile(@"ExpansionShared\Sound\vacsnd.hot") && HasFile(@"GameData\Ranger4.iff")) mask |= 0x08;
        if (HasFile(@"GameData\RangerD.iff")) mask |= 0x10;
        if (HasFile(@"ExpansionPack5\Sound\sep5snds.hot") && HasFile(@"GameData\Ranger5.iff")) mask |= 0x20;
        if (HasFile(@"GameData\Ranger6.iff")) mask |= 0x40;
        return mask;
    }

    private static readonly ConditionalWeakTable<FarArchive, Dictionary<string, List<(string FileName, FarArchive.Entry Entry)>>> ArchiveDirectoryCache = new();

    private static Dictionary<string, List<(string FileName, FarArchive.Entry Entry)>> DirectoryIndex(FarArchive archive) =>
        ArchiveDirectoryCache.GetValue(archive, static far =>
        {
            var index = new Dictionary<string, List<(string FileName, FarArchive.Entry Entry)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in far.Entries)
            {
                var raw = entry.Name;
                var slash = raw.LastIndexOf('/');
                var backslash = raw.LastIndexOf('\\');
                var sep = Math.Max(slash, backslash);
                var folder = sep < 0 ? "" : raw[..sep].Replace('\\', '/');
                var fileName = sep < 0 ? raw : raw[(sep + 1)..];
                if (!index.TryGetValue(folder, out var bucket))
                {
                    bucket = [];
                    index[folder] = bucket;
                }
                bucket.Add((fileName, entry));
            }
            return index;
        });

    private static CharacterSearchPath Build(string game, List<string> windowsFolders, ObjectRegistry? registry, bool lastRegisteredFirst)
    {
        var archives = new Dictionary<string, FarArchive>(StringComparer.Ordinal);
        FarArchive Archive(string path) => archives.TryGetValue(path, out var archive) ? archive : archives[path] = FarArchive.Open(path);
        var folders = new List<(string, IReadOnlyList<string>)>();
        var files = new List<CharacterFile>();

        void Add(string windowsFolder, string? folder)
        {
            var sources = new List<string>();
            var found = new List<CharacterFile>();
            if (folder is not null)
            {
                sources.Add(folder);
                foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
                    if (!path.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
                        found.Add(new CharacterFile(Path.GetFileName(path), windowsFolder, path, () => File.ReadAllBytes(path)));
            }
            // Checks archives in the folder, then parent archives matching folder name.
            var parts = windowsFolder.Split('\\');
            var parent = parts.Length > 1 ? ObjectRegistry.Resolve(game, string.Join('\\', parts[..^1])) : null;
            foreach (var (directory, prefix) in new[] { (folder, ""), (parent, parts[^1]) })
            {
                if (directory is null) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*.far").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var archive = Archive(path);
                    var dirIndex = DirectoryIndex(archive);
                    if (!dirIndex.TryGetValue(prefix, out var bucket)) continue;
                    sources.Add(prefix.Length == 0 ? path : path + "::" + prefix + "/");
                    foreach (var (fileName, entry) in bucket)
                    {
                        var captured = entry;
                        found.Add(new CharacterFile(fileName, windowsFolder, path + "::" + captured.Name, () => archive.Read(captured)));
                    }
                }
            }
            if (sources.Count == 0) return;
            folders.Add((windowsFolder, sources));
            files.AddRange(found);
        }

        foreach (var windowsFolder in windowsFolders)
            Add(windowsFolder, ObjectRegistry.Resolve(game, windowsFolder));
        if (registry is not null)
            foreach (var folder in DownloadFolders(registry, lastRegisteredFirst))
            {
                if (folder.ArchivePrefix is null)
                {
                    Add(Path.GetRelativePath(game, folder.Path).Replace('/', '\\'), folder.Path);
                    continue;
                }

                var archive = Archive(folder.Path);
                var logicalFolder = Path.GetRelativePath(game, folder.Path).Replace('/', '\\') + "\\" + folder.ArchivePrefix.Replace('/', '\\');
                var dirIndex = DirectoryIndex(archive);
                var normalizedPrefix = folder.ArchivePrefix.Replace('\\', '/');
                if (dirIndex.TryGetValue(normalizedPrefix, out var bucket) && bucket.Count > 0)
                {
                    var found = new List<CharacterFile>(bucket.Count);
                    foreach (var (fileName, entry) in bucket)
                    {
                        var captured = entry;
                        found.Add(new CharacterFile(fileName, logicalFolder, folder.Path + "::" + captured.Name, () => archive.Read(captured)));
                    }
                    folders.Add((logicalFolder, [folder.Path + "::" + folder.ArchivePrefix + "/"]));
                    files.AddRange(found);
                }
            }
        return new CharacterSearchPath(folders, files);
    }

    private static IEnumerable<(string Path, string? ArchivePrefix)> DownloadFolders(ObjectRegistry registry, bool lastRegisteredFirst)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = lastRegisteredFirst ? registry.Entries.Reverse() : registry.Entries;
        foreach (var entry in entries)
        {
            if (entry.Scan != ObjectScan.Downloads) continue;
            var separator = entry.Source.IndexOf("::", StringComparison.Ordinal);
            if (separator >= 0)
            {
                var archive = entry.Source[..separator];
                var item = entry.Source[(separator + 2)..].Replace('\\', '/');
                var slash = item.LastIndexOf('/');
                var prefix = slash < 0 ? "" : item[..slash];
                if (seen.Add(archive + "\0" + prefix)) yield return (archive, prefix);
            }
            else if (Path.GetDirectoryName(entry.Source) is { } folder && seen.Add(folder)) yield return (folder, null);
        }
    }
}
