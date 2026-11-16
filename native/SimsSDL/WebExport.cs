namespace SimsSDL;

public static class WebExport
{
    public static string Export(
        string templates,
        IReadOnlyList<NeighborhoodWebExport.Family> families,
        string destination,
        InstalledGameLanguage? language = null) =>
        NeighborhoodWebExport.Export(templates, families, destination, language);

    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        NeighborhoodWebExport.Fill(template, values);
}
