using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>The build-mode wallpaper tool's single-segment paint and erase (cNewWallpaperTool without a modifier or with
/// Ctrl; FUN_0048a1d1 / FUN_0048a3ca call the planner's FUN_00493897 test and FUN_004939fc apply, and the mouse-up commit
/// charges the pending amount to expense category 7 like the wall tool). Shift room fill and Ctrl+Shift room erase (FUN_0048aa53, modes 2 and 3) are <see cref="Fill"/> with <c>erase</c> false and true respectively.
/// <para>A wall edge is owned by one tile's record and mirrored on the neighbour across it, and each record keeps the
/// pattern of its own side: bit 1 at file byte 4, bit 2 at byte 5, bit 8 at byte 6, bit 4 at byte 7, and a diagonal's
/// two sides at bytes 6 (0x10 side 2, 0x20 side 1) and 7 (0x10 side 4, 0x20 side 3).</para>
/// <para>The test (FUN_00493897): the tile lies strictly inside the lot border, the record has the edge, and the side's
/// pattern differs from the target (0 when erasing). A fence or balustrade style (2, 12, 13, 14, read with FUN_00467ece)
/// refuses with error 0x2e. Painting costs the pattern's price and must be affordable together with the pending amount
/// (FUN_00492b81, which otherwise sets the insufficient-funds flag); erasing refunds trunc(-0.5 - 0.8 * price of the
/// old pattern) (constants 0x667440 and 0x6681c0). A tile flagged 0x20 in Arry 8 (outside the lot) is left alone (FUN_00488924).</para></summary>
public sealed class WallpaperTool(LotWalls walls, WallpaperCatalog catalog, IFamilyBudget budget, LotTiles? tiles = null)
{
    public const int ExpenseCategory = 7;
    public const int FenceError = 0x2e;
    private const double RefundFactor = 0.800000011920929;
    private static readonly int[] FenceStyles = [2, 12, 13, 14];

    public static bool CanChangeStyle(byte style) => style != 0 && style is not (2 or 12 or 13 or 14);

    /// <summary>Tool +0x20: the amount a commit will charge (negative is a refund).</summary>
    public int Pending { get; private set; }

    /// <summary>Tool +0x40: the last segment was refused for lack of funds.</summary>
    public bool InsufficientFunds { get; private set; }

    /// <summary>The last refusal's error code (0 when none).</summary>
    public int Error { get; private set; }

    /// <summary>Paints (or erases, with <paramref name="erase"/>) the pattern on one side of one wall edge and returns
    /// whether it changed. <paramref name="bit"/> is 1, 2, 4, 8, 0x10 or 0x20; <paramref name="side"/> picks a diagonal's
    /// side (0x10: 2 or 4, 0x20: 1 or 3) and defaults to 4 and 1.</summary>
    public bool Dot(int x, int y, int bit, int side, int pattern, bool erase)
    {
        InsufficientFunds = false;
        Error = 0;
        if (bit is not (1 or 2 or 4 or 8 or 0x10 or 0x20)) throw new ArgumentException("bit must be 1, 2, 4, 8, 16 or 32");
        if (bit is 0x10) side = side == 0 ? 4 : side;
        else if (bit is 0x20) side = side == 0 ? 1 : side;
        if ((bit == 0x10 && side is not (2 or 4)) || (bit == 0x20 && side is not (1 or 3)) || (bit < 0x10 && side != 0))
            throw new ArgumentException("side does not belong to that edge");
        if (!erase && !catalog.Contains(pattern)) throw new ArgumentException($"Wall pattern {pattern} is not in the build catalog");
        if (x < 1 || y < 1 || x > LotWalls.Size - 2 || y > LotWalls.Size - 2) return false;
        if (tiles is not null && !tiles.InsideLot(x, y)) return false;
        var record = walls[x, y];
        if ((record[0] & bit) != bit) return false;
        var slot = bit switch { 1 => 4, 2 => 5, 4 => 7, 8 => 6, _ => side is 1 or 2 ? 6 : 7 };
        var target = erase ? 0 : pattern;
        var current = record[slot];
        if (current == target) return false;
        var style = bit switch
        {
            1 => record[2],
            2 => record[3],
            4 => walls[x + 1, y][2],
            8 => walls[x, y + 1][3],
            _ => record[3],
        };
        if (FenceStyles.Contains(style))
        {
            Error = FenceError;
            return false;
        }
        int cost;
        if (erase) cost = unchecked((int)(-0.5 - catalog.Price(current) * RefundFactor));
        else
        {
            cost = catalog.Price(target);
            if (cost >= 1 && Pending + cost > budget.ReadFamilyBudget())
            {
                InsufficientFunds = true;
                return false;
            }
        }
        record[slot] = (byte)target;
        Pending += cost;
        return true;
    }

