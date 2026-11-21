using SimsEngine.Formats;

namespace SimsEngine.Content;

public enum ValidationMode
{
    /// <summary>Rejects malformed known chunks, but allows unknown or legacy metadata chunk types with a warning.</summary>
    Compatible,

    /// <summary>Rejects both malformed known chunks and unrecognized chunk types.</summary>
    Strict,
}

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record ChunkValidationIssue(
    ValidationSeverity Severity,
    string ChunkType,
    ushort ChunkId,
    string Message);

public sealed record IffValidationReport(
    string SourceName,
    bool IsValid,
    IReadOnlyList<ChunkValidationIssue> Issues,
    IReadOnlyList<uint> ObjectGuids,
    string? SemiGlobalName);

/// <summary>Validates an IFF file and its chunks using SimsEngine's format parsers so only valid game data
/// can enter a content-addressed chunk store.</summary>
public static class IffChunkValidator
{
    public static IffValidationReport Validate(ReadOnlyMemory<byte> iffBytes, string sourceName, ValidationMode mode = ValidationMode.Compatible)
    {
        IffFile file;
        try
        {
            file = new IffFile(iffBytes, sourceName);
        }
        catch (Exception ex) when (ex is ResourceFormatException or ArgumentException or IndexOutOfRangeException)
        {
            return new IffValidationReport(
                sourceName,
                IsValid: false,
                Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "IFF ", 0, ex.Message)],
                ObjectGuids: [],
                SemiGlobalName: null);
        }

        return Validate(file, mode);
    }

    public static IffValidationReport Validate(IffFile file, ValidationMode mode = ValidationMode.Compatible)
    {
        var issues = new List<ChunkValidationIssue>();
        var guids = new List<uint>();
        string? semiGlobal = null;

        if (file.Chunks.Count == 0)
        {
            issues.Add(new ChunkValidationIssue(ValidationSeverity.Error, "IFF ", 0, "IFF contains no chunks."));
        }

        foreach (var chunk in file.Chunks)
        {
            var type = chunk.Type;
            try
            {
                if (type == ResourceType.Objd)
                {
                    var objd = Objd.Read(file, chunk);
                    if (objd.Guid != 0)
                        guids.Add(objd.Guid);
                }
                else if (type == ResourceType.Objf)
                {
                    _ = Objf.Read(file, chunk);
                }
                else if (type == ResourceType.Glob)
                {
                    var glob = Glob.Read(file, chunk);
                    if (!string.IsNullOrWhiteSpace(glob.Name))
                        semiGlobal ??= glob.Name;
                }
                else if (type == ResourceType.Bhav)
                {
                    _ = Bhav.Read(file, chunk);
                }
                else if (type == ResourceType.Bcon)
                {
                    _ = Bcon.Read(file, chunk);
                }
                else if (type == ResourceType.Str || type == ResourceType.Ctss || type == ResourceType.Ttas)
                {
                    _ = StringTable.Read(file, chunk);
                }
                else if (type == ResourceType.Ttab)
                {
                    _ = InteractionTable.Read(file, chunk);
                }
                else if (type == ResourceType.Slot)
                {
                    _ = SlotTable.Read(file, chunk);
                }
                else if (type == ResourceType.Dgrp)
                {
                    _ = DrawGroup.Read(file, chunk);
                }
                else if (type == ResourceType.Palt)
                {
                    _ = Palette.Read(file, chunk);
                }
                else if (type == ResourceType.Spr1)
                {
                    _ = Sprite.Read(file, chunk);
                }
                else if (type == ResourceType.Spr2)
                {
                    _ = DepthSprite.Read(file, chunk);
                }
                else if (type == ResourceType.Tprp)
                {
                    _ = TreeParameterNames.Read(file, chunk);
                }
                else if (type == ResourceType.Trcn)
                {
                    _ = ConstantLabels.Read(file, chunk);
                }
                else if (type == ResourceType.Fcns)
                {
                    _ = SimulatorConstants.Read(file, chunk);
                }
                else if (type == ResourceType.Rsmp)
                {
                    _ = ResourceMap.Read(chunk);
                }
                else if (type == ResourceType.Bmp)
                {
                    if (chunk.Data.Length < 14)
                        issues.Add(new ChunkValidationIssue(ValidationSeverity.Error, type.ToString(), chunk.Id, "BMP_ chunk is too small."));
                }
                else if (type == ResourceType.Fwav)
                {
                    if (chunk.Data.Length == 0)
                        issues.Add(new ChunkValidationIssue(ValidationSeverity.Warning, type.ToString(), chunk.Id, "FWAV chunk is empty."));
                }
                else
                {
                    var severity = mode == ValidationMode.Strict ? ValidationSeverity.Error : ValidationSeverity.Warning;
                    issues.Add(new ChunkValidationIssue(severity, type.ToString(), chunk.Id, $"Unrecognized chunk type '{type}'."));
                }
            }
            catch (Exception ex) when (ex is ResourceFormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                issues.Add(new ChunkValidationIssue(ValidationSeverity.Error, type.ToString(), chunk.Id, ex.Message));
            }
        }

        var isValid = issues.All(issue => issue.Severity != ValidationSeverity.Error);
        return new IffValidationReport(file.Source, isValid, issues, guids, semiGlobal);
    }
}
