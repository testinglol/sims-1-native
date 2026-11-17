using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

internal static class NeighborhoodEdits
{
    public static object Apply(string edit, string game, string profile, byte language = 1, int mapMode = 1)
    {
        var userPath = Neighborhoods.UserData(profile);
        var parts = edit.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var value))
            throw new ArgumentException("Neighborhood edits look like evict:HOUSE, delete:FAMILY, bulldoze:HOUSE, import:INDEX, rezone:HOUSE:TYPE or description:HOUSE:NAME_BASE64:TEXT_BASE64");
        return parts[0] switch
        {
            "rezone" when parts.Length == 3 && int.TryParse(parts[2], out var zoningType) => Rezone(userPath, game, value, zoningType, language),
            "movein" or "moveIn" when parts.Length == 3 && int.TryParse(parts[2], out var famId) => MoveIn(userPath, game, value, famId, language),
            "description" when parts.Length == 4 && mapMode == 1 => SetLotDescription(userPath, game, value, Decode(parts[2]), Decode(parts[3]), language),
            "description" => throw new InvalidOperationException("Lot descriptions can only be edited from the home neighborhood map"),
            "evict" => Evict(userPath, game, value, language),
            "delete" => Delete(userPath, game, value),
            "bulldoze" => Bulldoze(userPath, game, value, language),
            "import" => ImportCandidate(userPath, profile, game, value, language),
            _ => throw new ArgumentException("Unknown neighborhood edit")
        };
    }

    private static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException error) { throw new ArgumentException("Lot description text is not valid base64", error); }
    }

    private static void WriteReplace(string path, ReadOnlyMemory<byte> bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes.ToArray());
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static object SetLotDescription(string userPath, string game, int house, string name, string description, byte language)
    {
        var zoning = NeighborhoodExport.ReadZoningDictionary(userPath, game);
        if (!zoning.ContainsKey((short)house)) throw new InvalidOperationException("The lot has no zoning record");
        var descPath = NeighborhoodExport.ResolveHouseDescriptionFile(userPath, game, house)
            ?? throw new InvalidOperationException("The lot has no editable name and description resource");
        var housesRoot = Path.GetFullPath(Path.Combine(userPath, "Houses"));
        Directory.CreateDirectory(housesRoot);
        var profileDescPath = Path.GetFullPath(Path.Combine(housesRoot, Path.GetFileName(descPath)));
        if (!profileDescPath.StartsWith(housesRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The lot description resource is not a writable profile file");
        var file = IffFile.Open(File.Exists(profileDescPath) ? profileDescPath : descPath);
        var chunkId = checked((ushort)(house + 2000));
        var chunk = file.Find(ResourceType.Str, chunkId)
            ?? throw new InvalidOperationException("The lot has no editable name and description resource");
        var table = StringTable.Read(file, chunk);
        if (table.ForLanguage(language).Count < 2)
            throw new InvalidOperationException("The lot has no editable name and description resource");
        var data1 = table.WriteWithValue(1, name, language);
        var intermediate = file.WithChunkDataResized(ResourceType.Str, chunkId, data1);
        var data2 = StringTable.Read(intermediate, intermediate.Find(ResourceType.Str, chunkId)!).WriteWithValue(2, description, language);
        var updated = intermediate.WithChunkDataResized(ResourceType.Str, chunkId, data2);
        WriteReplace(profileDescPath, updated.Bytes);
        return new { house, name, description };
    }

    internal static bool IsBulldozed(string userPath, int house, string? gameRoot = null)
    {
        var target = NeighborhoodExport.ResolveUserFile(userPath, gameRoot, Path.Combine("Houses", $"House{house:D2}.iff"));
        if (target is null || !File.Exists(target)) return false;
        try
        {
            var lot = IffFile.Open(target);
            if (lot.Find(ResourceType.Parse("SIMI"), 1) is not { } simiChunk) return false;
            var simi = SimulationState.Read(lot, simiChunk);
            if (simi.Field0xA4 == 0 && simi.Field0xA0 == 0) return true;
            var tiles = LotTiles.Read(lot);
            for (var level = 1; level <= 2; level++)
            {
                var walls = LotWalls.Read(lot, level);
                var floors = LotFloors.Read(lot, level);
                for (var x = 0; x < LotWalls.Size; x++)
                for (var y = 0; y < LotWalls.Size; y++)
                {
                    if (!tiles.Buildable(x, y, level)) continue;
                    if (walls[x, y][0] != 0 || floors[x, y] != 0)
                        return false;
                }
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private static (ushort ChunkId, Family Family)? FindFamilyForHouse(IffFile ngh, int house)
    {
        var all = ngh.OfType(ResourceType.Parse("FAMI"))
            .Where(c => c.Id != 0)
            .Select(c => (c.Id, Family: Family.Read(ngh, c)))
            .ToArray();
        var active = all.FirstOrDefault(f => f.Family.HouseNumber == house && (f.Family.Flags & 1) != 0);
        if (active.Family is not null) return active;
        var any = all.FirstOrDefault(f => f.Family.HouseNumber == house);
        return any.Family is not null ? any : null;
    }

    private static object Rezone(string userPath, string game, int house, int type, byte language)
    {
        if (type is not (0 or 1)) throw new ArgumentException("Zoning type must be 0 (residential) or 1 (community)");
        var zoning = NeighborhoodExport.ReadZoningDictionary(userPath, game);
        if (!zoning.ContainsKey((short)house)) throw new InvalidOperationException("The lot has no zoning record");
        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff")
            ?? throw new FileNotFoundException("Neighborhood.iff not found");
        var ngh = IffFile.Open(nghPath);
        if (FindFamilyForHouse(ngh, house) is not null)
            throw new InvalidOperationException("Evict the family before rezoning its lot");
        if (!IsBulldozed(userPath, house, game))
            throw new InvalidOperationException("Bulldoze the lot before rezoning it");

        var zoningSourcePath = NeighborhoodExport.ResolveUserFile(userPath, game, "LotZoning.iff")
            ?? throw new FileNotFoundException("LotZoning.iff not found");
        var file = IffFile.Open(zoningSourcePath);
        var chunk = file.Find(ResourceType.Str, 1) ?? throw new InvalidDataException("LotZoning.iff has no zoning table");
        var table = StringTable.Read(file, chunk);
        var list = table.ForLanguage(1);
        var index = Enumerable.Range(0, list.Count).FirstOrDefault(i => list[i].Split(',')[0].Trim() == house.ToString(CultureInfo.InvariantCulture), -1);
        if (index < 0) throw new InvalidOperationException("The lot is missing from the zoning table");
        var updatedBytes = table.WriteWithValue(index + 1, $"{house}, {(type == 1 ? "community" : "residential")}", 1);
        var updatedFile = file.WithChunkDataResized(ResourceType.Str, 1, updatedBytes);
        var profileZoningPath = Path.Combine(userPath, "LotZoning.iff");
        WriteReplace(profileZoningPath, updatedFile.Bytes);
        return new { rezoned = house, type };
    }

    private static bool LeavesOnEvict(XObject entity)
    {
        if ((entity[ObjectData.MovementFlags] & 0x10) != 0) return false;
        if (entity is XPerson) return true;
        return entity.Definition[(int)ObjdField.BuildModeType] == 0;
    }

    private static byte[] WriteFamilyWithFlags(Family family, ushort chunkId, uint flags)
    {
        var bytes = family.Write(chunkId);
        if (bytes.Length > 0 && bytes[0] != 0)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), flags);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32, 4), flags);
        return bytes;
    }

    private static IffFile ZeroSimiFunds(IffFile lot)
    {
        var simiType = ResourceType.Parse("SIMI");
        if (lot.Find(simiType, 1) is not { } simiChunk) return lot;
        var simi = SimulationState.Read(lot, simiChunk);
        if (simi.Version <= 1) return lot;
        var globalCount = simi.Version < 0x12 ? 0x10 : simi.Version < 0x41 ? 0x20 : 0x40;
        var offset = 12 + globalCount * 2 + (simi.Version < 0x12 ? 4 : 0) + 6;
        var bytes = simiChunk.Data.ToArray();
        if (bytes.Length >= offset + 4)
        {
            bytes.AsSpan(offset, 4).Clear();
            return lot.WithChunkData(simiType, 1, bytes);
        }
        return lot;
    }

    internal static void RunNamedTreeOnAllObjects(SimulationWorld world, string treeName)
    {
        foreach (var item in world.WalkOrder.ToArray())
        {
            if (world.ObjectById(item.Id) is null) continue;
            if (item.TypeRecord?.BehaviorScope is not BehaviorScope scope) continue;
            var treeId = scope.FindNamedTree(treeName);
            if (treeId == 0) continue;
            try
            {
                TreeRunner.RunWithParameters(world, item, scope, treeId, 0, []);
            }
            catch (SimulationException) { }
        }
        if (world.PendingRemovals.Count > 0)
        {
            foreach (var id in world.PendingRemovals.Distinct().ToArray())
                world.KillObject(id);
            world.PendingRemovals.Clear();
        }
    }

    private static void ClearLotDescriptionStrings(string userPath, string? game, int house, byte language)
    {
        var chunkId = checked((ushort)(house + 2000));
        var fileNames = house switch
        {
            >= 21 and <= 31 => new[] { "DTDesc.iff", "NeighborhoodDesc.iff", "Desc.iff" },
            >= 40 and <= 49 => new[] { "VIDesc.iff", "NeighborhoodDesc.iff", "Desc.iff" },
            >= 81 and <= 89 => new[] { "STDesc.iff", "NeighborhoodDesc.iff", "Desc.iff" },
            >= 90 and <= 99 => new[] { "MTDesc.iff", "NeighborhoodDesc.iff", "Desc.iff" },
            _ => new[] { "NeighborhoodDesc.iff", "Desc.iff" }
        };

        foreach (var fileName in fileNames)
        {
            var housesSource = NeighborhoodExport.ResolveUserFile(userPath, game, Path.Combine("Houses", fileName));
            if (housesSource is not null && File.Exists(housesSource))
            {
                var housesTarget = Path.Combine(userPath, "Houses", Path.GetFileName(housesSource));
                TryClearDescriptionChunk(housesSource, housesTarget, chunkId, language);
            }

            var directSource = NeighborhoodExport.ResolveUserFile(userPath, game, fileName);
            if (directSource is not null && File.Exists(directSource))
            {
                var directTarget = Path.Combine(userPath, Path.GetFileName(directSource));
                TryClearDescriptionChunk(directSource, directTarget, chunkId, language);
            }
        }
    }

    private static void TryClearDescriptionChunk(string sourcePath, string targetPath, ushort chunkId, byte language)
    {
        try
        {
            var openPath = File.Exists(targetPath) ? targetPath : sourcePath;
            var file = IffFile.Open(openPath);
            if (file.Find(ResourceType.Str, chunkId) is not { } chunk) return;
            var table = StringTable.Read(file, chunk);
            var lang = table.Entries.Any(e => e.Language == language) ? language
                : table.Entries.Any(e => e.Language == 1) ? (byte)1
                : table.Entries.FirstOrDefault()?.Language ?? language;
            if (table.Entries.Count(e => e.Language == lang) < 1) return;
            var data1 = table.WriteWithValue(1, "", lang);
            var updated = file.WithChunkDataResized(ResourceType.Str, chunkId, data1);
            var table2 = StringTable.Read(updated, updated.Find(ResourceType.Str, chunkId)!);
            if (table2.Entries.Count(e => e.Language == lang) >= 2)
            {
                var data2 = table2.WriteWithValue(2, "", lang);
                updated = updated.WithChunkDataResized(ResourceType.Str, chunkId, data2);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            WriteReplace(targetPath, updated.Bytes);
        }
        catch { }
    }

    internal static object MoveIn(string userPath, int house, int familyId, string? game = null, byte language = 1)
        => MoveIn(userPath, game ?? "", house, familyId, language);

    internal static object MoveIn(string userPath, string game, int house, int familyId, byte language = 1)
    {
        var zoning = NeighborhoodExport.ReadZoningDictionary(userPath, game);
        if (zoning.TryGetValue((short)house, out var zone) && zone != 0)
            throw new InvalidOperationException("Only a residential lot can be moved into");
        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff")
            ?? throw new FileNotFoundException("Neighborhood.iff not found");
        var ngh = IffFile.Open(nghPath);
        if (FindFamilyForHouse(ngh, house) is not null)
            throw new InvalidOperationException("Someone already lives on this lot");
        var famiType = ResourceType.Parse("FAMI");
        var famiChunk = ngh.Find(famiType, checked((ushort)familyId))
            ?? throw new ArgumentException("The family does not exist");
        var family = Family.Read(ngh, famiChunk);
        if (family.HouseNumber != 0)
            throw new InvalidOperationException("The family already lives in a house");

        var lotPrice = 0;
        var lotSourcePath = NeighborhoodExport.ResolveUserFile(userPath, game, Path.Combine("Houses", $"House{house:D2}.iff"));
        if (lotSourcePath is not null && File.Exists(lotSourcePath))
        {
            var lot = IffFile.Open(lotSourcePath);
            if (lot.Find(ResourceType.Parse("SIMI"), 1) is { } simiChunk)
            {
                var simi = SimulationState.Read(lot, simiChunk);
                lotPrice = Math.Max(0, simi.Field0x9C + simi.Field0xA0 + (int)(simi.Field0xA4 * 0.7f));
            }
        }

        var availableFunds = family.Budget + family.Value;
        if (availableFunds < lotPrice)
            throw new InvalidOperationException("The family cannot afford this lot");

        if (house is 90 or 91 or 92 && ngh.Find(ResourceType.Parse("NGBH"), 1) is { } ngbhChunk)
        {
            var requiredMagiCoins = house == 91 ? 150 : 100;
            var ngbhData = NeighborhoodData.Read(ngh, ngbhChunk);
            var nbrsChunk = ngh.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
            var neighbors = nbrsChunk is not null ? Neighbors.ReadRecoveringSupportedPrefix(ngh, nbrsChunk).Entries : [];
            var byGuid = neighbors.GroupBy(n => n.Guid).ToDictionary(g => g.Key, g => g.First());

            var hasMagiCoinTracking = ngbhData.Version >= 0x40 &&
                (ngbhData.Inventories.Count > 0 || family.Members.Any(g => byGuid.TryGetValue(g, out var n) && ngbhData.Inventories.ContainsKey(n.NeighborId)));
            if (hasMagiCoinTracking)
            {
                var totalMagiCoins = NeighborhoodExport.GetMagicoinsForFamily(family, neighbors, ngbhData);
                if (totalMagiCoins < requiredMagiCoins)
                    throw new InvalidOperationException("The family does not have enough MagiCoins for this lot");

                var updatedInventories = ngbhData.Inventories.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<NeighborhoodData.InventoryItem>)kv.Value.ToList());
                var remaining = requiredMagiCoins;
                foreach (var guid in family.Members)
                {
                    if (remaining <= 0) break;
                    if (!byGuid.TryGetValue(guid, out var neighbor)) continue;
                    if (!updatedInventories.TryGetValue(neighbor.NeighborId, out var roItems)) continue;
                    var items = roItems.ToList();
                    for (var i = 0; i < items.Count && remaining > 0; i++)
                    {
                        var item = items[i];
                        if (!NeighborhoodExport.IsMagiCoinItem(item)) continue;
                        var deduct = Math.Min(remaining, (int)item.Count);
                        var newCount = (ushort)(item.Count - deduct);
                        remaining -= deduct;
                        if (newCount == 0)
                        {
                            items.RemoveAt(i);
                            i--;
                        }
                        else
                        {
                            items[i] = item with { Count = newCount };
                        }
                    }
                    updatedInventories[neighbor.NeighborId] = items;
                }
                var swap = ngbhChunk.Data.Length > 0 && ngbhChunk.Data.Span[0] != 0;
                var updatedNgbh = ngbhData.WithInventories(updatedInventories);
                ngh = ngh.WithChunkDataResized(ngbhChunk.Type, ngbhChunk.Id, updatedNgbh.WriteChunkData(swap));
            }
        }

        var updatedFamily = family
            .WithHouseNumber(house)
            .WithBudget(availableFunds - lotPrice)
            .WithValue(lotPrice);
        var updatedFlags = (family.Flags & ~8u) | 4u;
        ngh = ngh.WithAddedOrReplacedChunk(famiType, famiChunk.Id, WriteFamilyWithFlags(updatedFamily, famiChunk.Id, updatedFlags));
        Directory.CreateDirectory(userPath);
        WriteReplace(Path.Combine(userPath, "Neighborhood.iff"), ngh.Bytes);
        return new { movedIn = (int)famiChunk.Id, house, budget = updatedFamily.Budget, value = updatedFamily.Value };
    }

    internal static object Evict(string userPath, string game, int house, byte language = 1)
    {
        var zoning = NeighborhoodExport.ReadZoningDictionary(userPath, game);
        if (!zoning.TryGetValue((short)house, out var zone) || zone != 0)
            throw new InvalidOperationException("Only a residential lot can be evicted");
        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff")
            ?? throw new FileNotFoundException("Neighborhood.iff not found");
        var ngh = IffFile.Open(nghPath);
        var found = FindFamilyForHouse(ngh, house)
            ?? throw new InvalidOperationException("Nobody lives on this lot");
        var (familyChunkId, family) = found;
        if (family.Field0x13C != 0)
            throw new InvalidOperationException("This family is on vacation");

        var lotSourcePath = NeighborhoodExport.ResolveUserFile(userPath, game, Path.Combine("Houses", $"House{house:D2}.iff"))
            ?? throw new FileNotFoundException($"House{house:D2}.iff not found");
        var lot = IffFile.Open(lotSourcePath);
        var registry = ObjectRegistry.Scan(game, Path.Combine(userPath, "Characters"));
        var library = GlobalLibrary.Open(game);
        var world = SavedLotObjectLoader.Load(lot, registry, library, language);

        RunNamedTreeOnAllObjects(world, "evict lot");

        var removed = 0;
        foreach (var entity in world.WalkOrder.ToArray())
        {
            if (world.ObjectById(entity.Id) is null) continue;
            if (!LeavesOnEvict(entity)) continue;
            if (entity.MultiTileMasterId != 0 && world.ObjectById(entity.MultiTileMasterId) is { } master)
                world.RemoveCreatedObjectGroup(master);
            else
                world.Remove(entity.Id);
            removed++;
        }

        world[SimulationGlobal.IsTutorialHouseNoYes] = 0;
        world[SimulationGlobal.DaysRunning] = 0;
        world.ReplaceSavedValuation(0, world.SavedSimulationState?.Field0xA4 ?? 0);
        if (world.SavedSimulationState is { BudgetDays.Count: > 0 } simi)
        {
            // Writes daily budget history records using version 1 with 8 spending categories.
            var clearedDays = simi.BudgetDays.Select(_ => new BudgetDay(true, new int[8])).ToArray();
            world.ReplaceSavedBudgetDays(clearedDays);
        }

        var (withObjects, _) = world.WriteSavedLotObjectsTo(lot);
        var savedLot = ZeroSimiFunds(world.WriteSavedSimulationStateTo(withObjects));
        var profileHousesDir = Path.Combine(userPath, "Houses");
        Directory.CreateDirectory(profileHousesDir);
        var profileHousePath = Path.Combine(profileHousesDir, $"House{house:D2}.iff");
        WriteReplace(profileHousePath, savedLot.Bytes);
        var runtimePath = profileHousePath + ".runtime.json";
        if (File.Exists(runtimePath)) File.Delete(runtimePath);

        var updatedFamily = family.WithHouseNumber(0).WithBudget(family.Budget + family.Value).WithValue(0);
        var updatedFlags = (family.Flags & ~8u) | 4u;
        ngh = ngh.WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), familyChunkId, WriteFamilyWithFlags(updatedFamily, familyChunkId, updatedFlags));
        var profileNghPath = Path.Combine(userPath, "Neighborhood.iff");
        WriteReplace(profileNghPath, ngh.Bytes);
        return new { evicted = (int)familyChunkId, house, removedObjects = removed };
    }

    internal static object Bulldoze(string userPath, string game, int house, byte language = 1)
    {
        var zoning = NeighborhoodExport.ReadZoningDictionary(userPath, game);
        if (!zoning.ContainsKey((short)house)) throw new InvalidOperationException("The lot has no zoning record");
        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff")
            ?? throw new FileNotFoundException("Neighborhood.iff not found");
        var ngh = IffFile.Open(nghPath);
        if (FindFamilyForHouse(ngh, house) is not null)
            throw new InvalidOperationException("Evict the family before bulldozing its house");

        var lotSourcePath = NeighborhoodExport.ResolveUserFile(userPath, game, Path.Combine("Houses", $"House{house:D2}.iff"))
            ?? throw new FileNotFoundException($"House{house:D2}.iff not found");
        var lot = IffFile.Open(lotSourcePath);
        var registry = ObjectRegistry.Scan(game, Path.Combine(userPath, "Characters"));
        var library = GlobalLibrary.Open(game);
        var world = SavedLotObjectLoader.Load(lot, registry, library, language);
        var tiles = LotTiles.Read(lot);

        world[SimulationGlobal.LotHasHouse] = 0;
        world[SimulationGlobal.LotSize] = 0;
        world[SimulationGlobal.IsTutorialHouseNoYes] = 0;
        world[SimulationGlobal.FilterFlagsForTransitScreen] = 0;
        world[SimulationGlobal.DaysRunning] = 0;
        world.ReplaceSavedValuation(0, 0);
        if (world.SavedSimulationState is { BudgetDays.Count: > 0 } simi)
        {
            var clearedDays = simi.BudgetDays.Select(_ => new BudgetDay(true, new int[8])).ToArray();
            world.ReplaceSavedBudgetDays(clearedDays);
        }

        var deleted = 0;
        foreach (var entity in world.WalkOrder.ToArray())
        {
            if (world.ObjectById(entity.Id) is null) continue;
            var stays = (entity[ObjectData.MovementFlags] & 0x10) != 0;
            var inWorld = entity.Position.X >= 0 && entity.Position.Y >= 0;
            var tileX = entity.Position.X >> 4;
            var tileY = entity.Position.Y >> 4;
            var buildable = tileX is >= 0 and < LotWalls.Size && tileY is >= 0 and < LotWalls.Size && tiles.Buildable(tileX, tileY, 1);
            if (!LeavesOnEvict(entity) && inWorld && (stays || !buildable)) continue;
            if (entity.MultiTileMasterId != 0 && world.ObjectById(entity.MultiTileMasterId) is { } master)
                world.RemoveCreatedObjectGroup(master);
            else
                world.Remove(entity.Id);
            deleted++;
        }

        for (var level = 1; level <= 2; level++)
        {
            var walls = LotWalls.Read(lot, level);
            var floors = LotFloors.Read(lot, level);
            for (var x = 1; x < LotWalls.Size - 1; x++)
            for (var y = 1; y < LotWalls.Size - 1; y++)
            {
                if (!tiles.Buildable(x, y, level)) continue;
                walls[x, y].Clear();
                floors[x, y] = 0;
            }
            if (level == 1)
            {
                for (var x = 0; x < LotWalls.Size; x++)
                for (var y = 0; y < LotWalls.Size; y++)
                    if (floors[x, y] > 65533) floors[x, y] = 0;
            }
            lot = floors.WriteTo(walls.WriteTo(lot));
        }

        var arryType = ResourceType.Parse("Arry");
        if (lot.Find(arryType, 9) is not null)
        {
            var pool = LotPool.Read(lot);
            for (var x = 0; x < LotWalls.Size; x++)
            for (var y = 0; y < LotWalls.Size; y++)
                pool.RemovePool(x, y);
            pool.RebuildDeck(LotTiles.Read(lot), LotTerrain.Read(lot), LotWalls.Read(lot, 1));
            lot = pool.WriteTo(lot);
        }
        if (lot.Find(arryType, 10) is not null)
        {
            var water = LotWater.Read(lot);
            for (var x = 0; x < LotWalls.Size; x++)
            for (var y = 0; y < LotWalls.Size; y++)
                water.ClearWater(x, y);
            lot = water.WriteTo(lot);
        }
        var roof = LotRoof.Read(lot);
        if (roof.Present)
        {
            roof.SetPitch(LotRoof.DefaultPitch);
            roof.SetStyle("");
            lot = roof.WriteTo(lot);
        }

        var (withObjects, _) = world.WriteSavedLotObjectsTo(lot);
        var savedLot = ZeroSimiFunds(world.WriteSavedSimulationStateTo(withObjects));
        var profileHousesDir = Path.Combine(userPath, "Houses");
        Directory.CreateDirectory(profileHousesDir);
        var profileHousePath = Path.Combine(profileHousesDir, $"House{house:D2}.iff");
        WriteReplace(profileHousePath, savedLot.Bytes);
        var runtimePath = profileHousePath + ".runtime.json";
        if (File.Exists(runtimePath)) File.Delete(runtimePath);

        if (ngh.OfType(ResourceType.Parse("NBRS")).FirstOrDefault() is { } nbrsChunk)
        {
            var nbrs = Neighbors.ReadRecoveringSupportedPrefix(ngh, nbrsChunk);
            if (nbrs.IsComplete)
            {
                var allFamilies = ngh.OfType(ResourceType.Parse("FAMI"))
                    .Where(c => c.Id != 0)
                    .Select(c => Family.Read(ngh, c))
                    .ToArray();
                var entries = nbrs.Entries.ToList();
                var removedNeighborIds = new HashSet<short>();
                var charDir = Path.GetFullPath(Path.Combine(userPath, "Characters")) + Path.DirectorySeparatorChar;
                for (var idx = entries.Count - 1; idx >= 0; idx--)
                {
                    var entry = entries[idx];
                    if (entry.PersonData is not { Count: > 67 } pd || pd[67] != house) continue;
                    if (allFamilies.Any(f => f.Members.Contains(entry.Guid))) continue;
                    removedNeighborIds.Add(entry.NeighborId);
                    entries.RemoveAt(idx);
                    if (registry.Find(entry.Guid) is { Scan: ObjectScan.People } charObj)
                    {
                        var charPath = Path.GetFullPath(charObj.Source);
                        if (charPath.StartsWith(charDir, StringComparison.OrdinalIgnoreCase) && File.Exists(charPath))
                            File.Delete(charPath);
                    }
                }
                if (removedNeighborIds.Count > 0)
                {
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var rels = entries[i].Relationships
                            .Where(kv => !removedNeighborIds.Contains((short)kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                        entries[i] = entries[i] with { Relationships = rels };
                    }
                    var updatedNbrs = nbrs.WithEntries(entries);
                    ngh = ngh.WithChunkDataResized(ResourceType.Parse("NBRS"), nbrsChunk.Id,
                        updatedNbrs.WriteChunkData(nbrsChunk.Data.Span[0] != 0));
                    var profileNghPath = Path.Combine(userPath, "Neighborhood.iff");
                    WriteReplace(profileNghPath, ngh.Bytes);
                }
            }
        }

        ClearLotDescriptionStrings(userPath, game, house, language);
        return new { bulldozed = house, deletedObjects = deleted };
    }

    private static object Delete(string userPath, string game, int familyId)
    {
        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff")
            ?? throw new FileNotFoundException("Neighborhood.iff not found");
        var ngh = IffFile.Open(nghPath);
        var famiChunk = ngh.Find(ResourceType.Parse("FAMI"), checked((ushort)familyId))
            ?? throw new ArgumentException("The family does not exist");
        var family = Family.Read(ngh, famiChunk);
        if (family.HouseNumber != 0)
            throw new InvalidOperationException("Evict a family before deleting it");

        var otherFamilies = ngh.OfType(ResourceType.Parse("FAMI"))
            .Where(c => c.Id != 0 && c.Id != famiChunk.Id)
            .Select(c => Family.Read(ngh, c))
            .ToArray();

        if (ngh.OfType(ResourceType.Parse("NBRS")).FirstOrDefault() is { } nbrsChunk)
        {
            var nbrs = Neighbors.ReadRecoveringSupportedPrefix(ngh, nbrsChunk);
            if (nbrs.IsComplete)
            {
                var entries = nbrs.Entries.ToList();
                var removedNeighborIds = new HashSet<short>();
                var charDir = Path.GetFullPath(Path.Combine(userPath, "Characters")) + Path.DirectorySeparatorChar;
                var registry = ObjectRegistry.Scan(game, Path.Combine(userPath, "Characters"));
                foreach (var guid in family.Members)
                {
                    var idx = entries.FindIndex(e => e.Guid == guid);
                    if (idx < 0) continue;
                    var entry = entries[idx];
                    if (entry.PersonData is { Count: > 61 } pd)
                    {
                        var copy = pd.ToArray();
                        copy[61] = 0;
                        entry = entry with { PersonData = copy };
                        entries[idx] = entry;
                    }
                    var shared = otherFamilies.Any(other => other.Members.Contains(guid));
                    if (shared) continue;
                    removedNeighborIds.Add(entry.NeighborId);
                    entries.RemoveAt(idx);
                    if (registry.Find(guid) is { Scan: ObjectScan.People } charObj)
                    {
                        var charPath = Path.GetFullPath(charObj.Source);
                        if (charPath.StartsWith(charDir, StringComparison.OrdinalIgnoreCase) && File.Exists(charPath))
                            File.Delete(charPath);
                    }
                }
                if (removedNeighborIds.Count > 0)
                {
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var rels = entries[i].Relationships
                            .Where(kv => !removedNeighborIds.Contains((short)kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                        entries[i] = entries[i] with { Relationships = rels };
                    }
                }
                var updatedNbrs = nbrs.WithEntries(entries);
                ngh = ngh.WithChunkDataResized(ResourceType.Parse("NBRS"), nbrsChunk.Id,
                    updatedNbrs.WriteChunkData(nbrsChunk.Data.Span[0] != 0));
            }
        }

        ngh = ngh.WithoutChunk(ResourceType.Parse("FAMs"), famiChunk.Id)
                 .WithoutChunk(ResourceType.Parse("FAMI"), famiChunk.Id);
        var profileNghPath = Path.Combine(userPath, "Neighborhood.iff");
        WriteReplace(profileNghPath, ngh.Bytes);
        return new { deleted = familyId };
    }

    internal sealed record ImportCandidateEntry(string File, string FullPath, string FamilyName, int House, string[] Members);

    private sealed record ParsedMember(short OldId, string Name, string Bio, IffChunk BodyChunk, StringTable BodyTable,
        IffChunk? CtssChunk, Neighbor? Record, bool IsPet, bool IsDog, bool IsChild, bool IsFemale, string Skin);

    private sealed record ParsedFam(IffFile Iff, Family Family, IffChunk? FamsChunk, string FamilyName,
        ParsedMember[] Members, short[] OldIds, Dictionary<short, List<NeighborhoodData.InventoryItem>> Inventories);

    private const int MaximumFamFileSize = 32 * 1024 * 1024;
    private const uint TemplatePersonGuid = 0x7FD96B54, TemplateCatGuid = 0x7BEA0977, TemplateDogGuid = 0x4A70DF92;
    internal const ushort StrayFamilyId = 4000;

    internal static (ImportCandidateEntry[] Candidates, bool Enabled) EnumerateImportCandidates(
        string userPath, string? profile = null, string? gameRoot = null, byte language = 1)
    {
        var candidateFolders = new List<string> { Path.Combine(userPath, "Import") };
        if (!string.IsNullOrEmpty(profile))
            candidateFolders.Add(Path.Combine(profile, "UserData", "Import"));
        if (!Directory.Exists(userPath) &&
            (string.IsNullOrEmpty(profile) || !Directory.Exists(Path.Combine(profile, "UserData"))) &&
            !string.IsNullOrEmpty(gameRoot))
        {
            candidateFolders.Add(Path.Combine(gameRoot, "UserData", "Import"));
        }

        var folders = candidateFolders
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (folders.Length == 0) return ([], false);

        var enabled = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ImportCandidateEntry>();
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".fam" or ".rdt" or ".cmt" or ".pub")
                    enabled = true;
                if (ext is not (".fam" or ".rdt" or ".cmt")) continue;
                var fileName = Path.GetFileName(file);
                if (!seen.Add(fileName)) continue;
                try
                {
                    var parsed = ParseFam(file, language);
                    list.Add(new ImportCandidateEntry(
                        fileName,
                        file,
                        parsed.FamilyName,
                        parsed.Family.HouseNumber,
                        parsed.Members.Select(m => m.Name).ToArray()));
                }
                catch (Exception error) when (error is InvalidDataException or ResourceFormatException or IOException or EndOfStreamException or ArgumentException)
                {
                }
            }
        }
        return (list.ToArray(), enabled);
    }

    internal static object ImportCandidate(string userPath, string? profile, string game, int index, byte language = 1)
    {
        var (candidates, _) = EnumerateImportCandidates(userPath, profile, game, language);
        if (index < 0 || index >= candidates.Length)
            throw new ArgumentException("There is no family to import at that position");
        return ImportFile(candidates[index].FullPath, userPath, game, language);
    }

    internal static object? ImportTutorialIfPresent(string userPath, string game, byte language = 1)
    {
        var importDir = Path.Combine(userPath, "Import");
        var path = Directory.Exists(importDir)
            ? Directory.EnumerateFiles(importDir).FirstOrDefault(f => Path.GetFileName(f).Equals("Tutorial.FAM", StringComparison.OrdinalIgnoreCase))
            : null;
        if (path is null) return null;
        try
        {
            var parsed = ParseFam(path, language);
            var house = parsed.Family.HouseNumber;
            short? previousZone = null;
            if (house is > 0 and < 100 && NeighborhoodExport.ReadZoningDictionary(userPath, game).TryGetValue((short)house, out var z) && z != 0)
            {
                previousZone = z;
                ForceLotZoning(userPath, game, house, 0);
            }
            try
            {
                return ImportFile(path, userPath, game, language);
            }
            catch
            {
                if (previousZone is { } pz) ForceLotZoning(userPath, game, house, pz);
                throw;
            }
        }
        catch (Exception error) when (error is InvalidDataException or ResourceFormatException or IOException or InvalidOperationException or ArgumentException or EndOfStreamException)
        {
            Console.Error.WriteLine("SimsSimulation: Couldn't import the family: " + error.Message);
            return null;
        }
    }

    internal static object? SeedStraysIfNeeded(string userPath, string game, byte language = 1)
    {
        if (!Directory.Exists(Path.Combine(game, "ExpansionPack5"))) return null;
        var template = Path.Combine(game, "TemplateFamilyUnleashed", "Strays_4000.FAM");
        if (!File.Exists(template)) return null;
        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff");
        if (nghPath is null || !File.Exists(nghPath)) return null;
        if (IffFile.Open(nghPath).Find(ResourceType.Parse("FAMI"), StrayFamilyId) is not null) return null;
        Directory.CreateDirectory(userPath);
        var copy = Path.Combine(userPath, $"Strays_{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(template, copy);
            return ImportFile(copy, userPath, game, language, StrayFamilyId);
        }
        catch (Exception error) when (error is InvalidDataException or ResourceFormatException or IOException or InvalidOperationException or ArgumentException or EndOfStreamException)
        {
            Console.Error.WriteLine("SimsSimulation: Couldn't seed the strays: " + error.Message);
            return null;
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }

    private static void ForceLotZoning(string userPath, string game, int house, int type)
    {
        var zoningSourcePath = NeighborhoodExport.ResolveUserFile(userPath, game, "LotZoning.iff");
        if (zoningSourcePath is null || !File.Exists(zoningSourcePath)) return;
        var file = IffFile.Open(zoningSourcePath);
        if (file.Find(ResourceType.Str, 1) is not { } chunk) return;
        var table = StringTable.Read(file, chunk);
        var list = table.ForLanguage(1);
        var index = Enumerable.Range(0, list.Count).FirstOrDefault(i => list[i].Split(',')[0].Trim() == house.ToString(CultureInfo.InvariantCulture), -1);
        if (index < 0) return;
        var updatedBytes = table.WriteWithValue(index + 1, $"{house}, {(type == 1 ? "community" : "residential")}", 1);
        var updatedFile = file.WithChunkDataResized(ResourceType.Str, 1, updatedBytes);
        WriteReplace(Path.Combine(userPath, "LotZoning.iff"), updatedFile.Bytes);
    }

    private static ParsedFam ParseFam(string path, byte language = 1)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumFamFileSize)
            throw new InvalidDataException("The family file is too large");
        var iff = IffFile.Open(path);
        var famiType = ResourceType.Parse("FAMI");
        var famiChunk = iff.OfType(famiType).FirstOrDefault(c => c.Id != 0)
            ?? iff.OfType(famiType).FirstOrDefault()
            ?? throw new InvalidDataException("The file has no family record");
        var family = Family.Read(iff, famiChunk);

        var famsType = ResourceType.Parse("FAMs");
        var famsChunk = iff.Find(famsType, famiChunk.Id) ?? iff.OfType(famsType).FirstOrDefault();
        var familyName = famsChunk is not null && StringTable.Read(iff, famsChunk).GetString(0, language) is { Length: > 0 } fn
            ? fn
            : (family.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(path));

        var expiChunk = iff.OfType(ResourceType.Parse("EXPi")).FirstOrDefault()
            ?? throw new InvalidDataException("The file lists no Sims");
        if (expiChunk.Data.Length < 14)
            throw new InvalidDataException("The family's Sim list is truncated");
        var oldIds = new List<short>();
        for (var offset = 14; offset + 2 <= expiChunk.Data.Length && oldIds.Count < 8; offset += 2)
        {
            var id = BinaryPrimitives.ReadInt16LittleEndian(expiChunk.Data.Span[offset..]);
            if (id == 0) break;
            oldIds.Add(id);
        }
        if (oldIds.Count == 0)
            throw new InvalidDataException("The family has no Sims");

        var nbrsChunk = iff.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        var neighbors = nbrsChunk is not null ? Neighbors.ReadRecoveringSupportedPrefix(iff, nbrsChunk).Entries : [];
        var neighborsById = neighbors.GroupBy(n => n.NeighborId).ToDictionary(g => g.Key, g => g.First());

        var uChrType = ResourceType.Parse("uChr");
        var members = new List<ParsedMember>(oldIds.Count);
        foreach (var oldId in oldIds)
        {
            var bodyChunk = iff.Find(uChrType, unchecked((ushort)oldId))
                ?? throw new InvalidDataException($"Sim {oldId} has no body strings");
            var bodyTable = StringTable.Read(iff, bodyChunk);
            var ctssChunk = iff.Find(ResourceType.Ctss, unchecked((ushort)oldId))
                ?? iff.Find(ResourceType.Ctss, unchecked((ushort)(oldId + 2000)));
            var ctssTable = ctssChunk is not null ? StringTable.Read(iff, ctssChunk) : null;
            neighborsById.TryGetValue(oldId, out var record);
            var name = ctssTable is not null && ctssTable.GetString(0, language) is { Length: > 0 } cName
                ? cName
                : record?.Name ?? "";
            var bio = ctssTable is not null ? ctssTable.GetString(1, language) : "";

            var age = bodyTable.GetString(0, 1).Trim().ToLowerInvariant();
            var isPet = age is "kat" or "dog" or "cat";
            var isDog = age == "dog";
            var isChild = age == "child";
            var genderStr = bodyTable.GetString(12, 1).Trim().ToLowerInvariant();
            var isFemale = isPet
                ? genderStr.EndsWith("female", StringComparison.Ordinal)
                : !genderStr.StartsWith("m", StringComparison.Ordinal);
            var rawSkin = bodyTable.GetString(14, 1).Trim().ToLowerInvariant();
            var skin = rawSkin is "lgt" or "med" or "drk" ? rawSkin : "lgt";

            members.Add(new ParsedMember(oldId, name, bio, bodyChunk, bodyTable, ctssChunk, record, isPet, isDog, isChild, isFemale, skin));
        }

        var inventories = new Dictionary<short, List<NeighborhoodData.InventoryItem>>();
        foreach (var chunk in iff.OfType(ResourceType.Parse("FINV")))
        {
            var data = chunk.Data.Span;
            if (data.Length < 6) throw new InvalidDataException($"Sim inventory {chunk.Id} is truncated");
            var owner = BinaryPrimitives.ReadInt16LittleEndian(data);
            var count = BinaryPrimitives.ReadInt32LittleEndian(data[2..]);
            if (count < 0 || count > 65536 || data.Length < 6 + count * 12)
                throw new InvalidDataException($"Sim inventory {chunk.Id} has an invalid token vector");
            var tokens = new List<NeighborhoodData.InventoryItem>(count);
            for (var i = 0; i < count; i++)
            {
                var token = data.Slice(6 + i * 12, 12);
                tokens.Add(new NeighborhoodData.InventoryItem(
                    BinaryPrimitives.ReadInt32LittleEndian(token),
                    BinaryPrimitives.ReadUInt32LittleEndian(token[4..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(token[8..])));
            }
            if (owner != 0) inventories[owner] = tokens;
        }

        return new ParsedFam(iff, family, famsChunk, familyName, members.ToArray(), oldIds.ToArray(), inventories);
    }

    internal static object ImportFile(string path, string userPath, string game, byte language = 1, ushort? keepChunkId = null)
    {
        var parsed = ParseFam(path, language);
        var unleashed = Directory.Exists(Path.Combine(game, "ExpansionPack5"));
        var people = parsed.Members.Where(m => unleashed || !m.IsPet).ToArray();
        if (people.Length == 0)
            throw new InvalidDataException("The family has no Sims that can be imported");

        var nghPath = NeighborhoodExport.ResolveUserFile(userPath, game, "Neighborhood.iff")
            ?? throw new FileNotFoundException("Neighborhood.iff not found");
        var ngh = IffFile.Open(nghPath);
        var charDir = Path.Combine(userPath, "Characters");
        Directory.CreateDirectory(charDir);
        var registry = ObjectRegistry.Scan(game, charDir);

        var famiType = ResourceType.Parse("FAMI");
        var famsType = ResourceType.Parse("FAMs");
        var nbrsType = ResourceType.Parse("NBRS");
        var bmpType = ResourceType.Parse("BMP_");

        var existingFamilies = ngh.OfType(famiType)
            .Where(c => c.Id != 0)
            .Select(c => (c.Id, Family: Family.Read(ngh, c)))
            .ToList();
        var usedFamilyChunkIds = new HashSet<ushort>(existingFamilies.Select(f => f.Id));
        ushort familyChunkId;
        if (keepChunkId is { } kept)
        {
            if (usedFamilyChunkIds.Contains(kept))
                throw new InvalidDataException($"Family {kept} already exists");
            familyChunkId = kept;
        }
        else
        {
            familyChunkId = 1;
            while (usedFamilyChunkIds.Contains(familyChunkId)) familyChunkId++;
        }

        var familyNumber = parsed.Family.FamilyNumber > 0 && existingFamilies.All(f => f.Family.FamilyNumber != parsed.Family.FamilyNumber)
            ? parsed.Family.FamilyNumber
            : (existingFamilies.Count == 0 ? 1 : Math.Max(0, existingFamilies.Max(f => f.Family.FamilyNumber)) + 1);

        var nbrsChunk = ngh.OfType(nbrsType).FirstOrDefault();
        var existingNeighbors = nbrsChunk is not null ? Neighbors.ReadRecoveringSupportedPrefix(ngh, nbrsChunk) : null;
        // Purges neighbor records whose GUIDs cannot be resolved to installed object or character
        // definitions.
        var neighborEntries = existingNeighbors?.Entries
            .Where(e => registry.Find(e.Guid) is not null)
            .ToList() ?? new List<Neighbor>();
        var usedNeighborIds = new HashSet<short>(neighborEntries.Select(e => e.NeighborId));
        var usedSlots = new HashSet<int>(neighborEntries.Select(e => e.Slot));
        var existingGuids = new HashSet<uint>(neighborEntries.Select(e => e.Guid));
        foreach (var entry in registry.Entries) existingGuids.Add(entry.Guid);

        var charNumber = 1;
        var memberGuids = new List<uint>(people.Length);
        var newIds = new Dictionary<short, short>(people.Length);
        var newNeighborsByOldId = new Dictionary<short, Neighbor>(people.Length);
        var guidTable = ObjectTypeTable.ReadGuidTable(parsed.Iff).ToList();

        foreach (var member in people)
        {
            while (File.Exists(Path.Combine(charDir, $"User{charNumber:D5}.iff")))
            {
                charNumber++;
                if (charNumber > 32000) charNumber = 1;
            }
            var charPath = Path.Combine(charDir, $"User{charNumber:D5}.iff");
            charNumber++;

            uint newGuid;
            do
            {
                var timePart = (uint)Environment.TickCount;
                var randPart = (uint)(Random.Shared.Next() & 0xffff);
                newGuid = (timePart << 16) ^ randPart;
            } while (newGuid == 0 || !existingGuids.Add(newGuid));
            memberGuids.Add(newGuid);
            if (member.Record is { Guid: not 0 } oldRecord)
                guidTable.Add((oldRecord.Guid, newGuid));

            var templateGuid = member.IsPet ? (member.IsDog ? TemplateDogGuid : TemplateCatGuid) : TemplatePersonGuid;
            var templateIff = ResolveTemplateCharacterIff(userPath, game, charDir, registry, templateGuid, member.IsPet);
            var objdChunk = templateIff.Find(ResourceType.Objd, 128)
                ?? templateIff.OfType(ResourceType.Objd).FirstOrDefault()
                ?? throw new InvalidDataException("Template character has no OBJD chunk.");
            var objdBytes = objdChunk.Data.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(objdBytes.AsSpan(0x8c, 4), templateGuid);
            BinaryPrimitives.WriteUInt32LittleEndian(objdBytes.AsSpan(0x1c, 4), newGuid);

            var newCharIff = templateIff.WithChunkDataResized(objdChunk.Type, objdChunk.Id, objdBytes);
            newCharIff = newCharIff.WithAddedOrReplacedChunk(ResourceType.Str, 200, member.BodyChunk.Data, 0, "body strings");

            byte[] ctssBytes;
            if (member.CtssChunk is not null && StringTable.Read(parsed.Iff, member.CtssChunk).GetString(0, language).Length > 0)
            {
                ctssBytes = member.CtssChunk.Data.ToArray();
            }
            else
            {
                ctssBytes = BuildCtssChunk(member.Name, member.Bio, language);
            }
            newCharIff = newCharIff.WithAddedOrReplacedChunk(ResourceType.Ctss, 2000, ctssBytes, 0, "catalog strings");

            var renderedPortrait = false;
            try
            {
                var previewPerson = CreateImportPreviewPerson(member);
                var headMeshes = AvatarSceneGeometry.For(game, registry)
                    .BuildHeadMeshes(previewPerson, 1, 0, (_, _) => 0, new LotCamera(0, 128, 128));
                newCharIff = SimPortrait.WithPortraits(newCharIff, headMeshes);
                renderedPortrait = true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"SimsSimulation: portrait for {member.Name} failed: {error.Message}");
            }

            if (!renderedPortrait && parsed.Iff.Find(bmpType, unchecked((ushort)member.OldId)) is { } famBmp)
            {
                newCharIff = newCharIff.WithAddedOrReplacedChunk(bmpType, 2004, famBmp.Data, 0, "thumbnail");
            }

            WriteReplace(charPath, newCharIff.Bytes);
            registry.RegisterFile(newCharIff, charPath);

            var nextSlot = 0;
            while (usedSlots.Contains(nextSlot) || usedNeighborIds.Contains((short)(nextSlot + 1)))
                nextSlot++;
            var nextNeighborId = checked((short)(nextSlot + 1));
            usedSlots.Add(nextSlot);
            usedNeighborIds.Add(nextNeighborId);
            newIds[member.OldId] = nextNeighborId;

            short[] personData;
            if (member.Record?.PersonData is { Count: > 0 } existingPd)
            {
                personData = new short[Math.Max(80, existingPd.Count)];
                for (var i = 0; i < existingPd.Count; i++) personData[i] = existingPd[i];
            }
            else
            {
                personData = new short[80];
                personData[58] = (short)(member.IsChild ? 9 : 18);
                personData[65] = (short)(member.IsFemale ? 1 : 0);
            }
            personData[61] = (short)familyChunkId;
            if (member.IsPet)
                personData[65] = (short)((personData[65] & 1) | (member.IsDog ? 2 : 4));

            var neighbor = new Neighbor(
                Slot: nextSlot,
                RecordVersion: member.Record?.RecordVersion is > 0 ? member.Record.RecordVersion : 8,
                Name: !string.IsNullOrEmpty(member.Record?.Name) ? member.Record.Name : member.Name,
                Field0x1C: member.Record?.Field0x1C ?? 0,
                PersonData: personData,
                NeighborId: nextNeighborId,
                Guid: newGuid,
                Relationships: new Dictionary<int, IReadOnlyList<int>>(),
                StoredFormat: member.Record?.StoredFormat is 4 or 10 ? member.Record.StoredFormat : 10,
                PersonDataTrailingWord: member.Record?.PersonDataTrailingWord ?? 0);
            newNeighborsByOldId[member.OldId] = neighbor;
        }

        foreach (var member in people)
        {
            var neighbor = newNeighborsByOldId[member.OldId];
            if (member.Record?.Relationships is { Count: > 0 } oldRels)
            {
                var mappedRels = new Dictionary<int, IReadOnlyList<int>>();
                foreach (var (otherKey, values) in oldRels)
                {
                    if (newIds.TryGetValue((short)otherKey, out var otherNewId))
                        mappedRels[otherNewId] = values.ToArray();
                }
                neighbor = neighbor with { Relationships = mappedRels };
            }
            neighborEntries.Add(neighbor);
        }

        var house = (short)parsed.Family.HouseNumber;
        var zoning = NeighborhoodExport.ReadZoningDictionary(userPath, game);
        var housed = house is > 0 and < 100 && zoning.ContainsKey(house);
        short? evicted = null;
        var isTutorialLot = false;

        if (housed)
        {
            if (FindFamilyForHouse(ngh, house) is { } previous)
            {
                evicted = (short)previous.ChunkId;
                var movedOut = previous.Family
                    .WithHouseNumber(0)
                    .WithBudget(previous.Family.Budget + previous.Family.Value)
                    .WithValue(0);
                var movedOutFlags = (previous.Family.Flags & ~8u) | 4u;
                ngh = ngh.WithAddedOrReplacedChunk(famiType, previous.ChunkId, WriteFamilyWithFlags(movedOut, previous.ChunkId, movedOutFlags));
            }

            if (parsed.Iff.Find(ResourceType.Parse("SIMI"), 1) is { } simiChunk)
            {
                try
                {
                    var simi = SimulationState.Read(parsed.Iff, simiChunk);
                    isTutorialLot = simi.Globals.Count > (int)SimulationGlobal.IsTutorialHouseNoYes &&
                                    simi.Globals[(int)SimulationGlobal.IsTutorialHouseNoYes] != 0;
                }
                catch { }
            }

            var lotBytes = ExtractLotIffBytes(parsed.Iff, parsed.OldIds, guidTable);
            var housesDir = Path.Combine(userPath, "Houses");
            Directory.CreateDirectory(housesDir);
            var targetHousePath = Path.Combine(housesDir, $"House{house:D2}.iff");
            WriteReplace(targetHousePath, lotBytes);
            var runtimePath = targetHousePath + ".runtime.json";
            if (File.Exists(runtimePath)) File.Delete(runtimePath);
        }

        var importedFamily = parsed.Family
            .WithMembership(familyNumber, memberGuids)
            .WithHouseNumber(housed ? house : 0);
        var friendsCount = NeighborhoodExport.RecountFamilyFriends(importedFamily, neighborEntries);
        var famiBytes = importedFamily.Write(familyChunkId);
        BinaryPrimitives.WriteInt32LittleEndian(famiBytes.AsSpan(28, 4), friendsCount);
        var importedFlags = (importedFamily.Flags == 0 ? 1u : importedFamily.Flags) | 0x8u;
        BinaryPrimitives.WriteUInt32LittleEndian(famiBytes.AsSpan(32, 4), importedFlags);
        ngh = ngh.WithAddedOrReplacedChunk(famiType, familyChunkId, famiBytes, 0, parsed.FamilyName);

        var famsBytes = parsed.FamsChunk is not null && StringTable.Read(parsed.Iff, parsed.FamsChunk).GetString(0, language).Length > 0
            ? parsed.FamsChunk.Data.ToArray()
            : BuildFamsChunk(parsed.FamilyName, language);
        ngh = ngh.WithAddedOrReplacedChunk(famsType, familyChunkId, famsBytes, 0, parsed.FamilyName);

        var maxSlot = neighborEntries.Count == 0 ? 0 : neighborEntries.Max(e => e.Slot) + 1;
        var updatedSlotCount = Math.Max(existingNeighbors?.SlotCount ?? 0, maxSlot);
        var nbrsVersion = existingNeighbors?.Version ?? 73;
        var updatedNbrs = new Neighbors(nbrsVersion, updatedSlotCount, neighborEntries);
        var nbrsSwap = nbrsChunk is not null && nbrsChunk.Data.Length > 0 && nbrsChunk.Data.Span[0] != 0;
        ngh = ngh.WithAddedOrReplacedChunk(nbrsType, nbrsChunk?.Id ?? 1, updatedNbrs.WriteChunkData(nbrsSwap));

        if (ngh.Find(ResourceType.Parse("NGBH"), 1) is { } ngbhChunk)
        {
            var updatedNgbhBytes = UpdateNgbhChunk(ngbhChunk.Data.ToArray(), housed ? house : (short)0, isTutorialLot, newIds, parsed.Inventories);
            ngh = ngh.WithChunkDataResized(ngbhChunk.Type, ngbhChunk.Id, updatedNgbhBytes);
        }

        Directory.CreateDirectory(userPath);
        WriteReplace(Path.Combine(userPath, "Neighborhood.iff"), ngh.Bytes);
        File.Delete(path);

        return new
        {
            imported = (int)familyChunkId,
            name = parsed.FamilyName,
            members = people.Select(p => p.Name).ToArray(),
            house = housed ? house : (short)0,
            evicted,
            skippedPets = parsed.Members.Length - people.Length
        };
    }

    private static IffFile ResolveTemplateCharacterIff(string userPath, string game, string charDir,
        ObjectRegistry registry, uint templateGuid, bool isPet)
    {
        if (isPet && registry.Find(templateGuid) is { } petEntry)
            return petEntry.File;
        string[] candidates =
        [
            Path.Combine(userPath, "Characters", "User00000.iff"),
            Path.Combine(game, "UserData", "Characters", "User00000.iff"),
            Path.Combine(game, "TemplateUserData", "Characters", "User00000.iff"),
            Path.Combine(game, "TemplateUserData", "UserData", "Characters", "User00000.iff")
        ];
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return IffFile.Open(candidate);
        if (registry.Find(templateGuid) is { } tplEntry)
            return tplEntry.File;
        if (Directory.Exists(charDir))
        {
            var files = Directory.GetFiles(charDir, "User*.iff");
            var chosen = !isPet
                ? files.FirstOrDefault(file =>
                {
                    try
                    {
                        var iff = IffFile.Open(file);
                        if (iff.OfType(ResourceType.Objd).FirstOrDefault() is not { } objdChunk) return false;
                        var bodyId = Objd.Read(iff, objdChunk)[ObjdField.BodyStringsId];
                        if (bodyId == 0 || iff.Find(ResourceType.Str, bodyId) is not { } strChunk) return true;
                        var strings = StringTable.Read(iff, strChunk).ForLanguage(1);
                        var mode = strings.Count > 12 ? strings[12].Trim().ToLowerInvariant() : "";
                        return !mode.StartsWith("dog", StringComparison.Ordinal) && !mode.StartsWith("cat", StringComparison.Ordinal);
                    }
                    catch
                    {
                        return false;
                    }
                }) ?? files.FirstOrDefault()
                : files.FirstOrDefault();
            if (chosen is not null)
                return IffFile.Open(chosen);
        }
        throw new FileNotFoundException($"Cannot find character template to clone in {userPath} or {game}.");
    }

    private static XPerson CreateImportPreviewPerson(ParsedMember member)
    {
        static (string, string) Pair(string value)
        {
            var trimmed = value.Trim();
            var separator = trimmed.IndexOf(',');
            if (separator < 0) return (trimmed, string.Empty);
            var mesh = trimmed[..separator].Trim();
            var tex = trimmed[(separator + 1)..].Trim();
            var normalized = mesh.Length > 0 && !mesh.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) && !mesh.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? mesh + ".apr"
                : mesh;
            return (normalized, tex);
        }

        var parts = Enumerable.Repeat((string.Empty, string.Empty), 5).ToArray();
        parts[0] = Pair(member.BodyTable.GetString(1, 1));
        if (!member.IsPet)
        {
            var handCode = member.IsChild ? 'u' : (member.IsFemale ? 'f' : 'm');
            var handTex = "huao" + member.Skin;
            parts[2] = ($"h{handCode}lo.apr", handTex);
            parts[3] = ($"h{handCode}ro.apr", handTex);
            parts[4] = Pair(member.BodyTable.GetString(2, 1));
        }

        var character = new PersonCharacter(0, 0, parts, [], [string.Empty, string.Empty, string.Empty], 3, new float[9]);
        var person = new XPerson(1, new short[XObject.DefinitionCount])
        {
            SavedCharacter = character,
            Position = new ObjectPosition(64 * 16, 64 * 16, 1)
        };
        person[PersonData.PersonAge] = (short)(member.IsChild ? 9 : 27);
        person[ObjectData.Direction] = 3;
        person.AnimatorBodyStringMode = (short)(member.IsPet
            ? (member.IsDog ? (member.IsFemale ? 3 : 2) : (member.IsFemale ? 5 : 4))
            : (member.IsFemale ? 1 : 0));
        return person;
    }

    private static byte[] ExtractLotIffBytes(IffFile iff, short[] oldIds, IReadOnlyList<(uint OldGuid, uint NewGuid)> guidTable)
    {
        var familyTypes = new HashSet<string>(StringComparer.Ordinal) { "FAMI", "FAMs", "EXPi", "uChr", "NBRS", "CTSS", "FINV", "XXXX" };
        var oldIdSet = oldIds.ToHashSet();
        var gtabType = ResourceType.Parse("Gtab");
        var gtabBytes = guidTable.Count > 0 ? ObjectTypeTable.WriteGuidTable(false, guidTable) : null;
        try
        {
            var lotIff = iff;
            foreach (var chunk in iff.Chunks)
            {
                var typeStr = chunk.Type.ToString();
                if (familyTypes.Contains(typeStr) || (typeStr == "BMP_" && oldIdSet.Contains(unchecked((short)chunk.Id))))
                    lotIff = lotIff.WithoutChunk(chunk.Type, chunk.Id);
            }
            if (gtabBytes is not null)
                lotIff = lotIff.WithAddedOrReplacedChunk(gtabType, 1, gtabBytes);
            return lotIff.Bytes.ToArray();
        }
        catch (ResourceFormatException)
        {
            using var stream = new MemoryStream();
            var header = iff.Bytes.Span[..IffFile.HeaderSize].ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60, 4), 0);
            stream.Write(header);
            Span<byte> chunkHeader = new byte[IffFile.ChunkHeaderSize];
            foreach (var chunk in iff.Chunks)
            {
                if (chunk.Type == ResourceType.Rsmp) continue;
                var typeStr = chunk.Type.ToString();
                if (typeStr == "Gtab" && chunk.Id == 1 && gtabBytes is not null) continue;
                if (familyTypes.Contains(typeStr) || (typeStr == "BMP_" && oldIdSet.Contains(unchecked((short)chunk.Id))))
                    continue;
                chunkHeader.Clear();
                BinaryPrimitives.WriteUInt32BigEndian(chunkHeader, chunk.Type.Code);
                BinaryPrimitives.WriteUInt32BigEndian(chunkHeader[4..], (uint)(IffFile.ChunkHeaderSize + chunk.Data.Length));
                BinaryPrimitives.WriteUInt16BigEndian(chunkHeader[8..], chunk.Id);
                BinaryPrimitives.WriteUInt16BigEndian(chunkHeader[10..], chunk.Flags);
                var label = Encoding.Latin1.GetBytes(chunk.Label);
                label.AsSpan(0, Math.Min(label.Length, 64)).CopyTo(chunkHeader[12..]);
                stream.Write(chunkHeader);
                stream.Write(chunk.Data.Span);
            }
            if (gtabBytes is not null)
            {
                chunkHeader.Clear();
                BinaryPrimitives.WriteUInt32BigEndian(chunkHeader, gtabType.Code);
                BinaryPrimitives.WriteUInt32BigEndian(chunkHeader[4..], (uint)(IffFile.ChunkHeaderSize + gtabBytes.Length));
                BinaryPrimitives.WriteUInt16BigEndian(chunkHeader[8..], 1);
                BinaryPrimitives.WriteUInt16BigEndian(chunkHeader[10..], 0);
                stream.Write(chunkHeader);
                stream.Write(gtabBytes);
            }
            return stream.ToArray();
        }
    }

    private static byte[] UpdateNgbhChunk(byte[] ngbhBytes, short house, bool isTutorialLot,
        Dictionary<short, short> newIds, Dictionary<short, List<NeighborhoodData.InventoryItem>> importedInventories)
    {
        if (ngbhBytes.Length < 16 || ngbhBytes[0] != 0) return ngbhBytes;
        var version = BinaryPrimitives.ReadInt32LittleEndian(ngbhBytes.AsSpan(4, 4));
        if (version >= 0x14 && ngbhBytes.Length >= 44 && house > 0)
        {
            // Importing a tutorial lot resets tutorial progress and sets neighborhood variable 0 to the
            // house number; importing non-tutorial lots clears it to 0.
            var currentTutorialHouse = BinaryPrimitives.ReadInt16LittleEndian(ngbhBytes.AsSpan(12, 2));
            if (isTutorialLot)
            {
                BinaryPrimitives.WriteInt16LittleEndian(ngbhBytes.AsSpan(12, 2), house);
                BinaryPrimitives.WriteInt16LittleEndian(ngbhBytes.AsSpan(14, 2), 0);
            }
            else if (currentTutorialHouse == house)
            {
                BinaryPrimitives.WriteInt16LittleEndian(ngbhBytes.AsSpan(12, 2), 0);
            }
        }

        var remappedInventories = new Dictionary<short, List<NeighborhoodData.InventoryItem>>();
        foreach (var (oldId, newId) in newIds)
        {
            if (importedInventories.TryGetValue(oldId, out var items))
                remappedInventories[newId] = items;
        }
        if (remappedInventories.Count == 0 || version < 0x40 || ngbhBytes.Length < 48)
            return ngbhBytes;

        var cursor = 44;
        var entryCount = BinaryPrimitives.ReadInt32LittleEndian(ngbhBytes.AsSpan(cursor, 4));
        if (entryCount < 0 || entryCount > 65536) return ngbhBytes;
        cursor += 4;
        var existingEntries = new List<(short NeighborId, List<NeighborhoodData.InventoryItem> Items)?>();
        for (var i = 0; i < entryCount; i++)
        {
            if (cursor + 4 > ngbhBytes.Length) return ngbhBytes;
            var present = BinaryPrimitives.ReadInt32LittleEndian(ngbhBytes.AsSpan(cursor, 4));
            cursor += 4;
            if (present == 0)
            {
                existingEntries.Add(null);
                continue;
            }
            if (cursor + 6 > ngbhBytes.Length) return ngbhBytes;
            var neighborId = BinaryPrimitives.ReadInt16LittleEndian(ngbhBytes.AsSpan(cursor, 2));
            var itemCount = BinaryPrimitives.ReadInt32LittleEndian(ngbhBytes.AsSpan(cursor + 2, 4));
            cursor += 6;
            if (itemCount < 0 || itemCount > 65536 || cursor + itemCount * 10 > ngbhBytes.Length) return ngbhBytes;
            var items = new List<NeighborhoodData.InventoryItem>(itemCount);
            for (var j = 0; j < itemCount; j++)
            {
                var span = ngbhBytes.AsSpan(cursor + j * 10, 10);
                items.Add(new NeighborhoodData.InventoryItem(
                    BinaryPrimitives.ReadInt32LittleEndian(span),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(span[8..])));
            }
            cursor += itemCount * 10;
            existingEntries.Add((neighborId, items));
        }

        foreach (var (newId, items) in remappedInventories)
        {
            var idx = existingEntries.FindIndex(e => e is { } val && val.NeighborId == newId);
            if (idx >= 0) existingEntries[idx] = (newId, items);
            else existingEntries.Add((newId, items));
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(ngbhBytes, 0, 44);
        writer.Write(existingEntries.Count);
        foreach (var entry in existingEntries)
        {
            if (entry is not { } valid)
            {
                writer.Write(0);
                continue;
            }
            writer.Write(1);
            writer.Write(valid.NeighborId);
            writer.Write(valid.Items.Count);
            foreach (var item in valid.Items)
            {
                writer.Write(item.Type);
                writer.Write(item.GUID);
                writer.Write(item.Count);
            }
        }
        if (cursor < ngbhBytes.Length)
            writer.Write(ngbhBytes, cursor, ngbhBytes.Length - cursor);
        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildCtssChunk(string name, string bio, byte language)
    {
        var encoding = ResourceReader.EncodingForLanguage(language);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((short)-3);
        writer.Write((short)2);
        writer.Write(language);
        writer.Write(encoding.GetBytes(name));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(language);
        writer.Write(encoding.GetBytes(bio));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildFamsChunk(string familyName, byte language)
    {
        var encoding = ResourceReader.EncodingForLanguage(language);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((short)-3);
        writer.Write((short)1);
        writer.Write(language);
        writer.Write(encoding.GetBytes(familyName));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Flush();
        return ms.ToArray();
    }
}
