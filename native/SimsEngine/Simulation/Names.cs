namespace SimsEngine.Simulation;

public enum DataOwner : short
{
    /// <summary>"My Attribute"</summary>
    MyAttribute = 0,
    /// <summary>"Stack Obj's Attribute"</summary>
    StackObjectAttribute = 1,
    TargetObjectAttribute = 2,
    My = 3,
    StackObject = 4,
    TargetObject = 5,
    /// <summary>"Global ( from Simulation )"</summary>
    Global = 6,
    /// <summary>"Literal Value"</summary>
    Literal = 7,
    /// <summary>"Temporary Storage"</summary>
    Temporary = 8,
    /// <summary>"Parameters"</summary>
    Parameter = 9,
    /// <summary>"Stack Object ID"</summary>
    StackObjectId = 10,
    /// <summary>"Temporary Storage[temp]"</summary>
    TemporaryByTemporary = 11,
    /// <summary>"check tree ad range"</summary>
    CheckTreeAdRange = 12,
    /// <summary>"stack obj's Temporary Storage"</summary>
    StackObjectTemporary = 13,
    /// <summary>"my motives"</summary>
    MyMotive = 14,
    /// <summary>"stack obj's motives"</summary>
    StackObjectMotive = 15,
    /// <summary>"stack object's slot"</summary>
    StackObjectSlot = 16,
    /// <summary>"stack obj's motive[temp]"</summary>
    StackObjectMotiveByTemporary = 17,
    /// <summary>"my person data"</summary>
    MyPersonData = 18,
    /// <summary>"stack obj's person data"</summary>
    StackObjectPersonData = 19,
    /// <summary>"my slot"</summary>
    MySlot = 20,
    /// <summary>"stack object's definition"</summary>
    StackObjectDefinition = 21,
    /// <summary>"stack obj attr[stack param]"</summary>
    StackObjectAttributeByParameter = 22,
    /// <summary>"room [temp 0]"</summary>
    RoomByTemporary0 = 23,
    /// <summary>"neighbor in stack object"</summary>
    NeighborInStackObject = 24,
    /// <summary>"Local Variable"</summary>
    Local = 25,
    /// <summary>"Constant Value"</summary>
    Constant = 26,
    /// <summary>"Dynamic Sprite Flag[temp] of Stack Object"</summary>
    StackObjectDynamicSpriteFlagByTemporary = 27,
    /// <summary>"check tree ad personality var"</summary>
    CheckTreeAdPersonalityVariable = 28,
    /// <summary>"check tree ad min"</summary>
    CheckTreeAdMin = 29,
    /// <summary>"my person data [temp]"</summary>
    MyPersonDataByTemporary = 30,
    /// <summary>"stack obj's person data [temp]"</summary>
    StackObjectPersonDataByTemporary = 31,
    /// <summary>"neighbor's person data"</summary>
    NeighborPersonData = 32,
    /// <summary>"job data [temp 0,1]"</summary>
    JobDataByTemporary0And1 = 33,
    /// <summary>"neighborhood data"</summary>
    NeighborhoodData = 34,
    /// <summary>"stack object's function"</summary>
    StackObjectFunction = 35,
    /// <summary>"my type attr"</summary>
    MyTypeAttribute = 36,
    /// <summary>"stack obj's type attr"</summary>
    StackObjectTypeAttribute = 37,
    /// <summary>"Neighbor's Object Definition"</summary>
    NeighborObjectDefinition = 38,
}

