using SimsEngine.Formats;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SimsEngine.Simulation;

/// <summary>One Sims.exe FUN_00420c69 tuning table, read from Global.iff FCNS 1 (adult), 5 (dog) or 6 (cat).</summary>
public sealed record PersonMotiveDecayProfile(
    float EnergySpan,
    float WakeHours,
    float WakeHour,
    float EnergyDrift,
    float HungerToBladderMultiplier,
    float ComfortDecrementActive,
    float ComfortDecrementLazy,
    float HungerDecrementRatio,
    float SocialDecrementBase,
    float SocialDecrementMultiplier,
    float FunDecrementAwake,
    float FunMultiplierAsleep,
    float HygieneDecrementAwake,
    float HygieneDecrementAsleep,
    float BladderDecrementAwake,
    float BladderDecrementAsleep,
    bool IsPet)
{
    internal static PersonMotiveDecayProfile Read(IffFile global, ushort id, bool isPet)
    {
        var chunk = global.Find(ResourceType.Fcns, id)
            ?? throw new InvalidDataException($"{global.Source}: missing FCNS {id} person motive tuning.");
        var constants = SimulatorConstants.Read(global, chunk);
        float Required(string name) => constants.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
            ?? throw new InvalidDataException($"{global.Source}: FCNS {id} is missing '{name}'.");

        return new PersonMotiveDecayProfile(
            Required("energy span"), Required("wake hours"), Required("wake hour"), Required("energy drift"),
            Required("hunger to bladder multiplier"), Required("comfort decrement active"),
            Required("comfort decrement lazy"), Required("hunger decrement ratio"), Required("social decrement base"),
            Required("social decrement multiplier"), Required("ent decrement, awake"), Required("ent multiplier, asleep"),
            Required("hyg decrement, awake"), Required("hyg decrement, asleep"), Required("bladder decrement, awake"),
            Required("bladder decrement, asleep"), isPet);
    }
}

/// <summary>One original STR# mood curve: motive value and its weight at each breakpoint.</summary>
public sealed record PersonMoodWeightCurve(IReadOnlyList<(float Motive, float Weight)> Points)
{
    public float Evaluate(float motive)
    {
        // FUN_004167d4 returns its zero-valued default when the curve has no points.
        if (Points.Count == 0) return 0f;
        if (Points.Count == 1 || motive >= Points[^1].Motive) return Points[^1].Weight;
        if (motive <= Points[0].Motive) return Points[0].Weight;

        for (var index = Points.Count - 2; index >= 0; index--)
        {
            var left = Points[index];
            var right = Points[index + 1];
            if (motive - right.Motive > 0f) return right.Weight;
            if (motive > left.Motive)
                return left.Weight + (right.Weight - left.Weight) * ((motive - left.Motive) / (right.Motive - left.Motive));
        }
        return Points[0].Weight;
    }

