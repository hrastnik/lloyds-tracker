using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LloydsTracker;

/// <summary>JSON storage in %APPDATA%\LloydsTracker\ — mirrors the macOS
/// ~/Library/Application Support/LloydsTracker/ layout and schema.</summary>
internal static class Store
{
    public static string Directory
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(baseDir, "LloydsTracker");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Emit Croatian characters (č, š, ž, đ) raw instead of \uXXXX, matching Swift's output.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new IsoDateTimeConverter(),
            new UpperGuidConverter(),
            new LowercaseEnumConverter<EntryKind>(),
            new LowercaseEnumConverter<PromptStyle>(),
        }
    };

    public static string DayKey(DateTime date) => Fmt.DayKey(date);

    public static string DayPath(string key) => Path.Combine(Directory, $"{key}.json");

    public static List<Entry> LoadDay(string key)
    {
        try
        {
            if (!File.Exists(DayPath(key))) return new List<Entry>();
            var data = File.ReadAllText(DayPath(key));
            var entries = JsonSerializer.Deserialize<List<Entry>>(data, Options) ?? new List<Entry>();
            return entries.OrderBy(e => e.Start).ToList();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    public static void SaveDay(string key, IEnumerable<Entry> entries)
    {
        var sorted = entries.OrderBy(e => e.Start).ToList();
        WriteAtomic(DayPath(key), JsonSerializer.Serialize(sorted, Options));
    }

    // MARK: Settings

    private static string SettingsPath => Path.Combine(Directory, "settings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
        => WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Options));

    // MARK: History

    private static string HistoryPath => Path.Combine(Directory, "history.json");

    public static List<string> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new List<string>();
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(HistoryPath), Options) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void SaveHistory(IEnumerable<string> history)
        => WriteAtomic(HistoryPath, JsonSerializer.Serialize(history.ToList(), Options));

    /// <summary>Write via a temp file + move, so a crash mid-write can't corrupt the target.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            // Best-effort persistence; never crash the tracker over a failed write.
        }
    }
}

/// <summary>ISO-8601 UTC ("2026-07-20T14:30:00Z"), stored in UTC, surfaced as local time —
/// matches Swift's .iso8601 Date strategy.</summary>
internal sealed class IsoDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
        // Lenient: accepts with/without fractional seconds and any offset; normalizes to local.
        var dto = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return dto.LocalDateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
}

/// <summary>Uppercase, hyphenated GUID to match Swift's UUID string form.</summary>
internal sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        return Guid.TryParse(s, out var g) ? g : Guid.NewGuid();
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>Enum as its lowercase name ("work"/"pause", "floating"/"fullscreen"),
/// matching Swift's String-backed enums.</summary>
internal sealed class LowercaseEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        return Enum.TryParse<TEnum>(s, ignoreCase: true, out var v) ? v : default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
