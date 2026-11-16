namespace SimsEngine.Formats;

/// <summary>Serialized character appearance data for a Sim, including body part mesh and texture string
/// pairs, head and outfit bindings, and scale transforms.</summary>
public sealed record PersonCharacter(
    int Value0xA4,
    int Value0xA0,
    IReadOnlyList<(string, string)> Parts,
    IReadOnlyList<(string, string)> ExtraParts,
    IReadOnlyList<string> Names,
    int? Value0x190,
    float[] Values);

/// <summary>Represents a serialized queue or active interaction record, containing action identifiers,
/// target object references, parameters, and versioned flags.</summary>
public sealed record PersonAction(
    int Value0x00,
    int Id,
    short Person,
    short Object0x08,
    short Object0x0C,
    int Value0x10,
    short[] Values0x14,
    int Value0x1C,
    short Value0x20,
    float Value0x24,
    int Flags);

public sealed record PersonObjectEntry(short Object, int Value, bool Flag);

public sealed record PersonValueEntry(int Value, float First, float Second);

/// <summary>Serialized state specific to a Sim object, containing character mesh bindings, motives, person
/// data variables, and interaction queue records.</summary>
public sealed record PersonState(
    PersonCharacter Character,
    float[] Values0x78C,
    float[] Values0x7CC,
    short[] PersonData,
    int Count0xAF0,
    PersonAction First,
    PersonAction Second,
    IReadOnlyList<PersonAction> Actions,
    IReadOnlyList<PersonObjectEntry> Objects,
    IReadOnlyList<PersonValueEntry> Values)
{
    internal static PersonState Read(ref PersistReader stream, int version)
    {
        var character = ReadCharacter(ref stream, version);
        var values78C = Singles(ref stream, 0x10);
        var values7CC = Singles(ref stream, 0x10);
        var personData = stream.Int16s(version < 0x47 ? 0x50 : 0x100);
        var count0xAF0 = stream.Int32();
        if (count0xAF0 < 0) throw stream.Error($"{count0xAF0} entries at +0xaf0");

        var first = ReadAction(ref stream, version);
        var second = ReadAction(ref stream, version);
        var actions = new PersonAction[stream.Count(2)];
        for (var index = 0; index < actions.Length; index++) actions[index] = ReadAction(ref stream, version);

        var objects = new PersonObjectEntry[stream.Count(1)];
        for (var index = 0; index < objects.Length; index++)
            objects[index] = new PersonObjectEntry(stream.Int16(), stream.Int32(), stream.Bool());
        var values = new PersonValueEntry[stream.Count(1)];
        for (var index = 0; index < values.Length; index++)
            values[index] = new PersonValueEntry(stream.Int32(), stream.Single(), stream.Single());

        return new PersonState(character, values78C, values7CC, personData, count0xAF0, first, second, actions, objects,
            values);
    }

    private static PersonCharacter ReadCharacter(ref PersistReader stream, int version)
    {
        var valueA4 = stream.Int32();
        var valueA0 = stream.Int32();
        var parts = new List<(string, string)>();
        var extraParts = new List<(string, string)>();
        if (version >= 0x33)
        {
            for (var index = 0; index < 5; index++) parts.Add((stream.String(), stream.String()));
            var count = stream.Count(2);
            for (var index = 0; index < count; index++) extraParts.Add((stream.String(), stream.String()));
        }
        var names = version > 0x3a ? new[] { stream.String(), stream.String(), stream.String() } : [];
        int? value190 = null;
        float[] values = [];
        if (version > 0x3c)
        {
            value190 = stream.Int32();
            values = Singles(ref stream, 9);
        }
        return new PersonCharacter(valueA4, valueA0, parts, extraParts, names, value190, values);
    }

    private static PersonAction ReadAction(ref PersistReader stream, int version)
    {
        var value00 = stream.Int32();
        var id = stream.Int32();
        var person = stream.Int16();
        var object08 = stream.Int16();
        var object0C = stream.Int16();
        var value10 = stream.Int32();
        var values14 = stream.Int16s(4);
        var value1C = stream.Int32();
        var value20 = stream.Int16();
        var value24 = stream.Single();
        int flags;
        if (version >= 0x3e) flags = stream.Int32();
        else
        {
            flags = (stream.Bool() ? 4 : 0) | (stream.Bool() ? 0x20 : 0);
            if ((flags & 4) != 0) flags |= (stream.Bool() ? 8 : 0) | (stream.Bool() ? 0x10 : 0);
            if (version > 0x3b) flags |= stream.Bool() ? 2 : 0;
        }
        return new PersonAction(value00, id, person, object08, object0C, value10, values14, value1C, value20, value24,
            flags);
    }

    internal static void Write(PersistWriter stream, PersonState state, int version)
    {
        WriteCharacter(stream, state.Character, version);
        foreach (var value in state.Values0x78C) stream.Single(value);
        foreach (var value in state.Values0x7CC) stream.Single(value);
        stream.Int16s(state.PersonData);
        stream.Int32(state.Count0xAF0);
        WriteAction(stream, state.First, version);
        WriteAction(stream, state.Second, version);
        stream.Int32(state.Actions.Count);
        foreach (var action in state.Actions) WriteAction(stream, action, version);
        stream.Int32(state.Objects.Count);
        foreach (var entry in state.Objects)
        {
            stream.Int16(entry.Object);
            stream.Int32(entry.Value);
            stream.Bool(entry.Flag);
        }
        stream.Int32(state.Values.Count);
        foreach (var entry in state.Values)
        {
            stream.Int32(entry.Value);
            stream.Single(entry.First);
            stream.Single(entry.Second);
        }
    }

    private static void WriteCharacter(PersistWriter stream, PersonCharacter character, int version)
    {
        stream.Int32(character.Value0xA4);
        stream.Int32(character.Value0xA0);
        if (version >= 0x33)
        {
            for (var index = 0; index < 5; index++)
            {
                stream.String(character.Parts[index].Item1);
                stream.String(character.Parts[index].Item2);
            }
            stream.Int32(character.ExtraParts.Count);
            foreach (var (first, second) in character.ExtraParts)
            {
                stream.String(first);
                stream.String(second);
            }
        }
        if (version > 0x3a)
            for (var index = 0; index < 3; index++) stream.String(character.Names[index]);
        if (version > 0x3c)
        {
            stream.Int32(character.Value0x190 ?? throw new InvalidDataException("The character has no +0x190 value to write."));
            foreach (var value in character.Values) stream.Single(value);
        }
    }

    private static void WriteAction(PersistWriter stream, PersonAction action, int version)
    {
        stream.Int32(action.Value0x00);
        stream.Int32(action.Id);
        stream.Int16(action.Person);
        stream.Int16(action.Object0x08);
        stream.Int16(action.Object0x0C);
        stream.Int32(action.Value0x10);
        stream.Int16s(action.Values0x14);
        stream.Int32(action.Value0x1C);
        stream.Int16(action.Value0x20);
        stream.Single(action.Value0x24);
        if (version >= 0x3e)
        {
            stream.Int32(action.Flags);
            return;
        }
        stream.Bool((action.Flags & 4) != 0);
        stream.Bool((action.Flags & 0x20) != 0);
        if ((action.Flags & 4) != 0)
        {
            stream.Bool((action.Flags & 8) != 0);
            stream.Bool((action.Flags & 0x10) != 0);
        }
        if (version > 0x3b) stream.Bool((action.Flags & 2) != 0);
    }

    private static float[] Singles(ref PersistReader stream, int count)
    {
        var values = new float[count];
        for (var index = 0; index < count; index++) values[index] = stream.Single();
        return values;
    }
}
