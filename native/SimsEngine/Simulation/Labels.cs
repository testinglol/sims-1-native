namespace SimsEngine.Simulation;

// Generated from the label tables in Behavior.iff; each member's comment is the label as the file spells it.

/// <summary>The primitive opcodes, named as Behavior.iff STR# 139 names them.</summary>
public enum Primitive : ushort
{
    /// <summary>"Sleep"</summary>
    Sleep = 0,
    /// <summary>"Generic Sims Call"</summary>
    GenericSimsCall = 1,
    /// <summary>"Expression"</summary>
    Expression = 2,
    /// <summary>"Find Best Interaction"</summary>
    FindBestInteraction = 3,
    /// <summary>"Grab"</summary>
    Grab = 4,
    /// <summary>"Drop"</summary>
    Drop = 5,
    /// <summary>"Change Suit/Accessory"</summary>
    ChangeSuitAccessory = 6,
    /// <summary>"Refresh"</summary>
    Refresh = 7,
    /// <summary>"Random Number"</summary>
    RandomNumber = 8,
    /// <summary>"Burn"</summary>
    Burn = 9,
    /// <summary>"Tutorial"</summary>
    Tutorial = 10,
    /// <summary>"Get Distance To"</summary>
    GetDistanceTo = 11,
    /// <summary>"Get Direction To"</summary>
    GetDirectionTo = 12,
    /// <summary>"Push Interaction"</summary>
    PushInteraction = 13,
    /// <summary>"Find Best Object for Function"</summary>
    FindBestObjectForFunction = 14,
    /// <summary>"Break Point"</summary>
    BreakPoint = 15,
    /// <summary>"Find Location For"</summary>
    FindLocationFor = 16,
    /// <summary>"Idle for Input"</summary>
    IdleForInput = 17,
    /// <summary>"Remove Object Instance"</summary>
    RemoveObjectInstance = 18,
    /// <summary>"Make New Character"</summary>
    MakeNewCharacter = 19,
    /// <summary>"Run Functional Tree"</summary>
    RunFunctionalTree = 20,
    /// <summary>"Show String ( UNUSED )"</summary>
    ShowStringUnused = 21,
    /// <summary>"Look Towards"</summary>
    LookTowards = 22,
    /// <summary>"Play Sound Event"</summary>
    PlaySoundEvent = 23,
    /// <summary>"old relationship ( DO NOT USED )"</summary>
    OldRelationshipDoNotUsed = 24,
    /// <summary>"Alter Budget"</summary>
    AlterBudget = 25,
    /// <summary>"Relationship"</summary>
    Relationship = 26,
    /// <summary>"Go To Relative Position"</summary>
    GoToRelativePosition = 27,
    /// <summary>"Run Tree by Name"</summary>
    RunTreeByName = 28,
    /// <summary>"Set Motive Change"</summary>
    SetMotiveChange = 29,
    /// <summary>"Gosub Found Action"</summary>
    GosubFoundAction = 30,
    /// <summary>"Set to Next"</summary>
    SetToNext = 31,
    /// <summary>"Test Object Type"</summary>
    TestObjectType = 32,
    /// <summary>"Find 5 Worst Motives"</summary>
    Find5WorstMotives = 33,
    /// <summary>"UI Effect"</summary>
    UIEffect = 34,
    /// <summary>"Special Effect"</summary>
    SpecialEffect = 35,
    /// <summary>"Dialog"</summary>
    Dialog = 36,
    /// <summary>"Test Sim Interacting With"</summary>
    TestSimInteractingWith = 37,
    /// <summary>"~unused"</summary>
    Unused = 38,
    /// <summary>"~unused"</summary>
    Unused2 = 39,
    /// <summary>"~unused"</summary>
    Unused3 = 40,
    /// <summary>"Set Balloon/Headline"</summary>
    SetBalloonHeadline = 41,
    /// <summary>"Create New Object Instance"</summary>
    CreateNewObjectInstance = 42,
    /// <summary>"Drop Onto"</summary>
    DropOnto = 43,
    /// <summary>"Animate Sim"</summary>
    AnimateSim = 44,
    /// <summary>"Go To Routing Slot"</summary>
    GoToRoutingSlot = 45,
    /// <summary>"Snap"</summary>
    Snap = 46,
    /// <summary>"Reach"</summary>
    Reach = 47,
    /// <summary>"Stop ALL Sounds"</summary>
    StopALLSounds = 48,
    /// <summary>"Notify the Stack Object out of Idle"</summary>
    NotifyTheStackObjectOutOfIdle = 49,
    /// <summary>"Add/Change the Action String"</summary>
    AddChangeTheActionString = 50,
    /// <summary>"Manage Inventory ( 1.x Animate Object in 2.0 )"</summary>
    ManageInventory1XAnimateObjectIn20 = 51,
    /// <summary>"Change Light Color ( 2.0 )"</summary>
    ChangeLightColor20 = 52,
    /// <summary>"Change Sun Color ( 2.0 )"</summary>
    ChangeSunColor20 = 53,
    /// <summary>"Point Light at Object ( 2.0 )"</summary>
    PointLightAtObject20 = 54,
    /// <summary>"~unused (in Sims Online)"</summary>
    UnusedInSimsOnline = 55,
    /// <summary>"~unused (in Sims Online)"</summary>
    UnusedInSimsOnline2 = 56,
}

