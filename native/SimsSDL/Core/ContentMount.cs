using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace SimsSDL.Core;

public sealed partial class GameContent
{
    private sealed class MountCache
    {
        public int SchemaVersion { get; set; }
        public string Identity { get; set; } = string.Empty;
        public string SourceFingerprint { get; set; } = string.Empty;
    }

    private sealed record OverlayPath(string[] Keys, string[] Names);

    public bool ReusableMount(string destination, string identity)
    {
        var cachePath = Path.Combine(destination, ".content-cache.json");
        if (!File.Exists(cachePath))
        {
            return false;
        }

        MountCache? cache;
        try
        {
            var data = File.ReadAllBytes(cachePath);
            cache = JsonSerializer.Deserialize<MountCache>(data, PackContentPaths.CamelCaseJsonReadOptions);
        }
        catch
        {
            return false;
        }

        if (cache is null || cache.SchemaVersion != 3 || !string.Equals(cache.Identity, identity, StringComparison.Ordinal))
        {
            return false;
        }

        if (cache.SourceFingerprint.StartsWith("live-base:", StringComparison.Ordinal))
        {
            try
            {
                var baseRoot = PackContentPaths.ResolveSymlinksInPath(Root);
                return string.Equals(cache.SourceFingerprint, ComputeLiveOverlayFingerprint(baseRoot), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        if (string.Equals(cache.SourceFingerprint, "live-base", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(cache.SourceFingerprint, ComputeSourceFingerprint(), StringComparison.Ordinal);
    }

    public void Materialize(string destination, string? cacheIdentity = null, bool nativeOverlay = false)
    {
        if (File.Exists(destination) || Directory.Exists(destination) || PackContentPaths.IsSymbolicLink(destination))
        {
            throw new ContentError($"Content mount already exists: {destination}");
        }

        var baseRoot = PackContentPaths.ResolveSymlinksInPath(Root);
        var target = PackContentPaths.ResolveSymlinksInPath(destination);
        if (PackContentPaths.RelativePath(target, baseRoot) is not null || PackContentPaths.SamePath(target, baseRoot))
        {
            throw new ContentError("Content mounts must be outside the base game");
        }

        var targetParent = Path.GetDirectoryName(target)
            ?? throw new ContentError($"Invalid mount destination: {destination}");
        Directory.CreateDirectory(targetParent);

        var staging = Path.Combine(targetParent, ".mount-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(staging);

        try
        {
            var casing = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceEntries = new List<string>();
            var usesLiveBase = nativeOverlay;

            if (usesLiveBase)
            {
                MaterializeLiveOverlay(baseRoot, staging);
            }
            else
            {
                MaterializeBaseTree(baseRoot, staging, casing, sourceEntries);
            }

            foreach (var (key, source) in Packs.Assets.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                var components = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var authoredRaw = Packs.AuthoredPaths.TryGetValue(key, out var authoredValue) ? authoredValue : string.Empty;
                var authored = authoredRaw.Split('/', StringSplitOptions.RemoveEmptyEntries);

                var relative = string.Empty;
                var prefix = string.Empty;
                for (var index = 0; index < components.Length; index++)
                {
                    var component = components[index];
                    prefix = prefix.Length == 0 ? component : prefix + "/" + component;
                    var name = authored.Length == components.Length &&
                               string.Equals(authored[index].ToLowerInvariant(), component, StringComparison.Ordinal)
                        ? authored[index]
                        : component;

                    relative = casing.TryGetValue(prefix, out var baseCased)
                        ? baseCased
                        : (relative.Length == 0 ? name : relative + "/" + name);
                }

                var output = Path.Combine(staging, relative);
                RemoveExistingEntry(output);
                CopyResource(source, output, shares: false);
            }

            var report = Packs.Report();
            var pluginPacks = Packs.Ordered.Where(static p => p.PluginAssembly is not null).ToList();
            var pluginReports = report.Plugins ?? [];
            if (pluginReports.Count != pluginPacks.Count)
            {
                throw new ContentError("Plugin report does not match its pack");
            }

            for (var i = 0; i < pluginReports.Count; i++)
            {
                var plugin = pluginReports[i];
                var pack = pluginPacks[i];
                if (pack.PluginAssembly is not { } assembly || pack.Manifest.Id != plugin.PackID)
                {
                    throw new ContentError("Plugin report does not match its pack");
                }

                var folder = Path.GetDirectoryName(Path.Combine(staging, plugin.Assembly))
                    ?? throw new ContentError($"Invalid plugin mount path: {plugin.Assembly}");
                var sourceFolder = Path.GetDirectoryName(assembly)!;
                foreach (var file in PackPlugins.PluginPayload(sourceFolder))
                {
                    CopyResource(file, Path.Combine(folder, Path.GetFileName(file)), shares: false);
                }

                if (!string.Equals(PackPlugins.PluginDigest(folder), plugin.Sha256, StringComparison.Ordinal))
                {
                    throw new ContentError($"Plugin {plugin.PackID} changed while its content mount was prepared");
                }
            }

            var packStackJson = BuildPackStackJson(report);
            File.WriteAllBytes(Path.Combine(staging, ".pack-stack.json"), packStackJson);

            if (DisabledDownloadFolders.Count > 0)
            {
                File.WriteAllLines(Path.Combine(staging, ".disabled-downloads.txt"), DisabledDownloadFolders);
            }

            if (cacheIdentity is not null)
            {
                var fingerprint = usesLiveBase ? ComputeLiveOverlayFingerprint(baseRoot) : ComputeFingerprint(sourceEntries);
                var cacheObj = new JsonObject
                {
                    ["identity"] = cacheIdentity,
                    ["schemaVersion"] = 3,
                    ["sourceFingerprint"] = fingerprint
                };
                File.WriteAllBytes(Path.Combine(staging, ".content-cache.json"), SerializeSortedJson(cacheObj));
            }

            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private string ComputeLiveOverlayFingerprint(string baseRoot)
    {
        var paths = new List<OverlayPath>(Packs.Assets.Count);
        foreach (var key in Packs.Assets.Keys)
        {
            var authoredStr = Packs.AuthoredPaths.TryGetValue(key, out var raw) ? raw : key;
            var names = authoredStr.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var keys = PackContentPaths.ResourceKey(key).Split('/', StringSplitOptions.RemoveEmptyEntries);
            paths.Add(new OverlayPath(keys, names));
        }

        var grouped = paths
            .GroupBy(static p => p.Keys[0], StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        var entries = new List<string>();
        void Visit(string sourceDir, string relPrefix, Dictionary<string, List<OverlayPath>> groups)
        {
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(sourceDir).OrderBy(static p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (relPrefix.Length == 0 &&
                    (name == "plugin" ||
                     name == ".plugins" ||
                     name == ".pack-stack.json" ||
                     name == ".content-cache.json" ||
                     IsNeighborhoodRoot(name)))
                {
                    continue;
                }

                var rel = relPrefix.Length == 0 ? name : relPrefix + "/" + name;
                entries.Add(rel);

                var key = PackContentPaths.ResourceKey(name);
                if (groups.TryGetValue(key, out var nested) &&
                    nested.Any(static p => p.Keys.Length > 1) &&
                    Directory.Exists(child) &&
                    !PackContentPaths.IsSymbolicLink(child))
                {
                    var subGroups = nested
                        .Where(static p => p.Keys.Length > 1)
                        .Select(static p => new OverlayPath(p.Keys[1..], p.Names[1..]))
                        .GroupBy(static p => p.Keys[0], StringComparer.Ordinal)
                        .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);
                    Visit(child, rel, subGroups);
                }
            }
        }

        Visit(baseRoot, string.Empty, grouped);
        return "live-base:" + ComputeFingerprint(entries);
    }

    private void MaterializeLiveOverlay(string baseRoot, string staging)
    {
        var paths = new List<OverlayPath>(Packs.Assets.Count);
        foreach (var key in Packs.Assets.Keys)
        {
            var authoredStr = Packs.AuthoredPaths.TryGetValue(key, out var raw) ? raw : key;
            var names = authoredStr.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var keys = PackContentPaths.ResourceKey(key).Split('/', StringSplitOptions.RemoveEmptyEntries);
            paths.Add(new OverlayPath(keys, names));
        }

        var grouped = paths
            .GroupBy(static p => p.Keys[0], StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        void Merge(string? sourceDir, string outputDir, List<OverlayPath> nestedPaths)
        {
            Directory.CreateDirectory(outputDir);
            var groups = nestedPaths
                .GroupBy(static p => p.Keys[0], StringComparer.Ordinal)
                .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

            var children = new Dictionary<string, string>(StringComparer.Ordinal);
            if (sourceDir is not null)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(sourceDir))
                {
                    var key = PackContentPaths.ResourceKey(Path.GetFileName(child));
                    if (!children.TryAdd(key, child))
                    {
                        throw new ContentError($"Base game has duplicate normalized paths in {sourceDir}");
                    }
                }
            }

            foreach (var (key, child) in children)
            {
                var childDest = Path.Combine(outputDir, Path.GetFileName(child));
                if (!groups.TryGetValue(key, out var nested))
                {
                    LinkOrCopyItem(child, childDest);
                    continue;
                }

                var isLeaf = nested.Any(static p => p.Keys.Length == 1);
                var hasDescendants = nested.Any(static p => p.Keys.Length > 1);
                if (hasDescendants)
                {
                    if (PackContentPaths.IsSymbolicLink(child) || !Directory.Exists(child))
                    {
                        throw new ContentError($"Pack resource path descends through a non-directory: {child}");
                    }

                    var subPaths = nested
                        .Where(static p => p.Keys.Length > 1)
                        .Select(static p => new OverlayPath(p.Keys[1..], p.Names[1..]))
                        .ToList();
                    Merge(child, childDest, subPaths);
                }
                else if (!isLeaf)
                {
                    LinkOrCopyItem(child, childDest);
                }
            }

            foreach (var (key, nested) in groups)
            {
                if (children.ContainsKey(key))
                {
                    continue;
                }

                var name = nested[0].Names[0];
                var hasLeaf = nested.Any(static p => p.Keys.Length == 1);
                var hasDescendants = nested.Any(static p => p.Keys.Length > 1);
                if (hasLeaf && hasDescendants)
                {
                    throw new ContentError($"Pack path is both a file and directory: {name}");
                }

                if (hasDescendants)
                {
                    var subPaths = nested
                        .Select(static p => new OverlayPath(p.Keys[1..], p.Names[1..]))
                        .ToList();
                    Merge(null, Path.Combine(outputDir, name), subPaths);
                }
            }
        }

        foreach (var source in Directory.EnumerateFileSystemEntries(baseRoot))
        {
            var name = Path.GetFileName(source);
            if (name == "plugin" ||
                name == ".plugins" ||
                name == ".pack-stack.json" ||
                name == ".content-cache.json" ||
                IsNeighborhoodRoot(name))
            {
                continue;
            }

            var key = PackContentPaths.ResourceKey(name);
            var stagedDest = Path.Combine(staging, name);
            if (grouped.TryGetValue(key, out var nested))
            {
                var hasDescendants = nested.Any(static p => p.Keys.Length > 1);
                var isDir = Directory.Exists(source) && !PackContentPaths.IsSymbolicLink(source);
                if (hasDescendants && !isDir)
                {
                    throw new ContentError($"Pack resource path descends through a file: {source}");
                }

                if (hasDescendants)
                {
                    var subPaths = nested
                        .Where(static p => p.Keys.Length > 1)
                        .Select(static p => new OverlayPath(p.Keys[1..], p.Names[1..]))
                        .ToList();
                    Merge(source, stagedDest, subPaths);
                }
            }
            else
            {
                LinkOrCopyItem(source, stagedDest);
            }
        }

        foreach (var (_, nested) in grouped)
        {
            var topName = nested[0].Names[0];
            var stagedTop = Path.Combine(staging, topName);
            if (File.Exists(stagedTop) || Directory.Exists(stagedTop) || PackContentPaths.IsSymbolicLink(stagedTop))
            {
                continue;
            }

            var hasDescendants = nested.Any(static p => p.Keys.Length > 1);
            var hasLeaf = nested.Any(static p => p.Keys.Length == 1);
            if (hasLeaf && hasDescendants)
            {
                throw new ContentError($"Pack path is both a file and directory: {topName}");
            }

            if (hasDescendants)
            {
                var subPaths = nested
                    .Select(static p => new OverlayPath(p.Keys[1..], p.Names[1..]))
                    .ToList();
                Merge(null, stagedTop, subPaths);
            }
        }

        if (Packs.Ordered.Any(static p => p.PluginAssembly is not null))
        {
            Directory.CreateDirectory(Path.Combine(staging, "plugin"));
        }
    }

    private static void MaterializeBaseTree(
        string baseRoot,
        string staging,
        Dictionary<string, string> casing,
        List<string> sourceEntries)
    {
        foreach (var entry in EnumerateBaseEntries(baseRoot))
        {
            var resolved = PackContentPaths.ResolveSymlinksInPath(entry.FullPath);
            var relative = PackContentPaths.RelativePath(resolved, baseRoot)
                ?? throw new ContentError("Base resource escapes game directory");
            var key = PackContentPaths.ResourceKey(relative);
            sourceEntries.Add(FormatSourceEntry(relative, entry.IsDirectory, entry.IsRegularFile, entry.FullPath));
            if (!casing.TryAdd(key, relative))
            {
                throw new ContentError($"Base game has duplicate normalized paths: {relative}");
            }

            var output = Path.Combine(staging, relative);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(output);
            }
            else if (entry.IsRegularFile)
            {
                CopyResource(entry.FullPath, output, shares: true);
            }
        }
    }

    private string ComputeSourceFingerprint()
    {
        var baseRoot = PackContentPaths.ResolveSymlinksInPath(Root);
        var values = new List<string>();
        foreach (var entry in EnumerateBaseEntries(baseRoot))
        {
            var resolved = PackContentPaths.ResolveSymlinksInPath(entry.FullPath);
            var relative = PackContentPaths.RelativePath(resolved, baseRoot)
                ?? throw new ContentError("Base resource escapes game directory");
            values.Add(FormatSourceEntry(relative, entry.IsDirectory, entry.IsRegularFile, entry.FullPath));
        }
        return ComputeFingerprint(values);
    }

    private readonly record struct BaseEntryInfo(string FullPath, bool IsDirectory, bool IsRegularFile);

    private static IEnumerable<BaseEntryInfo> EnumerateBaseEntries(string baseRoot)
    {
        if (!Directory.Exists(baseRoot))
        {
            throw new ContentError("Cannot enumerate base game");
        }

        var stack = new Stack<(string Dir, bool IsRoot)>();
        stack.Push((baseRoot, true));

        while (stack.Count > 0)
        {
            var (currentDir, isRoot) = stack.Pop();
            List<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(currentDir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ContentError($"Cannot enumerate base game: {ex.Message}");
            }

            children.Sort(StringComparer.Ordinal);
            foreach (var entry in children)
            {
                if (PackContentPaths.IsHiddenEntry(entry))
                {
                    continue;
                }

                var name = Path.GetFileName(entry);
                if (isRoot && IsNeighborhoodRoot(name))
                {
                    continue;
                }

                if (PackContentPaths.IsSymbolicLink(entry))
                {
                    throw new ContentError($"Base game contains a symbolic link: {entry}");
                }

                var isDir = Directory.Exists(entry);
                var isFile = !isDir && File.Exists(entry);
                yield return new BaseEntryInfo(entry, isDir, isFile);

                if (isDir)
                {
                    stack.Push((entry, false));
                }
            }
        }
    }

    private static void CopyResource(string source, string output, bool shares)
    {
        var parent = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (shares)
        {
            if (OperatingSystem.IsLinux() && LinkNative(source, output) == 0)
            {
                return;
            }
            if (OperatingSystem.IsWindows() && CreateHardLinkW(output, source, IntPtr.Zero))
            {
                return;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            const uint COPYFILE_DATA = 1u << 3;
            const uint COPYFILE_CLONE = 1u << 24;
            if (CopyFileNative(source, output, IntPtr.Zero, COPYFILE_DATA | COPYFILE_CLONE) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new ContentError($"Cannot copy content resource: {Path.GetFileName(source)}, errno {err}");
            }
            return;
        }

        try
        {
            File.Copy(source, output, overwrite: false);
            if (!shares)
            {
                ClearReadOnlyIfPresent(output);
            }
        }
        catch (Exception ex)
        {
            throw new ContentError($"Cannot copy content resource: {Path.GetFileName(source)}, {ex.Message}");
        }
    }

    private static volatile bool s_windowsSymlinksUnavailable;

    private static void LinkOrCopyItem(string source, string destination)
    {
        bool isDir = Directory.Exists(source);
        if (!OperatingSystem.IsWindows() || !s_windowsSymlinksUnavailable)
        {
            try
            {
                if (isDir)
                {
                    Directory.CreateSymbolicLink(destination, source);
                }
                else
                {
                    File.CreateSymbolicLink(destination, source);
                }
                return;
            }
            catch
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw;
                }
                s_windowsSymlinksUnavailable = true;
            }
        }

        if (isDir)
        {
            if (OperatingSystem.IsWindows() && TryCreateWindowsJunction(destination, source))
            {
                return;
            }

            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                LinkOrCopyItem(child, Path.Combine(destination, Path.GetFileName(child)));
            }
        }
        else if (!CreateHardLinkW(destination, source, IntPtr.Zero))
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void RemoveExistingEntry(string path)
    {
        if (PackContentPaths.IsSymbolicLink(path))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
            return;
        }

        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (UnauthorizedAccessException)
            {
                ClearReadOnlyIfPresent(path);
                File.Delete(path);
            }
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ClearReadOnlyIfPresent(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                var cleared = attrs & ~FileAttributes.ReadOnly;
                File.SetAttributes(path, cleared == 0 ? FileAttributes.Normal : cleared);
            }
        }
        catch
        {
        }
    }

    private static string FormatSourceEntry(string relative, bool isDirectory, bool isRegularFile, string fullPath)
    {
        var kind = isDirectory ? "d" : (isRegularFile ? "f" : "o");
        long size = -1;
        long modifiedNs = 0;

        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            if (isRegularFile && info is FileInfo fileInfo)
            {
                size = fileInfo.Length;
            }
            modifiedNs = (info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        }
        catch
        {
        }

        return $"{kind}\t{relative}\t{size}\t{modifiedNs}";
    }

    private static bool IsNeighborhoodRoot(string name)
    {
        var lower = name.ToLowerInvariant();
        if (!lower.StartsWith("userdata", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = lower["userdata".Length..];
        return suffix.All(char.IsAsciiDigit);
    }

    private static string ComputeFingerprint(List<string> entries)
    {
        entries.Sort(StringComparer.Ordinal);
        return PackContentPaths.Sha256Hex(string.Join('\n', entries));
    }

    private static byte[] BuildPackStackJson(PackReport report)
    {
        var plugins = new JsonArray();
        foreach (var plugin in report.Plugins ?? [])
        {
            plugins.Add(new JsonObject
            {
                ["apiVersion"] = plugin.ApiVersion,
                ["assembly"] = plugin.Assembly,
                ["entryType"] = plugin.EntryType,
                ["packId"] = plugin.PackID,
                ["sha256"] = plugin.Sha256,
                ["stateVersion"] = plugin.StateVersion,
                ["version"] = plugin.Version
            });
        }

        var panels = new JsonArray();
        foreach (var panel in report.Panels ?? [])
        {
            panels.Add(new JsonObject
            {
                ["id"] = panel.Id,
                ["packId"] = panel.PackID,
                ["path"] = panel.Path,
                ["sha256"] = panel.Sha256
            });
        }

        var details = new JsonArray();
        foreach (var detail in report.PackDetails ?? [])
        {
            var entry = new JsonObject
            {
                ["id"] = detail.Id,
                ["migratesFrom"] = ToStringArray(detail.MigratesFrom),
                ["version"] = detail.Version
            };
            if (detail.Objects is not null)
            {
                var objArr = new JsonArray();
                foreach (var map in detail.Objects)
                {
                    objArr.Add(new JsonObject
                    {
                        ["from"] = map.From,
                        ["to"] = map.To
                    });
                }
                entry["objects"] = objArr;
            }
            details.Add(entry);
        }

        var objects = new JsonArray();
        foreach (var obj in report.Objects)
        {
            objects.Add(new JsonObject
            {
                ["guid"] = obj.Guid,
                ["owner"] = obj.Owner
            });
        }

        var resources = new JsonArray();
        foreach (var res in report.Resources)
        {
            resources.Add(new JsonObject
            {
                ["owner"] = res.Owner,
                ["path"] = res.Path
            });
        }

        var metadata = new JsonObject
        {
            ["baseGameVersion"] = report.BaseGameVersion,
            ["fingerprint"] = report.Fingerprint,
            ["objects"] = objects,
            ["packDetails"] = details,
            ["packs"] = ToStringArray(report.Packs),
            ["panels"] = panels,
            ["plugins"] = plugins,
            ["resources"] = resources,
            ["schemaVersion"] = report.SchemaVersion
        };
        if (report.CoreFingerprint is not null)
            metadata["coreFingerprint"] = report.CoreFingerprint;

        return SerializeSortedJson(metadata);
    }

    private static JsonArray ToStringArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values)
        {
            arr.Add(value);
        }
        return arr;
    }

    internal static byte[] SerializeSortedJson(JsonObject root)
    {
        var sorted = SortJsonObject(root);
        return JsonSerializer.SerializeToUtf8Bytes(sorted, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    internal static JsonNode? SortJsonNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return SortJsonObject(obj);
        }
        if (node is JsonArray arr)
        {
            var sortedArr = new JsonArray();
            foreach (var item in arr)
            {
                sortedArr.Add(SortJsonNode(item?.DeepClone()));
            }
            return sortedArr;
        }
        return node?.DeepClone();
    }

    private static JsonObject SortJsonObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in obj.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            sorted[key] = SortJsonNode(value?.DeepClone());
        }
        return sorted;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "copyfile", SetLastError = true)]
    private static extern int CopyFileNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to,
        IntPtr state,
        uint flags);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static bool TryCreateWindowsJunction(string junctionPath, string targetDir)
    {
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_ALL = 0x00000007;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        string fullTarget = Path.GetFullPath(targetDir);
        string printName = fullTarget;
        string substituteName = fullTarget.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\??\" + fullTarget[4..]
            : @"\??\" + fullTarget;

        byte[] subBytes = Encoding.Unicode.GetBytes(substituteName);
        byte[] printBytes = Encoding.Unicode.GetBytes(printName);
        int pathBufferLen = subBytes.Length + 2 + printBytes.Length + 2;
        int reparseDataLen = 8 + pathBufferLen;
        if (reparseDataLen > 16 * 1024 - 8)
        {
            return false;
        }

        byte[] buffer = new byte[8 + reparseDataLen];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), IO_REPARSE_TAG_MOUNT_POINT);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)reparseDataLen);
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 2), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), (ushort)subBytes.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12, 2), (ushort)(subBytes.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14, 2), (ushort)printBytes.Length);
        subBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + subBytes.Length + 2);

        try
        {
            Directory.CreateDirectory(junctionPath);
        }
        catch
        {
            return false;
        }

        IntPtr handle = CreateFileW(
            junctionPath,
            GENERIC_WRITE,
            FILE_SHARE_ALL,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            try { Directory.Delete(junctionPath, recursive: false); } catch { }
            return false;
        }

        bool ok;
        try
        {
            ok = DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            _ = CloseHandle(handle);
        }

        if (!ok)
        {
            try { Directory.Delete(junctionPath, recursive: false); } catch { }
            return false;
        }

        return true;
    }
}