public enum ExpressionOperator : byte
{
    /// <summary>"&gt;"</summary>
    GreaterThan = 0,
    /// <summary>"&lt;"</summary>
    LessThan = 1,
    /// <summary>"Equals?"</summary>
    EqualTo = 2,
    /// <summary>"+="</summary>
    AddTo = 3,
    /// <summary>"-="</summary>
    SubtractFrom = 4,
    /// <summary>"Assign To:"</summary>
    AssignTo = 5,
    /// <summary>"*="</summary>
    MultiplyBy = 6,
    /// <summary>"/="</summary>
    DivideBy = 7,
    /// <summary>"Flag Set?"</summary>
    IsFlagSet = 8,
    /// <summary>"Set Flag"</summary>
    SetFlag = 9,
    /// <summary>"Clear Flag"</summary>
    ClearFlag = 10,
    /// <summary>"++ and &lt;"</summary>
    IncrementAndLessThan = 11,
    /// <summary>"mod="</summary>
    ModuloBy = 12,
    /// <summary>"and="</summary>
    AndWith = 13,
    /// <summary>"&gt;="</summary>
    GreaterThanOrEqualTo = 14,
    /// <summary>"&lt;="</summary>
    LessThanOrEqualTo = 15,
    /// <summary>"Not Equal To"</summary>
    NotEqualTo = 16,
    /// <summary>"-- and &gt;"</summary>
    DecrementAndGreaterThan = 17,
    /// <summary>"or="</summary>
    OrWith = 18,
    /// <summary>"xor="</summary>
    XorWith = 19,
    /// <summary>"=sqrt(RHS)"</summary>
    AssignSquareRoot = 20,
}

