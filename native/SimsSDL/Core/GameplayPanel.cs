using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public static class GroundTileExtensions
{
    public static GroundTile ToGroundTile(this LotVertex vertex) => new(vertex.X, vertex.Y);
}

public sealed record GameplayPanelState
{
    public sealed record Career
    {
        [JsonPropertyName("type")]
        public required int Type { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }

        [JsonPropertyName("performance")]
        public required int Performance { get; init; }

        [JsonPropertyName("age")]
        public required int Age { get; init; }

        [JsonPropertyName("atWorkSchool")]
        public required int AtWorkSchool { get; init; }

        [JsonPropertyName("school")]
        public bool? School { get; init; }

        [JsonPropertyName("track")]
        public string? Track { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("jobDescription")]
        public string? JobDescription { get; init; }

        [JsonPropertyName("salary")]
        public int? Salary { get; init; }

        [JsonPropertyName("startHour")]
        public int? StartHour { get; init; }

        [JsonPropertyName("endHour")]
        public int? EndHour { get; init; }

        [JsonPropertyName("currentLevelRequirements")]
        public IReadOnlyList<int>? CurrentLevelRequirements { get; init; }

        [JsonPropertyName("nextLevelRequirements")]
        public IReadOnlyList<int>? NextLevelRequirements { get; init; }

        [JsonPropertyName("performanceLabel")]
        public string? PerformanceLabel { get; init; }

        /// <summary>Report card grade looked up from <c>work.iff</c> string table 0x1001 using the
        /// one-based index of job performance.</summary>
        [JsonPropertyName("reportCardGrade")]
        public string? ReportCardGrade { get; init; }

        [JsonPropertyName("familyFriends")]
        public int? FamilyFriends { get; init; }

        [JsonConstructor]
        public Career() { }

        [SetsRequiredMembers]
        public Career(
            int type,
            int level,
            int performance,
            int age,
            int atWorkSchool,
            bool? school = null,
            string? track = null,
            string? title = null,
            string? jobDescription = null,
            int? salary = null,
            int? startHour = null,
            int? endHour = null,
            IReadOnlyList<int>? currentLevelRequirements = null,
            IReadOnlyList<int>? nextLevelRequirements = null,
            string? performanceLabel = null,
            string? reportCardGrade = null,
            int? familyFriends = null)
        {
            Type = type;
            Level = level;
            Performance = performance;
            Age = age;
            AtWorkSchool = atWorkSchool;
            School = school;
            Track = track;
            Title = title;
            JobDescription = jobDescription;
            Salary = salary;
            StartHour = startHour;
            EndHour = endHour;
            CurrentLevelRequirements = currentLevelRequirements;
            NextLevelRequirements = nextLevelRequirements;
            PerformanceLabel = performanceLabel;
            ReportCardGrade = reportCardGrade;
            FamilyFriends = familyFriends;
        }

        [JsonIgnore]
        public bool IsSchool => School ?? (Age < 18);

        [JsonIgnore]
        public bool HasJob => IsSchool || (Type != 0 && !string.IsNullOrEmpty(Title));

        [JsonIgnore]
        public int? FriendsNeeded =>
            NextLevelRequirements is { Count: > 0 } requirements && requirements[0] > (FamilyFriends ?? 0)
                ? requirements[0]
                : null;

        public void Validate()
        {
            if (!IsInt16Range(Type) || !IsInt16Range(Level) || !IsInt16Range(Performance) ||
                !IsInt16Range(Age) || !IsInt16Range(AtWorkSchool) ||
                (Track is not null && Track.Length > 512) ||
                (Title is not null && Title.Length > 512) ||
                (JobDescription is not null && JobDescription.Length > 512) ||
                (PerformanceLabel is not null && PerformanceLabel.Length > 512) ||
                (ReportCardGrade is not null && ReportCardGrade.Length > 512) ||
                Salary is < 0 ||
                FamilyFriends is < 0 ||
                StartHour is < 0 or > 24 ||
                EndHour is < 0 or > 24)
            {
                throw new ContentError("Invalid career data");
            }

            if ((CurrentLevelRequirements is not null && CurrentLevelRequirements.Count != 10) ||
                (NextLevelRequirements is not null && NextLevelRequirements.Count != 10))
            {
                throw new ContentError("Invalid career requirements");
            }
        }
    }

    public sealed record Fame
    {
        [JsonPropertyName("score")]
        public required int Score { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("skills")]
        public required IReadOnlyList<int> Skills { get; init; }

