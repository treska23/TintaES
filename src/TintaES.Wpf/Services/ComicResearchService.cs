using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Investiga una obra una sola vez mediante Tavily y conserva una ficha compacta en el equipo.
/// No descarga páginas completas ni entrega HTML al traductor: solo respuestas y fragmentos
/// breves con sus fuentes.
/// </summary>
public sealed class ComicResearchService
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("https://api.tavily.com/"),
        Timeout = TimeSpan.FromSeconds(70)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TintaES",
        "comic-research");

    public ComicResearchContext? TryLoad(string title)
    {
        string identity = BuildIdentityKey(title);
        string path = GetCachePath(identity);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            ComicResearchContext? context = JsonSerializer.Deserialize<ComicResearchContext>(
                File.ReadAllText(path),
                JsonOptions);
            return context is { HasUsefulContent: true } ? context : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<ComicResearchContext> ResearchAsync(
        string title,
        string apiKey,
        CancellationToken cancellationToken)
    {
        title = NormalizeTitle(title);
        if (title.Length < 2)
        {
            throw new ArgumentException("Escribe el título o la colección del cómic.", nameof(title));
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Falta la clave de Tavily.", nameof(apiKey));
        }

        string[] queries =
        [
            $"{title} comic issue plot characters relationships synopsis",
            $"{title} comic official Spanish edition character names terminology Spain"
        ];

        var findings = new List<string>();
        var sources = new List<ComicResearchSource>();
        foreach (string query in queries)
        {
            TavilySearchResponse response = await SearchAsync(query, apiKey.Trim(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(response.Answer))
            {
                findings.Add(Compact(response.Answer));
            }

            foreach (TavilySearchResult result in response.Results
                         .Where(result => !string.IsNullOrWhiteSpace(result.Url))
                         .OrderByDescending(result => result.Score)
                         .Take(5))
            {
                string snippet = Compact(result.Content);
                sources.Add(new ComicResearchSource
                {
                    Title = string.IsNullOrWhiteSpace(result.Title) ? result.Url : Compact(result.Title),
                    Url = result.Url.Trim(),
                    Snippet = snippet
                });

                if (snippet.Length >= 35)
                {
                    findings.Add(snippet.Length <= 420 ? snippet : snippet[..420].TrimEnd() + "…");
                }
            }
        }

        var context = new ComicResearchContext
        {
            IdentityKey = BuildIdentityKey(title),
            ComicTitle = title,
            ResearchQuery = string.Join(" | ", queries),
            ResearchedAtUtc = DateTimeOffset.UtcNow,
            Findings = findings
                .Where(value => value.Length >= 20)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            Sources = sources
                .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(10)
                .ToList()
        };

        if (!context.HasUsefulContent)
        {
            throw new InvalidOperationException(
                "Tavily no devolvió información suficiente para identificar la obra.");
        }

        Save(context);
        return context;
    }

    public void Save(ComicResearchContext context)
    {
        if (!context.HasUsefulContent)
        {
            return;
        }

        Directory.CreateDirectory(_cacheDirectory);
        string identity = string.IsNullOrWhiteSpace(context.IdentityKey)
            ? BuildIdentityKey(context.ComicTitle)
            : context.IdentityKey;
        context.IdentityKey = identity;
        string path = GetCachePath(identity);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(context, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public static string BuildIdentityKey(string title)
    {
        string normalized = Regex.Replace(
            NormalizeTitle(title).ToLowerInvariant(),
            @"[^\p{L}\p{N}]+",
            " ").Trim();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private async Task<TavilySearchResponse> SearchAsync(
        string query,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            query,
            search_depth = "basic",
            topic = "general",
            max_results = 5,
            include_answer = "advanced",
            include_raw_content = false,
            include_images = false
        });

        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryReadError(body);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Tavily respondió con HTTP {(int)response.StatusCode}."
                    : detail);
        }

        return JsonSerializer.Deserialize<TavilySearchResponse>(body, JsonOptions)
            ?? new TavilySearchResponse();
    }

    private string GetCachePath(string identity) => Path.Combine(_cacheDirectory, identity + ".json");

    private static string NormalizeTitle(string value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private static string Compact(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private static string TryReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            foreach (string property in new[] { "detail", "error", "message" })
            {
                if (document.RootElement.TryGetProperty(property, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // Se devolverá el estado HTTP cuando la respuesta no sea JSON.
        }
        return string.Empty;
    }

    private sealed class TavilySearchResponse
    {
        public string Answer { get; set; } = string.Empty;
        public List<TavilySearchResult> Results { get; set; } = [];
    }

    private sealed class TavilySearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public double Score { get; set; }
    }
}
