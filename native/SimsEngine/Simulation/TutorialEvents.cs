using SimsEngine.Content;

namespace SimsEngine.Simulation;

/// <summary>The interface state Mac Tut_CheckForEvents (Sims.exe FUN_00476675) reads each time cDDDSimsView::Simulate
/// runs.</summary>
public sealed record TutorialUiState
{
    /// <summary>A house is loaded and the control panel is in live mode (CPState::GetCPMode is 0).</summary>
    public bool Live { get; init; }
    /// <summary>The enabled live popup client's name ("motives", "job", "rel", "skill", "per.ity", or another
    /// name), or null when no live popup client is enabled.</summary>
    public string? Popup { get; init; }
    /// <summary>The image id (Res_CPanel.h) of the last button that sent its command, when that button has one.</summary>
    public int? LastButtonImage { get; init; }
    /// <summary>The object id of the Sim whose portrait button sent the last command, or 0.</summary>
    public short LastButtonPerson { get; init; }
    /// <summary>The house view's rotation (DAT_006e54d8+0x84).</summary>
    public int Rotation { get; init; }
    /// <summary>How many times the house view's offsets were set (the HouseViewer::SetOffsets counter).</summary>
    public int Scrolls { get; init; }
    /// <summary>The image ids of the main window's buttons whose state (cTSWinBtn +0xd8) is not 0.</summary>
    public IReadOnlyCollection<int> SetButtons { get; init; } = [];
}

/// <summary>Mac Tut_CheckForEvents and Sims.exe FUN_00476675. The tutorial owner's "query wait event" tree leaves the
/// awaited event kind in Temp0 (its BCON 4097 "ui events") and the event data in Temp1; a matching interface state runs
/// "got wait event".</summary>
public sealed class TutorialEvents
{
    public const int ControllerGuid = unchecked((int)0xC3249A1D);

    /// <summary>The value the rotation (kind 6) and scroll (kind 7) cases wait to change (lbl_105BEEC0, DAT_006c4da8);
    /// -1 when none is held.</summary>
    public int Watched { get; private set; } = -1;
    private (short Kind, string? Popup)? deliveredPopup;

    /// <summary>Neighborhood::LoadHouse creates the original tutorial controller out of world when tutorials
    /// are enabled, neighborhood word +0x12a is below 3, and simulator global 26 is nonzero.</summary>
    public static XObject? EnsureController(SimulationWorld world, bool tutorialDisabled = false)
    {
        if (tutorialDisabled || world.NeighborhoodData[1] >= 3 || world[SimulationGlobal.IsTutorialHouseNoYes] == 0 ||
            world.WalkOrder.Any(item => item.TypeRecord?.Key == ControllerGuid)) return null;
        var controller = world.ObjectFactory?.Invoke(ControllerGuid);
        if (controller is null) return null;
        world.Add(controller);
        world.InitializeCreatedObject(controller);
        return controller;
    }

    public static bool PopupEventMatches(short kind, bool liveMode, string? popup) => kind switch
    {
        1 => liveMode,
        2 => popup == "motives",
        9 => popup == "rel",
        11 => popup == "job",
        13 => popup == "skill",
        14 => popup is null,
        15 => popup == "per.ity",
        _ => false,
    };

    /// <summary>Simulator global 12 takes the last button's image id (0 when there is no last button); the owner's
    /// query tree then names the awaited event, and a match delivers it in the same owner scope.</summary>
    public bool Check(SimulationWorld world, TutorialUiState ui)
    {
        // Tut_CheckForEvents rewrites global 12 on every poll from the last button's image ID, defaulting to zero
        // when the last control has no numeric image. In particular, a person button must not leave a prior
        // universal-button ID in this global.
        world[SimulationGlobal.LastGZButtonID] = (short)(ui.LastButtonImage ?? 0);
        var owner = world.TutorialOwner;
        if (owner?.TypeRecord?.BehaviorScope is not BehaviorScope scope) return false;
        var query = scope.FindNamedTree("query wait event");
        var queryResult = query != 0 && TreeRunner.RunWithParameters(world, owner, scope, query, 0, []);
        if (!queryResult)
        {
            Watched = -1;
            deliveredPopup = null;
            return false;
        }
        var kind = owner.Temporaries[0];
        var data = owner.Temporaries[1];
        bool matched;
        switch (kind)
        {
            case 4:
                matched = ui.LastButtonImage == data;
                break;
            case 5:
                // Mac resolves the event's neighbor ID to its portrait, then compares that portrait with the
                // person button that sent the last command (TutorialEffects.s case 5). The Windows routine
                // searches the main-window tree for the button carrying the same per-person image ID. Match the
                // clicked Sim's neighbor ID here; TypeRecordKey identifies the shared Sim object definition and
                // can be identical across different people.
                matched = ui.LastButtonPerson != 0 && world.Neighbors.ContainsKey(data) &&
                    world.ObjectById(ui.LastButtonPerson) is XPerson person && person[PersonData.NeighborId] == data;
                break;
            case 6 or 7:
            {
                var current = kind == 6 ? ui.Rotation : ui.Scrolls;
                if (Watched == -1) { Watched = current; return false; }
                matched = Watched != current;
                break;
            }
            case 8:
                matched = ui.SetButtons.Contains(data);
                break;
            default:
                matched = PopupEventMatches(kind, ui.Live, ui.Popup) && deliveredPopup != (kind, ui.Popup);
                if (matched) deliveredPopup = (kind, ui.Popup);
                break;
        }
        if (!matched) return false;
        var deliver = scope.FindNamedTree("got wait event");
        if (deliver != 0) TreeRunner.RunWithParameters(world, owner, scope, deliver, 0, []);
        Watched = -1;
        return true;
    }
}