/// <summary>The simulation globals, named as Behavior.iff STR# 129 ("global labels") names them.</summary>
public enum SimulationGlobal : short
{
    /// <summary>"Hour"</summary>
    Hour = 0,
    /// <summary>"Day of Month"</summary>
    DayOfMonth = 1,
    /// <summary>"Zoom Level(S/M/L)"</summary>
    ZoomLevelSML = 2,
    /// <summary>"Selected Person ID"</summary>
    SelectedPersonID = 3,
    /// <summary>"Time Of Day"</summary>
    TimeOfDay = 4,
    /// <summary>"Minute"</summary>
    Minute = 5,
    /// <summary>"Second"</summary>
    Second = 6,
    /// <summary>"Month"</summary>
    Month = 7,
    /// <summary>"Year"</summary>
    Year = 8,
    /// <summary>"Current Family"</summary>
    CurrentFamily = 9,
    /// <summary>"Current House"</summary>
    CurrentHouse = 10,
    /// <summary>"Unused"</summary>
    Unused = 11,
    /// <summary>"Last GZ Button ID"</summary>
    LastGZButtonID = 12,
    /// <summary>"Budget Mod 10000"</summary>
    BudgetMod10000 = 13,
    /// <summary>"Budget Div 10000"</summary>
    BudgetDiv10000 = 14,
    /// <summary>"Current Language"</summary>
    CurrentLanguage = 15,
    /// <summary>"Speed"</summary>
    Speed = 16,
    /// <summary>"Paused"</summary>
    Paused = 17,
    /// <summary>"Held Sim Speed"</summary>
    HeldSimSpeed = 18,
    /// <summary>"Mode"</summary>
    Mode = 19,
    /// <summary>"Game Edition"</summary>
    GameEdition = 20,
    /// <summary>"Inhibit Move In"</summary>
    InhibitMoveIn = 21,
    /// <summary>"Lot Has House"</summary>
    LotHasHouse = 22,
    /// <summary>"Lot Size"</summary>
    LotSize = 23,
    /// <summary>"Unsed in 1.x and 2.0"</summary>
    UnsedIn1XAnd20 = 24,
    /// <summary>"Debug flags"</summary>
    DebugFlags = 25,
    /// <summary>"Is Tutorial House (No/Yes)"</summary>
    IsTutorialHouseNoYes = 26,
    /// <summary>"Indoor Tiles"</summary>
    IndoorTiles = 27,
    /// <summary>"Days Running"</summary>
    DaysRunning = 28,
    /// <summary>"Maximum Day Number"</summary>
    MaximumDayNumber = 29,
    /// <summary>"Free Will"</summary>
    FreeWill = 30,
    /// <summary>"House radio station"</summary>
    HouseRadioStation = 31,
    /// <summary>"Simless Build Mode"</summary>
    SimlessBuildMode = 32,
    /// <summary>"Machine Level"</summary>
    MachineLevel = 33,
    /// <summary>"Lot Transit Info"</summary>
    LotTransitInfo = 34,
    /// <summary>"Lot Type"</summary>
    LotType = 35,
    /// <summary>"Enable Grass Simulation"</summary>
    EnableGrassSimulation = 36,
    /// <summary>"Filter Flags for Transit Screen"</summary>
    FilterFlagsForTransitScreen = 37,
}

