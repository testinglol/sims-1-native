namespace SimsEngine.Simulation;

public sealed class PictureInPictureOptions
{
    public bool Live { get; set; } = true;
}

/// <summary>Represents a picture-in-picture event from the Special Effect primitive. Bit flags control
/// window state, main view centering, snapshot triggers, and caption text from string table 0x131.</summary>
public sealed record PictureInPictureEvent(long Sequence, int ObjectId, bool Open, bool Force, bool SnapshotRequested,
    int DurationMilliseconds, byte Flags, byte Byte2, byte Byte3, string? Text);

public sealed partial class SimulationWorld
{
    private const short PictureInPictureCaptionTable = 0x131;
    private const byte PictureInPictureForceFlag = 0x08;
    private const byte PictureInPictureSnapshotFlag = 0x10;
    private static long s_pictureInPictureSequence;

    public PictureInPictureOptions? PictureInPicture { get; set; }

    public List<PictureInPictureEvent> PictureInPictureEvents { get; } = [];

    /// <summary>Determines whether a picture-in-picture effect is dispatched based on gameplay options,
    /// residential lot zoning, forced override flags, and family membership.</summary>
    internal bool AllowsPictureInPicture(XObject me, byte flags)
    {
        var options = PictureInPicture;
        if (options is null) return true;
        var force = (flags & PictureInPictureForceFlag) != 0;
        if (!options.Live && !force) return false;
        if (StateClass(unchecked((short)ActiveLotNumber)) == 1 || force) return true;
        if (me is not XPerson person) return false;
        var currentFamily = this[SimulationGlobal.CurrentFamily];
        var personFamily = person.SelectionWord606 != 0 ? person.SelectionWord606 : person[PersonData.FamilyNumber];
        var autoFollow = person[PersonData.AutoFollowHotDate];
        return (personFamily == currentFamily || (currentFamily == 0 && autoFollow != 0)) &&
               (personFamily != 0 || autoFollow != 0);
    }

    /// <summary>Determines whether an object is positioned strictly inside the lot perimeter, excluding
    /// outer border tiles.</summary>
    private static bool TileInsideLotEdge(XObject target)
    {
        var x = target.Position.X >> 4;
        var y = target.Position.Y >> 4;
        return x >= 1 && y >= 1 && x < LotGeometry.LotDimension - 1 && y < LotGeometry.LotDimension - 1;
    }

    internal void RecordPictureInPicture(XObject me, XObject stackObject, in SpecialEffectRequest request,
        Content.BehaviorScope? scope = null, short treeId = 0)
    {
        if (PictureInPicture is null) return;
        var flags = request.Byte4;
        string? text = null;
        if (request.Byte5 != 0)
        {
            var index = request.Byte5 - 1;
            text = ScopeStringTableEntry(scope, treeId, PictureInPictureCaptionTable, index)
                ?? StringTableEntry(stackObject, me, PictureInPictureCaptionTable, index);
        }
        PictureInPictureEvents.Add(new PictureInPictureEvent(Interlocked.Increment(ref s_pictureInPictureSequence), stackObject.Id,
            (flags & 1) != 0, (flags & PictureInPictureForceFlag) != 0, (flags & PictureInPictureSnapshotFlag) != 0 && TileInsideLotEdge(stackObject),
            request.DurationMs, flags, request.Byte2, request.Byte3, string.IsNullOrEmpty(text) ? null : text));
        if (PictureInPictureEvents.Count > 16) PictureInPictureEvents.RemoveAt(0);
    }

    private string? ScopeStringTableEntry(Content.BehaviorScope? scope, short treeId, short table, int index)
    {
        if (scope is null) return null;
        var treeFile = scope.FileFor(treeId);
        foreach (var file in new[] { treeFile, scope.Private, scope.SemiGlobal, scope.Global })
        {
            if (file is null) continue;
            var chunk = file.Find(Formats.ResourceType.Str, unchecked((ushort)table))
                ?? file.Find(Formats.ResourceType.Ctss, unchecked((ushort)table));
            if (chunk is null) continue;
            var strings = Formats.StringTable.Read(file, chunk).ForLanguage(Language);
            return index >= 0 && index < strings.Count ? strings[index] : null;
        }
        return null;
    }
}
