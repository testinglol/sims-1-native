using SimsEngine.Formats;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SimsEngine.Simulation;

/// <summary>Represents a motive decay profile loaded from Global.iff FCNS resource 1 for adults, 5 for
/// dogs, or 6 for cats.</summary>
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

/// <summary>Represents a piecewise linear mood weight curve defining motive value breakpoints and weights.</summary>
public sealed record PersonMoodWeightCurve(IReadOnlyList<(float Motive, float Weight)> Points)
{
    public float Evaluate(float motive)
    {
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
            // Adult and child happiness use string tables 0x1f6 and 0x1f8, while interaction score effects
            // load from 0x1f5 and 0x1f7.
            AdultMoodCurves = ReadMoodCurves(global, 0x1f6),
            ChildMoodCurves = ReadMoodCurves(global, 0x1f8),
            AdultScoreCurves = ReadScoreCurves(global, 0x1f5),
            ChildScoreCurves = ReadScoreCurves(global, 0x1f7)
        };
    }

    /// <summary>Loads motive score curves from string tables where each entry corresponds to a score row,
    /// sorting breakpoints in ascending motive order.</summary>
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

    /// <summary>Applies motive decay and calculates age-appropriate mood weighting on a 60-tick interval.</summary>
    public void Apply(XPerson person, int gameHour)
    {
        ArgumentNullException.ThrowIfNull(person);
        Apply(person.Motives, person.SavedPersonValues7CC, person.PersonData, gameHour);
    }

    /// <summary>Applies motive decay and mood weighting directly against raw motive/person-data arrays.</summary>
    public void Apply(float[] motives, float[] savedValues7CC, short[] personData, int gameHour)
    {
        ArgumentNullException.ThrowIfNull(motives);
        ArgumentNullException.ThrowIfNull(savedValues7CC);
        ArgumentNullException.ThrowIfNull(personData);

        var gender = personData[(int)PersonData.Gender];
        var tuning = (gender & 2) != 0 ? Dog : (gender & 4) != 0 ? Cat : Adult;
        var asleep = motives[(int)Motive.SleepState] < 0;
        var energy = motives[(int)Motive.Energy];

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
        motives[(int)Motive.Energy] = energy;

        var hunger = motives[(int)Motive.Hunger];
        var priorHunger = savedValues7CC[(int)Motive.Hunger];
        var hungerGain = hunger - priorHunger;
        if (hungerGain > 0)
            motives[(int)Motive.Bladder] -= hungerGain * tuning.HungerToBladderMultiplier;

        var active = personData[(int)PersonData.ActivePlayfulPersonality];
        motives[(int)Motive.Comfort] -= active * (tuning.ComfortDecrementActive - tuning.ComfortDecrementLazy) * 0.001f
            + tuning.ComfortDecrementLazy;
        motives[(int)Motive.Hunger] -= (100f + hunger) * tuning.HungerDecrementRatio;

        if (!asleep)
            motives[(int)Motive.Social] -= personData[(int)PersonData.OutgoingFriendlyPersonality] * tuning.SocialDecrementMultiplier
                + tuning.SocialDecrementBase;

        if (asleep)
            motives[(int)Motive.Fun] *= tuning.FunMultiplierAsleep;
        else
            motives[(int)Motive.Fun] -= tuning.FunDecrementAwake;

        motives[(int)Motive.Hygiene] -= asleep ? tuning.HygieneDecrementAsleep : tuning.HygieneDecrementAwake;
        motives[(int)Motive.Bladder] -= asleep ? tuning.BladderDecrementAsleep : tuning.BladderDecrementAwake;

        var clampLen = Math.Min(motives.Length, savedValues7CC.Length);
        for (var index = 0; index < motives.Length; index++)
        {
            var value = float.IsFinite(motives[index]) ? motives[index] : 0f;
            motives[index] = Math.Clamp(value, -100f, 100f);
            if (index < clampLen)
                savedValues7CC[index] = motives[index];
        }

        ApplyMood(motives, personData);
    }

    private void ApplyMood(float[] motives, short[] personData)
    {
        var curves = personData[(int)PersonData.PersonAge] is > 0 and <= 17 ? ChildMoodCurves : AdultMoodCurves;
        if (curves.Count != MoodMotiveIndices.Length) return;

        var gender = personData[(int)PersonData.Gender];
        var numerator = 0f;
        var denominator = 0f;
        for (var curveIndex = 0; curveIndex < MoodMotiveIndices.Length; curveIndex++)
        {
            var value = motives[MoodMotiveIndices[curveIndex]];
            // Excludes the default Room motive (13) for pets, but includes it for Sims.
            if (gender >= 2 && value == 13f) continue;
            var weight = curves[curveIndex].Evaluate(value);
            numerator += weight * value;
            denominator += weight;
        }
        if (denominator > 0f && float.IsFinite(numerator / denominator))
            motives[(int)Motive.Mood] = Math.Clamp(numerator / denominator, -100f, 100f);
    }
}