        [JsonPropertyName("skillRequirements")]
        public required IReadOnlyList<int> SkillRequirements { get; init; }

        [JsonPropertyName("friendStarPower")]
        public required int FriendStarPower { get; init; }

        [JsonPropertyName("friendsNeeded")]
        public required int FriendsNeeded { get; init; }

        [JsonPropertyName("nextScore")]
        public required int NextScore { get; init; }

        [JsonConstructor]
        public Fame() { }

        [SetsRequiredMembers]
        public Fame(
            int score,
            int level,
            string title,
            IReadOnlyList<int> skills,
            IReadOnlyList<int> skillRequirements,
            int friendStarPower,
            int friendsNeeded,
            int nextScore)
        {
            Score = score;
            Level = level;
            Title = title;
            Skills = skills;
            SkillRequirements = skillRequirements;
            FriendStarPower = friendStarPower;
            FriendsNeeded = friendsNeeded;
            NextScore = nextScore;
        }

        public void Validate()
        {
            if (Score is < 0 or > 1000 ||
                Level is < 0 or > 10 ||
                Title.Length > 512 ||
                Skills.Count != 6 ||
                SkillRequirements.Count != 6 ||
                FriendStarPower is < 0 or > 32767 ||
                FriendsNeeded is < 0 or > 32767 ||
                NextScore is < 0 or > 1000)
            {
                throw new ContentError("Invalid fame data");
            }

            foreach (int skill in Skills)
            {
                if (skill is < 0 or > 32767)
                    throw new ContentError("Invalid fame data");
            }

            foreach (int req in SkillRequirements)
            {
                if (req is < 0 or > 10)
                    throw new ContentError("Invalid fame data");
            }
        }
    }

    public sealed record Image
    {
        [JsonPropertyName("file")]
        public required string File { get; init; }

        [JsonPropertyName("columns")]
        public required int Columns { get; init; }

        [JsonPropertyName("rows")]
        public required int Rows { get; init; }

        [JsonConstructor]
        public Image() { }

        [SetsRequiredMembers]
        public Image(string file, int columns, int rows)
        {
            File = file;
            Columns = columns;
            Rows = rows;
        }

