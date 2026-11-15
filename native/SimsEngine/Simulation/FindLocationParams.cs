namespace SimsEngine.Simulation;

/// <summary>The search parameters of the Find Location For primitive (opcode 0x10), the 0x18-byte struct Sims.exe
/// builds on the stack (FUN_00434876 initializes it, modes 3 and 4, 5 and the rest fill it) and hands to the lot
/// search FUN_00434895. The port replicates initialization and field fills and implements candidate traversal
/// against native lot data. Full wall, water, outside/slope and original placement-virtual checks remain incomplete.
/// <para>Layout, by struct offset: +0 whether the center counts (always 1 in the fills); +4 and +8 the center in
/// full units; +12 the placement height; +16 the direction, or -1 when the mode names none (modes 3 and 4 write
/// the target's direction, mode 4 turned by 2; the other modes keep the initialized low word, which reads -1);
/// +20 is set for the first attempt and cleared for the one retry; FUN_00434895 uses it to require the candidate's
/// +0x28 tile-map value to match the center value. +21 is the inverse of operand byte 2 bit 1 and, when set, passes only
/// candidates whose room lookup (FUN_004325af) is zero: level 1's room 0, the room holding tile (0, 0). +22 is operand byte 2 bit 2 and gates an additional water-grid check. +23 enables
/// random-center selection when the center is (1, 1) or the search mode is zero. Native code models the +20
/// comparison and +21 room-zero gate using retained room keys where room maps are loaded, and retains Arry 9
/// bytes for the level-1 water gates. The random-center path and original placement virtual remain incomplete.</para></summary>
public sealed class FindLocationParams
{
    public bool UseCenter { get; set; } = true;
    public int CenterX { get; set; }
    public int CenterY { get; set; }
    public int Height { get; set; }
    public short Direction { get; set; } = -1;
    public byte ContinueFlag { get; set; } = 1;
    public byte FlagB { get; set; }
    public byte FlagC { get; set; }
}
