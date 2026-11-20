using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record PackReport(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("baseGameVersion")] string BaseGameVersion,
    [property: JsonPropertyName("packs")] IReadOnlyList<string> Packs,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("resources")] IReadOnlyList<PackReport.Resource> Resources,
    [property: JsonPropertyName("objects")] IReadOnlyList<PackObjectDefinition> Objects,
    [property: JsonPropertyName("avatarResources"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PackAvatarResource>? AvatarResources = null,
    [property: JsonPropertyName("reservations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ObjectGUIDReservation>? Reservations = null,
    [property: JsonPropertyName("plugins"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PackPluginReport>? Plugins = null,
    [property: JsonPropertyName("panels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PackPanelReport>? Panels = null,
    [property: JsonPropertyName("packDetails"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PackDetailReport>? PackDetails = null,
    [property: JsonPropertyName("coreFingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CoreFingerprint = null)
{
    public sealed record Resource(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("owner")] string Owner,
        [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers,
        [property: JsonPropertyName("declaredOverride")] bool DeclaredOverride,
        [property: JsonPropertyName("bytes")] int Bytes,
        [property: JsonPropertyName("sha256")] string Sha256);
}

public sealed partial class PackStack
{
    public PackReport Report()
    {
        var providers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var overrides = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var pack in Ordered)
        {
            overrides[pack.Manifest.Id] = new HashSet<string>(
                pack.Manifest.Overrides.Select(PackContentPaths.ResourceKey),
                StringComparer.Ordinal);

            foreach (var path in pack.Assets.Keys)
            {
                if (!providers.TryGetValue(path, out var list))
                {
                    list = [];
                    providers[path] = list;
                }
                list.Add(pack.Manifest.Id);
            }
        }

        var resources = new List<PackReport.Resource>(Assets.Count);
        foreach (var (path, source) in Assets.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            var data = File.ReadAllBytes(source);
            var owners = providers[path];
            var owner = owners[^1];
            resources.Add(new PackReport.Resource(
                Path: path,
                Owner: owner,
                Providers: owners,
                DeclaredOverride: overrides[owner].Contains(path),
                Bytes: data.Length,
                Sha256: PackContentPaths.Sha256Hex(data)));
        }

        var packIDs = Ordered.Select(static p => p.Manifest.Id + "@" + p.Manifest.Version);
        var fingerprints = resources.Select(static r => r.Path + ":" + r.Sha256);
        var reserved = Reservations.Select(static r => $"object-guid:{r.Owner}:{r.Name}:{r.Guid}");
        var plugins = PluginReports();
        var panels = PanelReports();

        var code = plugins
            .Select(static p => $"plugin:{p.PackID}:{p.Sha256}:{p.StateVersion}")
            .Concat(panels.Select(static p => $"panel:{p.PackID}:{p.Id}:{p.Sha256}"));

        var fingerprintLines = packIDs.Concat(fingerprints).Concat(reserved).Concat(code);
        var fingerprint = PackContentPaths.Sha256Hex(string.Join('\n', fingerprintLines));

        string? coreFingerprint = null;
        if (Ordered.Any(static p => SaveCompatibility.OptionalFeaturePackIds.Contains(p.Manifest.Id)))
        {
            var coreOrdered = Ordered
                .Where(static p => !SaveCompatibility.OptionalFeaturePackIds.Contains(p.Manifest.Id))
                .ToArray();
            if (coreOrdered.Length == 0)
            {
                coreFingerprint = "unmounted-base-1.90";
            }
            else
            {
                var corePackIds = coreOrdered.Select(static p => p.Manifest.Id + "@" + p.Manifest.Version);
                var coreFingerprints = resources
                    .Where(static r => !SaveCompatibility.OptionalFeaturePackIds.Contains(r.Owner))
                    .Select(static r => r.Path + ":" + r.Sha256);
                var coreReserved = Reservations
                    .Where(static r => !SaveCompatibility.OptionalFeaturePackIds.Contains(r.Owner))
                    .Select(static r => $"object-guid:{r.Owner}:{r.Name}:{r.Guid}");
                var coreCode = plugins
                    .Where(static p => !SaveCompatibility.OptionalFeaturePackIds.Contains(p.PackID))
                    .Select(static p => $"plugin:{p.PackID}:{p.Sha256}:{p.StateVersion}")
                    .Concat(panels
                        .Where(static p => !SaveCompatibility.OptionalFeaturePackIds.Contains(p.PackID))
                        .Select(static p => $"panel:{p.PackID}:{p.Id}:{p.Sha256}"));
                var coreLines = corePackIds.Concat(coreFingerprints).Concat(coreReserved).Concat(coreCode);
                coreFingerprint = PackContentPaths.Sha256Hex(string.Join('\n', coreLines));
            }
        }

        return new PackReport(
            SchemaVersion: 1,
            BaseGameVersion: "1.90",
            Packs: packIDs.ToList(),
            Fingerprint: fingerprint,
            Resources: resources,
            Objects: Objects,
            AvatarResources: AvatarResources,
            Reservations: Reservations,
            Plugins: plugins.Count == 0 ? null : plugins,
            Panels: panels.Count == 0 ? null : panels,
            PackDetails: PackDetails(),
            CoreFingerprint: coreFingerprint);
    }
}
