using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>Provides audio playback for pre-lot screens such as the loading screen and neighborhood view.</summary>
internal sealed class FrontendAudioServer(TextWriter output, string session, string profile, string? game = null, byte languageCode = 1)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Commands = ["audio", "audioEvent", "audioStopOwner", "audioEnded", "audioState", "audioVolume", "quit"];
    private HitAudioRuntime? audio;
    private bool running = true;

    public async Task Run(ChannelReader<string>? externalInput = null)
    {
        audio = new HitAudioRuntime(Path.Combine(session, "audio"), profile, Write, game);
        Write(new { type = "hello", protocolVersion = 1, mode = "frontendAudio", architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            languageCode, commands = Commands });
        ChannelReader<string> reader;
        if (externalInput is not null)
        {
            reader = externalInput;
        }
        else
        {
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true });
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is { } line)
                    {
                        if (line.Length > 65_536) line = "{\"op\":\"invalidOversizedRequest\"}";
                        await channel.Writer.WriteAsync(line);
                    }
                }
                finally { channel.Writer.TryComplete(); }
            });
            reader = channel.Reader;
        }
        var watch = Stopwatch.StartNew();
        var nextTick = watch.Elapsed.TotalSeconds;
        while (running)
        {
            while (reader.TryRead(out var line)) Handle(line);
            if (reader.Completion.IsCompleted) break;
            var now = watch.Elapsed.TotalSeconds;
            if (now >= nextTick)
            {
                audio.Tick();
                nextTick += 1.0 / 60;
                if (now - nextTick > 0.25) nextTick = now;
            }
            await Task.Delay(2);
        }
        audio.Dispose();
        Write(new { type = "stopped" });
    }

    private void Handle(string line)
    {
        string? id = null;
        string? operation = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            id = request.TryGetProperty("id", out var requestID) ? requestID.ToString() : null;
            operation = request.GetProperty("op").GetString();
            object? result;
            switch (operation)
            {
                case "audio":
                    audio!.Enable(request.GetProperty("enabled").GetBoolean());
                    result = new { enabled = audio.Enabled };
                    break;
                case "audioEvent":
                    // Pre-lot audio only accepts unowned sound events and the neighborhood ambience owner
                    // ID.
                    int? owner = request.TryGetProperty("ownerID", out var ownerValue) ? ownerValue.GetInt16() : null;
                    if (owner.HasValue && owner != -25 && owner != HitAudioRuntime.NeighborhoodOwnerID) throw new ArgumentException("Invalid audio owner");
                    result = new { played = audio!.Play(request.GetProperty("name").GetString()!, null, owner) };
                    break;
                case "audioStopOwner":
                    var stopOwner = request.GetProperty("ownerID").GetInt16();
                    if (stopOwner != -25 && stopOwner != HitAudioRuntime.NeighborhoodOwnerID) throw new ArgumentException("Invalid external audio owner");
                    audio!.RemoveOwner(stopOwner);
                    result = new { removed = true };
                    break;
                case "audioVolume": audio!.SetVolume(request.GetProperty("group").GetString()!, request.GetProperty("volume").GetSingle()); result = audio.State(); break;
                case "audioState": result = audio!.State(); break;
                case "audioEnded": audio!.End(request.GetProperty("voice").GetInt64()); result = new { accepted = true }; break;
                case "quit": running = false; result = new { stopping = true }; break;
                default: throw new ArgumentException("Unsupported frontend audio request");
            }
            Write(new { type = "response", id, operation, ok = true, result });
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine(error);
            Write(new { type = "response", id, operation, ok = false, error = error.Message });
        }
    }

    private void Write(object value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, Json));
        output.Flush();
    }
}
