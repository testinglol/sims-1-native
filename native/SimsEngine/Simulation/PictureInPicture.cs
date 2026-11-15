namespace SimsEngine.Simulation;

/// <summary>The Live PIP play option as the Special Effect primitive reads it (Sims.exe FUN_004cffb7 / Mac
/// <c>cOptionsMgr::GetLivePIP</c>). Auto Snapshot is not held here: Sims.exe reads it in the viewer (FUN_004d39b5 calls
/// FUN_004e642d), so the client applies it to the <see cref="PictureInPictureEvent.SnapshotRequested"/> bit.</summary>
public sealed class PictureInPictureOptions
{
    public bool Live { get; set; } = true;
}

/// <summary>A Special Effect request that passed the primitive's Live PIP gate, for the client's viewer. Flags are the
/// operand's byte 4 as FUN_0044aae6 splits them: bit 0 opens (clear closes) the picture-in-picture window, bit 2 clear
/// is the frame argument, bit 3 forces the event onto the main view (center it, never a window), bit 4 asks the viewer to
/// take an automatic snapshot, and bit 6 set skips the 0x10c notice. <see cref="Text"/> is entry byte 5 of the STR# 0x131
/// table of the stack object's scope, the snapshot caption (Sims.exe parses its UI tokens; they are left as written).</summary>
public sealed record PictureInPictureEvent(long Sequence, int ObjectId, bool Open, bool Force, bool SnapshotRequested,
    int DurationMilliseconds, byte Flags, byte Byte2, byte Byte3, string? Text);

public sealed partial class SimulationWorld
{
    /// <summary>The string table of the Special Effect caption (FUN_00470383 with 0x131).</summary>
    private const short PictureInPictureCaptionTable = 0x131;
    private const byte PictureInPictureForceFlag = 0x08;
    private const byte PictureInPictureSnapshotFlag = 0x10;
    private static long s_pictureInPictureSequence;

    /// <summary>Live PIP state of a session that renders picture-in-picture. When null the primitive keeps recording every
    /// dispatch without a gate (the headless engine has no viewer).</summary>
    public PictureInPictureOptions? PictureInPicture { get; set; }

    /// <summary>The most recent picture-in-picture events, for the client to apply once each by sequence.</summary>
    public List<PictureInPictureEvent> PictureInPictureEvents { get; } = [];

    /// <summary>FUN_0044aae6's gate before it dispatches an effect. Nothing happens (the primitive answers true) unless
    /// Live PIP is on or flag bit 3 forces it, and then the lot is a home lot (Neighborhood::GetZoningType 1, FUN_004074bc),
    /// bit 3 is set, or the running object is a Sim of the current family (its word +0x606 equals the family global, or
    /// the family is 0 and +0x626 is set, and at least one of +0x606/+0x626 is nonzero). The Sim branch also
    /// requires the running object's definition type to be 2 (person).</summary>
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

    /// <summary>FUN_00433136: a forced event is only photographed when the object's tile is strictly inside the lot's
    /// edge ring (1 through dimension - 2 on both axes).</summary>
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
