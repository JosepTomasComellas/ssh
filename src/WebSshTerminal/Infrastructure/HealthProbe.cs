using System.Text.Json;

namespace WebSshTerminal.Infrastructure;

/// <summary><c>WebSshTerminal --healthcheck</c>: consulta la readiness local; exit 0 només si és correcta.</summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("WEBSSH_HEALTH_URL") ?? "http://127.0.0.1:8080";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), "/health/ready"));
            // El filtre d'hosts només accepta l'host públic configurat: la sonda local l'ha d'usar també.
            if (Uri.TryCreate(Environment.GetEnvironmentVariable("WebSsh__PublicUrl"), UriKind.Absolute, out var pub))
                request.Headers.Host = pub.Host;
            using var res = await http.SendAsync(request);
            if (!res.IsSuccessStatusCode) return 1;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
            return doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "ok" ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return 1;
        }
    }
}
