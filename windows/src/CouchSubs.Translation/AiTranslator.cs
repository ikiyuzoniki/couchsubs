// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchSubs.Core;
using CouchSubs.Core.Dubbing;

namespace CouchSubs.Translation;

public enum AiProvider { Gemini, OpenAi }

/// <summary>
/// İsteğe bağlı çevrimiçi çeviri: kullanıcının kendi Gemini (Google) ya da OpenAI anahtarıyla.
/// DeepLTranslator ile aynı akış: altyazı cümle cümle gider, konuşmacı adları çevrilmez, önceki satırlar
/// bağlam olarak eklenir. Servise ulaşılamazsa ya da kota biterse o satır cihaz üstü modelle çevrilir.
/// Hangi servisin kullanılacağı anahtarın biçiminden anlaşılır (bkz. <see cref="Detect"/>).
/// </summary>
public sealed class AiTranslator : ITextTranslator
{
    // Model adları değişirse yalnız bu iki satırı değiştirmek yeter.
    public const string GeminiModel = "gemini-3.1-flash-lite";
    public const string OpenAiModel = "gpt-4.1-mini";

    private const int CacheLimit = 4096;
    private const int ContextLines = 3;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(8) });

    private const string SystemPrompt =
        "You translate English video game subtitles into natural, spoken Turkish for a player watching on a TV. " +
        "Rules: Use informal 'sen' in dialogue. Keep proper nouns, character names, place names and in-game item names " +
        "as they are unless a well-known Turkish form exists. Keep the meaning, tone and brevity; subtitles must stay short and readable. " +
        "Translate idioms and slang by meaning, not word for word. " +
        "The text was read from the screen by OCR and may contain small reading mistakes (for example 'l' for 'I', '0' for 'O'); silently fix obvious ones. " +
        "Never translate the 'context' lines, they are only there so you understand who or what is being talked about. " +
        "Do not translate the 'names' list; those words must stay unchanged. Do not add explanations. " +
        "Reply with JSON only, in exactly this shape: {\"translations\": [\"...\"]}, " +
        "one Turkish string for each item of 'sentences', in the same order.";

    private readonly string _key;
    private readonly AiProvider _provider;
    private readonly ITextTranslator? _fallback;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<SpokenLine>> _speech = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _names = new(StringComparer.Ordinal);
    private readonly Queue<string> _recent = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public AiTranslator(string key, AiProvider provider, ITextTranslator? fallback, HttpClient? http = null)
    {
        _key = key.Trim();
        _provider = provider;
        _fallback = fallback;
        _http = http ?? SharedClient.Value;
    }

    /// <summary>Son hata; çeviri düzgün gidiyorsa null.</summary>
    public OnlineKeyState? LastError { get; private set; }

    /// <summary>Anahtarın biçiminden servisi bulur: Gemini "AIza…", OpenAI "sk-…". Tanınmazsa null (DeepL sayılır).</summary>
    public static AiProvider? Detect(string key)
    {
        key = key.Trim();
        if (key.StartsWith("AIza", StringComparison.Ordinal)) return AiProvider.Gemini;
        if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return null;
        if (key.StartsWith("sk-", StringComparison.Ordinal)) return AiProvider.OpenAi;
        return null;
    }

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_cache.TryGetValue(text, out var cached)) return cached;
        if (DateTimeOffset.UtcNow < _pausedUntil) return await FallbackAsync(text, cancellationToken).ConfigureAwait(false);

        var pieces = SubtitleSegmenter.Split(text);
        foreach (var name in pieces.Select(SubtitleSegmenter.LabelName).OfType<string>())
            foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2))
                _names[word.ToLowerInvariant()] = 0;
        var names = _names.Keys.ToHashSet(StringComparer.Ordinal);
        var sources = pieces.Where(p => p.Translate).Select(p => SubtitleSegmenter.NormalizeCase(p.Text, names)).ToList();

        string[] translated;
        try
        {
            translated = sources.Count == 0 ? [] : await RequestAsync(sources, names, cancellationToken).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or AiException
                                      && !cancellationToken.IsCancellationRequested)
        {
            LastError = error is AiException ai ? ai.State : OnlineKeyState.Unreachable;
            _pausedUntil = DateTimeOffset.UtcNow + RetryAfter;
            return await FallbackAsync(text, cancellationToken).ConfigureAwait(false);
        }

        var builder = new StringBuilder();
        var translations = new List<string?>(pieces.Count);
        var next = 0;
        foreach (var piece in pieces)
        {
            var sentence = piece.Translate ? translated[next++] : null;
            builder.Append(sentence ?? piece.Text);
            translations.Add(sentence);
        }
        if (_speech.Count >= 512) _speech.Clear();
        _speech[text] = SpeechScript.Lines(pieces, translations);
        var result = builder.ToString();
        if (_cache.Count >= CacheLimit) _cache.Clear();
        _cache[text] = result;
        _recent.Enqueue(text);
        while (_recent.Count > ContextLines) _recent.Dequeue();
        return result;
    }

    public IReadOnlyList<SpokenLine> SpokenLines(string text) =>
        _speech.TryGetValue(text, out var lines) ? lines
        : _cache.TryGetValue(text, out var translation) ? [new SpokenLine(text, translation)]
        : _fallback?.SpokenLines(text) ?? [];

    private async Task<string> FallbackAsync(string text, CancellationToken cancellationToken) =>
        _fallback is null ? throw new TranslatorException(TranslatorStatus.NeedsDownload)
            : await _fallback.TranslateAsync(text, cancellationToken).ConfigureAwait(false);

    private async Task<string[]> RequestAsync(List<string> sentences, IReadOnlySet<string> names, CancellationToken cancellationToken)
    {
        var input = new JsonObject
        {
            ["context"] = ToArray(_recent),
            ["names"] = ToArray(names),
            ["sentences"] = ToArray(sentences),
        };
        var userText = input.ToJsonString();

        using var request = _provider == AiProvider.Gemini ? GeminiRequest(userText) : OpenAiRequest(userText);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reply = _provider == AiProvider.Gemini ? ReadGemini(json) : ReadOpenAi(json);
            return ParseTranslations(reply, sentences.Count);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
                                      or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new AiException(OnlineKeyState.Unreachable);
        }
    }

    private HttpRequestMessage GeminiRequest(string userText)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = JsonValue.Create(SystemPrompt) }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = JsonValue.Create("user"),
                ["parts"] = new JsonArray(new JsonObject { ["text"] = JsonValue.Create(userText) }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = JsonValue.Create(0.2),
                ["responseMimeType"] = JsonValue.Create("application/json"),
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModel}:generateContent"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, _key, AiProvider.Gemini);
        return request;
    }

    private HttpRequestMessage OpenAiRequest(string userText)
    {
        var body = new JsonObject
        {
            ["model"] = JsonValue.Create(OpenAiModel),
            ["temperature"] = JsonValue.Create(0.2),
            ["response_format"] = new JsonObject { ["type"] = JsonValue.Create("json_object") },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = JsonValue.Create("system"), ["content"] = JsonValue.Create(SystemPrompt) },
                new JsonObject { ["role"] = JsonValue.Create("user"), ["content"] = JsonValue.Create(userText) }),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.openai.com/v1/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, _key, AiProvider.OpenAi);
        return request;
    }

    private static void Authorize(HttpRequestMessage request, string key, AiProvider provider)
    {
        if (provider == AiProvider.Gemini) request.Headers.Add("x-goog-api-key", key);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static string ReadGemini(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parts = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            // Düşünme parçaları (thought) yanıta karışmasın.
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("text", out var value)) text.Append(value.GetString());
        }
        return text.ToString();
    }

    private static string ReadOpenAi(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    /// <summary>Yanıttaki {"translations": [...]} listesini okur; sayı uymuyorsa hata (o satır yerel modelle çevrilir).</summary>
    internal static string[] ParseTranslations(string reply, int expected)
    {
        var text = reply.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var first = text.IndexOf('\n');
            var last = text.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) text = text[(first + 1)..last].Trim();
        }
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("translations");
        var result = new List<string>();
        foreach (var item in array.EnumerateArray()) result.Add(item.GetString() ?? "");
        if (result.Count != expected || result.Any(string.IsNullOrWhiteSpace)) throw new AiException(OnlineKeyState.Unreachable);
        return [.. result];
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(JsonValue.Create(item));
        return array;
    }

    /// <summary>Ayarlardaki "Dene" düğmesi: anahtar geçerli mi. Bu servisler kullanımı sormaya izin vermediği için sayı dönmez.</summary>
    public static async Task<OnlineKeyCheck> CheckAsync(string key, AiProvider provider, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        if (key.Length == 0) return new(OnlineKeyState.Invalid);
        try
        {
            var address = provider == AiProvider.Gemini
                ? "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1"
                : "https://api.openai.com/v1/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address));
            Authorize(request, key, provider);
            using var response = await (http ?? SharedClient.Value).SendAsync(request, cancellationToken).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
            return new(OnlineKeyState.Valid);
        }
        catch (AiException error) { return new(error.State); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(OnlineKeyState.Unreachable);
        }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = "";
        try { body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException) { }
        throw new AiException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => OnlineKeyState.Invalid,
            HttpStatusCode.TooManyRequests => OnlineKeyState.QuotaExceeded,
            // Gemini geçersiz anahtara 400 döner.
            HttpStatusCode.BadRequest when body.Contains("API_KEY_INVALID", StringComparison.Ordinal)
                                         || body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) => OnlineKeyState.Invalid,
            _ => OnlineKeyState.Unreachable,
        });
    }
}

internal sealed class AiException(OnlineKeyState state) : Exception($"AI translation: {state}")
{
    public OnlineKeyState State { get; } = state;
}