        public void Validate()
        {
            if (!File.StartsWith("ui-", StringComparison.Ordinal) ||
                !File.EndsWith(".bmp", StringComparison.Ordinal) ||
                File.Length != 71 ||
                Columns is < 1 or > 5 ||
                Rows is < 1 or > 4)
            {
                throw new ContentError("Invalid gameplay image");
            }

            ReadOnlySpan<char> hash = File.AsSpan(3, 64);
            foreach (char c in hash)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    throw new ContentError("Invalid gameplay image");
            }
        }
    }

    public sealed record HousePanel
    {
        [JsonPropertyName("squareFeet")]
        public int? SquareFeet { get; init; }

        [JsonPropertyName("bedrooms")]
        public int? Bedrooms { get; init; }

        [JsonPropertyName("bathrooms")]
        public int? Bathrooms { get; init; }

        [JsonPropertyName("lotSize")]
        public int? LotSize { get; init; }

        [JsonPropertyName("sizeScore")]
        public int? SizeScore { get; init; }

        [JsonPropertyName("furnishingsScore")]
        public int? FurnishingsScore { get; init; }

        [JsonPropertyName("yardScore")]
        public int? YardScore { get; init; }

        [JsonPropertyName("upkeepScore")]
        public int? UpkeepScore { get; init; }

        [JsonPropertyName("layoutScore")]
        public int? LayoutScore { get; init; }
    }

    public sealed record InventoryItem
    {
        [JsonPropertyName("type")]
        public required int Type { get; init; }

        [JsonPropertyName("guid")]
        public required uint Guid { get; init; }

        [JsonPropertyName("count")]
        public required int Count { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("icon")]
        public Image? Icon { get; init; }

        [JsonConstructor]
        public InventoryItem() { }

        [SetsRequiredMembers]
        public InventoryItem(int type, uint guid, int count, string? name = null, string? description = null, Image? icon = null)
        {
            Type = type;
            Guid = guid;
            Count = count;
            Name = name;
            Description = description;
            Icon = icon;
        }
    }

    public sealed record Relationship
    {
        public sealed record LivePosition
        {
            [JsonPropertyName("x")]
            public required float X { get; init; }

            [JsonPropertyName("y")]
            public required float Y { get; init; }

            [JsonPropertyName("level")]
            public required int Level { get; init; }

            [JsonConstructor]
            public LivePosition() { }

            [SetsRequiredMembers]
            public LivePosition(float x, float y, int level)
            {
                X = x;
                Y = y;
                Level = level;
            }
        }

        [JsonPropertyName("neighborID")]
        public required int NeighborId { get; init; }

        [JsonPropertyName("guid")]
        public required uint Guid { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("zodiac")]
        public int? Zodiac { get; init; }

        [JsonPropertyName("personality")]
        public IReadOnlyDictionary<string, int>? Personality { get; init; }

        [JsonPropertyName("values")]
        public required IReadOnlyList<int> Values { get; init; }

        [JsonPropertyName("household")]
        public required bool Household { get; init; }

        [JsonPropertyName("portrait")]
        public Image? Portrait { get; init; }

        [JsonPropertyName("reverseDaily")]
        public int? ReverseDaily { get; init; }

        [JsonPropertyName("famous")]
        public bool? Famous { get; init; }

        [JsonPropertyName("mutualFriend")]
        public bool? MutualFriend { get; init; }

        [JsonPropertyName("livePosition")]
        public LivePosition? Position { get; init; }

        [JsonConstructor]
        public Relationship() { }

        [SetsRequiredMembers]
        public Relationship(
            int neighborId,
            uint guid,
            string name,
            IReadOnlyList<int> values,
            bool household,
            string? description = null,
            int? zodiac = null,
            IReadOnlyDictionary<string, int>? personality = null,
            Image? portrait = null,
            int? reverseDaily = null,
            bool? famous = null,
            bool? mutualFriend = null,
            LivePosition? position = null)
        {
            NeighborId = neighborId;
            Guid = guid;
            Name = name;
            Description = description;
            Zodiac = zodiac;
            Personality = personality;
            Values = values;
            Household = household;
            Portrait = portrait;
            ReverseDaily = reverseDaily;
            Famous = famous;
            MutualFriend = mutualFriend;
            Position = position;
        }

        [JsonIgnore]
        public int Daily => Values.Count > 0 ? Values[0] : 0;

        [JsonIgnore]
        public int Lifetime => Values.Count > 2 ? Values[2] : 0;

        [JsonIgnore]
        public bool Friend => MutualFriend ?? (Daily >= 50 && (ReverseDaily ?? 0) >= 50);

        [JsonIgnore]
        public bool Crush => Values.Count > 1 && Values[1] != 0;

        [JsonIgnore]
        public bool Love => Values.Count > 3 && Values[3] != 0;

        public void Validate()
        {
            if (NeighborId is < 1 or > 32767 ||
                Name.Length > 512 ||
                Values.Count > 1024 ||
                (ReverseDaily is { } rev && !IsInt16Range(rev)))
            {
                throw new ContentError("Invalid relationship data");
            }

            foreach (int value in Values)
            {
                if (!IsInt16Range(value))
                    throw new ContentError("Invalid relationship data");
            }

            Portrait?.Validate();
        }
    }

    public sealed record Action
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("token")]
        public required string Token { get; init; }

        [JsonPropertyName("active")]
        public required bool Active { get; init; }

        [JsonPropertyName("visible")]
        public required bool Visible { get; init; }

        [JsonPropertyName("cancellable")]
        public required bool Cancellable { get; init; }

        [JsonPropertyName("cancelRequested")]
        public required bool CancelRequested { get; init; }

        [JsonPropertyName("icon")]
        public Image? Icon { get; init; }

        [JsonConstructor]
        public Action() { }

        [SetsRequiredMembers]
        public Action(
            string token,
            bool active,
            bool visible,
            bool cancellable,
            bool cancelRequested,
            string? name = null,
            Image? icon = null)
        {
            Name = name;
            Token = token;
            Active = active;
            Visible = visible;
            Cancellable = cancellable;
            CancelRequested = cancelRequested;
            Icon = icon;
        }
    }

    public sealed record Sim
    {
        [JsonPropertyName("id")]
        public required int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("internalName")]
        public string? InternalName { get; init; }

        [JsonPropertyName("tooltipName")]
        public string? TooltipName { get; init; }

        [JsonPropertyName("household")]
        public required bool Household { get; init; }

        [JsonPropertyName("portrait")]
        public Image? Portrait { get; init; }

        [JsonPropertyName("pieHead")]
        public Image? PieHead { get; init; }

        [JsonPropertyName("mood")]
        public required int Mood { get; init; }

        [JsonPropertyName("motives")]
        public required IReadOnlyDictionary<string, int> Motives { get; init; }

        [JsonPropertyName("actions")]
        public required IReadOnlyList<Action> Actions { get; init; }

        [JsonPropertyName("personality")]
        public IReadOnlyDictionary<string, int>? Personality { get; init; }

        [JsonPropertyName("personType")]
        public int? PersonType { get; init; }

        /// <summary>PersonData gender flags, where bits 0x2 and 0x4 encode dog and cat species
        /// respectively.</summary>
        [JsonPropertyName("gender")]
        public int? Gender { get; init; }

        [JsonPropertyName("zodiac")]
        public int? Zodiac { get; init; }

        [JsonPropertyName("skills")]
        public IReadOnlyDictionary<string, int>? Skills { get; init; }

        /// <summary>Pet skill aliases that repurpose the shared PersonData skill words.</summary>
        [JsonPropertyName("petSkills")]
        public IReadOnlyDictionary<string, int>? PetSkills { get; init; }

        [JsonPropertyName("career")]
        public GameplayPanelState.Career? Career { get; init; }

        [JsonPropertyName("fame")]
        public GameplayPanelState.Fame? Fame { get; init; }

        [JsonPropertyName("interests")]
        public IReadOnlyList<int>? Interests { get; init; }

        [JsonPropertyName("relationships")]
        public IReadOnlyList<Relationship>? Relationships { get; init; }

        [JsonPropertyName("inventory")]
        public IReadOnlyList<InventoryItem>? Inventory { get; init; }

        [JsonConstructor]
        public Sim() { }

        [SetsRequiredMembers]
        public Sim(
            int id,
            string name,
            bool household,
            int mood,
            IReadOnlyDictionary<string, int> motives,
            IReadOnlyList<Action> actions,
            string? internalName = null,
            string? tooltipName = null,
            Image? portrait = null,
            Image? pieHead = null,
            IReadOnlyDictionary<string, int>? personality = null,
            int? personType = null,
            int? gender = null,
            int? zodiac = null,
            IReadOnlyDictionary<string, int>? skills = null,
            IReadOnlyDictionary<string, int>? petSkills = null,
            GameplayPanelState.Career? career = null,
            GameplayPanelState.Fame? fame = null,
            IReadOnlyList<int>? interests = null,
            IReadOnlyList<Relationship>? relationships = null,
            IReadOnlyList<InventoryItem>? inventory = null)
        {
            Id = id;
            Name = name;
            InternalName = internalName;
            TooltipName = tooltipName;
            Household = household;
            Portrait = portrait;
            PieHead = pieHead;
            Mood = mood;
            Motives = motives;
            Actions = actions;
            Personality = personality;
            PersonType = personType;
            Gender = gender;
            Zodiac = zodiac;
            Skills = skills;
            PetSkills = petSkills;
            Career = career;
            Fame = fame;
            Interests = interests;
            Relationships = relationships;
            Inventory = inventory;
        }

        [JsonIgnore]
        public bool HasHumanInterests =>
            Interests is { Count: var count } &&
            count == OriginalGameplayLayout.Interests.Count &&
            PersonType is >= 0 and <= 1 &&
            ((Gender ?? 0) & 6) == 0 &&
            Career is not null;

        [JsonIgnore]
        public bool HasHumanCareer =>
            Skills is not null &&
            Career is not null &&
            PersonType is >= 0 and <= 1 &&
            ((Gender ?? 0) & 6) == 0 &&
            KeysMatch(Skills, OriginalGameplayLayout.Skills) &&
            Career.Age >= 0;

        [JsonIgnore]
        public bool HasPetSkills =>
            Gender is { } gender &&
            (gender & 6) != 0 &&
            PetSkills is not null &&
            KeysMatch(PetSkills, OriginalGameplayLayout.PetSkills);

        [JsonIgnore]
        public bool HasHumanPersonality =>
            Personality is not null &&
            PersonType is >= 0 and <= 1 &&
            KeysMatch(Personality, OriginalGameplayLayout.Traits);
    }

    [JsonPropertyName("selectedSim")]
    public required int SelectedSim { get; init; }

    [JsonPropertyName("hours")]
    public required int Hours { get; init; }

    [JsonPropertyName("minutes")]
    public required int Minutes { get; init; }

    [JsonPropertyName("budget")]
    public int? Budget { get; init; }

    [JsonPropertyName("familyFriends")]
    public int? FamilyFriends { get; init; }

    [JsonPropertyName("sims")]
    public required IReadOnlyList<Sim> Sims { get; init; }

    [JsonPropertyName("canUndo")]
    public bool? CanUndo { get; init; }

    [JsonPropertyName("canRedo")]
    public bool? CanRedo { get; init; }

    [JsonPropertyName("housePanel")]
    public HousePanel? House { get; init; }

    [JsonConstructor]
    public GameplayPanelState() { }

    [SetsRequiredMembers]
    public GameplayPanelState(
        int selectedSim,
        int hours,
        int minutes,
        IReadOnlyList<Sim> sims,
        int? budget = null,
        int? familyFriends = null,
        bool? canUndo = null,
        bool? canRedo = null,
        HousePanel? house = null)
    {
        SelectedSim = selectedSim;
        Hours = hours;
        Minutes = minutes;
        Budget = budget;
        FamilyFriends = familyFriends;
        Sims = sims;
        CanUndo = canUndo;
        CanRedo = canRedo;
        House = house;
    }

    [JsonIgnore]
    public IReadOnlyList<Sim> Household => Sims.Where(s => s.Household).ToArray();

    [JsonIgnore]
    public Sim? Selected => Sims.FirstOrDefault(s => s.Household && s.Id == SelectedSim);

    public void Validate()
    {
        if (Sims.Count > 128 ||
            Household.Count > 8 ||
            Sims.Select(s => s.Id).Distinct().Count() != Sims.Count ||
            Hours is < 0 or >= 24 ||
            Minutes is < 0 or >= 60 ||
            (SelectedSim != 0 && Selected is null))
        {
            throw new ContentError("Invalid gameplay panel state");
        }

        if (House is { } housePanel)
        {
            if (housePanel.SquareFeet is < 0 ||
                housePanel.Bedrooms is < 0 or > 32767 ||
                housePanel.Bathrooms is < 0 or > 32767 ||
                housePanel.LotSize is < 0 or > 2 ||
                housePanel.SizeScore is < 0 or > 100 ||
                housePanel.FurnishingsScore is < 0 or > 100 ||
                housePanel.YardScore is < 0 or > 100 ||
                housePanel.UpkeepScore is < 0 or > 100 ||
                housePanel.LayoutScore is < 0 or > 100)
            {
                throw new ContentError("Invalid House panel state");
            }
        }

        foreach (Sim sim in Sims)
        {
            if (sim.Id is < 1 or > 32767 ||
                sim.Name.Length > 512 ||
                sim.Actions.Count > 128 ||
                sim.Actions.Select(a => a.Token).Distinct(StringComparer.Ordinal).Count() != sim.Actions.Count ||
                sim.Motives.Count > 16 ||
                !sim.Motives.Values.All(IsInt16Range))
            {
                throw new ContentError("Invalid Sim panel state");
            }

            sim.Portrait?.Validate();
            sim.PieHead?.Validate();
            sim.Career?.Validate();
            sim.Fame?.Validate();

            if (sim.Relationships is { } relationships)
            {
                if (relationships.Count > 32767 ||
                    relationships.Select(r => r.NeighborId).Distinct().Count() != relationships.Count)
                {
                    throw new ContentError("Invalid relationship identities");
                }
                foreach (Relationship relationship in relationships)
                    relationship.Validate();
            }

            if (sim.Inventory is { } inventory)
            {
                if (inventory.Count > 4096 ||
                    !inventory.All(item => item.Count is >= 0 and <= 32767 && (item.Name?.Length ?? 0) <= 512))
                {
                    throw new ContentError("Invalid inventory data");
                }
                foreach (InventoryItem item in inventory)
                {
                    if ((item.Name?.Length ?? 0) > 512 || (item.Description?.Length ?? 0) > 4096)
                        throw new ContentError("Invalid inventory text");
                    item.Icon?.Validate();
                }
            }

            if (sim.Interests is { } interests)
            {
                if (interests.Count != OriginalGameplayLayout.Interests.Count || !interests.All(IsInt16Range))
                    throw new ContentError("Invalid interest data");
            }

            if (sim.Skills is { } skills)
            {
                if (!KeysMatch(skills, OriginalGameplayLayout.Skills) || !skills.Values.All(IsInt16Range))
                    throw new ContentError("Invalid skill data");
            }

            if (sim.PetSkills is { } petSkills)
            {
                if (!KeysMatch(petSkills, OriginalGameplayLayout.PetSkills) || !petSkills.Values.All(IsInt16Range))
                    throw new ContentError("Invalid pet skill data");
            }

            if (sim.Personality is { } personality)
            {
                if (!KeysMatch(personality, OriginalGameplayLayout.Traits) || !personality.Values.All(IsInt16Range))
                    throw new ContentError("Invalid personality data");
            }

            if ((sim.PersonType is { } pt && !IsInt16Range(pt)) ||
                (sim.Gender is { } g && !IsInt16Range(g)) ||
                (sim.Zodiac is { } z && !IsInt16Range(z)))
            {
                throw new ContentError("Invalid Sim type, gender, or zodiac");
            }

            foreach (Action action in sim.Actions)
            {
                if (string.IsNullOrEmpty(action.Token) ||
                    action.Token.Length > 64 ||
                    (action.Name?.Length ?? 0) > 512)
                {
                    throw new ContentError("Invalid queued action");
                }
                action.Icon?.Validate();
            }
        }
    }

    private static bool IsInt16Range(int value) => value is >= -32768 and <= 32767;

    private static bool KeysMatch(IReadOnlyDictionary<string, int> dict, IReadOnlyList<string> expected)
    {
        if (dict.Count != expected.Count)
            return false;
        foreach (string key in expected)
        {
            if (!dict.ContainsKey(key))
                return false;
        }
        return true;
    }
}

