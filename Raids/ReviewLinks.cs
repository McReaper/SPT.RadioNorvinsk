using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Raids;

/// <summary>
/// Finds a raid's recording in Raid Review (Ekky's RaidReview server mod), for the message that closes the raid.
/// Nothing here references that mod: it is found by its assembly's name and asked through the web API its own page
/// uses, on the server's loopback. Without it, or without <c>raidReviewUrl</c>, every raid simply goes without a link.
/// </summary>
/// <remarks>
/// Each player whose game runs Raid Review's plugin records the raid on their own. Raid Review keeps no trace of the
/// Fika match, so the raid's recordings are told apart by player and by age: those of its players that are newer than
/// anything Raid Review had finished when the raid started.
/// </remarks>
internal sealed class ReviewLinks
{
    private const string RaidReviewAssembly = "RaidReview";
    private const int DefaultPort = 7829;

    // A recording reaches Raid Review as its player's game leaves the raid, give or take a few seconds: the host's can
    // land after the server has heard the raid is over.
    private const int Attempts = 10;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly JsonDocumentOptions ConfigOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Uri _api;
    private readonly string _publicUrl;
    private readonly ISptLogger<RadioNorvinskMod> _logger;

    // Host id (Fika's match id) -> the newest recording Raid Review had finished when the raid started.
    private readonly ConcurrentDictionary<string, Task<long?>> _since = new();

    private ReviewLinks(Uri api, string publicUrl, ISptLogger<RadioNorvinskMod> logger)
    {
        _api = api;
        _publicUrl = publicUrl;
        _logger = logger;
    }

    /// <summary>No Raid Review, or no address to link to: raids go without a link.</summary>
    public static ReviewLinks Off { get; } = new(null, null, null);

    public static ReviewLinks Create(string publicUrl, ISptLogger<RadioNorvinskMod> logger)
    {
        var raidReview = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == RaidReviewAssembly);
        if (raidReview == null)
        {
            return Off;
        }

        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            logger.OnAir("[Radio Norvinsk] Raid Review is installed: set raidReviewUrl in config/config.json to link each raid to its recording.");
            return Off;
        }

        if (!Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            logger.Error("[Radio Norvinsk] raidReviewUrl in config/config.json is not an http(s) URL: raids go without a link to Raid Review.");
            return Off;
        }

        // The page's own routes (/raid/{id}) follow its path: whatever the setting holds past the path would break them.
        var page = url.GetLeftPart(UriPartial.Path).TrimEnd('/');
        logger.OnAir($"[Radio Norvinsk] Raid Review found: raids end with a link to {page}.");
        return new ReviewLinks(new Uri($"http://127.0.0.1:{Port(raidReview)}/api/raids"), page, logger);
    }

    /// <summary>A match's raid is running: notes what Raid Review already holds, so the raid's own recordings stand out.</summary>
    /// <param name="hostId">Fika's match id, which is its host's profile id.</param>
    public void RaidStarted(string hostId)
    {
        if (_api != null)
        {
            _since[hostId] = Task.Run(NewestFinishedAsync);
        }
    }

    /// <summary>
    /// Hands <paramref name="post"/> the link to the raid's recording, or null when there is none. With Raid Review, on a
    /// background thread, once the recording has reached it: 20 s at most.
    /// </summary>
    public void Find(RaidPlayers raid, Action<string> post)
    {
        if (_api == null || raid == null || !_since.TryRemove(raid.HostId, out var since))
        {
            post(null);
            return;
        }

        _ = Task.Run(async () =>
        {
            string link = null;
            try
            {
                link = await LinkAsync(raid, await since);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.Warning($"[Radio Norvinsk] Raid Review didn't answer, the raid goes without its link: {ex.Message}");
            }

            try
            {
                post(link);
            }
            catch (Exception ex)
            {
                _logger.Error("[Radio Norvinsk] Could not announce the end of a raid.", ex);
            }
        });
    }

    /// <summary>The match is over without anything to link: drops what <see cref="RaidStarted"/> noted.</summary>
    public void Forget(string hostId)
    {
        _since.TryRemove(hostId, out _);
    }

    private async Task<long?> NewestFinishedAsync()
    {
        try
        {
            return (await RecordingsAsync()).Where(recording => recording.Finished).Select(recording => (long?)recording.Id).Max() ?? 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.Warning($"[Radio Norvinsk] Raid Review didn't answer at the start of a raid, which will go without its link: {ex.Message}");
            return null;
        }
    }

    private async Task<string> LinkAsync(RaidPlayers raid, long? since)
    {
        if (since == null)
        {
            return null;
        }

        Recording best = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(Retry);
            }

            // Raid Review drops a recording that never finished: a link to one would soon lead nowhere.
            var recordings = (await RecordingsAsync())
                .Where(recording => recording.Id > since && recording.Finished && raid.Players.Contains(recording.ProfileId))
                .ToList();

            // The host's game runs the bots: only its recording holds their minds (SAIN, quests). The other games see
            // them from the outside. Without it, the longest recording tells the most.
            best = recordings.Where(recording => recording.ProfileId == raid.HostId).MaxBy(recording => recording.Id);
            if (best != null)
            {
                break;
            }

            best = recordings.MaxBy(recording => recording.TimeInRaid);
        }

        // Raid Review's page routes by path, not by fragment: its server answers /raid/{id} with the page itself.
        return best == null ? null : $"{_publicUrl}/raid/{Uri.EscapeDataString(best.RaidId)}";
    }

    // GET /api/raids: every recording Raid Review keeps, newest first, one row of its raid table each.
    private async Task<List<Recording>> RecordingsAsync()
    {
        using var response = await Http.GetAsync(_api);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(body);

        var recordings = new List<Recording>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return recordings;
        }

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && Number(row, "id") is { } id && Text(row, "raidId") is { Length: > 0 } raidId)
            {
                recordings.Add(new Recording(id, raidId, Text(row, "profileId"), Number(row, "timeInRaid") ?? 0,
                    !string.IsNullOrEmpty(Text(row, "exitStatus"))));
            }
        }

        return recordings;
    }

    // Raid Review serves its page and its API on web_client_port, from its own config/config.json.
    private static int Port(Assembly raidReview)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(raidReview.Location) ?? "", "config", "config.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path), ConfigOptions);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("web_client_port", out var value)
                && value.TryGetInt32(out var port) && port is > 0 and <= 65535)
            {
                return port;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
        }

        return DefaultPort;
    }

    // SQLite hands columns back as they were stored: id as a number, timeInRaid as text.
    private static long? Number(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static string Text(JsonElement row, string name)
    {
        return row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <param name="TimeInRaid">In milliseconds.</param>
    /// <param name="Finished">Raid Review has heard the end of it: it has an exit status.</param>
    private sealed record Recording(long Id, string RaidId, string ProfileId, long TimeInRaid, bool Finished);
}

/// <summary>The players of one Fika match.</summary>
/// <param name="HostId">The host's profile id, which is also Fika's match id.</param>
/// <param name="Players">Every player's profile id, the host's included.</param>
internal sealed record RaidPlayers(string HostId, IReadOnlySet<string> Players);
