using System.Globalization;

namespace SimsEngine.Simulation;

public enum OverheadFeedbackKind
{
    Currency,
    SkillUp,
    Magenta,
    Relationship,
}

public sealed record OverheadFeedbackEvent(
    short PersonId,
    OverheadFeedbackKind Kind,
    string Text,
    uint BackgroundRgb,
    uint ForegroundRgb,
    int Delta,
    int StartTick,
    int ExpiresAtTick,
    int StackIndex);

public sealed partial class SimulationWorld
{
    public const int OverheadFeedbackDurationTicks = 100;

    private readonly List<OverheadFeedbackEvent> _overheadFeedback = [];

    public void ClearOverheadFeedback() => _overheadFeedback.Clear();

    public void ProcessPersonMoneyOverhead(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);

        var raw = person[PersonData.MoneyAmountOverHead];
        if (raw == 0)
            return;

        person[PersonData.MoneyAmountOverHead] = 0;

        var nowTick = Clock.Ticks;
        PruneExpiredOverheadFeedback(nowTick);
        var stackIndex = CountActiveOverheadForPerson(person.Id);

        OverheadFeedbackKind kind;
        string text;
        uint bgRgb;
        uint fgRgb;
        int delta;

        if (raw > -10001)
        {
            // Branch A: Currency delta (-10000..-1 or >= 1) — bright green on dark navy pill (004a6bc2.c:26-29, 004d3696.c)
            kind = OverheadFeedbackKind.Currency;
            delta = raw;
            text = raw > 0
                ? $"+${raw.ToString(CultureInfo.InvariantCulture)}"
                : $"-${Math.Abs((int)raw).ToString(CultureInfo.InvariantCulture)}";
            bgRgb = 0x000052u;
            fgRgb = 0x00CA39u;
        }
        else if (raw >= -20000)
        {
            // Branch B: Golden skill-up (-20000..-10001) — black on gold pill + sting_char_winner (004a6bc2.c:31-37, 004d36b5.c)
            kind = OverheadFeedbackKind.SkillUp;
            delta = -10000 - raw;
            text = delta.ToString(CultureInfo.InvariantCulture);
            bgRgb = 0xFFCB17u;
            fgRgb = 0x000000u;
            PlaySoundEvent?.Invoke("sting_char_winner", person.Id, false, false);
        }
        else
        {
            // Branch C: Magenta text (<= -20001) — black on magenta pill, no sound (004a6bc2.c:39-42, 004d3709.c)
            kind = OverheadFeedbackKind.Magenta;
            delta = -20000 - raw;
            text = delta.ToString(CultureInfo.InvariantCulture);
            bgRgb = 0xFF00FFu;
            fgRgb = 0x000000u;
        }

        _overheadFeedback.Add(new OverheadFeedbackEvent(
            person.Id,
            kind,
            text,
            bgRgb,
            fgRgb,
            delta,
            nowTick,
            nowTick + OverheadFeedbackDurationTicks,
            stackIndex));
        DisplayRefreshRequested = true;
    }

    public void RecordRelationshipOverhead(NeighborState source, NeighborState target, int delta)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (delta == 0)
            return;

        var person = WalkOrder.OfType<XPerson>()
            .FirstOrDefault(item => item[PersonData.NeighborId] == source.Id);
        if (person is null)
            return;

        // Only show relationship indicators for Sims who are actually present on the lot
        // (avoids flooding Sims with '-' indicators during passive daily decay against off-lot neighbors).
        var targetOnLot = WalkOrder.OfType<XPerson>()
            .Any(item => item[PersonData.NeighborId] == target.Id);
        if (!targetOnLot)
            return;

        var nowTick = Clock.Ticks;
        PruneExpiredOverheadFeedback(nowTick);

        // Coalesce/replace any existing relationship indicator for this Sim instead of stacking a tall vertical tower.
        for (var i = _overheadFeedback.Count - 1; i >= 0; i--)
        {
            if (_overheadFeedback[i].PersonId == person.Id && _overheadFeedback[i].Kind == OverheadFeedbackKind.Relationship)
                _overheadFeedback.RemoveAt(i);
        }
        var stackIndex = CountActiveOverheadForPerson(person.Id);

        // Matches social BHAV 4137/4138/416 thresholds for SPR# 615..618 ('++', '+', '-', '--')
        var text = delta > 10 ? "++"
            : delta > 0 ? "+"
            : delta > -9 ? "-"
            : "--";

        _overheadFeedback.Add(new OverheadFeedbackEvent(
            person.Id,
            OverheadFeedbackKind.Relationship,
            text,
            0x000052u,
            delta > 0 ? 0x00CA39u : 0xD10000u,
            delta,
            nowTick,
            nowTick + OverheadFeedbackDurationTicks,
            stackIndex));
        DisplayRefreshRequested = true;
    }

    public IReadOnlyList<OverheadFeedbackEvent> ActiveOverheadFeedback(int? tickCounter = null)
    {
        var nowTick = tickCounter ?? Clock.Ticks;
        PruneExpiredOverheadFeedback(nowTick);
        return _overheadFeedback.Count == 0 ? [] : [.. _overheadFeedback];
    }

    private void PruneExpiredOverheadFeedback(int nowTick)
    {
        for (var i = _overheadFeedback.Count - 1; i >= 0; i--)
        {
            var entry = _overheadFeedback[i];
            if (nowTick < entry.StartTick || nowTick >= entry.ExpiresAtTick)
                _overheadFeedback.RemoveAt(i);
        }
    }

    private int CountActiveOverheadForPerson(short personId)
    {
        var count = 0;
        for (var i = 0; i < _overheadFeedback.Count; i++)
        {
            if (_overheadFeedback[i].PersonId == personId)
                count++;
        }
        return count;
    }
}
