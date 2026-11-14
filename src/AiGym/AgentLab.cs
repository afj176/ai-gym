using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGym;

public enum LabState { Ready, Planning, AwaitingReview, Running, Paused, Completed, Failed, Cancelled, Interrupted, SetupRequired }
public enum LabEffect { Read, ModelRequest }
public sealed record LabTool(string Id, string Title, LabEffect Effect, string Arguments, string Description, bool SessionBound = true);
public sealed record LabEvent(long Sequence, DateTimeOffset At, Guid? StepId, string Kind, string Message, int? Completed = null, int? Total = null);
public sealed record LabResult(string Kind, string Summary, string Data = "{}", string Qualification = "", bool RequiresReview = false);
public sealed class LabStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Tool { get; set; } = "";
    public string Title { get; set; } = "";
    public string Arguments { get; set; } = "{}";
    public LabState State { get; set; } = LabState.Ready;
    public LabResult? Result { get; set; }
    public string Error { get; set; } = "";
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
}
public sealed class LabRun
{
    [JsonIgnore] public string? CheckpointFingerprint { get; set; }
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public string Prompt { get; set; } = "";
    public string Title { get; set; } = "New experiment";
    public string Scope { get; set; } = "";
    public string Context { get; set; } = "{}";
    public LabState State { get; set; } = LabState.Ready;
    public List<LabStep> Steps { get; set; } = [];
    public List<LabEvent> Events { get; set; } = [];
    public void Emit(Guid? step, string kind, string message, int? completed = null, int? total = null)
    {
        if (message.Length > 4000 || completed < 0 || total < 0 || completed > total) throw new ArgumentException("Invalid event bounds.");
        Events.Add(new((Events.LastOrDefault()?.Sequence ?? 0) + 1, DateTimeOffset.UtcNow, step, kind, message, completed, total));
        if (Events.Count > 500) Events.RemoveAt(0);
    }
    public void Validate()
    {
        if (Version != 1 || Id == Guid.Empty || !Enum.IsDefined(State) || Prompt.Length is < 1 or > 4000 || Title.Length > 160 || Scope.Length > 200 || Context.Length > 32000 || Steps.Count > 16 || Events.Count > 500 || Steps.Select(x => x.Id).Distinct().Count() != Steps.Count)
            throw new InvalidDataException("Invalid Agent Labs checkpoint; preserved without changes.");
        foreach (var s in Steps)
            if (s.Id == Guid.Empty || s.Tool.Length is < 1 or > 80 || s.Title.Length is < 1 or > 160 || s.Arguments.Length > 16000 || s.Result?.Data.Length > 2 * 1024 * 1024 || !Enum.IsDefined(s.State)) throw new InvalidDataException("Invalid lab step.");
    }
}
public sealed class LabSetupException(string message) : Exception(message);

