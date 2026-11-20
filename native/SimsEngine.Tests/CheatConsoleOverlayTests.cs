using System.Numerics;
using SimsEngine.Tests.E2E;
using SimsSDL;
using SimsSDL.Core;
using SimsSDL.UI;

namespace SimsEngine.Tests;

public sealed class CheatConsoleOverlayTests
{
    private static SimsBitmapFont? TryLoadFont()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return null;
        var content = new GameContent(gameDir);
        return new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
    }

    [Fact]
    public void OriginalCheatConsoleOverlay_OpenCloseAndGeometry_MatchesDecompilation()
    {
        if (TryLoadFont() is not { } font)
            return;

        Assert.Equal(new CGRect(20, 20, 200, 21), OriginalCheatConsoleOverlay.LogicalRect);
        Assert.Equal(255, OriginalCheatConsoleOverlay.MaxLength);
        Assert.Equal((0x40, 0x5D, 0x5F, 0xFF), OriginalCheatConsoleOverlay.BackgroundRgba);
        Assert.Equal((255, 255, 255), OriginalCheatConsoleOverlay.ForegroundRgb);

        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        Assert.False(console.Visible);

        console.Open(nowSeconds: 10.0);
        Assert.True(console.Visible);
        Assert.Equal(string.Empty, console.Text);
        Assert.Equal(0, console.CursorIndex);

        console.TextInput(new string('a', 300));
        Assert.Equal(255, console.Text.Length);

        console.Close();
        Assert.False(console.Visible);
        Assert.Equal(string.Empty, console.Text);
        Assert.Equal(0, console.CursorIndex);
    }

    [Fact]
    public void OriginalCheatConsoleOverlay_EditingNavigationAndHistory_Works()
    {
        if (TryLoadFont() is not { } font)
            return;

        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        console.Open(nowSeconds: 1.0);
        console.TextInput("hello world");
        Assert.Equal("hello world", console.Text);
        Assert.Equal(11, console.CursorIndex);

        console.MoveLeft(word: true);
        Assert.Equal(6, console.CursorIndex);
        console.Backspace();
        Assert.Equal("helloworld", console.Text);

        console.MoveHome();
        Assert.Equal(0, console.CursorIndex);
        console.MoveRight(word: false, select: true);
        console.MoveRight(word: false, select: true);
        console.Delete();
        Assert.Equal("lloworld", console.Text);

        console.SelectAll();
        console.TextInput("rosebud");
        Assert.Equal("rosebud", console.Text);

        console.RecordExecutedCommand("rosebud");
        console.RecordExecutedCommand("move_objects on");
        console.Open(nowSeconds: 2.0);
        console.TextInput("partial");
        console.HistoryUp();
        Assert.Equal("move_objects on", console.Text);
        console.HistoryUp();
        Assert.Equal("rosebud", console.Text);
        console.HistoryDown();
        Assert.Equal("move_objects on", console.Text);
        console.HistoryDown();
        Assert.Equal("partial", console.Text);
    }

    [Fact]
    public void OriginalCheatConsoleOverlay_SemicolonAndBangRepeat_MatchesDecompilation()
    {
        if (TryLoadFont() is not { } font)
            return;

        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        console.Open(nowSeconds: 1.0);
        console.TextInput("!");
        Assert.False(console.TryPrepareSegments(out _, out var errTitle, out var errMsg));
        Assert.Equal("Sorry", errTitle);
        Assert.Equal("No previous cheat.", errMsg);

        console.Open(nowSeconds: 2.0);
        console.TextInput("rosebud;!;!");
        Assert.True(console.TryPrepareSegments(out var segments, out _, out _));
        Assert.Equal(["rosebud", "rosebud", "rosebud"], segments);

        console.RecordExecutedCommand("autonomy 50");
        console.Open(nowSeconds: 3.0);
        console.TextInput("! 0");
        Assert.True(console.TryPrepareSegments(out var replacedArgSegments, out _, out _));
        Assert.Equal(["autonomy 0"], replacedArgSegments);

        console.Open(nowSeconds: 4.0);
        console.TextInput("  ; ;  ");
        Assert.True(console.TryPrepareSegments(out var emptySegments, out _, out _));
        Assert.Empty(emptySegments);
    }

    [Fact]
    public void OriginalCheatConsoleOverlay_DrawItems_RendersBoxTextAndBlinkingCaret()
    {
        if (TryLoadFont() is not { } font)
            return;

        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        console.Open(nowSeconds: 10.0);
        console.TextInput("rosebud");

        var onDraws = console.DrawItems(2.0f, nowSeconds: 10.1);
        Assert.Equal(3, onDraws.Count); // background + text + caret
        Assert.Equal(new Vector4(40f, 40f, 400f, 42f), onDraws[0].Rect);

        var offDraws = console.DrawItems(2.0f, nowSeconds: 10.6);
        Assert.Equal(2, offDraws.Count); // background + text (caret blinked off at 0.5s)
    }

    [Fact]
    public void OriginalNeighborhoodOverlay_NessieAndCars_InvokePlaySoundAndActivate()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 0);
        using var server = E2EHarness.StartServer(profile, house: 0);
        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var manifestPath = Path.Combine(profile.Session, "neighborhood.json");

        Assert.True(server.HandleJson("neighborhood", new { mode = 1 }).GetProperty("ok").GetBoolean());
        var overlay = new OriginalNeighborhoodOverlay(content, new UIDevice(), manifestPath, profile.Session, font);
        var played = new List<string>();
        overlay.PlaySound = played.Add;

        Assert.True(overlay.TriggerNessie(time: 1.0));
        Assert.Contains("nessie_sfx", played);
        Assert.False(overlay.TriggerNessie(time: 1.5)); // only one Nessie at a time

        Assert.True(server.HandleJson("neighborhood", new { mode = 5 }).GetProperty("ok").GetBoolean());
        var studio = new OriginalNeighborhoodOverlay(content, new UIDevice(), manifestPath, profile.Session, font);
        studio.TriggerCars(speed: 2, count: 2);
        var draws = studio.DrawItems(new CGSize(800, 600), 1.0f);
        Assert.NotEmpty(draws);
    }
}
