namespace SimsNative.Plugins;

/// <summary>Versions of the gameplay plugin contract that the native simulation host accepts.</summary>
public static class PluginApi
{
    /// <summary>The contract version implemented by this assembly.</summary>
    public const int Version = 5;

    /// <summary>
    /// Contract versions the host can load. A plugin declares exactly one in its pack manifest. Version 2 added lot
    /// objects, pie menus, queued interactions and motive changes; version 4 adds reciprocal social roles, recipient
    /// response and per-role lifecycle status; version 5 adds Buy Mode catalog browser and filtering hooks. Older
    /// plugins keep the version-specific throwing defaults.
    /// </summary>
    public static readonly IReadOnlyList<int> SupportedVersions = [1, 2, 3, 4, 5];
}

/// <summary>
/// Entry point of a code-bearing gameplay pack. The host constructs the type named by the manifest's
/// <c>plugin.entryType</c> with a public parameterless constructor, only after the player trusted that exact assembly.
/// All calls arrive on the simulation thread between VM updates.
/// </summary>
public interface ISimsPlugin
{
    /// <summary>Called once after the plugin is constructed and before the first tick.</summary>
    void Initialize(IPluginContext context);

    /// <summary>Called after every simulation update that advanced game time.</summary>
    void OnTick(long update) { }

    /// <summary>Called when the player activates a button declared in one of this pack's panel descriptions.</summary>
    void OnPanelAction(string panelId, string action) { }

    /// <summary>
    /// Handles a console cheat command. Return a non-null message if handled (empty string is valid for silent success),
    /// or null if this plugin does not handle the command. Throw <see cref="ArgumentException"/> for invalid usage.
    /// </summary>
    string? OnCheat(string command, string[] args) => null;

    /// <summary>
    /// API version 2: rescores one candidate of a Sim's autonomous choice. The host calls it only on plugins that
    /// implement it, after the original scoring and before the Sim picks among its four best candidates. Return the
    /// score to use; 0 rules the candidate out. Keep it fast and deterministic: it runs for every candidate.
    /// </summary>
    float ScoreAutonomy(PluginAutonomyChoice choice, float score) => score;

    /// <summary>
    /// API version 2: extra pie menu entries for a household Sim on an object (another Sim included). Called each time
    /// the player opens a menu; return at most 8 entries. A "/" in a name opens a submenu, as in the original menus.
    /// </summary>
    IEnumerable<PluginMenuEntry> GetMenuEntries(short simId, PluginObject target) => [];

    /// <summary>
    /// API version 2: the player chose one of this plugin's entries. The host checks first that the plugin still offers
    /// it for that Sim and object. Nothing is queued for the Sim; queue interactions or change needs from here.
    /// </summary>
    void OnMenuEntry(short simId, PluginObject target, string entryId) { }

    /// <summary>
    /// API version 3: restricts an interaction that passed the original object's tests.
    /// Called for player menus and again before queuing, including queues requested
    /// through the plugin SDK. Return false to withhold it. This cannot create an
    /// interaction or override the original tests; already queued actions are unchanged.
    /// </summary>
    bool AllowInteraction(short simId, PluginObject target, PluginInteraction interaction) => true;

    /// <summary>
    /// API version 4: extra pie menu entries when clicking terrain/ground while a household Sim is selected.
    /// Return at most 8 entries. A "/" in a name opens a submenu.
    /// </summary>
    IEnumerable<PluginMenuEntry> GetGroundMenuEntries(short simId, PluginGroundPoint point) => [];

    /// <summary>
    /// API version 4: the player chose one of this plugin's ground pie menu entries.
    /// </summary>
    void OnGroundMenuEntry(short simId, PluginGroundPoint point, string entryId) { }

    /// <summary>Returns opaque plugin state to store under the pack's namespace in a native save, or null for none.</summary>
    byte[]? SaveState() => null;

