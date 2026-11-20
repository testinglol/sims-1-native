using SimsEngine.Formats;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public class AudioParityTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void NativeSoundBank_ResolvesVoicePatchesByDemographic()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-voice-demo-{Guid.NewGuid():N}");
        try
        {
            var bank = new SoundBank(gameDir, tempDir);

            var male = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            var female = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            var child = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);

            Assert.NotNull(male);
            Assert.NotNull(female);
            Assert.NotNull(child);

            Assert.Equal("VOX", male.Group);
            Assert.Equal("VOX", female.Group);
            Assert.Equal("VOX", child.Group);

            Assert.Contains("gigglem", male.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglef", female.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglek", child.RelativePath, StringComparison.OrdinalIgnoreCase);

            Assert.NotEqual(male.RelativePath, female.RelativePath);
            Assert.NotEqual(male.RelativePath, child.RelativePath);
            Assert.NotEqual(female.RelativePath, child.RelativePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_NeighborhoodMusicModes_ResolveAllExpansions()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-modes-demo-{Guid.NewGuid():N}");
        try
        {
            var bank = new SoundBank(gameDir, tempDir);

            var superstar = bank.Resolve("station_superstar");
            Assert.NotNull(superstar);
            Assert.Equal("MUSIC", superstar.Group);
            Assert.Contains("sep6_", superstar.RelativePath, StringComparison.OrdinalIgnoreCase);

            var magicTown = bank.Resolve("music_magictown");
            Assert.NotNull(magicTown);
            Assert.Equal("MUSIC", magicTown.Group);
            Assert.Contains("sep7_", magicTown.RelativePath, StringComparison.OrdinalIgnoreCase);

            var vacation = bank.Resolve("station_vacation");
            Assert.NotNull(vacation);
            Assert.Equal("MUSIC", vacation.Group);

            var dt = bank.Resolve("station_dtnhood");
            Assert.NotNull(dt);
            Assert.Equal("MUSIC", dt.Group);

            var unleashed = bank.Resolve("station_unleashed");
            Assert.NotNull(unleashed);
            Assert.Equal("MUSIC", unleashed.Group);

            var nhood = bank.Resolve("bkground_nhood1");
            Assert.NotNull(nhood);
            Assert.Equal("MUSIC", nhood.Group);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_PlaylistRotation_AdvancesTrackIndex()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-playlist-demo-{Guid.NewGuid():N}");
        try
        {
            var bank = new SoundBank(gameDir, tempDir);

            var track1 = bank.Resolve("bkground_buy1");
            var track2 = bank.Resolve("bkground_buy1");
            var track3 = bank.Resolve("bkground_buy1");

            Assert.NotNull(track1);
            Assert.NotNull(track2);
            Assert.NotNull(track3);

            Assert.NotEqual(track1.RelativePath, track2.RelativePath);
            Assert.NotEqual(track2.RelativePath, track3.RelativePath);
            Assert.NotEqual(track1.RelativePath, track3.RelativePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_ResolveVoice_AllDemographics_And_UnknownFallback()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-voice-all-demo-{Guid.NewGuid():N}");
        try
        {
            var bank = new SoundBank(gameDir, tempDir);

            var male = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            var female = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            var child = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);

            Assert.NotNull(male);
            Assert.NotNull(female);
            Assert.NotNull(child);

            Assert.Contains("gigglem", male.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglef", female.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglek", child.RelativePath, StringComparison.OrdinalIgnoreCase);

            var maleUpper = bank.ResolveVoice("vox_laugh_giggle", 'M', 1);
            var femaleUpper = bank.ResolveVoice("vox_laugh_giggle", 'F', 1);
            var childUpper = bank.ResolveVoice("vox_laugh_giggle", 'K', 1);

            Assert.NotNull(maleUpper);
            Assert.NotNull(femaleUpper);
            Assert.NotNull(childUpper);

            Assert.Equal(male.RelativePath, maleUpper.RelativePath);
            Assert.Equal(female.RelativePath, femaleUpper.RelativePath);
            Assert.Equal(child.RelativePath, childUpper.RelativePath);

            // Unknown or invalid demographic codes fall back to adult male ('m') during voice resolution.
            var unknownZ = bank.ResolveVoice("vox_laugh_giggle", 'z', 1);
            var unknownQ = bank.ResolveVoice("vox_laugh_giggle", '?', 1);
            var unknown0 = bank.ResolveVoice("vox_laugh_giggle", '0', 1);
            var unknownEx = bank.ResolveVoice("vox_laugh_giggle", '!', 1);

            Assert.NotNull(unknownZ);
            Assert.NotNull(unknownQ);
            Assert.NotNull(unknown0);
            Assert.NotNull(unknownEx);

            Assert.Equal(male.RelativePath, unknownZ.RelativePath);
            Assert.Equal(male.RelativePath, unknownQ.RelativePath);
            Assert.Equal(male.RelativePath, unknown0.RelativePath);
            Assert.Equal(male.RelativePath, unknownEx.RelativePath);

            // When child voice assets are missing for an event, voice resolution falls back to available
            // female or male variations.
            var childFallback = bank.ResolveVoice("vox_flirtee_giggle", 'k', 1);
            Assert.NotNull(childFallback);
            Assert.True(
                childFallback.RelativePath.Contains("gigglef", StringComparison.OrdinalIgnoreCase) ||
                childFallback.RelativePath.Contains("gigglem", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_CacheKeyIsolation_PreventsCrossDemographicAndVariationCollisions()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-cache-isolation-{Guid.NewGuid():N}");
        try
        {
            var bank = new SoundBank(gameDir, tempDir);

            var m1 = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            Assert.NotNull(m1);
            Assert.Contains("gigglem", m1.RelativePath, StringComparison.OrdinalIgnoreCase);

            var f1 = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            Assert.NotNull(f1);
            Assert.Contains("gigglef", f1.RelativePath, StringComparison.OrdinalIgnoreCase);

            var k1 = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);
            Assert.NotNull(k1);
            Assert.Contains("gigglek", k1.RelativePath, StringComparison.OrdinalIgnoreCase);

            var k2 = bank.ResolveVoice("vox_laugh_giggle", 'k', 2);
            Assert.NotNull(k2);
            Assert.Contains("gigglek", k2.RelativePath, StringComparison.OrdinalIgnoreCase);

            var generic = bank.Resolve("vox_laugh_giggle");

            var m1Recheck = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            var f1Recheck = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            var k1Recheck = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);
            var k2Recheck = bank.ResolveVoice("vox_laugh_giggle", 'k', 2);

            Assert.Equal(m1.RelativePath, m1Recheck!.RelativePath);
            Assert.Equal(f1.RelativePath, f1Recheck!.RelativePath);
            Assert.Equal(k1.RelativePath, k1Recheck!.RelativePath);
            Assert.Equal(k2.RelativePath, k2Recheck!.RelativePath);

            Assert.NotEqual(m1.RelativePath, f1.RelativePath);
            Assert.NotEqual(f1.RelativePath, k1.RelativePath);
            Assert.NotEqual(m1.RelativePath, k1.RelativePath);
            Assert.NotEqual(k1.RelativePath, k2.RelativePath);
            if (generic != null)
            {
                Assert.NotEqual(m1.RelativePath, generic.RelativePath);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_ExpansionMusicModes_AndPlaylistRotation_RoundRobinWrapAround()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-music-modes-rot-{Guid.NewGuid():N}");
        try
        {
            var bank = new SoundBank(gameDir, tempDir);

            var superstar = bank.Resolve("station_superstar");
            Assert.NotNull(superstar);
            Assert.Equal("MUSIC", superstar.Group);
            Assert.Contains("sep6_", superstar.RelativePath, StringComparison.OrdinalIgnoreCase);

            var superstarTrans = bank.Resolve("music_superstar_transition");
            Assert.NotNull(superstarTrans);
            Assert.Equal("MUSIC", superstarTrans.Group);
            Assert.Contains("sep6_", superstarTrans.RelativePath, StringComparison.OrdinalIgnoreCase);

            var magicTown = bank.Resolve("music_magictown");
            Assert.NotNull(magicTown);
            Assert.Equal("MUSIC", magicTown.Group);
            Assert.Contains("sep7_", magicTown.RelativePath, StringComparison.OrdinalIgnoreCase);

            var magicTownCredits = bank.Resolve("music_magictown_credits");
            Assert.NotNull(magicTownCredits);
            Assert.Equal("MUSIC", magicTownCredits.Group);
            Assert.Contains("sep7_credits", magicTownCredits.RelativePath, StringComparison.OrdinalIgnoreCase);

            var unleashed = bank.Resolve("station_unleashed");
            Assert.NotNull(unleashed);
            Assert.Equal("MUSIC", unleashed.Group);

            var nhoodUS = bank.Resolve("nhoodus");
            Assert.NotNull(nhoodUS);
            Assert.Equal("MUSIC", nhoodUS.Group);

            var magicBuild = bank.Resolve("music_magictown_build");
            Assert.NotNull(magicBuild);
            Assert.Equal("MUSIC", magicBuild.Group);

            var magicBuy = bank.Resolve("music_magictown_buy");
            Assert.NotNull(magicBuy);
            Assert.Equal("MUSIC", magicBuy.Group);

            var rotDir = Path.Combine(Path.GetTempPath(), $"sims-music-rot-fresh-{Guid.NewGuid():N}");
            try
            {
                var rotBank = new SoundBank(gameDir, rotDir);

                var superstarTracks = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    var track = rotBank.Resolve("station_superstar");
                    Assert.NotNull(track);
                    superstarTracks.Add(track.RelativePath);
                }

                Assert.Equal(6, superstarTracks.Distinct().Count());

                var wrappedTrack1 = rotBank.Resolve("station_superstar");
                Assert.NotNull(wrappedTrack1);
                Assert.Contains("_0_", wrappedTrack1.RelativePath);
                Assert.Equal(superstarTracks[0], wrappedTrack1.RelativePath);

                var wrappedTrack2 = rotBank.Resolve("station_superstar");
                Assert.NotNull(wrappedTrack2);
                Assert.Contains("_1_", wrappedTrack2.RelativePath);
                Assert.Equal(superstarTracks[1], wrappedTrack2.RelativePath);

                var c1 = rotBank.Resolve("music_magictown_credits");
                var c2 = rotBank.Resolve("music_magictown_credits");
                var c3 = rotBank.Resolve("music_magictown_credits");

                Assert.NotNull(c1);
                Assert.NotNull(c2);
                Assert.NotNull(c3);
                Assert.Equal(c1.RelativePath, c2.RelativePath);
                Assert.Equal(c2.RelativePath, c3.RelativePath);
            }
            finally
            {
                try { Directory.Delete(rotDir, true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeAudio_MRUDuplicateVoiceSuppression_RapidFireAndTimeout()
    {
        var messages = new List<object>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-voice-timing-{Guid.NewGuid():N}");
        try
        {
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) messages.Add(msg);
            });

            var p1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(p1);
            Assert.Single(messages);

            Thread.Sleep(100);
            var p2 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            if (p2)
            {
                Assert.Equal(2, messages.Count);
                var f1 = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
                var f2 = messages[1].GetType().GetProperty("file")?.GetValue(messages[1])?.ToString();
                Assert.NotEqual(f1, f2);
            }

            Thread.Sleep(900);
            var p3 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            if (p3)
            {
                var fLast = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
                Assert.NotNull(fLast);
            }

            Thread.Sleep(3150);

            var prevCount = messages.Count;
            var pAfterTimeout = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(pAfterTimeout);
            Assert.Equal(prevCount + 1, messages.Count);

            var fTimeout = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
            var fInitial = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
            Assert.Equal(fInitial, fTimeout);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeAudio_MRUDuplicateVoiceSuppression_MultiSpeakerIsolation()
    {
        var messages = new List<object>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-voice-multi-{Guid.NewGuid():N}");
        try
        {
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) messages.Add(msg);
            });

            audio.SetSpeakerDemographic(71, 'f');
            audio.SetSpeakerDemographic(72, 'k');
            audio.SetSpeakerDemographic(73, 'm');

            var p71_1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 71, pan: false, zoom: false);
            Assert.True(p71_1);

            var p72_1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 72, pan: false, zoom: false);
            Assert.True(p72_1);

            var p73_1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 73, pan: false, zoom: false);
            Assert.True(p73_1);

            Assert.Equal(3, messages.Count);

            var f71 = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
            var f72 = messages[1].GetType().GetProperty("file")?.GetValue(messages[1])?.ToString();
            var f73 = messages[2].GetType().GetProperty("file")?.GetValue(messages[2])?.ToString();

            Assert.Contains("_f1", f71);
            Assert.Contains("_k1", f72);
            Assert.Contains("_m1", f73);

            var p71_2 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 71, pan: false, zoom: false);
            if (p71_2)
            {
                var f71_2 = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
                Assert.Contains("_f2", f71_2);
            }

            var p72_diff = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 72, pan: false, zoom: false);
            Assert.True(p72_diff);
            var f72_diff = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
            Assert.Contains("vox_laugh_giggle_k1", f72_diff);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeAudio_RealAssets_VoiceAlternationAndSuppression()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var messages = new List<object>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-voice-real-{Guid.NewGuid():N}");
        try
        {
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) messages.Add(msg);
            }, game: gameDir);

            var fPlayed1 = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 55, pan: false, zoom: false, voiceTypeOverride: 'f');
            Assert.True(fPlayed1);

            var fPlayed2 = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 55, pan: false, zoom: false, voiceTypeOverride: 'f');
            Assert.True(fPlayed2);

            Assert.Equal(2, messages.Count);
            var fFile1 = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
            var fFile2 = messages[1].GetType().GetProperty("file")?.GetValue(messages[1])?.ToString();
            Assert.NotNull(fFile1);
            Assert.NotNull(fFile2);
            Assert.NotEqual(fFile1, fFile2);
            Assert.Contains("gigglef", fFile1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglef", fFile2, StringComparison.OrdinalIgnoreCase);

            var mPlayed1 = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 66, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(mPlayed1);
            var countAfterM1 = messages.Count;

            // Immediate repeats of single-variation voice events replay the only available variation
            // instead of returning null.
            var mPlayed2 = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 66, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(mPlayed2);
            Assert.Equal(countAfterM1 + 1, messages.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_ExpansionPacksDownloadsStationsAndPetVox_ResolveParity()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-exp-dl-parity-{Guid.NewGuid():N}");
        try
        {
            using var bank = new SoundBank(gameDir, tempDir);

            var bedCoin = bank.Resolve("Bed_CoinDrop");
            Assert.NotNull(bedCoin);

            var cuckoo = bank.Resolve("Cuckoo_Clock");
            Assert.NotNull(cuckoo);

            var hamster = bank.Resolve("Hamster_Attack");
            Assert.NotNull(hamster);

            // Pet voice events resolve directly by cue name without appending demographic suffixes.
            var dogBark = bank.ResolveVoice("voxdog_barkb", 'm');
            Assert.NotNull(dogBark);
            Assert.Equal("VOX", dogBark.Group);

            var catMew = bank.ResolveVoice("voxcat_mew", 'f');
            Assert.NotNull(catMew);
            Assert.Equal("VOX", catMew.Group);

            Assert.True(bank.IsDemographicVoxEvent("clown_picture_vox"));
            Assert.True(bank.IsDemographicVoxEvent("sick_cough_mild_sitting"));
            var coughF = bank.ResolveVoice("sick_cough_mild_sitting", 'f');
            var coughM = bank.ResolveVoice("sick_cough_mild_sitting", 'm');
            Assert.NotNull(coughF);
            Assert.NotNull(coughM);
            Assert.NotEqual(coughF.RelativePath, coughM.RelativePath);

            Assert.Null(bank.Resolve("bkground_live"));
            Assert.Null(bank.Resolve("bkground_option"));
            Assert.Null(bank.Resolve("bkground_options"));
            Assert.Null(bank.Resolve("bkground_family"));
            Assert.Null(bank.Resolve("bkground_fade"));
            Assert.Equal(0, bank.GetMusicMode("bkground_live"));
            Assert.Equal(7, bank.GetMusicMode("bkground_option"));
            Assert.Equal(7, bank.GetMusicMode("bkground_options"));
            Assert.Equal(8, bank.GetMusicMode("bkground_family"));
            Assert.Equal(9, bank.GetMusicMode("bkground_fade"));

            var radioClass = bank.Resolve("radio_class");
            Assert.NotNull(radioClass);
            Assert.Equal("MUSIC", radioClass.Group);

            var ezCheap = bank.Resolve("station_ezcheap");
            Assert.NotNull(ezCheap);
            Assert.Equal("MUSIC", ezCheap.Group);

            var tvAction = bank.Resolve("tv_action_norm");
            Assert.NotNull(tvAction);

            var piano = bank.Resolve("play_piano");
            Assert.NotNull(piano);
            Assert.Equal("MUSIC", piano.Group);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void PlaySoundEventAndStopAllSounds_ResolveStackObjectIdWhenClockManagerIndexDiffers()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var privateFile = new IffFile(
            ChunkBuilder.File(("FWAV", 10, new ChunkBuilder().CString("shower1_swateron").ToArray())),
            "shower.iff");
        var globalFile = new IffFile(ChunkBuilder.File(), "global.iff");
        var scope = new SimsEngine.Content.BehaviorScope(globalFile, privateFile, null);

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-owner-id-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var audio = new HitAudioRuntime(tempDir, "test", _ => { });
            var world = new SimsEngine.Simulation.SimulationWorld
            {
                PlaySoundEvent = (name, owner, pan, zoom) => audio.PlayForSimulationObject(name, owner, pan, zoom),
                StopOwnerSounds = owner => audio.RemoveOwner(owner)
            };

            var sim = world.Add(new SimsEngine.Simulation.XPerson(100, PrimitiveTestWorld.Definition(SimsEngine.Simulation.XPerson.PersonType)));
            var shower = world.Add(new SimsEngine.Simulation.XObject(42, PrimitiveTestWorld.Definition()));
            short showerStackKey = 2;
            sim.SetObjectTableEntry(showerStackKey, shower);
            Assert.Same(shower, world.ObjectByStackObject(sim, showerStackKey));
            Assert.NotEqual(shower.Id, showerStackKey);

            var simFrame = new SimsEngine.Simulation.StackFrame(stackObject: showerStackKey, [], []) { Table = scope };
            var simContext = new SimsEngine.Simulation.TreeContext(world, sim, simFrame);
            byte[] playOnStackObj = [10, 0, 0, 0, 2, 0, 0, 0];
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.PlaySoundEventPrimitive.Execute(simContext, playOnStackObj));
            Assert.Equal(1, audio.ActiveVoiceCount);

            var showerFrame = new SimsEngine.Simulation.StackFrame(stackObject: 0, [], []) { Table = scope };
            var showerContext = new SimsEngine.Simulation.TreeContext(world, shower, showerFrame);
            byte[] stopOnMe = [0, 0, 0, 0];
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.StopAllSoundsPrimitive.Execute(showerContext, stopOnMe));
            Assert.Equal(0, audio.ActiveVoiceCount);

            byte[] playOnMe = [10, 0, 0, 0, 0, 0, 0, 0];
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.PlaySoundEventPrimitive.Execute(showerContext, playOnMe));
            Assert.Equal(1, audio.ActiveVoiceCount);

            byte[] stopOnStackObj = [1, 0, 0, 0];
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.StopAllSoundsPrimitive.Execute(simContext, stopOnStackObj));
            Assert.Equal(0, audio.ActiveVoiceCount);

            var nullStackFrame = new SimsEngine.Simulation.StackFrame(stackObject: 0, [], []) { Table = scope };
            var nullStackContext = new SimsEngine.Simulation.TreeContext(world, sim, nullStackFrame);
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.PlaySoundEventPrimitive.Execute(nullStackContext, playOnStackObj));
            Assert.Equal(0, audio.ActiveVoiceCount);
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.StopAllSoundsPrimitive.Execute(nullStackContext, stopOnStackObj));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBankAndNativeAudio_ShippedSoundDataLoopClassificationParity()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-loop-class-parity-{Guid.NewGuid():N}");
        try
        {
            var events = new List<object>();
            using var bank = new SoundBank(gameDir, tempDir);
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) events.Add(msg);
            }, game: gameDir)
            {
                SuppressActiveDuplicates = false
            };

            string[] expectedLoops =
            [
                "fire_burning",
                "fire_burning_small",
                "fireplace_loop",
                "shower1_swateron",
                "dishw_srun_cheap",
                "fountain_tinkle",
                "hottub_running",
                "stove_bubble",
                "sink_filling",
                "bkground_load"
            ];
            foreach (var cue in expectedLoops)
            {
                var res = bank.Resolve(cue);
                Assert.True(res is not null, $"Expected '{cue}' to resolve");
                Assert.True(res.IsLoop, $"Expected '{cue}' to be looped (resolved file: {res.RelativePath})");
            }

            string[] expectedPlaylists =
            [
                "bkground_buy1",
                "bkground_build",
                "bkground_nhood1",
                "tv_action_norm",
                "radio_class"
            ];
            foreach (var cue in expectedPlaylists)
            {
                var res = bank.Resolve(cue);
                Assert.True(res is not null, $"Expected '{cue}' to resolve");
                Assert.False(res.IsLoop, $"Expected '{cue}' playlist track to be non-looped (resolved file: {res.RelativePath})");
                Assert.True(res.IsPlaylist, $"Expected '{cue}' to be marked IsPlaylist");
            }

            var fireplace = bank.Resolve("fireplace_loop")!;
            Assert.Contains("fireplace_loop", fireplace.RelativePath, StringComparison.OrdinalIgnoreCase);

            string[] expectedOneShots =
            [
                "door_bamboo_open",
                "door_bamboo_close",
                "coffee_grind_loop",
                "Sting_Fire",
                "ui_panel_collapse",
                "fridge_open",
                "fridge_close",
                "toilet_flush",
                "book_read_vox"
            ];
            foreach (var cue in expectedOneShots)
            {
                var res = bank.Resolve(cue);
                if (res is not null)
                {
                    Assert.False(res.IsLoop, $"Expected '{cue}' to be one-shot (IsLoop=false), got IsLoop=true ({res.RelativePath})");
                }
            }

            events.Clear();
            if (bank.Resolve("door_bamboo_open") is not null)
            {
                Assert.True(audio.PlayForSimulationObject("door_bamboo_open", ownerID: 12, pan: true, zoom: true));
                var bambooMsg = events[^1];
                Assert.Equal(false, bambooMsg.GetType().GetProperty("loop")?.GetValue(bambooMsg));
            }

            if (bank.Resolve("vox_moron_screamb") is not null || bank.IsDemographicVoxEvent("vox_moron_screamb"))
            {
                events.Clear();
                if (audio.PlayForSimulationObject("vox_moron_screamb", ownerID: 13, pan: true, zoom: true, voiceTypeOverride: 'm'))
                {
                    var screamMsg = events[^1];
                    Assert.Equal(false, screamMsg.GetType().GetProperty("loop")?.GetValue(screamMsg));
                }
            }

            events.Clear();
            Assert.True(audio.PlayForSimulationObject("fire_burning_small", ownerID: 88, pan: true, zoom: true));
            Assert.True(audio.PlayForSimulationObject("fire_burning_small", ownerID: 88, pan: true, zoom: true));
            Assert.Single(events);
            Assert.Equal(true, events[0].GetType().GetProperty("loop")?.GetValue(events[0]));
            audio.RemoveOwner(88);
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop");

            events.Clear();
            Assert.True(audio.Play("river_loop", ownerID: -25));
            Assert.True(audio.Play("river_loop", ownerID: -25));
            Assert.Single(events);
            audio.RemoveOwner(-25);
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_FootstepsAndCrossHotIds_NeverCollideWithExpansionHitLists()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-footstep-parity-{Guid.NewGuid():N}");
        try
        {
            using var bank = new SoundBank(gameDir, tempDir);

            (string Cue, string ExpectedSubstring)[] footsteps =
            [
                ("footstep_hard", "feet_hard"),
                ("footstep_terrain", "feet_terrain"),
                ("footstep_soft", "feet_soft"),
                ("footstep_medium", "feet_medium"),
                ("footstep_hard_noshoe", "footstep_hard_noshoe"),
                ("footstep_medium_noshoe", "footstep_medium_noshoe"),
                ("footstep_soft_noshoe", "footstep_soft_noshoe"),
                ("footstep_puddle", "puddle"),
                ("footstep_plant", "plant"),
                ("footstep_trash", "trash"),
                ("footstep_roach", "roach")
            ];

            foreach (var (cue, expectedSubstring) in footsteps)
            {
                var res = bank.Resolve(cue);
                Assert.NotNull(res);
                Assert.Equal("FX", res.Group);
                Assert.False(res.IsLoop);
                Assert.DoesNotContain("stripper", res.RelativePath, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(expectedSubstring, res.RelativePath, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SimulationPause_PausesInWorldPianoMusic_WhileKeepingModeMusicPlaying()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-piano-pause-{Guid.NewGuid():N}");
        try
        {
            var events = new List<object>();
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) events.Add(msg);
            }, game: gameDir);

            Assert.True(audio.Play("bkground_buy1"));
            Assert.True(audio.PlayForSimulationObject("piano_play", ownerID: 77, pan: true, zoom: true, pianoSkillLevel: 0));
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk", ownerID: 55, pan: true, zoom: true));

            var modeVoiceId = Convert.ToInt64(events[0].GetType().GetProperty("voice")!.GetValue(events[0]));
            var pianoVoiceId = Convert.ToInt64(events[1].GetType().GetProperty("voice")!.GetValue(events[1]));
            var honkVoiceId = Convert.ToInt64(events[2].GetType().GetProperty("voice")!.GetValue(events[2]));
            Assert.Equal("MUSIC", events[1].GetType().GetProperty("group")!.GetValue(events[1])?.ToString());
            Assert.Equal(true, events[1].GetType().GetProperty("loop")!.GetValue(events[1]));
            Assert.Equal(77, Convert.ToInt32(events[1].GetType().GetProperty("ownerID")!.GetValue(events[1])));
            Assert.Contains("pianol1", events[1].GetType().GetProperty("file")!.GetValue(events[1])?.ToString(), StringComparison.OrdinalIgnoreCase);

            events.Clear();
            audio.SetSimulationPaused(true);

            Assert.Contains(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "pause" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == pianoVoiceId);
            Assert.Contains(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "pause" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == honkVoiceId);
            Assert.DoesNotContain(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "pause" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == modeVoiceId);

            // Switching modes stops mode music without killing the paused in-world piano voice.
            events.Clear();
            audio.StopModeMusic();
            Assert.Contains(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == modeVoiceId);
            Assert.DoesNotContain(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == pianoVoiceId);

            events.Clear();
            audio.SetSimulationPaused(false);

            Assert.Contains(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "resume" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == pianoVoiceId);
            Assert.Contains(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "resume" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == honkVoiceId);

            // High creativity picks from Level 10 piano tracks.
            events.Clear();
            audio.RemoveOwner(77);
            Assert.True(audio.PlayForSimulationObject("piano_play", ownerID: 77, pan: true, zoom: true, pianoSkillLevel: 10));
            Assert.Contains("pianol10", events[^1].GetType().GetProperty("file")!.GetValue(events[^1])?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FastMode_SuppressesRepeatedCarHonks_IncludingNonPositionalHonkTracks()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-car-honk-fast-{Guid.NewGuid():N}");
        try
        {
            var events = new List<object>();
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) events.Add(msg);
            }, game: gameDir)
            {
                SuppressActiveDuplicates = true
            };

            audio.SetSimulationSpeed(10);

            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk", ownerID: 55, pan: true, zoom: true));
            Assert.Single(events);
            var firstVoiceId = Convert.ToInt64(events[0].GetType().GetProperty("voice")!.GetValue(events[0]));

            // Active duplicate from the same car or another car is suppressed even though car_*_honk uses kArgsNone.
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk", ownerID: 55, pan: true, zoom: true));
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk", ownerID: 56, pan: true, zoom: true));
            Assert.Single(events);

            // Even after the first honk voice ends, rapid re-triggers from the same car stay throttled in fast mode.
            audio.End(firstVoiceId);
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk", ownerID: 55, pan: true, zoom: true));
            Assert.Single(events);

            // A different car arriving after the first honk finished can still honk once.
            Assert.True(audio.PlayForSimulationObject("car_schoolbus_honk", ownerID: 56, pan: true, zoom: true));
            Assert.Equal(2, events.Count);

            // Returning to normal speed (1x) allows the car to honk again without fast-mode throttling.
            audio.SetSimulationSpeed(1);
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk", ownerID: 55, pan: true, zoom: true));
            Assert.Equal(3, events.Count);

            // Expansion local honk cues (HotDate.hot) also resolve and are throttled in fast mode.
            audio.SetSimulationSpeed(10);
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk_local", ownerID: 58, pan: true, zoom: true));
            var localHonkCount = events.Count;
            Assert.True(audio.PlayForSimulationObject("car_standardsedan_honk_local", ownerID: 58, pan: true, zoom: true));
            Assert.Equal(localHonkCount, events.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void StereoStationSwitch_StopsPreviousStation_AndPrunesDeletedOwnerWhilePaused()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-stereo-switch-{Guid.NewGuid():N}");
        try
        {
            var events = new List<object>();
            using var audio = new HitAudioRuntime(tempDir, "test", msg =>
            {
                if (msg is not null) events.Add(msg);
            }, game: gameDir)
            {
                SuppressActiveDuplicates = true
            };

            Assert.True(audio.PlayForSimulationObject("stereo_rock_mod", ownerID: 88, pan: true, zoom: true));
            var rockVoiceId = Convert.ToInt64(events[0].GetType().GetProperty("voice")!.GetValue(events[0]));

            // Switching to Classical on the same stereo stops the Rock voice first so stations never overlap.
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("stereo_classical_mod", ownerID: 88, pan: true, zoom: true));
            Assert.Contains(events, e =>
                e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" &&
                Convert.ToInt64(e.GetType().GetProperty("voice")?.GetValue(e)) == rockVoiceId);
            Assert.Equal(1, audio.ActiveVoiceCount);

            // Deleting the stereo while simulation is paused still prunes its voice on Tick(world).
            audio.SetSimulationPaused(true);
            var emptyWorld = new SimsEngine.Simulation.SimulationWorld();
            audio.Tick(emptyWorld);
            Assert.Equal(0, audio.ActiveVoiceCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