public static partial class OriginalGameplayLayout
{
    public static CGRect FamilyFriendsButton(CGRect funds, double fontHeight, double captionWidth) =>
        new(funds.MinX - captionWidth, funds.MinY, captionWidth, fontHeight);

    public readonly record struct PanelGrid
    {
        public double Width { get; }
        public int RelationshipCapacity { get; }
        public int InterestColumns { get; }
        public int InterestCapacity => InterestColumns * 3;

        public PanelGrid(double width)
        {
            Width = double.IsFinite(width) ? Math.Max(800.0, Math.Min(16384.0, Math.Floor(width))) : 800.0;
            RelationshipCapacity = Math.Max(1, (int)((Width - 566.0) / 45.0));
            InterestColumns = Math.Max(1, (int)((Width - 544.0) / 128.0));
        }

        public CGRect Interest(int index) =>
            new(533 + index / 3 * 128, 68 + index % 3 * 27, 125, 25);

        public CGRect RelationshipArrow(bool next) =>
            new(next ? Width - 11 : 544, 82, 9, 49);

        public CGRect InterestArrow(bool next) =>
            new(next ? Width - 11 : 522, 83, 9, 49);

        public int PageCount(int items, int capacity) =>
            Math.Max(1, (Math.Max(0, items) + capacity - 1) / capacity);

        public int ResizedPage(int page, int oldCapacity, int newCapacity, int items) =>
            Math.Min(Math.Max(0, page) * oldCapacity / newCapacity, PageCount(items, newCapacity) - 1);
    }

