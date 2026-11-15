using System.Globalization;

namespace SimsSDL.Core;

/// <summary>
/// The seven controls in <c>cWinNeighborhoodUL::BuildFilterToolbar</c> and <c>ProcessFilterToolbar</c>.
/// </summary>
public sealed class NeighborhoodLotFilter
{
    public static IReadOnlyList<int> Masks { get; } = [16, 32, 8, 4, 2, 64, 1];
    public static IReadOnlyList<int> Positions { get; } = [225, 275, 325, 375, 425, 475, 525];

    public int AvailableMask { get; }
    public int SelectedMask { get; private set; }

    public NeighborhoodLotFilter(IReadOnlyList<int> communityFlags, int savedMask = 2)
    {
        int available = 0;
        for (int i = 0; i < communityFlags.Count; i++)
        {
            available |= communityFlags[i];
        }

        AvailableMask = available;
        SelectedMask = Masks.Contains(savedMask) ? savedMask : 16;
    }

    public bool IsEnabled(int index) =>
        index >= 0 && index < Masks.Count && (AvailableMask & Masks[index]) != 0;

    public void Select(int index)
    {
        if (!IsEnabled(index))
        {
            return;
        }

        SelectedMask = Masks[index];
    }

    public bool Marks(bool community, int flags) => community && (flags & SelectedMask) != 0;
}

public static class NeighborhoodTravel
{
    public readonly record struct Response(byte Code, string Text);

    public static int? ModeForDialogKind(byte kind) => kind switch
    {
        5 => 2,
        7 => 3,
        8 => 4,
        11 => 5,
        13 => 7,
        _ => null,
    };

    public static int? Mode(byte kind) => ModeForDialogKind(kind);

    public static Response? ResponseForDestination(int destination)
    {
        if (destination != -1 && (destination < 1 || destination > 100))
        {
            return null;
        }

        return new Response(destination > 0 ? (byte)1 : (byte)0, destination.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The STR# 134 (<c>NghRollover</c>) entry a neighborhood lot popup shows,
/// as Mac <c>cWinLotPopup::GetPopupIndex</c> picks it.
/// </summary>
public static class LotPopupIndex
{
    /// <summary>
    /// The neighborhood screen's lot mode (<c>cWinNeighborhood</c> +0xcc).
    /// </summary>
    public enum Mode
    {
        Browse = 0,
        MoveIn = 1,
        Evict = 2,
        Rezone = 3,
        Visit = 4,
    }

    /// <summary>
    /// <c>Neighborhood::GetZoningType</c>: 1 residential, 2 community, 0 for a lot the zoning table does not list.
    /// </summary>
    public enum Zoning
    {
        Unlisted = 0,
        Residential = 1,
        Community = 2,
    }

    /// <summary>
    /// Selects the STR# 134 entry index for a neighborhood lot popup.
    /// </summary>
    public static int Index(
        Mode mode,
        Zoning zoning,
        bool tutorialHouse,
        bool occupied,
        bool lotHasHouse,
        bool canAfford = false)
    {
        if (mode == Mode.Visit)
        {
            return zoning == Zoning.Community ? 21 : occupied ? 0 : 20;
        }

        if (tutorialHouse)
        {
            return mode switch
            {
                Mode.MoveIn => 8,
                Mode.Evict => 9,
                _ => 6,
            };
        }

        if (occupied)
        {
            return mode is Mode.Evict or Mode.Rezone ? 9 : 0;
        }

        return mode switch
        {
            Mode.Evict => lotHasHouse ? 10 : 11,
            Mode.Rezone => 19,
            Mode.MoveIn => zoning == Zoning.Community
                ? 18
                : lotHasHouse ? (canAfford ? 3 : 2) : (canAfford ? 5 : 4),
            _ => zoning == Zoning.Residential ? 1 : 17,
        };
    }
}

/// <summary>
/// The lot popup text as Mac <c>cWinLotPopup::SetHouseInfo</c> (and <c>Sims.exe 0x0054fcb8</c>) builds it:
/// the street line and <c>"\n\n"</c>, then the body.
/// </summary>
public static class LotPopupText
{
    public record struct Lot(
        int House,
        LotPopupIndex.Zoning Zoning,
        string Address,
        string Name,
        string Description,
        string Cost,
        string FamilyName,
        string FamilyNetWorth,
        int FamilyFriends);

    /// <summary>
    /// The moving-in family's name and <c>MovingInFamilyCanAfford</c> outputs:
    /// its cash and the cash left after the price.
    /// </summary>
    public record struct MovingIn(string Name, string Cash, string Remaining);

    /// <summary>
    /// Downtown, Vacation Island, Studio Town and Magic Town lots show only their name and description.
    /// </summary>
    public static bool IsPackLot(int house) =>
        house is (>= 21 and <= 30) or (>= 40 and <= 48) or (>= 81 and <= 89) or (>= 93 and <= 99);

    public static string Text(
        int index,
        bool visit,
        Lot lot,
        MovingIn? movingIn,
        Func<int, string> template,
        Func<string, IReadOnlyList<string>, string> format)
    {
        string description = lot.Description + "\n ";
        string body;
        if (IsPackLot(lot.House))
        {
            body = format("%s\n%s", [lot.Name, description]);
        }
        else
        {
            switch (lot.Zoning)
            {
                case LotPopupIndex.Zoning.Community:
                    body = format(template(index), [lot.Name, description]);
                    break;
                case LotPopupIndex.Zoning.Unlisted:
                    body = "";
                    break;
                case LotPopupIndex.Zoning.Residential:
                    switch (index)
                    {
                        case 0:
                        case 9:
                            string family = format(
                                template(index),
                                [lot.FamilyName, lot.FamilyNetWorth, lot.FamilyFriends.ToString(CultureInfo.InvariantCulture)]);
                            body = visit ? format(template(20), [family, ""]) : family;
                            break;
                        case 1:
                            body = format(template(index), [lot.Name, description, lot.Cost]);
                            break;
                        case 2:
                        case 4:
                            body = format(
                                template(index),
                                [lot.Name, description, lot.Cost, movingIn?.Name ?? "", movingIn?.Cash ?? ""]);
                            break;
                        case 3:
                        case 5:
                            body = format(
                                template(index),
                                [lot.Name, description, lot.Cost, movingIn?.Remaining ?? ""]);
                            break;
                        case >= 17 and <= 21:
                            body = format(template(index), [lot.Name, description]);
                            break;
                        case 6:
                        case 8:
                        case 10:
                        case 11:
                        case 12:
                            body = format(template(index), []);
                            break;
                        default:
                            body = "";
                            break;
                    }

                    break;
                default:
                    body = "";
                    break;
            }
        }

        return lot.Address + "\n\n" + body;
    }
}

public static class TutorialReset
{
    /// <summary>
    /// <c>cSimsApp::ResetTutorial</c> uses <c>CopyFileA</c> with fail-if-exists, then clears the read-only attribute.
    /// </summary>
    public static void Copy(string source, string importDirectory)
    {
        string destination = Path.Combine(importDirectory, "Tutorial.FAM");
        File.Copy(source, destination, overwrite: false);

        FileAttributes attributes = File.GetAttributes(destination);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(destination);
            File.SetUnixFileMode(destination, mode | UnixFileMode.UserWrite);
        }
    }
}
