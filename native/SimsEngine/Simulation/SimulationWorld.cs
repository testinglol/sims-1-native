using SimsEngine.Formats;

namespace SimsEngine.Simulation;

public class FamePromotionChecks
{
    public static readonly int[] DefaultFameStarThresholds = [2, 9, 21, 35, 52, 78, 156, 300, 550, 900, 0];
    public static readonly int[] DefaultFameFriendThresholds = [0, 0, 0, 0, 2, 4, 7, 11, 14, 18, 0];
    public static readonly int[][] DefaultFameSkillThresholds =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 1, 2, 3, 4, 6, 6, 7, 8, 10, 0],
        [0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 1, 2, 3, 4, 4, 6, 7, 8, 9, 0]
    ];

    public IReadOnlyList<int> FameStarThresholds { get; init; } = DefaultFameStarThresholds;
    public IReadOnlyList<IReadOnlyList<int>> FameSkillThresholds { get; init; } = DefaultFameSkillThresholds;
    public IReadOnlyList<int> FameFriendThresholds { get; init; } = DefaultFameFriendThresholds;

    public FamePromotionChecks()
    {
        FameStarThresholds = DefaultFameStarThresholds;
        FameFriendThresholds = DefaultFameFriendThresholds;
        FameSkillThresholds = DefaultFameSkillThresholds;
    }

    public FamePromotionChecks(FameTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        FameStarThresholds = tuning.FameStarThresholds;
        FameFriendThresholds = tuning.FameFriendThresholds;
        FameSkillThresholds = tuning.FameSkillThresholds;
    }

    public GenericSimsCallRecordTarget ToTarget() => new()
    {
        FameStarThresholds = FameStarThresholds,
        FameSkillThresholds = FameSkillThresholds,
        FameFriendThresholds = FameFriendThresholds,
    };

    public static implicit operator GenericSimsCallRecordTarget(FamePromotionChecks checks) =>
        checks?.ToTarget()!;

    public bool EvaluateFamePromotion(short personId, short[] personRecord, SimulationWorld world)
    {
        ArgumentNullException.ThrowIfNull(personRecord);
        ArgumentNullException.ThrowIfNull(world);
        return world.EvaluateFamePromotion(personId);
    }

    public bool TryDecrementFameStarPower(short personId, short[] personRecord, SimulationWorld world)
    {
        ArgumentNullException.ThrowIfNull(personRecord);
        ArgumentNullException.ThrowIfNull(world);
        return world.TryDecrementFameStarPower(personId);
    }
}

public sealed partial class SimulationWorld
{
    public SimulationWorld()
    {
        GenericSimsCallRecordCallbackTarget = new FamePromotionChecks();
    }

    public void OnStarPowerChange(short personId, short oldStars, short newStars)
    {
        if (oldStars == newStars) return;

        PersistentPersonDataDirty = true;

        RecordUiMessage(0x110, $"Sim {personId} fame star changed: {oldStars * 0.5f:0.0}* -> {newStars * 0.5f:0.0}*");

        if (newStars >= 10 && Host is not null)
        {
        }
    }

    public bool EvaluateFamePromotion(short personId)
    {
        var target = GenericSimsCallRecordCallbackTarget;
        if (target is null) return false;

        var personRecordIndex = -1;
        if (ObjectById(personId) is XPerson livePerson)
        {
            try { personRecordIndex = PersonRecordIndex(livePerson); }
            catch (InvalidOperationException) { }
        }

        if (personRecordIndex < 0)
        {
            personRecordIndex = PersonRecordNeighborIds.IndexOf(personId);
        }
        if (personRecordIndex < 0) return false;

        var candidate = EvaluateFamePromotion(personRecordIndex, target);
        if (candidate is not null && MeetsFameFriendPowerGate(personRecordIndex, candidate.FriendPowerThreshold))
        {
            var oldStars = candidate.CurrentStarPower;
            ApplyFamePromotion(candidate);
            var newStars = candidate.PersonData[(int)PersonData.FameStarPower09];
            OnStarPowerChange(personId, oldStars, newStars);
            return true;
        }

        return false;
    }

    /// <summary>Decrements a Sim's current star power without reducing their highest attained fame level,
    /// clamping the result at zero.</summary>
    public bool TryDecrementFameStarPower(short personId)
    {
        var target = GenericSimsCallRecordCallbackTarget;
        if (target is null) return false;

        var personRecordIndex = -1;
        if (ObjectById(personId) is XPerson livePerson)
        {
            try { personRecordIndex = PersonRecordIndex(livePerson); }
            catch (InvalidOperationException) { }
        }

        if (personRecordIndex < 0)
        {
            personRecordIndex = PersonRecordNeighborIds.IndexOf(personId);
        }
        if (personRecordIndex < 0) return false;

        var personData = PersonRecordData(personRecordIndex);
        var oldStars = personData is not null ? personData[(int)PersonData.FameStarPower09] : (short)0;
        var success = TryDecrementFameStarPower(personRecordIndex, target.FameStarThresholds);
        if (success && personData is not null)
        {
            var newStars = personData[(int)PersonData.FameStarPower09];
            OnStarPowerChange(personId, oldStars, newStars);
        }
        return success;
    }
}
