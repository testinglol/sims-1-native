using SimsEngine.Content;

namespace SimsEngine.Simulation;

public sealed record TutorialUiState
{
    public bool Live { get; init; }
    /// <summary>Specifies the active live-mode popup panel identifier, such as motives, job, rel, skill, or
    /// per.ity.</summary>
    public string? Popup { get; init; }
    public int? LastButtonImage { get; init; }
    public short LastButtonPerson { get; init; }
    public int Rotation { get; init; }
    public int Scrolls { get; init; }
    public IReadOnlyCollection<int> SetButtons { get; init; } = [];
}

/// <summary>Monitors tutorial progression by querying the controller tree, which returns the awaited UI
/// event type in Temp 0 and parameter in Temp 1 to trigger the corresponding event handler.</summary>
public sealed class TutorialEvents
{
    public const int ControllerGuid = unchecked((int)0xC3249A1D);

    public int Watched { get; private set; } = -1;
    private (short Kind, string? Popup)? deliveredPopup;

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

    public bool Check(SimulationWorld world, TutorialUiState ui)
    {
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