/// <summary>Finite app-owned tools, not shell/MCP/UI automation. Every invocation is explicit.</summary>
public sealed class LabEngine
{
    readonly Dictionary<string, (LabTool Tool, Func<LabStep, Action<string, int?, int?>, CancellationToken, Task<LabResult>> Execute)> tools = new(StringComparer.Ordinal);
    int executing;
    public IEnumerable<LabTool> Tools => tools.Values.Select(x => x.Tool);
    public void Register(LabTool tool, Func<LabStep, Action<string, int?, int?>, CancellationToken, Task<LabResult>> execute) => tools.Add(tool.Id, (tool, execute));
    public LabTool Descriptor(string id) => tools.TryGetValue(id, out var t) ? t.Tool : throw new LabSetupException("This tool is not installed: " + id);
    public List<LabStep> ParsePlan(string text, string finishReason)
    {
        if (finishReason != "stop" || text.Length > 24000) throw new InvalidDataException("Incomplete plan. No tools were scheduled.");
        text = text.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && text.EndsWith("```")) text = text[7..^3].Trim();
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        var root = json.RootElement;
        Exact(root, "steps");
        var steps = root.GetProperty("steps");
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 16) throw new InvalidDataException("A plan needs 1–16 supported steps.");
        return steps.EnumerateArray().Select(s => {
            Exact(s, "tool", "title", "arguments");
            string id = s.GetProperty("tool").GetString() ?? "";
            _ = Descriptor(id);
            var title = s.GetProperty("title").GetString() ?? "";
            var args = s.GetProperty("arguments");
            if (title.Length is < 1 or > 160 || args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > 16000) throw new InvalidDataException("Invalid step title or arguments.");
            return new LabStep { Tool = id, Title = title, Arguments = args.GetRawText() };
        }).ToList();
    }
    public static void Exact(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an object.");
        var properties = obj.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Length != names.Length || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length || properties.Except(names, StringComparer.Ordinal).Any()) throw new InvalidDataException("Unexpected or duplicate fields.");
    }
    public async Task Execute(LabRun run, LabStep step, string currentScope, bool approved, Action changed, CancellationToken ct)
    {
        if(Interlocked.CompareExchange(ref executing,1,0)!=0)throw new InvalidOperationException("One lab step can run at a time.");
        try{await ExecuteOwned(run,step,currentScope,approved,changed,ct);}finally{Interlocked.Exchange(ref executing,0);}
    }
    async Task ExecuteOwned(LabRun run, LabStep step, string currentScope, bool approved, Action changed, CancellationToken ct)
    {
        if (run.State == LabState.Running) throw new InvalidOperationException("One lab step can run at a time.");
        var tool = Descriptor(step.Tool);
        if (tool.SessionBound && run.Scope != currentScope) throw new LabSetupException("The source session changed. Start a new experiment with the current scope.");
        if (!run.Steps.Contains(step) || step.State == LabState.Completed || run.Steps.TakeWhile(s => s != step).Any(s => s.State != LabState.Completed)) throw new InvalidOperationException("Complete prior steps first. Completed steps cannot replay effects.");
        if (!approved) { run.State = step.State = LabState.AwaitingReview; changed(); return; }
        step.Started = DateTimeOffset.UtcNow; step.Finished = null; step.Error = ""; step.Result = null;
        run.State = step.State = LabState.Running;
        run.Emit(step.Id, "Started", tool.Title); changed();
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await tools[step.Tool].Execute(step, (message, done, total) => { run.Emit(step.Id, "Progress", message, done, total); changed(); }, ct);
            if(result.Kind.Length is <1 or >80||result.Summary.Length>4000||result.Data.Length>2*1024*1024||result.Qualification.Length>8000)throw new InvalidDataException("Tool result exceeds the bounded lab artifact contract. Use a smaller source scope; existing domain checkpoints remain available.");
            // Successful return can follow an irreversible commit. Do not mislabel that commit as cancelled.
            step.Result = result; step.State = LabState.Completed;
            run.State = !result.RequiresReview&&run.Steps.All(s => s.State == LabState.Completed) ? LabState.Completed : LabState.AwaitingReview;
            run.Emit(step.Id, "Completed", result.Summary);
        }
        catch (LabSetupException e) { step.Error = e.Message; run.State = step.State = LabState.SetupRequired; run.Emit(step.Id, "SetupRequired", e.Message); }
        catch (OperationCanceledException) { run.State = step.State = LabState.Cancelled; step.Error = "Cancelled. Completed steps and domain checkpoints are retained. Review before retrying."; run.Emit(step.Id, "Cancelled", step.Error); }
        catch (Exception e) { run.State = step.State = LabState.Failed; step.Error = e is System.Net.Http.HttpRequestException ? "Local endpoint request failed. Check the model connection." : e.Message; run.Emit(step.Id, "Failed", step.Error.Length > 4000 ? step.Error[..4000] : step.Error); }
        finally { step.Finished = DateTimeOffset.UtcNow; changed(); }
    }
}

public sealed class LabStore
{
    readonly string directory;
    public LabStore(string root) { directory = Path.Combine(root, "agent-labs"); }
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public void Save(LabRun run)
    {
        run.Validate(); Directory.CreateDirectory(directory);
        using var gate=new FileStream(Path.Combine(directory,"writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var path = Path.Combine(directory, run.Id + ".json");
        var fingerprint=File.Exists(path)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))):null;
        if(fingerprint!=run.CheckpointFingerprint)throw new IOException("Lab checkpoint changed in another process. Reload before saving; newer work was preserved.");
        if (!File.Exists(path) && Directory.GetFiles(directory, "*.json").Length >= 20) throw new IOException("Agent Labs keeps 20 runs. Archive an old run file before creating another.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(run, Options);
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("Lab checkpoint exceeds 4 MiB. Existing file was preserved.");
        var temp = path + "." + Guid.NewGuid() + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true);run.CheckpointFingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public IReadOnlyList<LabRun> Load()
    {
        if (!Directory.Exists(directory)) return [];
        var runs = new List<LabRun>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderDescending().Take(20))
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversized lab checkpoint preserved: " + Path.GetFileName(path));
            var bytes=File.ReadAllBytes(path);
            var run = JsonSerializer.Deserialize<LabRun>(bytes, Options) ?? throw new InvalidDataException("Unreadable lab checkpoint."); run.Validate();
            run.CheckpointFingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (run.State is LabState.Running or LabState.Planning) run.State = LabState.Interrupted;
            foreach (var s in run.Steps.Where(s => s.State == LabState.Running)) { s.State = LabState.Interrupted; s.Error = "App exited during this step. Inspect domain checkpoints before retrying."; }
            runs.Add(run);
        }
        return runs.OrderByDescending(r => r.Created).ToArray();
    }
}