    /// <summary>
    /// Restores state written by <see cref="SaveState"/>. <paramref name="savedStateVersion"/> is the manifest
    /// <c>plugin.stateVersion</c> at save time and is never newer than the running plugin's version; migrate older data here.
    /// </summary>
    void LoadState(int savedStateVersion, byte[] data) { }
}

/// <summary>Services the host gives one plugin. Every call is validated by the host; plugins cannot reach the VM directly.</summary>
public interface IPluginContext
{
    /// <summary>API3: reads a saved attribute (0..255) of this pack's own object.</summary>
    short GetObjectAttribute(short objectId, int index) => throw new NotSupportedException("Object attributes need API3");
    /// <summary>API3: writes a saved attribute (0..255) of this pack's own object. Original/other-pack objects are rejected.</summary>
    void SetObjectAttribute(short objectId, int index, short value) => throw new NotSupportedException("Object attributes need API3");
    /// <summary>API3: selects an OBJD graphic offset on this pack's own object.</summary>
    void SetObjectGraphic(short objectId, short graphic) => throw new NotSupportedException("Object graphics need API3");

    /// <summary>The contract version the host is serving for this plugin.</summary>
    int ApiVersion { get; }

    /// <summary>The owning pack ID; also the namespace of this plugin's save data.</summary>
    string PackId { get; }

    /// <summary>The pack version from its manifest.</summary>
    string PackVersion { get; }

    /// <summary>The manifest's <c>plugin.stateVersion</c>.</summary>
    int StateVersion { get; }

    /// <summary>Read-mostly access to the running lot.</summary>
    IPluginSimulation Simulation { get; }

    /// <summary>
    /// Small string values persisted automatically under this pack's namespace in native saves.
    /// Limited to 256 keys of at most 64 characters and values of at most 4096 characters.
    /// </summary>
    IDictionary<string, string> Values { get; }

    /// <summary>Sets a value bound by an element of one of this pack's declared panels.</summary>
    void SetPanelValue(string panelId, string binding, string value);

    /// <summary>
    /// API version 2: opens one of this pack's declared panels for the player. Only allowed from
    /// <see cref="ISimsPlugin.OnMenuEntry"/>, so a panel opens only when the player asked for it; set its values first.
    /// </summary>
    void ShowPanel(string panelId) => throw new NotSupportedException("ShowPanel needs plugin API version 2");

    /// <summary>
    /// API version 2: like <see cref="ShowPanel(string)"/>, but the clients draw the panel beside a Sim or object on the lot
    /// (the menu's Sim or target, or any object in <see cref="IPluginSimulation.Objects"/>), follow it as the view moves, and
    /// close the panel when the object leaves the lot. The panel keeps its size; its frame's position is ignored.
    /// </summary>
    void ShowPanel(string panelId, short anchorObjectId) => throw new NotSupportedException("ShowPanel needs plugin API version 2");

    /// <summary>API3: neighborhood-scoped key/value storage persisted with the save.</summary>
    IDictionary<string, string> NeighborhoodValues => throw new NotSupportedException("Neighborhood state needs API 3");
    /// <summary>API3: version of neighborhood-scoped plugin state stored in the active save.</summary>
    int NeighborhoodStateVersion => throw new NotSupportedException("Neighborhood state needs API 3");
    /// <summary>API3: reads a legacy sidecar file from the neighborhood directory if present.</summary>
    byte[]? ReadLegacyNeighborhoodData(string filename) => throw new NotSupportedException("Legacy import needs API 3");
    /// <summary>API3: reads a read-only data file bundled inside the pack.</summary>
    byte[] ReadPluginData(string filename) => throw new NotSupportedException("Plugin data needs API 3");
    /// <summary>API3: reads a pack-owned RGBA image resource.</summary>
    PluginImage ReadImageResource(string resource) => throw new NotSupportedException("Image resources need API 3");
    /// <summary>API3: plays a pack-owned WAV resource at an object, optionally looping it under a pack-local cue key.</summary>
    void PlaySoundResource(string resource, short ownerObjectId, string cue, bool loop = false) => throw new NotSupportedException("Pack sound resources need API 3");
    /// <summary>API3: stops a loop started with the same cue on the same object.</summary>
    void StopSoundResource(short ownerObjectId, string cue) => throw new NotSupportedException("Pack sound resources need API 3");