/// <summary>The fields of an object's data, named as Behavior.iff STR# 141 ("Data labels") names them.</summary>
public enum ObjectData : short
{
    /// <summary>"graphic"</summary>
    Graphic = 0,
    /// <summary>"direction"</summary>
    Direction = 1,
    /// <summary>"container id"</summary>
    ContainerId = 2,
    /// <summary>"slot number"</summary>
    SlotNumber = 3,
    /// <summary>"allowed height flags"</summary>
    AllowedHeightFlags = 4,
    /// <summary>"wall adjacency flags"</summary>
    WallAdjacencyFlags = 5,
    /// <summary>"route id"</summary>
    RouteId = 6,
    /// <summary>"room impact"</summary>
    RoomImpact = 7,
    /// <summary>"flags"</summary>
    Flags = 8,
    /// <summary>"route preference"</summary>
    RoutePreference = 9,
    /// <summary>"route penalty"</summary>
    RoutePenalty = 10,
    /// <summary>"object id"</summary>
    ObjectId = 11,
    /// <summary>"target id"</summary>
    TargetId = 12,
    /// <summary>"wall placement flags"</summary>
    WallPlacementFlags = 13,
    /// <summary>"slot hier number"</summary>
    SlotHierNumber = 14,
    /// <summary>"repair state"</summary>
    RepairState = 15,
    /// <summary>"light source"</summary>
    LightSource = 16,
    /// <summary>"walk style"</summary>
    WalkStyle = 17,
    /// <summary>"sim age"</summary>
    SimAge = 18,
    /// <summary>"unused gender"</summary>
    UnusedGender = 19,
    /// <summary>"tree table entry"</summary>
    TreeTableEntry = 20,
    /// <summary>"birth minute"</summary>
    BirthMinute = 21,
    /// <summary>"speed"</summary>
    Speed = 22,
    /// <summary>"rotation notches"</summary>
    RotationNotches = 23,
    /// <summary>"birth hour"</summary>
    BirthHour = 24,
    /// <summary>"lockout count"</summary>
    LockoutCount = 25,
    /// <summary>"parent id"</summary>
    ParentId = 26,
    /// <summary>"weight"</summary>
    Weight = 27,
    /// <summary>"support strength"</summary>
    SupportStrength = 28,
    /// <summary>"room"</summary>
    Room = 29,
    /// <summary>"room placement"</summary>
    RoomPlacement = 30,
    /// <summary>"prep value"</summary>
    PrepValue = 31,
    /// <summary>"cook value"</summary>
    CookValue = 32,
    /// <summary>"surface value"</summary>
    SurfaceValue = 33,
    /// <summary>"hidden"</summary>
    Hidden = 34,
    /// <summary>"temperature"</summary>
    Temperature = 35,
    /// <summary>"dispose value"</summary>
    DisposeValue = 36,
    /// <summary>"wash dish value"</summary>
    WashDishValue = 37,
    /// <summary>"eating surface value"</summary>
    EatingSurfaceValue = 38,
    /// <summary>"dirty level"</summary>
    DirtyLevel = 39,
    /// <summary>"flag field 2"</summary>
    FlagField2 = 40,
    /// <summary>"current value"</summary>
    CurrentValue = 41,
    /// <summary>"placement flags"</summary>
    PlacementFlags = 42,
    /// <summary>"movement flags"</summary>
    MovementFlags = 43,
    /// <summary>"maximum grade"</summary>
    MaximumGrade = 44,
    /// <summary>"birth year"</summary>
    BirthYear = 45,
    /// <summary>"birth month"</summary>
    BirthMonth = 46,
    /// <summary>"birth day"</summary>
    BirthDay = 47,
    /// <summary>"age in months"</summary>
    AgeInMonths = 48,
    /// <summary>"size"</summary>
    Size = 49,
    /// <summary>"hide interaction"</summary>
    HideInteraction = 50,
    /// <summary>"lighting contribution (0..100)"</summary>
    LightingContribution0100 = 51,
    /// <summary>"primitive result"</summary>
    PrimitiveResult = 52,
    /// <summary>"wall block flags"</summary>
    WallBlockFlags = 53,
    /// <summary>"primitive result id"</summary>
    PrimitiveResultId = 54,
    /// <summary>"unused classical score"</summary>
    UnusedClassicalScore = 55,
    /// <summary>"unused eclectic score"</summary>
    UnusedEclecticScore = 56,
    /// <summary>"unused trailer score"</summary>
    UnusedTrailerScore = 57,
    /// <summary>"object version"</summary>
    ObjectVersion = 58,
    /// <summary>"category"</summary>
    Category = 59,
    /// <summary>"simulate on pause"</summary>
    SimulateOnPause = 60,
    /// <summary>"serving surface value"</summary>
    ServingSurfaceValue = 61,
    /// <summary>"use count"</summary>
    UseCount = 62,
    /// <summary>"exclusive placement flags"</summary>
    ExclusivePlacementFlags = 63,
    /// <summary>"gardening value"</summary>
    GardeningValue = 64,
    /// <summary>"wash hands value"</summary>
    WashHandsValue = 65,
    /// <summary>"function score"</summary>
    FunctionScore = 66,
    /// <summary>"slot count"</summary>
    SlotCount = 67,
}