    public enum RelationshipFilter
    {
        Family,
        Friends,
        Famous,
        All,
    }

    public static IReadOnlyList<GameplayPanelState.Relationship> Relationships(
        IEnumerable<GameplayPanelState.Relationship> values,
        RelationshipFilter filter)
    {
        return values
            .Where(value => value.Values.Count > 0 && filter switch
            {
                RelationshipFilter.Family => value.Household,
                RelationshipFilter.Friends => value.Friend,
                RelationshipFilter.Famous => value.Famous == true,
                RelationshipFilter.All => true,
                _ => false,
            })
            .OrderByDescending(r => r.Household)
            .ThenByDescending(r => r.Position is not null)
            .ThenByDescending(r => r.Daily)
            .ThenBy(r => r.NeighborId)
            .ToArray();
    }

    public enum InventorySort
    {
        Magic,
        Other,
        Gift,
    }

    public static bool Includes(this InventorySort sort, int type) => sort switch
    {
        InventorySort.Magic => type == 7,
        InventorySort.Other => type == 8,
        InventorySort.Gift => type is 0 or 4 or 5,
        _ => false,
    };

    public static IReadOnlyList<GameplayPanelState.InventoryItem> InventoryItems(
        IEnumerable<GameplayPanelState.InventoryItem> values,
        InventorySort sort)
    {
        // Duplicate GUIDs overwrite quantity with the last entry rather than summing.
        var byGuid = new Dictionary<uint, GameplayPanelState.InventoryItem>();
        foreach (var item in values)
        {
            if (sort.Includes(item.Type))
                byGuid[item.Guid] = item;
        }
        // Inventory items are sorted by GUID interpreted as a signed integer.
        return byGuid.Values.OrderBy(item => unchecked((int)item.Guid)).ToArray();
    }