    /// <summary>FUN_0048aa53 (Shift): paints or erases the pattern on every wall face of the room that holds tile (x, y).
    /// Each tile of the room's list contributes every cardinal bit of its record, mirror bits included, plus any dividing
    /// diagonal wall sides facing the room. Fence/railing segments (error 0x2e) are skipped without aborting the room fill;
    /// insufficient funds rolls back all segments modified during the fill and returns 0.</summary>
    public int Fill(RoomMap rooms, int x, int y, int pattern, bool erase, int side = 0)
    {
        InsufficientFunds = false;
        Error = 0;
        if (x < 0 || y < 0 || x >= LotWalls.Size || y >= LotWalls.Size) return 0;
        var room = rooms.RoomAt(x, y);
        if (room == RoomMap.NoRoom && x is >= 1 and <= LotWalls.Size - 2 && y is >= 1 and <= LotWalls.Size - 2)
        {
            var diagonal = walls[x, y][0] & 0x30;
            if (diagonal == 0x10)
            {
                var roomSide2 = rooms.RoomAtPosition(x * 16 + 4, y * 16 + 4);
                var roomSide4 = rooms.RoomAtPosition(x * 16 + 12, y * 16 + 12);
                room = side switch
                {
                    2 or 3 => roomSide2,
                    4 => roomSide4,
                    _ => roomSide4 != RoomMap.NoRoom && !rooms.IsOutside(roomSide4) ? roomSide4
                        : roomSide2 != RoomMap.NoRoom && !rooms.IsOutside(roomSide2) ? roomSide2
                        : roomSide4 != RoomMap.NoRoom ? roomSide4 : roomSide2,
                };
            }
            else if (diagonal == 0x20)
            {
                var roomSide1 = rooms.RoomAtPosition(x * 16 + 4, y * 16 + 12);
                var roomSide3 = rooms.RoomAtPosition(x * 16 + 12, y * 16 + 4);
                room = side switch
                {
                    1 or 2 => roomSide1,
                    3 => roomSide3,
                    _ => roomSide1 != RoomMap.NoRoom && !rooms.IsOutside(roomSide1) ? roomSide1
                        : roomSide3 != RoomMap.NoRoom && !rooms.IsOutside(roomSide3) ? roomSide3
                        : roomSide1 != RoomMap.NoRoom ? roomSide1 : roomSide3,
                };
            }
        }
        if (room == RoomMap.NoRoom) return 0;

        var initialPending = Pending;
        var snapshot = new List<(int X, int Y, byte Byte4, byte Byte5, byte Byte6, byte Byte7)>();
        var changed = 0;
        var sawFenceError = false;

        bool TryPaint(int tx, int ty, int bit, int edgeSide)
        {
            var record = walls[tx, ty];
            var before = (tx, ty, record[4], record[5], record[6], record[7]);
            if (Dot(tx, ty, bit, edgeSide, pattern, erase))
            {
                snapshot.Add(before);
                changed++;
                return true;
            }
            if (InsufficientFunds)
            {
                for (var i = snapshot.Count - 1; i >= 0; i--)
                {
                    var item = snapshot[i];
                    var cell = walls[item.X, item.Y];
                    cell[4] = item.Byte4;
                    cell[5] = item.Byte5;
                    cell[6] = item.Byte6;
                    cell[7] = item.Byte7;
                }
                Pending = initialPending;
                InsufficientFunds = true;
                Error = 0;
                return false;
            }
            if (Error == FenceError)
            {
                sawFenceError = true;
                Error = 0;
            }
            return true;
        }

        foreach (var (tx, ty) in rooms.TilesInRoom(room).ToArray())
        {
            if (tx < 1 || ty < 1 || tx > LotWalls.Size - 2 || ty > LotWalls.Size - 2) continue;
            var flags = walls[tx, ty][0];
            foreach (var bit in new[] { 1, 2, 4, 8 })
            {
                if ((flags & bit) == 0) continue;
                if (!TryPaint(tx, ty, bit, 0)) return 0;
            }
            var diag = flags & 0x30;
            if (diag == 0x10)
            {
                if (!TryPaint(tx, ty, 0x10, 2) || !TryPaint(tx, ty, 0x10, 4)) return 0;
            }
            else if (diag == 0x20)
            {
                if (!TryPaint(tx, ty, 0x20, 1) || !TryPaint(tx, ty, 0x20, 3)) return 0;
            }
        }

        for (var ty = 1; ty < LotWalls.Size - 1; ty++)
        for (var tx = 1; tx < LotWalls.Size - 1; tx++)
        {
            if (rooms.RoomAt(tx, ty) != RoomMap.NoRoom) continue;
            var diag = walls[tx, ty][0] & 0x30;
            if (diag == 0x10)
            {
                if (rooms.RoomAtPosition(tx * 16 + 4, ty * 16 + 4) == room && !TryPaint(tx, ty, 0x10, 2)) return 0;
                if (rooms.RoomAtPosition(tx * 16 + 12, ty * 16 + 12) == room && !TryPaint(tx, ty, 0x10, 4)) return 0;
            }
            else if (diag == 0x20)
            {
                if (rooms.RoomAtPosition(tx * 16 + 4, ty * 16 + 12) == room && !TryPaint(tx, ty, 0x20, 1)) return 0;
                if (rooms.RoomAtPosition(tx * 16 + 12, ty * 16 + 4) == room && !TryPaint(tx, ty, 0x20, 3)) return 0;
            }
        }

        Error = changed == 0 && sawFenceError ? FenceError : 0;
        return changed;
    }

    /// <summary>FUN_00492bc4: charges the pending amount and clears it.</summary>
    public int Commit()
    {
        var amount = Pending;
        if (amount != 0) budget.AlterFamilyBudget(ExpenseCategory, amount);
        Pending = 0;
        return amount;
    }
}
