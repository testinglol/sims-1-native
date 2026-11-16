namespace SimsSDL.Core;

public sealed record class InstalledGameLanguage(int Code, string Name, ushort? WindowsLanguageID)
{
    public static InstalledGameLanguage EnglishUS { get; } = new(1, "English (US)", null);

    private static readonly (ushort WindowsID, int Code, string Name)[] SupportedLanguages =
    [
        (0x0409, 1, "English (US)"),
        (0x0809, 2, "English (UK)"),
        (0x040C, 3, "French"),
        (0x080C, 3, "French"),
        (0x100C, 3, "French"),
        (0x0407, 4, "German"),
        (0x0807, 4, "German"),
        (0x0C07, 4, "German"),
        (0x0410, 5, "Italian"),
        (0x0810, 5, "Italian"),
        (0x040A, 6, "Spanish"),
        (0x0C0A, 6, "Spanish"),
        (0x0413, 7, "Dutch"),
        (0x0813, 7, "Dutch"),
        (0x0406, 8, "Danish"),
        (0x041D, 9, "Swedish"),
        (0x0414, 10, "Norwegian"),
        (0x040B, 11, "Finnish"),
        (0x040D, 12, "Hebrew"),
        (0x0419, 13, "Russian"),
        (0x0816, 14, "Portuguese"),
        (0x0416, 14, "Portuguese"),
        (0x0411, 15, "Japanese"),
        (0x0415, 16, "Polish"),
        (0x0804, 17, "Simplified Chinese"),
        (0x1004, 17, "Simplified Chinese"),
        (0x0404, 18, "Traditional Chinese"),
        (0x0C04, 18, "Traditional Chinese"),
        (0x1404, 18, "Traditional Chinese"),
        (0x041E, 19, "Thai"),
        (0x0412, 20, "Korean"),
    ];

    public static InstalledGameLanguage Detect(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        try
        {
            string exePath = Path.Combine(gameRoot, "Sims.exe");
            if (!File.Exists(exePath))
                return EnglishUS;

            byte[] data = File.ReadAllBytes(exePath);
            return Parse(data) ?? EnglishUS;
        }
        catch
        {
            return EnglishUS;
        }
    }

    public static InstalledGameLanguage? Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        HashSet<ushort>? languageIDs = PEVersionLanguages.ParseResourceLanguageIDs(data);
        if (languageIDs is null)
            return null;

        foreach (var (windowsID, code, name) in SupportedLanguages)
        {
            if (languageIDs.Contains(windowsID))
                return new InstalledGameLanguage(code, name, windowsID);
        }

