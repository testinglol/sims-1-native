using System.Text.Json.Serialization;

namespace SimsSDL;

public sealed record PluginEnvironment
{
    public sealed record EffectData
    {
        public sealed record Footprint
        {
            [JsonPropertyName("sequence")]
            public long Sequence { get; init; }

            [JsonPropertyName("x")]
            public float X { get; init; }

            [JsonPropertyName("y")]
            public float Y { get; init; }

            [JsonPropertyName("heading")]
            public float Heading { get; init; }

            [JsonPropertyName("foot")]
            public float Foot { get; init; }
        }

        public sealed record IndicatorData
        {
            [JsonPropertyName("index")]
            public int Index { get; init; }

            [JsonPropertyName("count")]
            public int Count { get; init; }

            [JsonPropertyName("strip")]
            public string Strip { get; init; } = string.Empty;

            [JsonPropertyName("weatherIcon")]
            public string WeatherIcon { get; init; } = string.Empty;

            [JsonPropertyName("weatherTooltip")]
            public string WeatherTooltip { get; init; } = string.Empty;

            [JsonPropertyName("cycleTooltip")]
            public string CycleTooltip { get; init; } = string.Empty;

            [JsonPropertyName("window")]
            public string Window { get; init; } = string.Empty;

            [JsonPropertyName("overlay")]
            public string Overlay { get; init; } = string.Empty;
        }

        public sealed record AmbientSoundData
        {
            [JsonPropertyName("cue")]
            public string Cue { get; init; } = string.Empty;

            [JsonPropertyName("resource")]
            public string Resource { get; init; } = string.Empty;

            [JsonPropertyName("gain")]
            public float Gain { get; init; }
        }

        [JsonPropertyName("surfaceMode")]
        public int SurfaceMode { get; init; }

        [JsonPropertyName("surfaceAmount")]
        public float SurfaceAmount { get; init; }

        [JsonPropertyName("cloudCover")]
        public float CloudCover { get; init; }

        [JsonPropertyName("precipitation")]
        public float Precipitation { get; init; }

        [JsonPropertyName("wetness")]
        public float Wetness { get; init; }

        [JsonPropertyName("audioGain")]
        public float AudioGain { get; init; }

        [JsonPropertyName("audioCutoff")]
        public float AudioCutoff { get; init; }

        [JsonPropertyName("atmosphere")]
        public bool Atmosphere { get; init; }

        [JsonPropertyName("displacedSurface")]
        public bool DisplacedSurface { get; init; }

        [JsonPropertyName("trailRecovery")]
        public float TrailRecovery { get; init; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, float> Parameters { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("footprints")]
        public List<Footprint> Footprints { get; init; } = [];

        [JsonPropertyName("indicator")]
        public IndicatorData? Indicator { get; init; }

        [JsonPropertyName("shaders")]
        public Dictionary<string, string> Shaders { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("ambientSounds")]
        public List<AmbientSoundData> AmbientSounds { get; init; } = [];
    }

    [JsonPropertyName("packId")]
    public string PackId { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("assetRoot")]
    public string AssetRoot { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public EffectData Effect { get; init; } = new();

    public string Asset(string path) => Path.Combine(AssetRoot, path);
}
