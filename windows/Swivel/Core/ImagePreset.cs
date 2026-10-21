using System.Text.Json.Serialization;

namespace Swivel;

/// A saved image recipe: maximum long edge, format, quality and metadata preference. Applying it lives in
/// `PresetActions` (Tools/ImagePresets.cs); this is the stored model, shared with the Mac app's JSON shape.
sealed record ImagePreset
{
    [JsonConverter(typeof(JsonStringEnumConverter<PresetFormat>))]
    public enum PresetFormat { [JsonStringEnumMemberName("jpeg")] Jpeg, [JsonStringEnumMemberName("png")] Png,
        [JsonStringEnumMemberName("heic")] Heic, [JsonStringEnumMemberName("tiff")] Tiff }

    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// For the built-in presets: the English name, translated when shown.
    [JsonPropertyName("nameKey")] public string? NameKey { get; init; }
    /// 0 keeps the original dimensions; never enlarges.
    [JsonPropertyName("longEdge")] public int LongEdge { get; init; }
    [JsonPropertyName("format")] public PresetFormat Format { get; init; } = PresetFormat.Jpeg;
    [JsonPropertyName("quality")] public int Quality { get; init; } = 80;
    [JsonPropertyName("stripMetadata")] public bool StripMetadata { get; init; }

    [JsonIgnore] public string DisplayName => NameKey is { } key ? L(key) : Name;
    [JsonIgnore] public string ActionId => $"preset.{Id}";

    [JsonIgnore]
    public bool IsValid =>
        Id.Length > 0 && Name.Trim().Length > 0 && Name.Length <= 60 &&
        (LongEdge == 0 || LongEdge is >= 64 and <= 16384) && Quality is >= 1 and <= 100;

    public static string FormatTitle(PresetFormat f) => f == PresetFormat.Jpeg ? "JPG" : f.ToString().ToUpperInvariant();
    public static string FormatExtension(PresetFormat f) => f == PresetFormat.Jpeg ? "jpg" : f.ToString().ToLowerInvariant();
    public static bool FormatIsLossy(PresetFormat f) => f is PresetFormat.Jpeg or PresetFormat.Heic;

    /// The long-edge field as typed: whole pixels, surrounding spaces allowed.
    public static int? LongEdgeFrom(string text) => int.TryParse(text.Trim(), out var n) ? n : null;

    public enum FormIssue { Name, Size }

    /// Which field of the preset form is wrong (the first one, top to bottom), so the sheet can point at it.
    public static FormIssue? FormIssueFor(string name, string longEdgeText)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 60) return FormIssue.Name;
        if (LongEdgeFrom(longEdgeText) is not { } pixels || !(pixels == 0 || pixels is >= 64 and <= 16384)) return FormIssue.Size;
        return null;
    }

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var size = LongEdge == 0 ? L("Original size") : L("Long edge %d px", LongEdge);
            var quality = FormatIsLossy(Format) ? " · " + L("Quality %d%%", Quality) : "";
            return $"{size} · {FormatTitle(Format)}{quality} · " + L(StripMetadata ? "Remove metadata" : "Keep metadata");
        }
    }

    public static readonly IReadOnlyList<ImagePreset> Starters =
    [
        new() { Id = "web", Name = "Web JPG", NameKey = "Web JPG", LongEdge = 1600, Format = PresetFormat.Jpeg, Quality = 80, StripMetadata = true },
        new() { Id = "small-png", Name = "Small PNG", NameKey = "Small PNG", LongEdge = 1200, Format = PresetFormat.Png, Quality = 100, StripMetadata = true },
        new() { Id = "full-jpg", Name = "Full-size JPG", NameKey = "Full-size JPG", LongEdge = 0, Format = PresetFormat.Jpeg, Quality = 92, StripMetadata = false },
    ];
}
