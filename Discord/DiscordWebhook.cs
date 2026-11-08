using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;
using RadioNorvinsk.Config;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Discord;

/// <summary>
/// Posts messages to the webhook from a background queue, in order, so the game never waits on Discord.
/// Every message is silent (no push notification) and can't ping anyone, whatever its text holds.
/// </summary>
internal sealed class DiscordWebhook
{
    // Discord's SUPPRESS_NOTIFICATIONS message flag: the message lands without a push or desktop notification.
    private const int SuppressNotifications = 1 << 12;
    private const int MaxAttempts = 5;
    private const int QueueCapacity = 100;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _url;
    private readonly PersonaConfig _persona;
    private readonly ISptLogger<RadioNorvinskMod> _logger;

    // While Discord is unreachable, the oldest messages give way to new ones instead of piling up.
    private readonly Channel<WebhookPayload> _queue = Channel.CreateBounded<WebhookPayload>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public DiscordWebhook(Uri url, PersonaConfig persona, ISptLogger<RadioNorvinskMod> logger)
    {
        _url = url;
        _persona = persona;
        _logger = logger;
        _ = Task.Run(PumpAsync);
    }

    /// <param name="label">Names the message in the server console, e.g. "death.headEyes (Reapy)".</param>
    /// <param name="content">The radio's line.</param>
    /// <param name="facts">Optional box of facts under the line.</param>
    public void Post(string label, string content, Embed facts = null)
    {
        _queue.Writer.TryWrite(new WebhookPayload
        {
            Label = label,
            Content = content,
            Username = string.IsNullOrWhiteSpace(_persona.Name) ? null : _persona.Name,
            AvatarUrl = string.IsNullOrWhiteSpace(_persona.AvatarUrl) ? null : _persona.AvatarUrl,
            Embeds = facts is { Fields.Count: > 0 } ? [facts] : null,
            Flags = SuppressNotifications,
        });
    }

    private async Task PumpAsync()
    {
        await foreach (var payload in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await SendAsync(payload);
            }
            catch (Exception ex)
            {
                _logger.Error("[Radio Norvinsk] Unexpected error while posting to Discord.", ex);
            }
        }
    }

    private async Task SendAsync(WebhookPayload payload)
    {
        var watch = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            TimeSpan wait;
            try
            {
                using var response = await Http.PostAsJsonAsync(_url, payload, Json);
                if (response.IsSuccessStatusCode)
                {
                    _logger.OnAir($"[Radio Norvinsk] Posted {payload.Label} ({watch.ElapsedMilliseconds} ms).");
                    return;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    wait = await RetryAfterAsync(response);
                }
                else if ((int)response.StatusCode >= 500)
                {
                    wait = Backoff(attempt);
                }
                else
                {
                    // 4xx other than 429: a bad payload or a deleted webhook. Retrying won't help.
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.Error($"[Radio Norvinsk] Discord refused {payload.Label} ({(int)response.StatusCode}): {Truncate(body, 300)}");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                wait = Backoff(attempt);
                _logger.Debug($"[Radio Norvinsk] Discord unreachable (attempt {attempt}/{MaxAttempts}): {ex.Message}");
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(wait);
            }
        }

        _logger.Warning($"[Radio Norvinsk] Dropped {payload.Label} after {MaxAttempts} attempts.");
    }

    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response)
    {
        // Discord puts the precise delay, in seconds, in the body; the header is rounded up.
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.TryGetProperty("retry_after", out var retryAfter) && retryAfter.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.5, 60));
            }
        }
        catch (JsonException)
        {
        }

        return response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
    }

    private static TimeSpan Backoff(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
    }

    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..length] + "...";
    }

    private sealed class WebhookPayload
    {
        [JsonIgnore]
        public string Label { get; init; }

        [JsonPropertyName("content")]
        public string Content { get; init; }

        [JsonPropertyName("embeds")]
        public Embed[] Embeds { get; init; }

        [JsonPropertyName("username")]
        public string Username { get; init; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; init; }

        [JsonPropertyName("flags")]
        public int Flags { get; init; }

        // Empty list: no @everyone, @here, role or user mention can ping, even if a nickname spells one.
        [JsonPropertyName("allowed_mentions")]
        public AllowedMentions AllowedMentions { get; init; } = new();
    }

    private sealed class AllowedMentions
    {
        [JsonPropertyName("parse")]
        public string[] Parse { get; init; } = [];
    }
}