public enum SimulationError : short
{
    /// <summary>"No Error"</summary>
    NoError = 0,
    /// <summary>"Bad operator."</summary>
    BadOperator = 1,
    /// <summary>"Temporary reference out of range."</summary>
    TemporaryReferenceOutOfRange = 2,
    /// <summary>"Bad parameter to update primitive."</summary>
    BadParameterToUpdatePrimitive = 3,
    /// <summary>"Random number range evaluated to zero."</summary>
    RandomNumberRangeEvaluatedToZero = 4,
    /// <summary>"Illegal primitive code."</summary>
    IllegalPrimitiveCode = 5,
    /// <summary>"Attribute number out of range."</summary>
    AttributeNumberOutOfRange = 6,
    /// <summary>"Data number out of range."</summary>
    DataNumberOutOfRange = 7,
    /// <summary>"Stack number out of range."</summary>
    StackNumberOutOfRange = 8,
    /// <summary>"Illegal owner field in data reference."</summary>
    IllegalOwnerFieldInDataReference = 9,
    ReferenceToTreeParameterWhenNoParameterExists = 10,
    /// <summary>"Reference to target object when none exists."</summary>
    ReferenceToTargetObjectWhenNoneExists = 12,
    /// <summary>"Flag reference is out of range."</summary>
    FlagReferenceIsOutOfRange = 13,
    /// <summary>"Undefined transition found."</summary>
    UndefinedTransitionFound = 14,
    /// <summary>"Reference to stack object out of range."</summary>
    ReferenceToStackObjectOutOfRange = 15,
    /// <summary>"Bad alert tree number."</summary>
    BadAlertTreeNumber = 16,
    /// <summary>"Bad gosub tree number."</summary>
    BadGosubTreeNumber = 17,
    /// <summary>"Zero distance in finding direction."</summary>
    ZeroDistanceInFindingDirection = 18,
    /// <summary>"Expression attempting to write in a read-only area."</summary>
    ExpressionAttemptingToWriteInAReadOnlyArea = 19,
    /// <summary>"Data value out of range."</summary>
    DataValueOutOfRange = 20,
    /// <summary>"No stack object present when required."</summary>
    NoStackObjectPresentWhenRequired = 21,
    /// <summary>"Reference to tree table entry that does not exist."</summary>
    ReferenceToTreeTableEntryThatDoesNotExist = 22,
    /// <summary>"Could not find object reference."</summary>
    CouldNotFindObjectReference = 23,
    /// <summary>"Standard number for a weight assignment is out of range."</summary>
    StandardNumberForAWeightAssignmentIsOutOfRange = 24,
    /// <summary>"Weight number for a weight assignment is out of range."</summary>
    WeightNumberForAWeightAssignmentIsOutOfRange = 25,
    /// <summary>"Motive number out of range or not an acceptable value."</summary>
    MotiveNumberOutOfRange = 26,
    /// <summary>"Slot number out of range."</summary>
    SlotNumberOutOfRange = 27,
    /// <summary>"Object referred to must be a person."</summary>
    ObjectReferredToMustBeAPerson = 28,
    /// <summary>"Balloon Type Is Invalid. Values are 0:thought, 1:scream, 2:speak."</summary>
    BalloonTypeIsInvalid = 29,
    /// <summary>"Search type is invalid. Types are 0: all motive, 1:mental motives, 2:physical mo..."</summary>
    SearchTypeIsInvalid = 30,
    /// <summary>"Tree table not found"</summary>
    TreeTableNotFound = 31,
    /// <summary>"Bad target slot specified in routing slot."</summary>
    BadTargetSlotSpecifiedInRoutingSlot = 32,
    /// <summary>"Person data field is out of range."</summary>
    PersonDataFieldIsOutOfRange = 33,
    /// <summary>"Show hide parameter is out of expected range."</summary>
    ShowHideParameterIsOutOfExpectedRange = 34,
    /// <summary>"Index into object definition out of range."</summary>
    IndexIntoObjectDefinitionOutOfRange = 35,
    /// <summary>"Invalid snapping mode specified"</summary>
    InvalidSnappingModeSpecified = 36,
    /// <summary>"Invalid routing slot specified"</summary>
    InvalidRoutingSlotSpecified = 37,
    /// <summary>"Divide by zero."</summary>
    DivideByZero = 38,
    /// <summary>"Bad parameter in look towards primitive."</summary>
    BadParameterInLookTowardsPrimitive = 39,
    /// <summary>"Bad room id specified"</summary>
    BadRoomIdSpecified = 40,
    /// <summary>"Bad room value requested."</summary>
    BadRoomValueRequested = 41,
    /// <summary>"Invalid global sim call."</summary>
    InvalidGlobalSimCall = 42,
    /// <summary>"Named tree does not exist."</summary>
    NamedTreeDoesNotExist = 43,
    /// <summary>"Bad parameter to call named tree."</summary>
    BadParameterToCallNamedTree = 44,
    /// <summary>"Unrecognized priority value."</summary>
    UnrecognizedPriorityValue = 45,
    /// <summary>"Invalid constant."</summary>
    InvalidConstant = 46,
    /// <summary>"Invalid suit location."</summary>
    InvalidSuitLocation = 47,
    /// <summary>"Invalid suit index."</summary>
    InvalidSuitIndex = 48,
    /// <summary>"Invliad dynamic sprite index"</summary>
    InvalidDynamicSpriteIndex = 49,
    /// <summary>"Invalid object id."</summary>
    InvalidObjectId = 50,
    /// <summary>"Local out of range."</summary>
    LocalOutOfRange = 51,
    /// <summary>"Object for routing either does not exist or is out of bounds."</summary>
    ObjectForRoutingDoesNotExistOrIsOutOfBounds = 52,
    /// <summary>"Invalid action in tutorial primitive."</summary>
    InvalidActionInTutorialPrimitive = 53,
    /// <summary>"More than one passing value specified."</summary>
    MoreThanOnePassingValueSpecified = 54,
    NeighborNotFound = 55,
    /// <summary>"Bad expense type specified."</summary>
    BadExpenseTypeSpecified = 56,
    /// <summary>"Career id not found."</summary>
    CareerIdNotFound = 57,
    /// <summary>"Job data not found."</summary>
    JobDataNotFound = 58,
    /// <summary>"Invalid skin color."</summary>
    InvalidSkinColor = 59,
    /// <summary>"Neighborhood data index out of range."</summary>
    NeighborhoodDataIndexOutOfRange = 60,
    /// <summary>"Start object required."</summary>
    StartObjectRequired = 61,
    /// <summary>"Tree break encountered."</summary>
    TreeBreakEncountered = 62,
    GlobalNumberOutOfRange = 63,
    TypeAttributeNumberOutOfRange = 64,
}

public sealed class SimulationException(SimulationError error, string? detail = null)
    : Exception($"Simulation error {(short)error}: {error}{(detail is null ? string.Empty : $" ({detail})")}")
{
    public SimulationError Error { get; } = error;
}