    public static PersonMoodWeightCurve Parse(string source, string value)
    {
        var points = new List<(float Motive, float Weight)>();
        var pattern = new Regex(@"\(\s*(-?[0-9]+(?:\.[0-9]+)?)\s*;\s*(-?[0-9]+(?:\.[0-9]+)?)\s*\)", RegexOptions.CultureInvariant);
        var consumed = 0;
        foreach (Match match in pattern.Matches(value))
        {
            if (!string.IsNullOrWhiteSpace(value[consumed..match.Index]))
                throw new InvalidDataException($"{source}: malformed mood curve '{value}'.");
            points.Add((float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
            consumed = match.Index + match.Length;
        }
        if (points.Count == 0 && string.IsNullOrWhiteSpace(value)) return new PersonMoodWeightCurve(points);
        if (points.Count == 0 || !string.IsNullOrWhiteSpace(value[consumed..]))
            throw new InvalidDataException($"{source}: malformed mood curve '{value}'.");
        if (points.Zip(points.Skip(1)).Any(pair => pair.First.Motive >= pair.Second.Motive))
            throw new InvalidDataException($"{source}: mood-curve motive breakpoints must be strictly increasing.");
        return new PersonMoodWeightCurve(points);
    }
}

/// <summary>The three FCNS tables initialized by Sims.exe FUN_00420c69.</summary>
public sealed record PersonMotiveDecayTuning(
    PersonMotiveDecayProfile Adult,
    PersonMotiveDecayProfile Dog,
    PersonMotiveDecayProfile Cat)
{
    private static readonly int[] MoodMotiveIndices = [7, 6, 8, 9, 15, 13, 14];
    public IReadOnlyList<PersonMoodWeightCurve> AdultMoodCurves { get; init; } = [];
    public IReadOnlyList<PersonMoodWeightCurve> ChildMoodCurves { get; init; } = [];
    public IReadOnlyList<PersonMoodWeightCurve> AdultScoreCurves { get; init; } = [];
    public IReadOnlyList<PersonMoodWeightCurve> ChildScoreCurves { get; init; } = [];

    /// <summary>The MotiveEffects curve of score row <paramref name="row"/> for this person's age, or null before the
    /// curves are loaded.</summary>
    public PersonMoodWeightCurve? ScoreCurve(XPerson person, int row)
    {
        var curves = person[PersonData.PersonAge] is > 0 and <= 17 ? ChildScoreCurves : AdultScoreCurves;
        return row < curves.Count ? curves[row] : null;
    }

    public static PersonMotiveDecayTuning Read(IffFile global)
    {
        ArgumentNullException.ThrowIfNull(global);
        var tuning = new PersonMotiveDecayTuning(
            PersonMotiveDecayProfile.Read(global, 1, isPet: false),
            PersonMotiveDecayProfile.Read(global, 5, isPet: true),
            PersonMotiveDecayProfile.Read(global, 6, isPet: true));
        return tuning with
        {
            // cXPerson::CalcHappy consumes the static adult/child MotiveCurveArray<7> sets,
            // initialized from Global STR# 0x1f6 and 0x1f8. STR# 0x1f5 and 0x1f7 are
            // separate per-person MotiveEffects resources and contain ten effect curves.
            AdultMoodCurves = ReadMoodCurves(global, 0x1f6),
            ChildMoodCurves = ReadMoodCurves(global, 0x1f8),
            AdultScoreCurves = ReadScoreCurves(global, 0x1f5),
            ChildScoreCurves = ReadScoreCurves(global, 0x1f7)
        };
    }

    /// <summary>cXPerson MotiveEffects (Mac person.s 0x10108640: MotiveCurveSet::LoadFromFile on person +0xa9c with STR#
    /// 0x1f5, or 0x1f7 for a child; Windows FUN_00459e8f) replaces the identity curves FUN_004213c0 builds. String n is
    /// the curve of score row n-1, and GetInteractionScore/GetCurrentScore read those rows. Points are added in
    /// ascending order of motive value, so the text order does not matter.</summary>
    private static IReadOnlyList<PersonMoodWeightCurve> ReadScoreCurves(IffFile global, ushort id)
    {
        var chunk = global.Find(ResourceType.Str, id)
            ?? throw new InvalidDataException($"{global.Source}: missing STR# {id} motive score curves.");
        var pattern = new Regex(@"\(\s*(-?[0-9]+(?:\.[0-9]+)?)\s*;\s*(-?[0-9]+(?:\.[0-9]+)?)\s*\)", RegexOptions.CultureInvariant);
        return StringTable.Read(global, chunk).Entries.Select(entry => new PersonMoodWeightCurve(
            pattern.Matches(entry.Value)
                .Select(match => (float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)))
                .OrderBy(point => point.Item1).ToArray())).ToArray();
    }

    private static IReadOnlyList<PersonMoodWeightCurve> ReadMoodCurves(IffFile global, ushort id)
    {
        var chunk = global.Find(ResourceType.Str, id)
            ?? throw new InvalidDataException($"{global.Source}: missing STR# {id} mood curves.");
        var table = StringTable.Read(global, chunk);
        if (table.Truncated || table.Entries.Count != MoodMotiveIndices.Length)
            throw new InvalidDataException($"{global.Source}: STR# {id} must contain seven complete mood curves.");
        return table.Entries.Select((entry, index) => PersonMoodWeightCurve.Parse(
            $"{global.Source}: STR# {id} mood curve {index} (motive {MoodMotiveIndices[index]})", entry.Value)).ToArray();
    }

    /// <summary>One FUN_00420fe9 update, called by the PERS wrapper after a successful object update when its
    /// 60-tick interval expires, then performs the age-appropriate mood weighting pass in FUN_0045d2d1.</summary>
    public void Apply(XPerson person, int gameHour)
    {
        ArgumentNullException.ThrowIfNull(person);
        var gender = person[PersonData.Gender];
        var tuning = (gender & 2) != 0 ? Dog : (gender & 4) != 0 ? Cat : Adult;
        var asleep = person[Motive.SleepState] < 0;
        var energy = person[Motive.Energy];

        if (asleep)
        {
            var gain = tuning.IsPet
                ? tuning.WakeHours / 30f
                : tuning.EnergySpan / (24f - tuning.WakeHours) / 30f;
            energy += gain;
            if (gameHour > tuning.WakeHour && gameHour < tuning.WakeHour + tuning.WakeHours)
                energy -= tuning.EnergyDrift;
        }
        else
        {
            var loss = tuning.IsPet
                ? tuning.EnergySpan / 30f
                : tuning.EnergySpan / tuning.WakeHours / 30f;
            energy -= loss;
        }
        person[Motive.Energy] = energy;

        var hunger = person[Motive.Hunger];
        var priorHunger = person.SavedPersonValues7CC[(int)Motive.Hunger];
        var hungerGain = hunger - priorHunger;
        if (hungerGain > 0)
            person[Motive.Bladder] -= hungerGain * tuning.HungerToBladderMultiplier;

        var active = person[PersonData.ActivePlayfulPersonality];
        person[Motive.Comfort] -= active * (tuning.ComfortDecrementActive - tuning.ComfortDecrementLazy) * 0.001f
            + tuning.ComfortDecrementLazy;
        person[Motive.Hunger] -= (100f + hunger) * tuning.HungerDecrementRatio;

        if (!asleep)
            person[Motive.Social] -= person[PersonData.OutgoingFriendlyPersonality] * tuning.SocialDecrementMultiplier
                + tuning.SocialDecrementBase;

        if (asleep)
            person[Motive.Fun] *= tuning.FunMultiplierAsleep;
        else
            person[Motive.Fun] -= tuning.FunDecrementAwake;

        person[Motive.Hygiene] -= asleep ? tuning.HygieneDecrementAsleep : tuning.HygieneDecrementAwake;
        person[Motive.Bladder] -= asleep ? tuning.BladderDecrementAsleep : tuning.BladderDecrementAwake;

        for (var index = 0; index < person.Motives.Length; index++)
        {
            person.Motives[index] = Math.Clamp(person.Motives[index], -100f, 100f);
            person.SavedPersonValues7CC[index] = person.Motives[index];
        }

        ApplyMood(person);
    }

    private void ApplyMood(XPerson person)
    {
        var curves = person[PersonData.PersonAge] is > 0 and <= 17 ? ChildMoodCurves : AdultMoodCurves;
        if (curves.Count != MoodMotiveIndices.Length) return;

        var gender = person[PersonData.Gender];
        var numerator = 0f;
        var denominator = 0f;
        for (var curveIndex = 0; curveIndex < MoodMotiveIndices.Length; curveIndex++)
        {
            var value = person.Motives[MoodMotiveIndices[curveIndex]];
            // Sims.exe excludes the default Room value (13) for pet gender codes, but includes it for people.
            if (gender >= 2 && value == 13f) continue;
            var weight = curves[curveIndex].Evaluate(value);
            numerator += weight * value;
            denominator += weight;
        }
        person[Motive.Mood] = numerator / denominator;
    }
}
