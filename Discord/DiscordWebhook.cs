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
    // One reader, in order: an edit always comes after the post of the message it edits is settled.
    private readonly Channel<Delivery> _queue = Channel.CreateBounded<Delivery>(
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
    /// <returns>The message, which <see cref="Edit"/> can change once it is out.</returns>
    public PostedMessage Post(string label, string content, Embed facts = null)
    {
        var message = new PostedMessage(label, content);
        var payload = new WebhookPayload
        {
            Content = content,
            Username = string.IsNullOrWhiteSpace(_persona.Name) ? null : _persona.Name,
            AvatarUrl = string.IsNullOrWhiteSpace(_persona.AvatarUrl) ? null : _persona.AvatarUrl,
            Embeds = facts is { Fields.Count: > 0 } ? [facts] : null,
            Flags = SuppressNotifications,
        };

        // wait=true: Discord answers with the message, whose id the radio needs to edit it.
        _queue.Writer.TryWrite(new Delivery(label, "Posted",
            () => Request(HttpMethod.Post, Query(_url, "wait=true"), payload),
            async response => message.Id = await MessageIdAsync(response)));
        return message;
    }

    /// <summary>
    /// Replaces the text of a message posted earlier. Its facts card, name and avatar stay. Nothing happens when the
    /// message never went out.
    /// </summary>
    public void Edit(PostedMessage message, string label, string content)
    {
        var payload = new WebhookEdit { Content = content };
        _queue.Writer.TryWrite(new Delivery(label, "Edited",
            () => message.Id == null ? null : Request(HttpMethod.Patch, MessageUrl(message.Id), payload),
            null));
    }

    private async Task PumpAsync()
    {
        await foreach (var delivery in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await SendAsync(delivery);
            }
            catch (Exception ex)
            {
                _logger.Error("[Radio Norvinsk] Unexpected error while posting to Discord.", ex);
            }
        }
    }

    private async Task SendAsync(Delivery delivery)
    {
        var watch = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            TimeSpan wait;
            try
            {
                using var request = delivery.Request();
                if (request == null)
                {
                    _logger.Debug($"[Radio Norvinsk] Skipped {delivery.Label}: the message it edits never went out.");
                    return;
                }

                using var response = await Http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    if (delivery.Delivered != null)
                    {
                        await delivery.Delivered(response);
                    }

                    _logger.OnAir($"[Radio Norvinsk] {delivery.Verb} {delivery.Label} ({watch.ElapsedMilliseconds} ms).");
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
                    // 4xx other than 429: a bad payload, a deleted webhook or message. Retrying won't help.
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.Error($"[Radio Norvinsk] Discord refused {delivery.Label} ({(int)response.StatusCode}): {Truncate(body, 300)}");
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

        _logger.Warning($"[Radio Norvinsk] Dropped {delivery.Label} after {MaxAttempts} attempts.");
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri url, object payload)
    {
        return new HttpRequestMessage(method, url) { Content = JsonContent.Create(payload, payload.GetType(), options: Json) };
    }

    // The webhook's own query (thread_id, for a thread) stays on every call.
    private static Uri Query(Uri url, string parameter)
    {
        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?');
        builder.Query = query.Length == 0 ? parameter : $"{query}&{parameter}";
        return builder.Uri;
    }

    private Uri MessageUrl(string id)
    {
        var builder = new UriBuilder(_url);
        builder.Path = $"{builder.Path.TrimEnd('/')}/messages/{id}";
        return builder.Uri;
    }

    // Reading the answer can't fail the post: the message is out, it just can't be edited.
    private static async Task<string> MessageIdAsync(HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                   && ulong.TryParse(id.GetString(), out _)
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
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

    /// <summary>One call to Discord, waiting in the queue.</summary>
    /// <param name="Verb">For the console: "Posted", "Edited".</param>
    /// <param name="Request">Built for each attempt. Null: nothing to send any more.</param>
    /// <param name="Delivered">Reads Discord's answer, once the call went through.</param>
    private sealed record Delivery(string Label, string Verb, Func<HttpRequestMessage> Request, Func<HttpResponseMessage, Task> Delivered);

    private sealed class WebhookPayload
    {
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

    // Fields left out stay as they were: the embeds, name and avatar.
    private sealed class WebhookEdit
    {
        [JsonPropertyName("content")]
        public string Content { get; init; }

        [JsonPropertyName("allowed_mentions")]
        public AllowedMentions AllowedMentions { get; init; } = new();
    }

    private sealed class AllowedMentions
    {
        [JsonPropertyName("parse")]
        public string[] Parse { get; init; } = [];
    }
}

/// <summary>A message on its way to Discord. Its id comes once Discord has it, and lets the radio edit it.</summary>
internal sealed class PostedMessage(string label, string content)
{
    public string Label { get; } = label;
    public string Content { get; } = content;

    /// <summary>Discord's id for the message. Null until it is out, and for good when it never went out.</summary>
    public string Id { get; set; }
}