    /// <summary>API4: attempts to place a pack-owned object near the specified tile, returning its lead ObjectId or null if obstructed.</summary>
    short? TryPlaceOwnedObject(uint guid, int tileX, int tileY, int level = 1, short direction = 0, short? clearForSimId = null) =>
        throw new NotSupportedException("Pack object placement needs API 4");

    /// <summary>API4: removes a pack-owned object previously placed on the lot if idle.</summary>
    bool TryRemoveOwnedObject(short objectId) =>
        throw new NotSupportedException("Pack object removal needs API 4");

    /// <summary>API4: sets or clears a transient per-Sim outdoor suit override (e.g. winterwear 17, or null to restore automatic).</summary>
    void SetSimSuitOverride(short simId, int? suitCategory) =>
        throw new NotSupportedException("Suit overrides need API 4");

    /// <summary>API4: waters outdoor flowers and garden plots on the active lot, returning the number of plants refreshed.</summary>
    int WaterOutdoorPlants() =>
        throw new NotSupportedException("Plant watering needs API 4");

    /// <summary>Writes a diagnostic line to the simulation log.</summary>
    void Log(string message);
}

/// <summary>A household Sim visible to plugins.</summary>
public sealed record PluginSim(short ObjectId, short NeighborId, string Name, bool Child)
{
    /// <summary>The Sim's currently playing clip name when the host can report it.</summary>
    public string? Animation { get; init; }
    /// <summary>True while the Sim is wearing transient outdoor clothing (such as winter outerwear).</summary>
    public bool WearingOutdoorSuit { get; init; }
}

/// <summary>World tile descriptor passed to plugin ground pie-menu hooks.</summary>
public sealed record PluginGroundPoint(int TileX, int TileY, int Level, bool BareTerrain, bool Outside, bool Sheltered);

/// <summary>
/// An object on the lot (API version 2). <paramref name="X"/> and <paramref name="Y"/> are tile coordinates; a
/// multi-tile object's parts share <paramref name="LeadObjectId"/>.
/// </summary>
public sealed record PluginObject(short ObjectId, uint Guid, string Name, int X, int Y, int Level, short LeadObjectId)
{
    /// <summary>
    /// True when the object stands in an outdoor room, one open to the lot edge, rather than in a room enclosed by walls.
    /// Hosts before this member always report false.
    /// </summary>
    public bool Outside { get; init; }
    /// <summary>True beneath a solid upper floor or generated roof, regardless of camera visibility. Older hosts report false.</summary>
    public bool Sheltered { get; init; }
}

/// <summary>A pie menu entry a Sim is offered on an object (API version 2), as the player would see it.</summary>
public sealed record PluginInteraction(int Id, string Name, short Param0, bool Global);

/// <summary>One participant's named role in a reciprocal Sim-to-Sim social interaction.</summary>
public sealed record PluginPairedSocialRole(short SimId, short PartnerId, string Role, PluginInteraction Interaction);

/// <summary>Lifecycle state for the two queued role actions. Finished means the source action trees ended; it does not assert their semantic success.</summary>
public enum PluginPairedSocialActionState
{
    /// <summary>Action has been enqueued and has not started running yet.</summary>
    Queued,
    /// <summary>Action tree is currently executing.</summary>
    Running,
    /// <summary>Action tree exited cleanly.</summary>
    Finished,
    /// <summary>Action was cancelled before finishing.</summary>
    Cancelled,
    /// <summary>One of the participating Sims left the lot or became unavailable.</summary>
    ParticipantMissing,
}

