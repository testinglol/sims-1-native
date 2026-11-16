using System.Buffers.Binary;
using System.Text.Json;
using SimsNative.Plugins;

internal static class PluginChecks
{
    private const string PackId = "local.example-allowance";
    private const string Version = "0.2.0";
    private const string EntryType = "Local.ExampleAllowance.AllowancePlugin";
    private const string Panel = "allowance";

    private sealed class ScriptedSimulation : IPluginSimulation
    {
        public short House { get; set; } = 12;
        public int Hours { get; set; } = 6;
        public int Minutes { get; set; }
        public bool Paused { get; set; }
        public int HouseholdFunds { get; set; } = 1000;
        public bool Broken { get; set; }
        public List<PluginSim> Sims { get; } = [];
        public IReadOnlyList<PluginSim> HouseholdSims => Broken ? throw new InvalidOperationException("scripted failure") : Sims;
        public short GetMotive(short objectId, int motive) => 0;
        public IReadOnlyList<PluginObject> Objects =>
            Sims.Select(sim => new PluginObject(sim.ObjectId, 0, sim.Name, 1, 1, 1, sim.ObjectId)).ToArray();

        public bool TryCharge(int amount)
        {
            if (amount <= 0 || amount > HouseholdFunds) return false;
            HouseholdFunds -= amount;
            return true;
        }

        public bool TryPay(int amount)
        {
            if (amount <= 0 || amount > 100_000 || HouseholdFunds > int.MaxValue - amount) return false;
            HouseholdFunds += amount;
            return true;
        }
    }

