using SimsEngine.Formats;

namespace SimsEngine.Content;

/// <summary>One file the character search path finds: its name, the search folder it was found in (a Windows path
/// below the installation, as Sims.exe spells it), and where its bytes come from.</summary>
public sealed record CharacterFile(string Name, string Folder, string Source, Func<ReadOnlyMemory<byte>> Read)
{
    public string Extension => Path.GetExtension(Name);
}

/// <summary>The folders Sims.exe searches for character files (suits, skeletons, meshes, textures and animations),
/// in the order it searches them.
/// <para>Sims.exe FUN_005837c0 builds two ';'-separated search paths. The skins path holds GameData\Skins\, then the
/// folders cmx, cmx2 to cmx10, cmxpreview, People1, People2, Accessories1 and Accessories2 under GameData\ that
/// exist, then, when the game setting short is above 3, ExpansionShared's Skins, SkinsBuy, Animations and Skeletons,
/// TemplateNPCs and ExpansionShared's Accessories, then each installed pack's Skins, Animations and Accessories from
/// Makin' Magic (bit 0x80) back to House Party (bit 2), then Livin' Large's ExpansionPackAssets (bit 1), and last the
/// folders of downloaded objects (FUN_0043beaa). FUN_0059f6cf then reads every .cmx and .bcf file on it. The
/// animation path holds GameData\Animation\ and the same downloaded-object folders; FUN_005a009d reads its files.
/// A third path, the texture path FUN_00413d0e builds while "Initializing vitaboy" and hands to FUN_005caef6, holds,
/// when the setting short is above 3, ExpansionShared's Accessories, Skins and SkinsBuy and TemplateNPCs, then each
/// installed pack's Accessories and Skins (Makin' Magic back to House Party), Livin' Large's ExpansionPackAssets,
/// GameData\Skins\, GameData\Textures2\, GameData\Textures\, and the downloaded-object folders, first registered
/// first.</para>
/// <para>Inferred, not traced: a .far in a folder is read as part of that folder, and a .far in a pack's folder holds
/// that pack's subfolders as entry folders (ExpansionPack2.far's "Skins/" entries are ExpansionPack2\Skins\); loose
/// files come before archive entries; names compare without case and the first file found under a name is the one
/// used; downloaded-object folders are those of objects from the Downloads scan, the last registered first (the
/// registry flag FUN_0043beaa tests, entry +0xb4 bits 0xc == 8, is not decoded, nor is its .ndx test).</para></summary>
public sealed class CharacterSearchPath
{
    private readonly Dictionary<string, CharacterFile> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The search folders that exist, in order, with what they resolved to.</summary>
    public IReadOnlyList<(string Folder, IReadOnlyList<string> Sources)> Folders { get; }
    /// <summary>Every file found, in search order (shadowed files included).</summary>
    public IReadOnlyList<CharacterFile> Files { get; }
    /// <summary>Files whose name an earlier folder already supplied.</summary>
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

    /// <summary>The file named <paramref name="name"/> (with its extension), first in search order.</summary>
    public CharacterFile? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The files used under each name with <paramref name="extension"/> (".cmx", ".skn", ...).</summary>
    public IEnumerable<CharacterFile> OfExtension(string extension) =>
        _byName.Values.Where(file => string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase));

    // The pack bits of the game setting, highest first as FUN_005837c0 adds them, with the pack's root folder names.
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

    /// <summary>The skins path. <paramref name="registry"/> supplies the downloaded objects' folders.</summary>
    public static CharacterSearchPath Skins(string game, ObjectRegistry? registry = null)
    {
        var folders = new List<string> { @"GameData\Skins" };
        folders.Add(@"GameData\cmx");
        for (var index = 2; index < 11; index++) folders.Add($@"GameData\cmx{index}");
        folders.Add(@"GameData\cmxpreview");
        folders.AddRange([@"GameData\People1", @"GameData\People2", @"GameData\Accessories1", @"GameData\Accessories2"]);
        var gameEditionMask = registry?.GameEditionMask ?? DetectGameEditionMask(game);
        // FUN_005837c0 uses the game-edition short, not folder presence, to include shared pack assets.
        if (gameEditionMask > 3)
            folders.AddRange([@"ExpansionShared\Skins", @"ExpansionShared\SkinsBuy", @"ExpansionShared\Animations",
                @"ExpansionShared\Skeletons", "TemplateNPCs", @"ExpansionShared\Accessories"]);
        foreach (var (bit, roots) in Packs)
            if ((gameEditionMask & bit) != 0 && Root(game, roots) is { } root)
                folders.AddRange([$@"{root}\Skins", $@"{root}\Animations", $@"{root}\Accessories"]);
        if ((gameEditionMask & 0x01) != 0 && Root(game, LivinLarge) is { } livinLarge) folders.Add($@"{livinLarge}\ExpansionPackAssets");
        return Build(game, folders, registry, lastRegisteredFirst: true);
    }

    /// <summary>The texture path (FUN_00413d0e).</summary>
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

    /// <summary>The animation path.</summary>
    public static CharacterSearchPath Animation(string game, ObjectRegistry? registry = null) =>
        Build(game, [@"GameData\Animation"], registry, lastRegisteredFirst: true);

    private static string? Root(string game, string[] names) =>
        names.FirstOrDefault(name => ObjectRegistry.Resolve(game, name) is not null);

    // FUN_00413d0e sets these edition bits from installed marker files; do not infer installation from directories.
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
            // Archives in the folder itself (entries without a folder), then archives in its parent whose entries
            // sit in a folder of this folder's name.
            var parts = windowsFolder.Split('\\');
            var parent = parts.Length > 1 ? ObjectRegistry.Resolve(game, string.Join('\\', parts[..^1])) : null;
            foreach (var (directory, prefix) in new[] { (folder, ""), (parent, parts[^1]) })
            {
                if (directory is null) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*.far").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var archive = Archive(path);
                    var any = false;
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.Name.Replace('\\', '/');
                        var slash = name.LastIndexOf('/');
                        var entryFolder = slash < 0 ? "" : name[..slash];
                        if (!string.Equals(entryFolder, prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        any = true;
                        found.Add(new CharacterFile(name[(slash + 1)..], windowsFolder, path + "::" + entry.Name, () => archive.Read(entry)));
                    }
                    if (any) sources.Add(prefix.Length == 0 ? path : path + "::" + prefix + "/");
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
                var found = new List<CharacterFile>();
                foreach (var entry in archive.Entries)
                {
                    var name = entry.Name.Replace('\\', '/');
                    var slash = name.LastIndexOf('/');
                    var entryFolder = slash < 0 ? "" : name[..slash];
                    if (!string.Equals(entryFolder, folder.ArchivePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(new CharacterFile(name[(slash + 1)..], logicalFolder, folder.Path + "::" + entry.Name, () => archive.Read(entry)));
                }
                if (found.Count > 0)
                {
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