/// <summary>Response recorded by the recipient's interaction-result chooser for the partner role.</summary>
public enum PluginSocialResponse
{
    /// <summary>No response chooser prompt has been issued.</summary>
    NotRequested,
    /// <summary>Waiting for the recipient's acceptance or rejection response.</summary>
    Awaiting,
    /// <summary>Recipient accepted the social interaction.</summary>
    Accepted,
    /// <summary>Recipient rejected the social interaction.</summary>
    Rejected,
    /// <summary>Recipient did not respond within the timeout window.</summary>
    TimedOut,
}

/// <summary>Current state of both participants in a queued paired social action.</summary>
public sealed record PluginPairedSocialActionStatus(long Id, PluginPairedSocialActionState State,
    short FirstSimId, string FirstRole, PluginPairedSocialActionState FirstState,
    short SecondSimId, string SecondRole, PluginPairedSocialActionState SecondState, PluginSocialResponse Response);

/// <summary>
/// A pie menu entry contributed by a plugin (API version 2). <paramref name="Id"/> is 1 to 64 letters, digits, '.', '-'
/// or '_' and is unique among the plugin's entries for one menu; <paramref name="Name"/> is 1 to 64 printable characters.
/// </summary>
public sealed record PluginMenuEntry(string Id, string Name);

/// <summary>A candidate of an autonomous choice (API version 2): which Sim, on which object, which pie menu entry.</summary>
public sealed record PluginAutonomyChoice(short SimId, bool Household, short ObjectId, uint ObjectGuid, int InteractionId, string Interaction)
{
    /// <summary>Whether the interaction belongs to the global table rather than the object's private table.</summary>
    public bool Global { get; init; }
}

/// <summary>Constrained simulation access. Writes go through the same validation the original game applies.</summary>
public interface IPluginSimulation
{
    /// <summary>The house number of the loaded lot.</summary>
    short House { get; }

    /// <summary>Game clock hour, 0 through 23.</summary>
    int Hours { get; }

    /// <summary>Game clock minute, 0 through 59.</summary>
    int Minutes { get; }

    /// <summary>True while the simulation is paused or a blocking dialog is open.</summary>
    bool Paused { get; }

    /// <summary>The active household's funds, or 0 when no household is active.</summary>
    int HouseholdFunds { get; }

    /// <summary>The active household's Sims on this lot.</summary>
    IReadOnlyList<PluginSim> HouseholdSims { get; }

    /// <summary>Reads one of a Sim's motives by its original motive index (0–15).</summary>
    short GetMotive(short objectId, int motive);

    /// <summary>Removes funds from the active household. Fails without a change when the amount is not positive or exceeds the funds.</summary>
    bool TryCharge(int amount);

    /// <summary>Adds funds to the active household. Amounts above 100,000 per call are rejected.</summary>
    bool TryPay(int amount);

    /// <summary>API version 2: every object on the lot, Sims included.</summary>
    IReadOnlyList<PluginObject> Objects => throw new NotSupportedException("Lot objects need plugin API version 2");

    /// <summary>API version 2: the pie menu a household Sim is offered on an object right now.</summary>
    IReadOnlyList<PluginInteraction> GetInteractions(short simId, short objectId) =>
        throw new NotSupportedException("Pie menus need plugin API version 2");

    /// <summary>
    /// API version 2: queues an interaction for a household Sim exactly as if the player picked it from the pie menu.
    /// Fails without a change while a blocking dialog is open or when the Sim is not offered that entry.
    /// </summary>
    bool TryQueueInteraction(short simId, short objectId, int interactionId, short param0 = 0, bool global = false) =>
        throw new NotSupportedException("Queuing interactions needs plugin API version 2");

    /// <summary>API version 2: the names of the actions in a household Sim's queue, the running one first.</summary>
    IReadOnlyList<string> GetQueuedActions(short simId) => throw new NotSupportedException("Action queues need plugin API version 2");

