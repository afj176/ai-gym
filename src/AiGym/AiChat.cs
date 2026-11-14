using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiGym;

public sealed record AiMessage(string Role, string Content);
public sealed record AiChatResult(string FinishReason, string? ObservedModel);

/// <summary>What a server reports about the loaded model: whether its prompt can be counted exactly, and the context it loaded.</summary>
public sealed record ChatServerInfo(bool Llama, int? Context, bool ManagedTokenCount = false, int? DecoderContext = null)
{
    public bool ExactContext => Llama || ManagedTokenCount;
}
public sealed record ChatTokenBudget(int Prompt, int Context, int? DecoderContext = null)
{
    public int Available => DecoderContext ?? Math.Max(0, Context - Prompt);
}

/// <summary>Streaming chat completions for Compare models and Agent lab. Sampling is left to the server defaults.</summary>
public sealed partial class LocalAiClient
{
    public const int ChatTextLimit = 4 * 1024 * 1024;
    const int ChatTimeoutMinutes = 15;
    public static void ValidateChatMessages(IReadOnlyList<AiMessage> messages)
    {
        if (messages.Count is < 1 or > 400 || messages.Any(x => x.Role is not ("system" or "user" or "assistant") || string.IsNullOrWhiteSpace(x.Content)) ||
            messages.Sum(x => (long)x.Content.Length) > ChatTextLimit)
            throw new ArgumentException("The request exceeds the application's 400-message / 4 Mi-character resource bound or contains an invalid message. Nothing is silently removed. The model's token context is checked separately when available.");
    }

