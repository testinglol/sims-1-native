using SimsEngine.Formats;

namespace SimsSimulation;

/// <summary>Loads the original ExpansionShared/work.iff CARR table used by Sims.exe FUN_00404ccb/FUN_00403647.</summary>
internal static class SimCareerCatalog
{
    public static CareerCatalog? Load(string gameRoot)
    {
        var candidates = new[]
        {
            Path.Combine(gameRoot, "ExpansionShared", "ExpansionShared.far"),
            Path.Combine(gameRoot, "GameData", "ExpansionShared", "ExpansionShared.far"),
            Path.Combine(gameRoot, "ExpansionShared.far"),
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var archive = FarArchive.Open(path);
            var entry = archive.Find("work.iff");
            if (entry is null) continue;
            var iff = new IffFile(archive.Read(entry), path + "::" + entry.Name);
            return CareerCatalog.Read(iff);
        }
        return null;
    }
}
