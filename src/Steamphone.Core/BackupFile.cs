using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Steamphone.Core;

public sealed class BackupFile
{
    [JsonRequired] public string Format { get; set; } = "steamphone-backup";
    [JsonRequired] public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired] public BackupSettings Settings { get; set; } = new();
    [JsonRequired] public List<BackupMapping> Mappings { get; set; } = [];
    [JsonRequired] public List<BackupSession> Sessions { get; set; } = [];
}

public sealed record BackupSettings(BaseStatus DefaultStatus = BaseStatus.Online, string Theme = "System", int PollSeconds = 2,
    VisibilityMode Visibility = VisibilityMode.Public, bool KeepConnectionWhenLocked = false, bool AnnounceGameWhileInvisible = false);
public sealed record BackupMapping(string PackageName, string DisplayName, long SteamAppId, bool Enabled);
public sealed record BackupSession(string SessionId, string PackageName, string DisplayName, long SteamAppId,
    long StartedUtcTicks, long LastObservedUtcTicks, long DurationTicks);
public sealed record ImportSummary(int Mappings, int Sessions);

/// <summary>A portable data format. Credentials and the installation identity are never part of this schema.</summary>
public static partial class BackupCodec
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const int MaximumRows = 100_000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static byte[] Encode(BackupFile backup)
    {
        Validate(backup);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Backup-ul depășește limita de 32 MB.");
        return bytes;
    }

    public static async Task<BackupFile> DecodeAsync(Stream stream, CancellationToken cancellation = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellation)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Fișierul depășește limita de 32 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellation);
        }
        BackupFile backup;
        try
        {
            var bytes = buffer.GetBuffer();
            var offset = buffer.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            backup = JsonSerializer.Deserialize<BackupFile>(bytes.AsSpan(offset, (int)buffer.Length - offset), Options)
                ?? throw new InvalidDataException("Fișierul de backup este gol.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Fișierul nu este un backup Steamphone valid.", ex); }
        Validate(backup);
        return backup;
    }

    public static void Validate(BackupFile backup)
    {
        if (backup.Format != "steamphone-backup" || backup.Version != 1)
            throw new InvalidDataException("Format sau versiune de backup incompatibilă.");
        if (backup.Settings is null || !Enum.IsDefined(backup.Settings.DefaultStatus) || !Enum.IsDefined(backup.Settings.Visibility) ||
            backup.Settings.Theme is not ("System" or "Light" or "Dark") ||
            backup.Settings.PollSeconds is not (1 or 2 or 3 or 5 or 10))
            throw new InvalidDataException("Preferințele din fișier sunt invalide.");
        if (backup.Mappings is null || backup.Sessions is null ||
            backup.Mappings.Count > MaximumRows || backup.Sessions.Count > MaximumRows)
            throw new InvalidDataException("Fișierul conține prea multe înregistrări sau liste invalide.");
        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in backup.Mappings)
        {
            if (mapping is null) throw new InvalidDataException("Mapare invalidă.");
            ValidateGame(mapping.PackageName, mapping.DisplayName, mapping.SteamAppId);
            if (!packages.Add(mapping.PackageName)) throw new InvalidDataException("Fișierul conține mapări duplicate.");
        }
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var session in backup.Sessions)
        {
            if (session is null) throw new InvalidDataException("Sesiune invalidă.");
            ValidateGame(session.PackageName, session.DisplayName, session.SteamAppId);
            if (!Guid.TryParseExact(session.SessionId, "N", out _) || !sessions.Add(session.SessionId) ||
                session.DurationTicks < 0 || session.StartedUtcTicks < 0 || session.StartedUtcTicks > DateTime.MaxValue.Ticks ||
                session.LastObservedUtcTicks < 0 || session.LastObservedUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Fișierul conține sesiuni invalide sau duplicate.");
            try { total = checked(total + session.DurationTicks); }
            catch (OverflowException ex) { throw new InvalidDataException("Durata totală din fișier este prea mare.", ex); }
        }
    }

    private static void ValidateGame(string package, string name, long appId)
    {
        if (string.IsNullOrWhiteSpace(package) || package.Length > 255 || !PackagePattern().IsMatch(package) ||
            string.IsNullOrWhiteSpace(name) || name.Length > 512 || appId is <= 0 or > uint.MaxValue)
            throw new InvalidDataException("Fișierul conține un nume de aplicație, pachet sau AppID invalid.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
}