    static Dictionary<string, object> ChatBody(AiEndpointProfile profile, IReadOnlyList<AiMessage> messages, int? maxTokens, bool llama)
    {
        if (maxTokens < 1) throw new ArgumentException("Maximum output tokens must be a positive whole number.");
        // The wire uses OpenAI's lowercase fields.
        var body = new Dictionary<string, object> { ["model"] = profile.Model, ["messages"] = messages.Select(m => new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content }).ToArray(), ["stream"] = true };
        if (maxTokens.HasValue) body["max_tokens"] = maxTokens.Value;
        if (llama)
        {
            body["stream_options"] = new { include_usage = true };
            body["reasoning_format"] = "auto";
        }
        return body;
    }

    async Task<JsonDocument?> ChatJson(AiEndpointProfile profile, string key, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(ValidateEndpoint(profile.Endpoint), path));
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[8192];
        int read; while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("Server metadata or tokenization exceeded 32 MiB."); buffer.Write(chunk, 0, read); }
        return JsonDocument.Parse(buffer.ToArray());
    }
    public async Task<ChatServerInfo> ChatServer(AiEndpointProfile profile, string key, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var models = await ChatJson(profile, key, "v1/models", null, deadline.Token);
        JsonElement model = default;
        bool verified = models != null && models.RootElement.TryGetProperty("data", out var list) && (model = list.EnumerateArray().FirstOrDefault(m => m.TryGetProperty("id", out var id) && id.GetString() == profile.Model)).ValueKind == JsonValueKind.Object;
        if (models != null && !verified) throw new InvalidOperationException("This server no longer lists the saved model ID. Start this model in Models or choose its current connection; a different model will not be substituted.");
        static int? Positive(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var number) && number > 0 ? number : null;
        var meta = model.ValueKind == JsonValueKind.Object && model.TryGetProperty("meta", out var metadata) ? metadata : default;
        // AI Gym's Transformers adapter (scripts/Serve-Transformers.py) names itself and counts prompt tokens exactly.
        bool textAdapter = model.ValueKind == JsonValueKind.Object && model.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String && owner.GetString() == "ai-gym-transformers";
        using var props = await ChatJson(profile, key, "props", null, deadline.Token);
        if (props == null)
        {
            bool counter = textAdapter && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("chat_token_count", out var protocol) && protocol.ValueKind == JsonValueKind.String && protocol.GetString() == "v1";
            var decoder = Positive(meta, "n_ctx_output");
            if (counter && (Positive(meta, "n_ctx") == null || !meta.TryGetProperty("n_ctx_output", out var window) || window.ValueKind != JsonValueKind.Null && decoder == null))
                throw new InvalidDataException("The Transformers server reported invalid context windows. Restart the model before sending.");
            return new(false, Positive(meta, "n_ctx"), counter, counter ? decoder : null);
        }
        var root = props.RootElement;
        bool llama = root.TryGetProperty("build_info", out _) && root.TryGetProperty("chat_template", out _);
        var context = root.TryGetProperty("default_generation_settings", out var settings) ? Positive(settings, "n_ctx") : null;
        return new(llama, context ?? Positive(meta, "n_ctx"));
    }
    /// <summary>The exact prompt size and loaded context, or null when this server cannot count tokens.</summary>
    public async Task<ChatTokenBudget?> MeasureChat(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages, ChatServerInfo server, CancellationToken ct)
    {
        if (!server.ExactContext) return null;
        ValidateChatMessages(messages);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var body = ChatBody(profile, messages, 1, server.Llama);
        if (server.ManagedTokenCount)
        {
            using var counted = await ChatJson(profile, key, "v1/chat/token-count", new { model = profile.Model, messages = body["messages"] }, deadline.Token);
            if (counted == null) throw new InvalidOperationException("The Transformers token counter is unavailable. Restart this managed model; no approximate count is substituted.");
            var value = counted.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("model", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != profile.Model ||
                !value.TryGetProperty("prompt_tokens", out var promptCount) || promptCount.ValueKind != JsonValueKind.Number || !promptCount.TryGetInt32(out int count) || count < 0 ||
                !value.TryGetProperty("context_tokens", out var inputWindow) || inputWindow.ValueKind != JsonValueKind.Number || !inputWindow.TryGetInt32(out int inputContext) || inputContext != server.Context ||
                !value.TryGetProperty("output_context_tokens", out var outputWindow) ||
                (server.DecoderContext.HasValue ? outputWindow.ValueKind != JsonValueKind.Number || !outputWindow.TryGetInt32(out int decoder) || decoder != server.DecoderContext : outputWindow.ValueKind != JsonValueKind.Null))
                throw new InvalidDataException("The token counter no longer matches the selected model or its context windows. Refresh the connection before sending.");
            return new(count, inputContext, server.DecoderContext);
        }
        using var applied = await ChatJson(profile, key, "apply-template", body, deadline.Token);
        if (applied == null) throw new InvalidOperationException("This llama.cpp server cannot apply its chat template for token budgeting. Update the runtime before sending; no approximate token count is substituted.");
        var prompt = applied.RootElement.GetProperty("prompt").GetString()!;
        using var tokens = await ChatJson(profile, key, "tokenize", new { content = prompt, add_special = false, parse_special = true }, deadline.Token);
        if (tokens == null) throw new InvalidOperationException("This server cannot tokenize its prompt. Update the llama.cpp runtime.");
        var context = server.Context ?? throw new InvalidOperationException("The server did not report its loaded context window.");
        return new(tokens.RootElement.GetProperty("tokens").GetArrayLength(), context);
    }
    /// <summary>
    /// The output limit to request: <paramref name="wanted"/>, lowered to what fits beside the prompt in the server's loaded
    /// context. Servers that report their context exactly (llama.cpp and AI Gym's Transformers adapter) are measured first;
    /// when the prompt leaves no room at all, this throws a plain explanation and nothing is sent. Servers that cannot be
    /// measured keep the requested limit and report any rejection themselves.
    /// </summary>
    public async Task<int> FitOutput(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages, int wanted, CancellationToken ct)
    {
        ChatTokenBudget? measured;
        try
        {
            var server = await ChatServer(profile, key, ct);
            measured = await MeasureChat(profile, key, messages, server, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Discovery is an optimisation, not a gate: the request itself still reports connection or identity problems.
            return wanted;
        }
        if (measured == null) return wanted;
        if (measured.Available == 0 || measured.Prompt > measured.Context)
            throw new ArgumentException($"The prompt uses {measured.Prompt:N0} of the {measured.Context:N0} context tokens loaded for {profile.Name}, leaving no room for an answer. Restart that model with a larger context window. Nothing was sent.");
        return Math.Min(wanted, measured.Available);
    }
    public async Task<AiChatResult> StreamChat(AiEndpointProfile profile, string key, IReadOnlyList<AiMessage> messages, int? maxTokens, Action<string> onText, CancellationToken cancellation)
    {
        var endpoint = ValidateEndpoint(profile.Endpoint);
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("Select and save an exact model ID.");
        ValidateChatMessages(messages);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(ChatTimeoutMinutes));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/chat/completions"));
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(ChatBody(profile, messages, maxTokens, false)), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Local chat returned HTTP {(int)response.StatusCode}; " + ((int)response.StatusCode is 400 or 422 ? "the server rejected this request. Reduce the output-token limit or shorten the prompt to fit the model context, and check the server log." : "check the saved model, key and server configuration."));
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new InvalidDataException("The endpoint did not return an event stream.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadChatStream(stream, onText, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException($"Local chat exceeded its {ChatTimeoutMinutes}-minute deadline. Partial output is incomplete."); }
        // A refused connection otherwise surfaces as the raw socket text ("No connection could be made because the target machine actively refused it").
        catch (HttpRequestException error) when (error.InnerException is System.Net.Sockets.SocketException)
        { throw new HttpRequestException($"Could not reach {profile.Name} at {endpoint}. Its model server is not running there; start the model in Models & connections, then try again.", error); }
    }

    // Incremental UTF-8 and SSE parsing. No ReadLineAsync allocation on an unbounded peer line.
    public static async Task<AiChatResult> ReadChatStream(Stream stream, Action<string> onText, CancellationToken cancellation)
    {
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var bytes = new byte[4096]; var chars = new char[8192];
        var pending = new StringBuilder(); var data = new StringBuilder();
        int retained = 0, output = 0, thinking = 0; string? finish = null, observedModel = null;
        bool ended = false;
        void Event()
        {
            if (data.Length == 0) return;
            var payload = data.ToString(); data.Clear();
            if (payload == "[DONE]")
            {
                if (finish == null) throw new InvalidDataException("The stream ended without a completion result. Partial output is incomplete.");
                if (finish == "stop" && output == 0 && thinking == 0) throw new InvalidDataException("The model returned an empty response.");
                // A natural stop still needs an actual answer before it counts as success.
                if (finish == "stop" && output == 0) throw new IOException("The model stopped after reasoning without returning an answer. This was not a connection failure.");
                ended = true; return;
            }
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw new InvalidDataException("The model server reported an error within the stream.");
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            {
                var current = model.GetString();
                if (observedModel != null && current != observedModel) throw new InvalidDataException("The server changed model identity during the answer.");
                observedModel = current;
            }
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return; // Optional usage-only event.
            if (finish != null) throw new InvalidDataException("The stream supplied answer data after its completion result.");
            if (choices.GetArrayLength() != 1 || choices[0].GetProperty("index").GetInt32() != 0) throw new InvalidDataException("Expected one streamed answer.");
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("reasoning_content", out var reasoning) || delta.TryGetProperty("reasoning", out reasoning))
                {
                    if (reasoning.ValueKind == JsonValueKind.String) { var text = reasoning.GetString() ?? ""; thinking += text.Length; retained += text.Length; if (retained > ChatTextLimit) throw new InvalidDataException("Reasoning and answer exceeded the 4 Mi-character resource bound."); }
                }
                if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null && tools.GetArrayLength() > 0)
                    throw new InvalidDataException("AI Gym does not execute model tool calls.");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    var text = content.GetString() ?? "";
                    output += text.Length;
                    retained += text.Length;
                    if (retained > ChatTextLimit) throw new InvalidDataException("Reasoning and answer exceeded the 4 Mi-character resource bound.");
                    if (text.Length > 0) onText(text);
                }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                finish = reason.GetString();
                if (finish is not ("stop" or "length")) throw new InvalidDataException("The server did not finish a normal text answer.");
            }
        }
        while (!ended)
        {
            var read = await stream.ReadAsync(bytes, cancellation);
            if (read == 0) throw new InvalidDataException("The chat stream disconnected before its completion marker. Partial output is incomplete.");
            var decoded = decoder.GetChars(bytes.AsSpan(0, read), chars.AsSpan(), false);
            for (int i = 0; i < decoded; i++)
            {
                if (chars[i] != '\n') { if (pending.Length + data.Length >= 256 * 1024) throw new InvalidDataException("A single stream event exceeded 256 Ki-characters."); pending.Append(chars[i]); continue; }
                var line = pending.ToString().TrimEnd('\r'); pending.Clear();
                if (line.Length == 0) Event();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    var value = line[5..]; if (value.StartsWith(' ')) value = value[1..];
                    data.Append(value);
                }
                if (ended) break;
            }
        }
        return new(finish!, observedModel);
    }
}
