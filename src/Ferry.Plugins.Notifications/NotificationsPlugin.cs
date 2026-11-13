using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ferry.Core;
using Ferry.Core.Http;
using Ferry.Core.Settings;
using Ferry.Core.Sites;

namespace Ferry.Plugins.Notifications;

/// <summary>What the Notifications plugin adds to Ferry: a message elsewhere when a batch of downloads finishes.</summary>
public static class NotificationsPlugin
{
    /// <summary>Off on a new install: it needs somewhere to send to, which you set up.</summary>
    public static readonly SettingKey Enabled = new("FERRY_PLUGIN_NOTIFICATIONS", SettingType.Boolean, "false",
        "A Telegram, Discord or webhook message when a batch of downloads finishes.")
    { Title = "Notifications" };

    private static readonly SettingKey TelegramChat = new("FERRY_TELEGRAM_CHAT", SettingType.Text, "",
        "The chat for your Telegram bot to write to.")
    { Title = "Telegram chat id" };

    private static readonly SettingKey FileNames = new("FERRY_NOTIFY_FILE_NAMES", SettingType.Boolean, "false",
        "Say which files finished, not only how many. They then pass through whichever service you send to.")
    { Title = "Include file names" };

    private static readonly PluginSecret TelegramToken = new("telegram-bot-token", "Telegram bot token", "From @BotFather. Kept encrypted on this PC.");
    private static readonly PluginSecret DiscordWebhook = new("discord-webhook", "Discord webhook", "A channel's webhook address. Kept encrypted on this PC.");
    private static readonly PluginSecret Webhook = new("notify-webhook", "Webhook", "An https address that is sent the message as JSON. Kept encrypted on this PC.");

    public static SitePlugin Plugin { get; } = new("Notifications", Enabled)
    {
        About = new(PluginGroup.Around, "\uEA8F", [], []),
        Options = [TelegramChat, FileNames],
        Secrets = [TelegramToken, DiscordWebhook, Webhook],
    };

    /// <summary>Tell everywhere that is set up; returns what couldn't be reached, for the log.</summary>
    public static async Task<IReadOnlyList<Exception>> SendAsync(PluginContext context, int done, int failed, IEnumerable<string> names, CancellationToken cancel)
    {
        var summary = failed > 0 ? $"{Product.Name}: {done} done, {failed} failed" : $"{Product.Name}: {done} done";
        var text = context.Settings.Boolean(FileNames) ? string.Join('\n', names.Prepend(summary)) : summary;
        var (http, sends) = (context.Http, new List<Task>());
        if (context.Secret(TelegramToken) is { } token && context.Settings.Text(TelegramChat) is { Length: > 0 } chat)
            sends.Add(PostAsync(http, new Uri($"https://api.telegram.org/bot{Uri.EscapeDataString(token)}/sendMessage"), JsonContent.Create(new TelegramMessage(chat, text), NotificationsJson.Default.TelegramMessage), cancel));
        if (Https(context.Secret(DiscordWebhook)) is { } discord)
            sends.Add(PostAsync(http, discord, JsonContent.Create(new DiscordMessage(text), NotificationsJson.Default.DiscordMessage), cancel));
        if (Https(context.Secret(Webhook)) is { } hook)
            sends.Add(PostAsync(http, hook, JsonContent.Create(new WebhookMessage(text, done, failed), NotificationsJson.Default.WebhookMessage), cancel));
        var failures = new List<Exception>();
        foreach (var send in sends)
        {
            try
            {
                await send;
            }
            catch (StageException e)
            {
                failures.Add(e);
            }
        }
        return failures;
    }

    private static Uri? Https(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;

    private static async Task PostAsync(HttpFetcher http, Uri url, HttpContent content, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.UserAgent.ParseAdd(Product.Name);
        using var response = await http.SendAsync(request, Stage.Resolve, cancel);
    }
}

internal sealed record TelegramMessage(string ChatId, string Text);

internal sealed record DiscordMessage(string Content);

internal sealed record WebhookMessage(string Text, int Done, int Failed);

/// <summary>Source-generated JSON for what is sent (no reflection, so Native AOT works).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TelegramMessage))]
[JsonSerializable(typeof(DiscordMessage))]
[JsonSerializable(typeof(WebhookMessage))]
internal sealed partial class NotificationsJson : JsonSerializerContext;
