namespace SimsEngine.Simulation;

/// <summary>Classifies the original PersonAge field. Sims.exe treats age zero as the adult sentinel and ages 1–17 as
/// children; ages above 17 are adults.</summary>
public static class PersonAgeCategory
{
    public static bool IsChild(short age) => age != 0 && age < 18;
    public static bool IsAdult(short age) => age == 0 || age >= 18;
}