    public static bool UsesAsianInventoryLayout(int languageCode) =>
        languageCode is 15 or 17 or 18 or 19 or 20;

    public static CGRect InventorySortButton(int index, bool asian = false) =>
        new(522, (asian ? 53 : 83) + index * 21, 20, 20);

    public static CGPoint InventoryTitle(bool asian = false) =>
        asian ? new CGPoint(530, 5) : new CGPoint(525, 50);

    public static readonly CGPoint InventoryButton = new(457, 116);

    /// <summary>Calculates inventory tile capacity: 5 slots below 1024px, 9 slots at 1024px, plus one slot
    /// per additional 56px.</summary>
    public static int InventoryCapacity(double width) =>
        width < 1024 ? 5 : 9 + (int)((Math.Min(width, 16384.0) - 1024.0) / 56.0);

    public static readonly CGSize InventoryTileSize = new(42, 60);

    public static CGRect InventoryTile(int index, bool asian = false) =>
        new(565 + index * 42, asian ? 29 : 79, 42, 60);

    public static CGRect InventoryArrow(bool next, double width, bool asian = false) =>
        new(next ? width - 11 : 554, asian ? 34 : 84, 9, 49);

    public static CGRect Relationship(int index) =>
        new(555 + index * 45, 68, 45, 90);

