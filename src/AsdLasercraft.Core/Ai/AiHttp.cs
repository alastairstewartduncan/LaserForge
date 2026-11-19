using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsdLasercraft.Core.Ai;

/// <summary>Shared HTTP plumbing for the AI clients: sends JSON and turns transport failures into <see cref="AiException"/>s.</summary>
public static class AiHttp
{
    public static HttpClient NewClient() => new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <summary>Sends the request and returns the status and body text. Network problems become friendly errors.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpRequestMessage msg, CancellationToken ct)
    {
        try
        {
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.StatusCode, text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new AiException(AiErrorKind.Cancelled, "Cancelled.");
        }
        catch (TaskCanceledException ex)
        {
            throw new AiException(AiErrorKind.Network, "The AI service took too long to answer. Check your internet connection and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException(AiErrorKind.Network, "Couldn't reach the AI service. Check the address and your connection, and that a local model server is running if you use one.", ex);
        }
    }

    public static HttpRequestMessage JsonPost(Uri uri, JsonNode body) => new(HttpMethod.Post, uri)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    /// <summary>Pulls the human-readable message out of the error shapes the big providers use.</summary>
    public static string ErrorDetail(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray arr && arr.Count > 0) node = arr[0];
            var err = node?["error"];
            if (err is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return err?["message"]?.GetValue<string>() ?? node?["message"]?.GetValue<string>() ?? "";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return ""; }
    }

    /// <summary>Wraps response parsing so a malformed answer becomes a readable error.</summary>
    public static AiResponse Parse(Func<AiResponse> parse)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException or FormatException)
        {
            throw new AiException(AiErrorKind.BadResponse, "The AI service sent an answer the app couldn't read.", ex);
        }
    }
}