/// <summary>The fields of a person's data, named as Behavior.iff STR# 200 ("Person Data Labels") names them.</summary>
public enum PersonData : short
{
    /// <summary>"Sitting?"</summary>
    Sitting = 0,
    /// <summary>"Money Amount over Head"</summary>
    MoneyAmountOverHead = 1,
    /// <summary>"Nice/Loyal Personality"</summary>
    NiceLoyalPersonality = 2,
    /// <summary>"Active/Playful Personality"</summary>
    ActivePlayfulPersonality = 3,
    /// <summary>"Generous Personality (1.0 NPC still use this)"</summary>
    GenerousPersonality10NPCStillUseThis = 4,
    /// <summary>"Playful/Smart Personality"</summary>
    PlayfulSmartPersonality = 5,
    /// <summary>"Outgoing/Friendly Personality"</summary>
    OutgoingFriendlyPersonality = 6,
    /// <summary>"Neat/Quiet Personality"</summary>
    NeatQuietPersonality = 7,
    /// <summary>"Current Outfit"</summary>
    CurrentOutfit = 8,
    /// <summary>"Cleaning Skill"</summary>
    CleaningSkill = 9,
    /// <summary>"Cooking/House Broken Skill"</summary>
    CookingHouseBrokenSkill = 10,
    /// <summary>"Charisma/Obedience Skill"</summary>
    CharismaObedienceSkill = 11,
    /// <summary>"Mechanical/Tricks Skill"</summary>
    MechanicalTricksSkill = 12,
    /// <summary>"i: Exercise ( Hot Date )"</summary>
    IExerciseHotDate = 13,
    /// <summary>"i: Food ( Hot Date )"</summary>
    IFoodHotDate = 14,
    /// <summary>"Creativity Skill"</summary>
    CreativitySkill = 15,
    /// <summary>"i: Parties  ( Hot Date )"</summary>
    IPartiesHotDate = 16,
    /// <summary>"Body/Furniture Skill"</summary>
    BodyFurnitureSkill = 17,
    /// <summary>"Logic Skill"</summary>
    LogicSkill = 18,
    /// <summary>"Unused &amp; Do NOT Use"</summary>
    UnusedAndDoNotUse = 19,
    /// <summary>"i: Style ( Hot Date )"</summary>
    IStyleHotDate = 20,
    /// <summary>"Costume Trunk offset ( House Party )"</summary>
    CostumeTrunkOffsetHouseParty = 21,
    /// <summary>"Hand Gesture - Left"</summary>
    HandGestureLeft = 22,
    /// <summary>"Hand Gesture - Right"</summary>
    HandGestureRight = 23,
    /// <summary>"Job Data"</summary>
    JobData = 24,
    /// <summary>"Tree Use Data Field #1"</summary>
    TreeUseDataField1 = 25,
    /// <summary>"i: Hollywood ( Hot Date )"</summary>
    IHollywoodHotDate = 26,
    /// <summary>"tick counter"</summary>
    TickCounter = 27,
    /// <summary>"Tree Use Data Field #2"</summary>
    TreeUseDataField2 = 28,
    /// <summary>"Motives Static?"</summary>
    MotivesStatic = 29,
    /// <summary>"Censorship Flags"</summary>
    CensorshipFlags = 30,
    /// <summary>"neighbor id"</summary>
    NeighborId = 31,
    /// <summary>"Person Type"</summary>
    PersonType = 32,
    /// <summary>"Priority"</summary>
    Priority = 33,
    /// <summary>"Greet Status"</summary>
    GreetStatus = 34,
    /// <summary>"visitor schedule"</summary>
    VisitorSchedule = 35,
    /// <summary>"autonomy level"</summary>
    AutonomyLevel = 36,
    /// <summary>"route entry flags"</summary>
    RouteEntryFlags = 37,
    /// <summary>"multiple routes"</summary>
    MultipleRoutes = 38,
    /// <summary>"routing complete"</summary>
    RoutingComplete = 39,
    /// <summary>"walking up stairs"</summary>
    WalkingUpStairs = 40,
    /// <summary>"head seek object"</summary>
    HeadSeekObject = 41,
    /// <summary>"head seek state"</summary>
    HeadSeekState = 42,
    /// <summary>"head seek finish action"</summary>
    HeadSeekFinishAction = 43,
    /// <summary>"head seek limit action"</summary>
    HeadSeekLimitAction = 44,
    /// <summary>"head seek timeout"</summary>
    HeadSeekTimeout = 45,
    /// <summary>"i: Travel / Toys"</summary>
    ITravelToys = 46,
    /// <summary>"i: Violence / Aliens"</summary>
    IViolenceAliens = 47,
    /// <summary>"i: Politics / Pets"</summary>
    IPoliticsPets = 48,
    /// <summary>"i: 60's / School"</summary>
    I60sSchool = 49,
    /// <summary>"i: Weather"</summary>
    IWeather = 50,
    /// <summary>"i: Sports"</summary>
    ISports = 51,
    /// <summary>"i: Music"</summary>
    IMusic = 52,
    /// <summary>"i: Outdoors"</summary>
    IOutdoors = 53,
    /// <summary>"i: Technology ( Hot Date )"</summary>
    ITechnologyHotDate = 54,
    /// <summary>"i: Romance ( Hot Date )"</summary>
    IRomanceHotDate = 55,
    /// <summary>"Job Type"</summary>
    JobType = 56,
    /// <summary>"Job Promotion Level"</summary>
    JobPromotionLevel = 57,
    /// <summary>"Person Age"</summary>
    PersonAge = 58,
    /// <summary>"Object Social Object ID"</summary>
    ObjectSocialObjectID = 59,
    /// <summary>"skin color"</summary>
    SkinColor = 60,
    /// <summary>"family number"</summary>
    FamilyNumber = 61,
    /// <summary>"route result"</summary>
    RouteResult = 62,
    /// <summary>"job performance"</summary>
    JobPerformance = 63,
    /// <summary>"Is Swimming?"</summary>
    IsSwimming = 64,
    /// <summary>"gender"</summary>
    Gender = 65,
    /// <summary>"private"</summary>
    Private = 66,
    /// <summary>"lingering house number"</summary>
    LingeringHouseNumber = 67,
    /// <summary>"Is Ghost?"</summary>
    IsGhost = 68,
    /// <summary>"last day at work"</summary>
    LastDayAtWork = 69,
    /// <summary>"zodiac"</summary>
    Zodiac = 70,
    /// <summary>"non-interruptible"</summary>
    NonInterruptible = 71,
    /// <summary>"route footprint mask"</summary>
    RouteFootprintMask = 72,
    /// <summary>"footprint extension"</summary>
    FootprintExtension = 73,
    /// <summary>"Render Display Flags"</summary>
    RenderDisplayFlags = 74,
    /// <summary>"Female Preference ( Hot Date )"</summary>
    FemalePreferenceHotDate = 75,
    /// <summary>"Male Preference ( Hot Date )"</summary>
    MalePreferenceHotDate = 76,
    /// <summary>"AutoFollow ( Hot Date )"</summary>
    AutoFollowHotDate = 77,
    /// <summary>"Freeze for Idle? ( Hot Date )"</summary>
    FreezeForIdleHotDate = 78,
    /// <summary>"Obj Soc Availability Flags ( Hot Date )"</summary>
    ObjSocAvailabilityFlagsHotDate = 79,
    /// <summary>"Fame Score"</summary>
    FameScore = 80,
    /// <summary>"Fame Star Power (0-9)"</summary>
    FameStarPower09 = 81,
    /// <summary>"Fame Star - High Watermark"</summary>
    FameStarHighWatermark = 82,
    /// <summary>"Magic Transgressions"</summary>
    MagicTransgressions = 83,
    /// <summary>"Is Living Ghost"</summary>
    IsLivingGhost = 84,
    /// <summary>"Scaling Sim"</summary>
    ScalingSim = 85,
    /// <summary>"Is Toad"</summary>
    IsToad = 86,
    /// <summary>"Is Person at Work/School"</summary>
    IsPersonAtWorkSchool = 87,
}

/// <summary>The motives a person has, named as Behavior.iff STR# 134 ("motive strings") names them; the unused ones are left out.</summary>
public enum Motive : short
{
    /// <summary>"Mood"</summary>
    Mood = 3,
    /// <summary>"Energy"</summary>
    Energy = 5,
    /// <summary>"Comfort"</summary>
    Comfort = 6,
    /// <summary>"Hunger"</summary>
    Hunger = 7,
    /// <summary>"Hygiene"</summary>
    Hygiene = 8,
    /// <summary>"Bladder"</summary>
    Bladder = 9,
    /// <summary>"Sleep state"</summary>
    SleepState = 11,
    /// <summary>"Room"</summary>
    Room = 13,
    /// <summary>"Social"</summary>
    Social = 14,
    /// <summary>"Fun"</summary>
    Fun = 15,
}
