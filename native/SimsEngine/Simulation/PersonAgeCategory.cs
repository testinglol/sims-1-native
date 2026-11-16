namespace SimsEngine.Simulation;

/// <summary>Classifies Sim age values: age 0 is the adult sentinel, ages 1–17 are children, and ages 18 and
/// older are adults.</summary>
public static class PersonAgeCategory
{
    public static bool IsChild(short age) => age != 0 && age < 18;
    public static bool IsAdult(short age) => age == 0 || age >= 18;
}