    /// <summary>
    /// API version 4: atomically queues both reciprocal participant roles of one Sim-to-Sim social action. Each role's
    /// interaction must currently be offered on the other participant. The participant whose action tree runs is the
    /// role's SimId; the stack object is its PartnerId. The returned handle reports each action tree's lifecycle.
    /// </summary>
    bool TryQueuePairedSocialAction(PluginPairedSocialRole first, PluginPairedSocialRole second, out long actionId)
    { actionId = 0; throw new NotSupportedException("Paired social actions need plugin API version 4"); }

    /// <summary>API version 4: reads lifecycle status for a pair queued by this simulation. Finished describes execution termination, not source-specific acceptance.</summary>
    PluginPairedSocialActionStatus? GetPairedSocialActionStatus(long actionId) => throw new NotSupportedException("Paired social actions need plugin API version 4");

    /// <summary>
    /// API version 2: changes one of a household Sim's needs, by the original motive index of <see cref="GetMotive"/>
    /// (energy 5, comfort 6, hunger 7, hygiene 8, bladder 9, social 14, fun 15), by up to 100 either way. The result is clamped to -100 through 100. Returns the new value.
    /// </summary>
    short ChangeMotive(short simId, int motive, int delta) => throw new NotSupportedException("Motive changes need plugin API version 2");
}


/// <summary>Optional plugin hook supplying lot environment lighting/weather parameters and neighborhood artwork transforms.</summary>
public interface IPluginEnvironmentProvider
{
    /// <summary>Returns environment parameters for the supplied game clock and actor positions, or null for default lighting.</summary>
    PluginEnvironment? GetEnvironment(PluginEnvironmentClock clock);
    /// <summary>Transforms a neighborhood UI image for the active environment state.</summary>
    PluginImage TransformNeighborhoodImage(PluginNeighborhoodImage image, PluginEnvironment environment) => image.Image;
}

/// <summary>Actor position and shelter state passed to environment providers.</summary>
public sealed record PluginActor(int Id, float X, float Y, int Level, bool Pet, bool Child, bool Swimming)
{
    /// <summary>True when the Sim stands in an outdoor room, as <see cref="PluginObject.Outside"/>.</summary>
    public bool Outside { get; init; }
    /// <summary>True beneath a solid upper floor or generated roof, regardless of camera visibility. Older hosts report false.</summary>
    public bool Sheltered { get; init; }
    /// <summary>True while the Sim is wearing transient outdoor clothing.</summary>
    public bool WearingOutdoorSuit { get; init; }
}
/// <summary>Looping lot-wide ambient sound bed requested by an environment provider.</summary>
public sealed record PluginAmbientSound(string Cue, string Resource, float Gain);
/// <summary>Simulation clock and actor snapshot passed to <see cref="IPluginEnvironmentProvider.GetEnvironment"/>.</summary>
public sealed record PluginEnvironmentClock(int House, int MapMode, int Month, int DayOfMonth, int Minutes, PluginActor[] Actors);
/// <summary>A single rendered footprint marker produced by an environment provider.</summary>
public sealed record PluginFootprint(long Sequence, float X, float Y, float Heading, float Foot);
/// <summary>Raw top-down RGBA8888 pixel buffer returned to/from plugin resources.</summary>
public sealed record PluginImage(int Width, int Height, byte[] Rgba);
/// <summary>Neighborhood UI image descriptor supplied to <see cref="IPluginEnvironmentProvider.TransformNeighborhoodImage"/>.</summary>
public sealed record PluginNeighborhoodImage(string Role, string Resource, PluginImage Image, PluginImage? Alternate,
    float X, float Y, float Scale);