    public static CGRect RelationshipFilterRect(int index) =>
        new(index == 0 ? 523 : 522, 64 + index * 21, 20, 20);

    public static int RelationshipWidth(int value) =>
        (Math.Min(100, Math.Max(-100, value)) + 100) / 5;

    public static int RelationshipExpression(int value) =>
        Math.Min(4, Math.Max(0, (value + 100) / 40));

    public static readonly IReadOnlyList<string> Interests =
    [
        "Travel", "Violence", "Politics", "The60s", "Weather", "Sports", "Music", "Outdoors",
        "Toys", "Aliens", "Pets", "School", "Exercise", "Food", "Parties", "Style", "Charisma", "Health", "Romance",
    ];

    public static IReadOnlyList<int> InterestIndices(int age) =>
        age is > 0 and < 18
            ? [8, 9, 10, 11, 4, 5, 6, 7]
            : [0, 1, 2, 3, 4, 5, 6, 7, 12, 13, 14, 15, 16, 17, 18];

    public static readonly IReadOnlyList<string> Skills =
        ["Cooking", "Mechanical", "Charisma", "Body", "Logic", "Creativity"];

    public static readonly IReadOnlyList<string> PetSkills =
        ["HouseBroken", "Obedience", "Tricks"];

    public static CGRect PetSkillBar(int index) =>
        new(727, 77 + index * 15, 40, 11);

    public static CGRect PetSkillLabel(int index) =>
        new(623, 75 + index * 15, 102, 16);

    /// <summary>Calculates pet skill bar width by dividing the stored skill value by 100, clamping to 0–10,
    /// and rendering 4 pixels per point.</summary>
    public static int PetSkillWidth(int value) =>
        Math.Min(40, Math.Max(0, value / 100 * 4));

    public static CGRect HouseBar(int index)
    {
        int[] ys = [56, 74, 92, 110, 129];
        return new CGRect(677, ys[Math.Min(Math.Max(0, index), 4)], 40, 16);
    }

    public static double HouseScoreLabelY(int index)
    {
        double[] ys = [57, 75, 93, 111, 129];
        return ys[Math.Min(Math.Max(0, index), 4)];
    }

    public static double HouseInfoLabelY(int index)
    {
        double[] ys = [68, 88, 108, 128];
        return ys[Math.Min(Math.Max(0, index), 3)];
    }

    public static CGRect SkillBar(int index) =>
        new(677, 57 + index * 15, 40, 11);

    public static CGRect SkillLabel(int index) =>
        new(613, 56 + index * 15, 62, 16);

    public static CGRect FameSkillBar(int index) =>
        new(730, 57 + index * 15, 40, 11);

    public static CGRect FameSkillLabel(int index) =>
        new(665, 56 + index * 15, 62, 16);

    public static readonly CGRect FameStarBar = new(523, 80, 80, 16);
    public static readonly CGRect FameTitleLabel = new(523, 95, 102, 16);
    public static readonly CGRect FameFriendsButton = new(523, 125, 120, 15);

    public static int? RequiredSkillWidth(int index, int value, GameplayPanelState.Career career)
    {
        if (career.NextLevelRequirements is not { Count: 10 } requirements ||
            index < 0 || index >= Skills.Count ||
            requirements[index + 1] <= value)
        {
            return null;
        }
        return PersonalityWidth(requirements[index + 1]);
    }

    public static readonly IReadOnlyList<string> Traits =
        ["Neat", "Outgoing", "Active", "Playful", "Nice"];

