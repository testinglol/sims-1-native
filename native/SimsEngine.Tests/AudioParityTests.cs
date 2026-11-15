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

            // Demographic suffix checks: 'm', 'f', and 'k'
            Assert.Contains("gigglem", male.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglef", female.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglek", child.RelativePath, StringComparison.OrdinalIgnoreCase);

            // Distinct audio files for each demographic
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

            // Superstar / Studio Town (Mode 5)
            var superstar = bank.Resolve("station_superstar");
            Assert.NotNull(superstar);
            Assert.Equal("MUSIC", superstar.Group);
            Assert.Contains("sep6_", superstar.RelativePath, StringComparison.OrdinalIgnoreCase);

            // Makin' Magic / Magic Town (Mode 7)
            var magicTown = bank.Resolve("music_magictown");
            Assert.NotNull(magicTown);
            Assert.Equal("MUSIC", magicTown.Group);
            Assert.Contains("sep7_", magicTown.RelativePath, StringComparison.OrdinalIgnoreCase);

            // Vacation Island (Mode 11)
            var vacation = bank.Resolve("station_vacation");
            Assert.NotNull(vacation);
            Assert.Equal("MUSIC", vacation.Group);

            // Downtown (Mode 10)
            var dt = bank.Resolve("station_dtnhood");
            Assert.NotNull(dt);
            Assert.Equal("MUSIC", dt.Group);

            // Unleashed / Old Town (Mode 4 / 12)
            var unleashed = bank.Resolve("station_unleashed");
            Assert.NotNull(unleashed);
            Assert.Equal("MUSIC", unleashed.Group);

            // Base Neighborhood (Mode 3)
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

            // Consecutive calls to Resolve for mode music should advance through playlist tracks
            var track1 = bank.Resolve("bkground_buy1");
            var track2 = bank.Resolve("bkground_buy1");
            var track3 = bank.Resolve("bkground_buy1");

            Assert.NotNull(track1);
            Assert.NotNull(track2);
            Assert.NotNull(track3);

            // All must be distinct audio files representing sequential rotation
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

            // Valid demographics: m, f, k
            var male = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            var female = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            var child = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);

            Assert.NotNull(male);
            Assert.NotNull(female);
            Assert.NotNull(child);

            Assert.Contains("gigglem", male.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglef", female.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gigglek", child.RelativePath, StringComparison.OrdinalIgnoreCase);

            // Case-insensitivity: 'M', 'F', 'K'
            var maleUpper = bank.ResolveVoice("vox_laugh_giggle", 'M', 1);
            var femaleUpper = bank.ResolveVoice("vox_laugh_giggle", 'F', 1);
            var childUpper = bank.ResolveVoice("vox_laugh_giggle", 'K', 1);

            Assert.NotNull(maleUpper);
            Assert.NotNull(femaleUpper);
            Assert.NotNull(childUpper);

            Assert.Equal(male.RelativePath, maleUpper.RelativePath);
            Assert.Equal(female.RelativePath, femaleUpper.RelativePath);
            Assert.Equal(child.RelativePath, childUpper.RelativePath);

            // Unknown / invalid demographics fallback to adult male ('m')
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

            // Child fallback when 'k' voice is missing:
            // "vox_flirtee_giggle" only has 'f' and 'm' patches, no 'k' patches in Sims 1 assets.
            var childFallback = bank.ResolveVoice("vox_flirtee_giggle", 'k', 1);
            Assert.NotNull(childFallback);
            // Must have fallen back to female ('f') or male ('m')
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

            // 1. Initial resolution of male variation 1
            var m1 = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            Assert.NotNull(m1);
            Assert.Contains("gigglem", m1.RelativePath, StringComparison.OrdinalIgnoreCase);

            // 2. Resolve female variation 1
            var f1 = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            Assert.NotNull(f1);
            Assert.Contains("gigglef", f1.RelativePath, StringComparison.OrdinalIgnoreCase);

            // 3. Resolve child variation 1
            var k1 = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);
            Assert.NotNull(k1);
            Assert.Contains("gigglek", k1.RelativePath, StringComparison.OrdinalIgnoreCase);

            // 4. Resolve child variation 2
            var k2 = bank.ResolveVoice("vox_laugh_giggle", 'k', 2);
            Assert.NotNull(k2);
            Assert.Contains("gigglek", k2.RelativePath, StringComparison.OrdinalIgnoreCase);

            // 5. Generic Resolve (non-voice event)
            var generic = bank.Resolve("vox_laugh_giggle");

            // 6. Re-query all voices and verify cache isolation (no overwrite or collision)
            var m1Recheck = bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
            var f1Recheck = bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
            var k1Recheck = bank.ResolveVoice("vox_laugh_giggle", 'k', 1);
            var k2Recheck = bank.ResolveVoice("vox_laugh_giggle", 'k', 2);

            Assert.Equal(m1.RelativePath, m1Recheck!.RelativePath);
            Assert.Equal(f1.RelativePath, f1Recheck!.RelativePath);
            Assert.Equal(k1.RelativePath, k1Recheck!.RelativePath);
            Assert.Equal(k2.RelativePath, k2Recheck!.RelativePath);

            // Ensure distinctness
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

            // 1. Expansion mode event mappings
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

            // 2. Round-robin playlist rotation with wrap-around
            var rotDir = Path.Combine(Path.GetTempPath(), $"sims-music-rot-fresh-{Guid.NewGuid():N}");
            try
            {
                var rotBank = new SoundBank(gameDir, rotDir);

                // Mode 5 (Superstar) has 6 tracks.
                var superstarTracks = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    var track = rotBank.Resolve("station_superstar");
                    Assert.NotNull(track);
                    superstarTracks.Add(track.RelativePath);
                }

                // Verify all 6 tracks are distinct
                Assert.Equal(6, superstarTracks.Distinct().Count());

                // Call #7 must wrap around and return track index 0
                var wrappedTrack1 = rotBank.Resolve("station_superstar");
                Assert.NotNull(wrappedTrack1);
                Assert.Contains("_0_", wrappedTrack1.RelativePath);
                Assert.Equal(superstarTracks[0], wrappedTrack1.RelativePath);

                // Call #8 must wrap around to track index 1
                var wrappedTrack2 = rotBank.Resolve("station_superstar");
                Assert.NotNull(wrappedTrack2);
                Assert.Contains("_1_", wrappedTrack2.RelativePath);
                Assert.Equal(superstarTracks[1], wrappedTrack2.RelativePath);

                // Mode 19 (Magic Town Credits) has only 1 track.
                // Verify multiple consecutive queries succeed and wrap cleanly without error.
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

            // 1. Rapid fire on speaker 42 within 3.0s window
            // Call 1 at t=0
            var p1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(p1);
            Assert.Single(messages);

            // Call 2 at ~100ms
            Thread.Sleep(100);
            var p2 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            if (p2)
            {
                Assert.Equal(2, messages.Count);
                var f1 = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
                var f2 = messages[1].GetType().GetProperty("file")?.GetValue(messages[1])?.ToString();
                Assert.NotEqual(f1, f2);
            }

            // Call 3 at ~1.0s (still within 3.0s window)
            Thread.Sleep(900);
            var p3 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            if (p3)
            {
                var fLast = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
                Assert.NotNull(fLast);
            }

            // 2. Wait until MRU 3.0s window expires (>3.1s from last call)
            Thread.Sleep(3150);

            // Call after 3.1s window expiry: must start fresh with variation 1
            var prevCount = messages.Count;
            var pAfterTimeout = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 42, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(pAfterTimeout);
            Assert.Equal(prevCount + 1, messages.Count);

            var fTimeout = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
            var fInitial = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
            Assert.Equal(fInitial, fTimeout); // Fresh sequence plays variation 1 again!
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

            // Set demographics per speaker ID
            audio.SetSpeakerDemographic(71, 'f');
            audio.SetSpeakerDemographic(72, 'k');
            audio.SetSpeakerDemographic(73, 'm');

            // Speaker 71 plays (female)
            var p71_1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 71, pan: false, zoom: false);
            Assert.True(p71_1);

            // Speaker 72 plays immediately (child) — must NOT be affected by speaker 71's history
            var p72_1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 72, pan: false, zoom: false);
            Assert.True(p72_1);

            // Speaker 73 plays immediately (male) — must NOT be affected by speaker 71 or 72
            var p73_1 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 73, pan: false, zoom: false);
            Assert.True(p73_1);

            Assert.Equal(3, messages.Count);

            var f71 = messages[0].GetType().GetProperty("file")?.GetValue(messages[0])?.ToString();
            var f72 = messages[1].GetType().GetProperty("file")?.GetValue(messages[1])?.ToString();
            var f73 = messages[2].GetType().GetProperty("file")?.GetValue(messages[2])?.ToString();

            // Distinct demographics used for each speaker
            Assert.Contains("_f1", f71);
            Assert.Contains("_k1", f72);
            Assert.Contains("_m1", f73);

            // Now speaker 71 plays again immediately -> triggers duplicate alternation for speaker 71 only
            var p71_2 = audio.PlayForSimulationObject("vox_talk_neutral", ownerID: 71, pan: false, zoom: false);
            if (p71_2)
            {
                var f71_2 = messages.Last().GetType().GetProperty("file")?.GetValue(messages.Last())?.ToString();
                Assert.Contains("_f2", f71_2);
            }

            // Speaker 72 plays a DIFFERENT event -> not suppressed or alternated
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

            // Female laugh has 2 variations in original assets: vox_laugh_gigglef1 and vox_laugh_gigglef2
            var fPlayed1 = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 55, pan: false, zoom: false, voiceTypeOverride: 'f');
            Assert.True(fPlayed1);

            // Immediate duplicate female laugh alternates to variation 2
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

            // Male laugh has only 1 variation in original assets: vox_laugh_gigglem1
            var mPlayed1 = audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 66, pan: false, zoom: false, voiceTypeOverride: 'm');
            Assert.True(mPlayed1);
            var countAfterM1 = messages.Count;

            // Immediate duplicate male laugh falls back to ChooseNonRepeatingIndex (which plays the single variation)
            // rather than returning null and dropping 1-variation sounds on repeat.
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

            // 1. ExpansionPack.far (Livin' Large) & Downloads/**/*.far
            var bedCoin = bank.Resolve("Bed_CoinDrop");
            Assert.NotNull(bedCoin);

            var cuckoo = bank.Resolve("Cuckoo_Clock");
            Assert.NotNull(cuckoo);

            var hamster = bank.Resolve("Hamster_Attack");
            Assert.NotNull(hamster);

            // 2. Pet VOX events resolve directly without appending 'm'/'f'/'k'
            var dogBark = bank.ResolveVoice("voxdog_barkb", 'm');
            Assert.NotNull(dogBark);
            Assert.Equal("VOX", dogBark.Group);

            var catMew = bank.ResolveVoice("voxcat_mew", 'f');
            Assert.NotNull(catMew);
            Assert.Equal("VOX", catMew.Group);

            // 3. Events ending in "vox" and generic_fm_smartlists
            Assert.True(bank.IsDemographicVoxEvent("clown_picture_vox"));
            Assert.True(bank.IsDemographicVoxEvent("sick_cough_mild_sitting"));
            var coughF = bank.ResolveVoice("sick_cough_mild_sitting", 'f');
            var coughM = bank.ResolveVoice("sick_cough_mild_sitting", 'm');
            Assert.NotNull(coughF);
            Assert.NotNull(coughM);
            Assert.NotEqual(coughF.RelativePath, coughM.RelativePath);

            // 4. Mode 0 (live) and Mode 7 (options) have no music directory (return null)
            Assert.Null(bank.Resolve("bkground_live"));
            Assert.Null(bank.Resolve("bkground_option"));
            Assert.Null(bank.Resolve("bkground_options"));

            // 5. Radio, TV, and Piano stations resolve actual tracks
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
            using var audio = new HitAudioRuntime(tempDir, gameDir, _ => { });
            var world = new SimsEngine.Simulation.SimulationWorld
            {
                PlaySoundEvent = (name, owner, pan, zoom) => audio.PlayForSimulationObject(name, owner, pan, zoom),
                StopOwnerSounds = owner => audio.RemoveOwner(owner)
            };

            // Sim has Id=100; place Shower (Id=42) at 1-based StackObjectKey=2 (2 != 42).
            var sim = world.Add(new SimsEngine.Simulation.XPerson(100, PrimitiveTestWorld.Definition(SimsEngine.Simulation.XPerson.PersonType)));
            var shower = world.Add(new SimsEngine.Simulation.XObject(42, PrimitiveTestWorld.Definition()));
            short showerStackKey = 2;
            sim.SetObjectTableEntry(showerStackKey, shower);
            Assert.Same(shower, world.ObjectByStackObject(sim, showerStackKey));
            Assert.NotEqual(shower.Id, showerStackKey);

            // 1. Start loop via StackObject (flags=2, StackObjectKey=3 -> resolves XObject.Id=42), stop via `me` on shower (word0=0 -> me.Id=42).
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

            // 2. Start loop via `me` on shower (flags=0 -> me.Id=42), stop via StackObject from sim (word0=1, StackObjectKey=3 -> shower.Id=42).
            byte[] playOnMe = [10, 0, 0, 0, 0, 0, 0, 0];
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.PlaySoundEventPrimitive.Execute(showerContext, playOnMe));
            Assert.Equal(1, audio.ActiveVoiceCount);

            byte[] stopOnStackObj = [1, 0, 0, 0];
            Assert.Equal(SimsEngine.Simulation.PrimitiveResult.True, SimsEngine.Simulation.StopAllSoundsPrimitive.Execute(simContext, stopOnStackObj));
            Assert.Equal(0, audio.ActiveVoiceCount);

            // 3. Missing/zero StackObject (StackObjectKey=0) does not attach positional sound to owner 0 or stop unrelated owners.
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

            // Legitimate continuous object/station loops MUST resolve IsLoop = true
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
                "tv_action_norm",
                "radio_class"
            ];
            foreach (var cue in expectedLoops)
            {
                var res = bank.Resolve(cue);
                Assert.True(res is not null, $"Expected '{cue}' to resolve");
                Assert.True(res.IsLoop, $"Expected '{cue}' to be looped (resolved file: {res.RelativePath})");
            }

            // fireplace_loop must resolve the crackling fireplace_loop patch (1819), not the 1s fireplace_on strike (3328)
            var fireplace = bank.Resolve("fireplace_loop")!;
            Assert.Contains("fireplace_loop", fireplace.RelativePath, StringComparison.OrdinalIgnoreCase);

            // One-shot cues (including "amb" substrings like bamboo/screamb, animation "_loop_start"/"_loopa", footsteps, UI, doors, stings, VOX) MUST NOT loop
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

            // Verify HitAudioRuntime.PlayForSimulationObject does NOT force loop=true on "amb" or "_loop" one-shot events
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

            // Re-triggering the same looped cue on the same owner deduplicates, and RemoveOwner stops it cleanly
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("fire_burning_small", ownerID: 88, pan: true, zoom: true));
            Assert.True(audio.PlayForSimulationObject("fire_burning_small", ownerID: 88, pan: true, zoom: true));
            Assert.Single(events);
            Assert.Equal(true, events[0].GetType().GetProperty("loop")?.GetValue(events[0]));
            audio.RemoveOwner(88);
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop");

            // External neighborhood owner (-25) non-positional ambient loop (river_loop) tracks owner -25 so RemoveOwner(-25) stops it
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

            // Track 1912 (footstep_hard) and Track 1913 (footstep_terrain) execute two-part HIT subroutines
            // (tkd_FootstepHard -> opcode 0x3f -> tkd__FootstepHard -> reg5 = 219/220) and collide numerically
            // with HouseParty.hot [GlobalHitList] 1912/1913 (stripper_dance_end_voxm* / stripper_dance_punch_voxm*).
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
}