    public static object Run(string pluginFolder)
    {
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("Plugin check failed: " + name);
            checks.Add(name);
        }
        static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return true; }
            return false;
        }

        var assemblyName = Directory.Exists(pluginFolder)
            ? Directory.EnumerateFiles(pluginFolder, "*.dll").Select(Path.GetFileName).FirstOrDefault(name => name != "SimsPluginApi.dll")
            : null;
        if (assemblyName == null) throw new InvalidDataException($"No plugin assembly in {pluginFolder}; run tools/build_example_plugin.command first");

        var root = Path.Combine(Path.GetTempPath(), "sims-plugins-" + Guid.NewGuid().ToString("N"));
        var mount = Path.Combine(root, "mount");
        var dataRoot = Path.Combine(root, "data");
        var installed = Path.Combine(mount, ".plugins", PackId);
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(Path.Combine(dataRoot, "Settings"));
        foreach (var file in Directory.EnumerateFiles(pluginFolder).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(installed, Path.GetFileName(file)));
        var digest = PluginHost.Digest(installed);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var events = new List<string>();
        var simulation = new ScriptedSimulation();

        PluginHost Host(string? sha = null, int api = PluginApi.Version, int state = 2, string? assembly = null,
            string entryType = EntryType, bool panel = true, object? trust = null)
        {
            var metadata = new
            {
                plugins = new[] { new PluginHost.PluginDescriptor(PackId, Version, assembly ?? $".plugins/{PackId}/{assemblyName}",
                    sha ?? digest, entryType, api, state) },
                panels = panel ? new[] { new PluginHost.PanelDescriptor(PackId, Panel, "panels/allowance.json", "") } : [],
                packDetails = new[] { new PluginHost.PackDescriptor(PackId, Version, ["0.1.0"]) }
            };
            File.WriteAllText(Path.Combine(mount, ".pack-stack.json"), JsonSerializer.Serialize(metadata, json));
            var trustPath = PluginHost.TrustStorePath(dataRoot);
            trust ??= new { schemaVersion = 1, plugins = new[] { new { packId = PackId, version = Version, sha256 = digest } } };
            if (trust is string text) File.WriteAllText(trustPath, text);
            else File.WriteAllText(trustPath, JsonSerializer.Serialize(trust, json));
            events.Clear();
            return PluginHost.Create(mount, dataRoot, value => events.Add(JsonSerializer.Serialize(value, json)),
                () => simulation.House, _ => simulation);
        }
        static PluginHost.PluginStatus Status(PluginHost host) => host.Statuses.Single();
        Dictionary<string, string> PanelValues(PluginHost host)
        {
            var state = JsonSerializer.SerializeToElement(host.PanelState(), json);
            return state.EnumerateArray().Where(item => item.GetProperty("panelId").GetString() == Panel)
                .Select(item => item.GetProperty("values").Deserialize<Dictionary<string, string>>()!).Single();
        }
        void Day(PluginHost host, long update)
        {
            simulation.Hours = 7; host.Tick(update);
            simulation.Hours = 8; host.Tick(update + 1);
        }

        try
        {
            using (var host = Host(trust: new { schemaVersion = 1, plugins = Array.Empty<object>() }))
                Require(Status(host).State == "untrusted", "an untrusted plugin does not load");
            using (var host = Host(trust: new { schemaVersion = 1, plugins = new[] { new { packId = PackId, version = "0.1.0", sha256 = digest } } }))
                Require(Status(host).State == "untrusted", "trust names an exact pack version");
            using (var host = Host(trust: "{ not json"))
                Require(Status(host).State == "untrusted", "an unreadable trust store trusts nothing");
            using (var host = Host(trust: new { schemaVersion = 2, plugins = new[] { new { packId = PackId, version = Version, sha256 = digest } } }))
                Require(Status(host).State == "untrusted", "an unknown trust store schema trusts nothing");
            using (var host = Host(sha: new string('0', 64)))
                Require(Status(host).State == "rejected", "a digest that does not match the mounted files is rejected");
            var extra = Path.Combine(installed, "tampered.json");
            File.WriteAllText(extra, "{}");
            using (var host = Host())
                Require(Status(host).State == "rejected", "a file added after validation changes the digest");
            File.Delete(extra);
            using (var host = Host(api: PluginApi.Version + 1))
                Require(Status(host).State == "rejected", "an unsupported API version is rejected");
            using (var host = Host(api: 1))
                Require(Status(host) is { State: "failed" } status && status.Reason!.Contains("plugin API version 2"),
                    "a version 1 plugin cannot reach version 2 calls");
            using (var host = Host(state: 0))
                Require(Status(host).State == "rejected", "a state version below 1 is rejected");
            File.Copy(Path.Combine(installed, assemblyName), Path.Combine(mount, assemblyName), true);
            using (var host = Host(assembly: assemblyName))
                Require(Status(host).State == "rejected", "assemblies outside .plugins are rejected");
            using (var host = Host(assembly: $".plugins/{PackId}/../../{assemblyName}"))
                Require(Status(host).State == "rejected", "assembly paths cannot climb out of .plugins");
            using (var host = Host(entryType: "Local.ExampleAllowance.Missing"))
                Require(Status(host) is { State: "failed" } status && status.Reason!.Contains("was not found"), "a missing entry type fails cleanly");
            using (var host = Host(panel: false))
                Require(Status(host) is { State: "failed" } status && status.Reason!.Contains("not declared"),
                    "writing an undeclared panel during Initialize fails the plugin");

            simulation.Sims.AddRange([new PluginSim(1, 10, "Parent", false), new PluginSim(2, 11, "Kid", true), new PluginSim(3, 12, "Baby", true)]);
            using (var host = Host())
            {
                Require(Status(host).State == "loaded" && events.Any(item => item.Contains("\"state\":\"loaded\"")), "a trusted, matching plugin loads");
                Require(PanelValues(host) is { } initial && initial["total"] == "§0" && initial["funds"] == "1000", "Initialize publishes its panel");
                host.FlushPanels();
                Require(events.Count(item => item.Contains("\"pluginPanels\"")) == 1, "Initialize's panel values flush on the first tick or request");
                Require(host.AutonomyScore != null, "a version 2 plugin that scores autonomy is consulted by the VM");
                events.Clear();
                Day(host, 1);
                Require(simulation.HouseholdFunds == 1100 && PanelValues(host)["total"] == "§100", "08:00 pays §50 per child");
                Require(events.Count(item => item.Contains("\"pluginPanels\"")) == 1, "changed panel values are flushed once per tick");
                host.Tick(3);
                Require(simulation.HouseholdFunds == 1100, "the allowance is paid once per morning");
                simulation.Paused = true;
                Day(host, 4);
                Require(simulation.HouseholdFunds == 1100, "nothing is paid while the game is paused");
                simulation.Paused = false;
                host.Tick(6);
                Require(simulation.HouseholdFunds == 1200, "the morning's allowance is paid once the game resumes");

                Require(Throws<ArgumentException>(() => host.PanelAction(PackId, "other", "pause")), "actions on undeclared panels are refused");
                Require(Throws<ArgumentException>(() => host.PanelAction(PackId, Panel, new string('a', 65))), "overlong actions are refused");
                Require(JsonSerializer.Serialize(host.PanelAction(PackId, Panel, "pause"), json).Contains("\"accepted\":true") &&
                        PanelValues(host)["status"] == "Payments paused", "the pause button reaches the plugin");
                Day(host, 7);
                Require(simulation.HouseholdFunds == 1200, "a paused allowance pays nothing");

                var saved = host.Save();
                var record = saved[PackId];
                var data = Convert.FromBase64String(record.Data!);
                Require(record.PackVersion == Version && record.StateVersion == 2 && data.Length == 5 &&
                        BinaryPrimitives.ReadInt32LittleEndian(data) == 200 && data[4] == 1, "Save writes state version 2 with the total and paused flag");
                Require(record.Values.TryGetValue("lastPaidHouse", out var house) && house == "12", "plugin values are saved under the pack namespace");

                var v1 = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(v1, 250);
                var old = new Dictionary<string, PluginHost.PluginSaveRecord>
                    { [PackId] = new("0.1.0", 1, Convert.ToBase64String(v1), new Dictionary<string, string>()) };
                host.ValidateSaved(old);
                host.Restore(old);
                Require(PanelValues(host) is { } migrated && migrated["total"] == "§250" && migrated["status"] == "Paid each morning at 8:00",
                    "a state version 1 save migrates with payments resumed");
                Require(host.Save()[PackId].Values.Count == 0, "Restore replaces the plugin's values with the save's");
                Day(host, 9);
                Require(simulation.HouseholdFunds == 1300 && PanelValues(host)["total"] == "§350", "a migrated save keeps paying");
                host.Restore(saved);
                Require(PanelValues(host)["total"] == "§200" && PanelValues(host)["status"] == "Payments paused" &&
                        host.Save()[PackId].Values["lastPaidHouse"] == "12", "a version 2 save restores exactly");
                host.Restore(null);
                Require(host.Save()[PackId].Values.Count == 0, "a save without plugin state clears the plugin's values");

                PluginHost.PluginSaveRecord Record(int state = 2, string? stateData = null, Dictionary<string, string>? values = null) =>
                    new(Version, state, stateData, values ?? []);
                bool Invalid(string pack, PluginHost.PluginSaveRecord candidate) =>
                    Throws<InvalidDataException>(() => host.ValidateSaved(new Dictionary<string, PluginHost.PluginSaveRecord> { [pack] = candidate }));
                Require(Invalid(PackId, Record(3)), "saves from a newer state version are refused");
                Require(Invalid(PackId, Record(0)), "saves with an invalid state version are refused");
                Require(Invalid("local.missing", Record()), "saves needing a missing plugin are refused");
                Require(Invalid(PackId, Record(stateData: "not base64!")), "corrupt saved state is refused");
                Require(Invalid(PackId, Record(values: Enumerable.Range(0, 257).ToDictionary(index => "k" + index, _ => ""))),
                    "saves with more than 256 values are refused");
                Require(Invalid(PackId, Record(values: new() { [new string('k', 65)] = "" })), "saves with overlong keys are refused");
                Require(Invalid(PackId, Record(values: new() { ["k"] = new string('v', 4097) })), "saves with overlong values are refused");

                // A plugin that throws an unhandled exception is disabled for the remainder of the session
                // and blocks saving to prevent state corruption.
                host.PanelAction(PackId, Panel, "resume");
                PluginObject Sim(short id) => new(id, 0, "Sim", 1, 1, 1, id);
                Require(host.MenuEntries(1, Sim(2)) is [{ PackId: PackId, Entry: "pay-now", Name: "Allowance/Pay Now" }, { Entry: "details" }] &&
                        host.MenuEntries(1, Sim(1)).Count == 0, "a plugin adds pie menu entries where it chooses to");
                var before = simulation.HouseholdFunds;
                Require(JsonSerializer.Serialize(host.ChooseMenuEntry(1, Sim(2), PackId, "pay-now"), json).Contains("\"accepted\":true") &&
                        simulation.HouseholdFunds == before + 50 && PanelValues(host)["chore"] == "Paid Kid §50", "choosing a plugin's menu entry reaches the plugin");
                Require(Throws<ArgumentException>(() => host.ChooseMenuEntry(1, Sim(1), PackId, "pay-now")) &&
                        Throws<ArgumentException>(() => host.ChooseMenuEntry(1, Sim(2), PackId, "other")) &&
                        Throws<InvalidOperationException>(() => host.ChooseMenuEntry(1, Sim(2), "local.other", "pay-now")) &&
                        simulation.HouseholdFunds == before + 50, "menu entries the plugin does not offer are refused");
                events.Clear();
                Require(JsonSerializer.Serialize(host.ChooseMenuEntry(1, Sim(2), PackId, "details"), json).Contains("\"panel\":\"allowance\",\"anchorId\":2") &&
                        events.Any(item => item.Contains("\"pluginPanelShow\"") && item.Contains("\"panelId\":\"allowance\"") && item.Contains("\"anchorId\":2")) &&
                        PanelValues(host)["chore"] == "Kid gets §50 each morning", "a menu choice can open the plugin's panel beside the Sim");
                simulation.Broken = true;
                Day(host, 11);
                Require(Status(host) is { State: "failed" } failed && failed.Reason!.StartsWith("OnTick:"), "a throwing plugin is disabled");
                simulation.Broken = false;
                var funds = simulation.HouseholdFunds;
                Day(host, 13);
                Require(simulation.HouseholdFunds == funds, "a disabled plugin no longer runs");
                Require(host.AutonomyScore == null, "a disabled plugin no longer scores autonomy");
                Require(Throws<InvalidOperationException>(() => host.PanelAction(PackId, Panel, "resume")), "a disabled plugin's panel refuses actions");
                Require(Throws<InvalidOperationException>(() => host.Save()), "a disabled plugin blocks saving");
                Require(Invalid(PackId, Record()), "a disabled plugin cannot accept a save");
            }

            IDictionary<string, string> bounded = new PluginHost.BoundedValues();
            Require(Throws<ArgumentException>(() => bounded[""] = "") && Throws<ArgumentException>(() => bounded[new string('k', 65)] = ""),
                "value keys must be 1 to 64 characters");
            Require(Throws<ArgumentException>(() => bounded["k"] = new string('v', 4097)), "values are limited to 4096 characters");
            for (var index = 0; index < 256; index++) bounded["k" + index] = "";
            bounded["k0"] = "replaced";
            Require(Throws<ArgumentException>(() => bounded.Add("extra", "")) && Throws<ArgumentException>(() => bounded["extra"] = ""),
                "a namespace holds at most 256 values, and existing keys can still change");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        return new { result = "passed", plugin = assemblyName, digest, checks };
    }
}