    public static readonly IReadOnlyList<string> Signs =
    [
        "Aries", "Taurus", "Gemini", "Cancer", "Leo", "Virgo",
        "Libra", "Scorpio", "Sagittarius", "Capricorn", "Aquarius", "Pisces",
    ];

    private static readonly string[] ZodiacTraitOrder = ["Outgoing", "Nice", "Playful", "Neat", "Active"];

    private static readonly float[][] ZodiacProfiles =
    [
        [8, 3, 3, 5, 6], [5, 4, 8, 5, 3], [7, 3, 3, 4, 8], [3, 6, 4, 6, 6],
        [10, 3, 4, 4, 4], [2, 5, 3, 9, 6], [8, 7, 6, 2, 2], [5, 3, 3, 6, 8],
        [3, 4, 7, 2, 9], [4, 5, 8, 7, 1], [4, 6, 7, 4, 4], [3, 7, 3, 5, 7],
    ];

    public static int Zodiac(IReadOnlyDictionary<string, int> personality, int stored)
    {
        if (stored != 0)
            return Math.Min(12, Math.Max(1, stored));

        int nearest = 1;
        float distance = 1e38f;
        for (int index = 0; index < ZodiacProfiles.Length; index++)
        {
            float[] profile = ZodiacProfiles[index];
            float squared = 0;
            for (int t = 0; t < ZodiacTraitOrder.Length; t++)
            {
                float val = (personality.TryGetValue(ZodiacTraitOrder[t], out int raw) ? raw : 0) * 0.009999999776482582f;
                float delta = val - profile[t];
                squared += delta * delta;
            }
            if (squared < distance)
            {
                nearest = index + 1;
                distance = squared;
            }
        }
        return nearest;
    }

    /// <summary>Generates default personality trait scores for a zodiac sign, mapped to Outgoing, Nice,
    /// Playful, Neat, and Active scaled by 100.</summary>
    public static Dictionary<string, int>? ZodiacPersonality(int sign)
    {
        if (sign is < 1 or > 12)
            return null;

        float[] profile = ZodiacProfiles[sign - 1];
        var result = new Dictionary<string, int>(ZodiacTraitOrder.Length, StringComparer.Ordinal);
        for (int i = 0; i < ZodiacTraitOrder.Length; i++)
        {
            result[ZodiacTraitOrder[i]] = (int)Math.Truncate(profile[i] * 100.0 + 0.5);
        }
        return result;
    }

    public static int PersonalityWidth(int value) =>
        Math.Min(10, Math.Max(0, value / 100)) * 4;

    public static CGRect Personality(int index, int labelWidth) =>
        new(520 + 202 - (labelWidth + 49), 58 + index * 18, labelWidth + 49, 14);

    public static readonly IReadOnlyList<string> Motives =
        ["Hunger", "Energy", "Comfort", "Fun", "Hygiene", "Social", "Bladder", "Room"];

    public static CGRect Portrait(int index) =>
        new(229 + (index % 4) * 45, 55 + (index / 4) * 45, 45, 45);

    public static CGRect Need(int index) =>
        new(520 + (index % 2) * 100, 71 + (index / 2) * 19, 100, 20);

    public static CGRect MoodRatingSegment(bool positive) =>
        new(425, positive ? 53 : 123, 27, 25);

    public static CGRect Action(int index) =>
        new(32 + index * 50, 32, 45, 45);

    public static int BarWidth(int value) =>
        (int)((Math.Min(100, Math.Max(-100, value)) + 100) * 0.3);

    public static ((byte X, byte Y, byte Z) Green, (byte X, byte Y, byte Z) Red) BarColors(int value)
    {
        float factor = MathF.Min(1f, MathF.Max(0f, (value + 50) * 0.01f));
        const float grayX = 64f;
        const float grayY = 93f;
        const float grayZ = 95f;

        static (byte X, byte Y, byte Z) Mix(float targetX, float targetY, float targetZ, float amount)
        {
            int weightByte = (int)((double)amount * 255.0 + 0.5);
            double weight = (double)(weightByte * (1.0f / 255.0f));

            byte Channel(float start, float end)
            {
                double blended = (double)start + (double)(end - start) * weight + 0.5;
                return (byte)Math.Clamp((int)blended, 0, 255);
            }

            return (Channel(grayX, targetX), Channel(grayY, targetY), Channel(grayZ, targetZ));
        }

        return (Mix(0f, 202f, 57f, factor), Mix(209f, 0f, 0f, 1f - factor));
    }
}