        return null;
    }

    private static class PEVersionLanguages
    {
        private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);
        private readonly record struct DirectoryEntry(ushort Id, bool Named, bool Directory, int Offset);

        public static HashSet<ushort>? ParseResourceLanguageIDs(byte[] data)
        {
            if (data.Length < 0x40 || data[0] != 0x4D || data[1] != 0x5A)
                return null;

            uint? peOffsetValue = U32(data, 0x3C);
            if (peOffsetValue is null || peOffsetValue.Value > int.MaxValue)
                return null;

            int peOffset = (int)peOffsetValue.Value;
            if (!Matches(data, peOffset, "PE\0\0"u8) ||
                U16(data, peOffset + 6) is not ushort sectionCount ||
                U16(data, peOffset + 20) is not ushort optionalSize ||
                !TryAdd(peOffset, 24, out int optionalStart) ||
                U16(data, optionalStart) is not ushort magic)
            {
                return null;
            }

            int directoriesOffset;
            int directoryCountOffset;
            if (magic == 0x10B)
            {
                if (!TryAdd(optionalStart, 96, out directoriesOffset) ||
                    !TryAdd(optionalStart, 92, out directoryCountOffset))
                {
                    return null;
                }
            }
            else if (magic == 0x20B)
            {
                if (!TryAdd(optionalStart, 112, out directoriesOffset) ||
                    !TryAdd(optionalStart, 108, out directoryCountOffset))
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            if (U32(data, directoryCountOffset) is not uint directoryCount ||
                directoryCount <= 2 ||
                !TryAdd(directoriesOffset, 24, out int dirEnd) ||
                !TryAdd(optionalStart, optionalSize, out int sectionTable) ||
                dirEnd > sectionTable ||
                U32(data, directoriesOffset + 16) is not uint resourceRva ||
                resourceRva == 0)
            {
                return null;
            }

            var sections = new List<Section>(sectionCount);
            for (int index = 0; index < sectionCount; index++)
            {
                if (!TryAdd(sectionTable, index * 40, out int header) ||
                    U32(data, header + 8) is not uint virtualSize ||
                    U32(data, header + 12) is not uint virtualAddress ||
                    U32(data, header + 16) is not uint rawSize ||
                    U32(data, header + 20) is not uint rawOffset)
                {
                    return null;
                }

                sections.Add(new Section(virtualAddress, virtualSize, rawOffset, rawSize));
            }

            int? rootOffset = RvaToOffset(resourceRva, sections, data.Length);
            if (rootOffset is null)
                return null;

            return VersionLanguages(data, rootOffset.Value, sections);
        }

        private static HashSet<ushort> VersionLanguages(byte[] data, int rootOffset, List<Section> sections)
        {
            List<DirectoryEntry>? types = DirectoryEntries(data, rootOffset);
            if (types is null)
                return [];

            DirectoryEntry? version = null;
            foreach (DirectoryEntry entry in types)
            {
                if (!entry.Named && entry.Id == 16 && entry.Directory)
                {
                    version = entry;
                    break;
                }
            }

            if (version is null || !TryAdd(rootOffset, version.Value.Offset, out int versionDirectory))
                return [];

            List<DirectoryEntry>? resources = DirectoryEntries(data, versionDirectory);
            if (resources is null)
                return [];

            var languageIDs = new HashSet<ushort>();
            foreach (DirectoryEntry resource in resources)
            {
                if (!resource.Directory || !TryAdd(rootOffset, resource.Offset, out int resourceOffset))
                    continue;

                List<DirectoryEntry>? languages = DirectoryEntries(data, resourceOffset);
                if (languages is null)
                    continue;

                foreach (DirectoryEntry language in languages)
                {
                    if (language.Named || language.Directory || !TryAdd(rootOffset, language.Offset, out int entry))
                        continue;

                    if (U32(data, entry) is not uint resourceDataRva ||
                        U32(data, entry + 4) is not uint resourceDataSize ||
                        resourceDataSize < 6 ||
                        RvaToOffset(resourceDataRva, sections, data.Length) is null)
                    {
                        continue;
                    }

                    languageIDs.Add(language.Id);
                }
            }

            return languageIDs;
        }

        private static List<DirectoryEntry>? DirectoryEntries(byte[] data, int offset)
        {
            if (U16(data, offset + 12) is not ushort namedCount ||
                U16(data, offset + 14) is not ushort idCount)
            {
                return null;
            }

            int count = namedCount + idCount;
            if (count > 4096 ||
                !TryAdd(offset, 16, out int entriesStart) ||
                !TryAdd(entriesStart, count * 8, out int end) ||
                end > data.Length)
            {
                return null;
            }

            var result = new List<DirectoryEntry>(count);
            for (int index = 0; index < count; index++)
            {
                int entry = entriesStart + index * 8;
                if (U32(data, entry) is not uint name || U32(data, entry + 4) is not uint target)
                    return null;

                bool named = (name & 0x8000_0000u) != 0;
                bool directory = (target & 0x8000_0000u) != 0;
                ushort rawId = unchecked((ushort)name);
                int child = (int)(target & 0x7FFF_FFFFu);
                result.Add(new DirectoryEntry(rawId, named, directory, child));
            }

            return result;
        }

        private static int? RvaToOffset(uint rva, List<Section> sections, int dataCount)
        {
            foreach (Section section in sections)
            {
                uint extent = Math.Max(section.VirtualSize, section.RawSize);
                if (rva < section.VirtualAddress || rva - section.VirtualAddress >= extent)
                    continue;

                uint delta = rva - section.VirtualAddress;
                if (delta >= section.RawSize)
                    return null;

                ulong raw = (ulong)section.RawOffset + delta;
                if (raw >= (ulong)dataCount)
                    return null;

                return (int)raw;
            }

            return null;
        }

        private static bool Matches(byte[] data, int offset, ReadOnlySpan<byte> bytes)
        {
            if (offset < 0 || !TryAdd(offset, bytes.Length, out int end) || end > data.Length)
                return false;
            return data.AsSpan(offset, bytes.Length).SequenceEqual(bytes);
        }

        private static ushort? U16(byte[] data, int offset)
        {
            if (offset < 0 || !TryAdd(offset, 2, out int end) || end > data.Length)
                return null;
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint? U32(byte[] data, int offset)
        {
            if (offset < 0 || !TryAdd(offset, 4, out int end) || end > data.Length)
                return null;
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        }

        private static bool TryAdd(int left, int right, out int value)
        {
            long sum = (long)left + right;
            if (sum is < 0 or > int.MaxValue)
            {
                value = 0;
                return false;
            }
            value = (int)sum;
            return true;
        }
    }
}