/// <summary>Cycle indicator UI metadata for custom weather/cycle HUD strips.</summary>
public sealed record PluginCycleIndicator(int Index, int Count, string Strip, string WeatherIcon, string WeatherTooltip, string CycleTooltip, string Window, string Overlay);
/// <summary>Active visual, audio, shader, and gameplay overrides supplied by an <see cref="IPluginEnvironmentProvider"/>.</summary>
public sealed record PluginEnvironment
{
    /// <summary>Ground surface overlay mode identifier.</summary>
    public int SurfaceMode { get; init; }
    /// <summary>Strength/coverage of the ground surface overlay (0..1).</summary>
    public float SurfaceAmount { get; init; }
    /// <summary>Cloud cover factor (0..1).</summary>
    public float CloudCover { get; init; }
    /// <summary>Precipitation intensity (0..1).</summary>
    public float Precipitation { get; init; }
    /// <summary>Surface wetness intensity (0..1).</summary>
    public float Wetness { get; init; }
    /// <summary>Ambient sound gain multiplier.</summary>
    public float AudioGain { get; init; } = 1;
    /// <summary>Ambient audio low-pass cutoff modulation.</summary>
    public float AudioCutoff { get; init; }
    /// <summary>Whether atmospheric rendering pass overrides are enabled.</summary>
    public bool Atmosphere { get; init; }
    /// <summary>Optional forced outdoor outfit category when outdoors.</summary>
    public int? OutdoorSuit { get; init; }
    /// <summary>Whether displaced surface rendering is active.</summary>
    public bool DisplacedSurface { get; init; }
    /// <summary>Rate at which accumulated surface trails recover.</summary>
    public float TrailRecovery { get; init; }
    /// <summary>Active actor footprint markers to render onto the surface trail buffer.</summary>
    public PluginFootprint[] Footprints { get; init; } = [];
    /// <summary>Object GUIDs whose own routines see the lot as a Vacation snow lot (global 428) while listed.</summary>
    public uint[] SnowLotObjects { get; init; } = [];
    /// <summary>Optional HUD cycle indicator strip metadata.</summary>
    public PluginCycleIndicator? Indicator { get; init; }
    /// <summary>Custom shader stage path overrides keyed by stage name.</summary>
    public Dictionary<string, string> Shaders { get; init; } = [];
    /// <summary>Scalar uniform overrides passed to environment shaders.</summary>
    public Dictionary<string, float> Parameters { get; init; } = [];
    /// <summary>Looping lot-wide environmental ambient sound beds managed by the pack.</summary>
    public PluginAmbientSound[] AmbientSounds { get; init; } = [];
}

/// <summary>API version 5: read-only catalog object descriptor supplied to <see cref="IPluginBuyCatalogProvider"/>.</summary>
public sealed record PluginCatalogObject(
    uint Guid,
    string Name,
    string SourcePath,
    int Price,
    int CategoryMask,
    int RoomMask,
    bool IsCustomContent);

/// <summary>API version 5: one named folder/collection of catalog object GUIDs exposed inside a plugin Buy Mode view.</summary>
public sealed record PluginBuyCollection(
    string Id,
    string Label,
    string? Tooltip,
    uint[] Guids);

/// <summary>API version 5: custom Buy Mode browser tab and optional standard-catalog declutter filter returned by a plugin.</summary>
public sealed record PluginBuyCatalogView(
    string TabId,
    string TabLabel,
    string? Hotkey,
    bool HideMatchedFromStandardCatalog,
    PluginBuyCollection[] Collections);

/// <summary>API version 5: optional plugin hook providing custom Buy Mode catalog collections (such as a CC folder browser)
/// and optional filtering of matched items out of standard Buy Mode categories.</summary>
public interface IPluginBuyCatalogProvider
{
    /// <summary>Returns a custom Buy Mode view over the currently loaded catalog objects, or null to provide none.</summary>
    PluginBuyCatalogView? GetBuyCatalogView(IReadOnlyList<PluginCatalogObject> objects);
}
