using System.Diagnostics;
using System.Text.Json;

namespace AiGym;
public sealed record LocalTrainingSpec(string Base,string Bundle,string Output,int Steps=20,int SequenceLength=512,int Rank=8,double LearningRate=.0002,string Device="cpu",int Seed=3407,int CheckpointEvery=1);
public sealed record LocalTrainingCheckpoint(string Directory,DateTime SavedAt,int? Step,string State);
public static class LocalTraining
{
    static string ReadShared(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    static string[] TailShared(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);long offset=Math.Max(0,file.Length-2*1024*1024);file.Seek(offset,SeekOrigin.Begin);using var reader=new StreamReader(file);if(offset>0)reader.ReadLine();List<string> lines=[];while(reader.ReadLine() is {} line)lines.Add(line);return lines.TakeLast(2000).ToArray();}
    public static string DirectoryFor(string root,Guid id)=>Path.Combine(root,"agent-labs","training",id.ToString());
    /// <summary>
    /// Output room a served adapter keeps beside a training-length prompt. Compare asks for up to 512 answer tokens and
    /// Agent lab planning for up to 2,048; the app lowers either request when the prompt leaves less room.
    /// </summary>
    public const int AdapterOutputReserve=2048;
    /// <summary>The loaded context for a served adapter: the run's sequence length plus <see cref="AdapterOutputReserve"/>, at least 4,096, within the base model's declared capacity.</summary>
    public static int AdapterContext(LocalTrainingSpec spec,ModelMemoryProfile capacity)
    {
        int wanted=Math.Max(4096,spec.SequenceLength+AdapterOutputReserve);
        return capacity.ContextDeclared?Math.Min(wanted,capacity.MaxContext):wanted;
    }
    /// <summary>Serves a trained adapter on the device the run was trained on, with <see cref="AdapterContext"/> loaded.</summary>
    public static async Task<ServedModel> StartAdapter(string root,Guid id,ModelServing owner,Action<string,int?,int?> progress,CancellationToken ct)
    {
        string folder=DirectoryFor(root,id);var spec=JsonSerializer.Deserialize<LocalTrainingSpec>(File.ReadAllText(Path.Combine(folder,"config.json")))!;Validate(spec);
        using var identity=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"identity.json")));using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"adapter-manifest.json")));
        if(manifest.RootElement.GetProperty("identity").GetString()!=identity.RootElement.GetProperty("hash").GetString())throw new InvalidDataException("Adapter and training identities differ.");
        async Task Verify(string directory,JsonElement files){int i=0,total=files.EnumerateObject().Count();foreach(var file in files.EnumerateObject()){if(Path.GetFileName(file.Name)!=file.Name)throw new InvalidDataException("Invalid recorded artifact filename.");await using var stream=File.OpenRead(Path.Combine(directory,file.Name));var hash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream,ct));if(!hash.Equals(file.Value.GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Recorded base/adapter file changed: "+file.Name);progress("Verified local model artifact \u00b7 "+file.Name,++i,total);}}
        await Verify(spec.Base,identity.RootElement.GetProperty("identity").GetProperty("weights"));string adapter=Path.Combine(folder,"adapter");await Verify(adapter,manifest.RootElement.GetProperty("files"));
        var inventory=await Task.Run(()=>ModelDiscovery.Scan([new("Training base",spec.Base)],ct),ct);var model=inventory.Models.SingleOrDefault(m=>m.Task=="Chat"&&Path.GetFullPath(m.Path).Equals(Path.GetFullPath(spec.Base),StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException("The training base has no managed text-serving adapter. The trained PEFT artifact is retained.");
        var settings=Store.Read<ModelLibrarySettings>("model-library.json")??new([],"","",false);settings=settings with{Python=JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(folder,"python.json")))!};
        var options=new ModelLaunchOptions(AdapterContext(spec,await Task.Run(()=>ModelMemory.Read(model,ct),ct)));
        progress($"Starting adapter on {(spec.Device=="cuda"?"GPU (CUDA)":"CPU")}, as trained \u00b7 {options.ContextTokens:N0}-token context",null,null);
        return await owner.Start(model with{Name=model.Name+" \u00b7 trained LoRA "+id.ToString()[..8],AdapterPath=adapter},settings,spec.Device=="cuda",Path.Combine(AppContext.BaseDirectory,"model-runtime"),ct,text=>progress(text,null,null),options);
    }
    public static void Validate(LocalTrainingSpec spec)
    {
        if(!Path.IsPathFullyQualified(spec.Base)||!Directory.Exists(spec.Base)||!File.Exists(Path.Combine(spec.Base,"config.json")))throw new ArgumentException("Choose an existing local Transformers base folder, not GGUF.");
        if(!Directory.Exists(spec.Bundle)||!File.Exists(Path.Combine(spec.Bundle,"manifest.json")))throw new ArgumentException("Choose or export an approved training bundle.");
        if(spec.Steps is <1 or >100000||spec.SequenceLength is <16 or >4096||spec.Rank is <1 or >64||spec.LearningRate is <=0 or >.01||!double.IsFinite(spec.LearningRate)||spec.CheckpointEvery is <1 or >100||spec.Device is not ("cpu" or "cuda"))throw new ArgumentException("Invalid bounded training configuration.");
    }
    public static async Task<JsonElement> Run(string python,string script,LocalTrainingSpec spec,bool probe,Action<string,int?,int?> progress,CancellationToken ct)
    {
        Validate(spec);if(!File.Exists(python)||!Path.GetFileName(python).Equals("python.exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(script))throw new ArgumentException("Choose an existing python.exe and the packaged training adapter.");
        Directory.CreateDirectory(spec.Output);using var lease=new FileStream(Path.Combine(spec.Output,"execution.lease"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string configPath=Path.Combine(spec.Output,"config.json"),serialized=JsonSerializer.Serialize(spec);
        if(File.Exists(configPath)&&File.ReadAllText(configPath)!=serialized)throw new InvalidOperationException("Saved training configuration differs; create a new training experiment.");
        File.WriteAllText(configPath,serialized);string pause=Path.Combine(spec.Output,"pause.request");if(File.Exists(pause))File.Delete(pause);
        GymFiles.Atomic(Path.Combine(spec.Output,"status.json"),new{state="Starting",step=0,total=spec.Steps,elapsedSeconds=0,detail="Starting selected local Python runtime. No optimizer step reported yet; retained checkpoints remain on disk."});
        var args=new List<string>{"-u",script,"--config",configPath};if(probe)args.Add("--probe");
        using var owner=OwnedModelProcess.Start(python,args,Path.Combine(spec.Output,"runtime.log"));
        DateTimeOffset? cancelled=null;string last="";JsonElement? status=null;
        while(true){
            if(ct.IsCancellationRequested&&cancelled==null){File.WriteAllText(pause,"Stop at the next optimizer boundary");cancelled=DateTimeOffset.UtcNow;}
            string path=Path.Combine(spec.Output,"status.json");
            try{if(File.Exists(path)){var text=ReadShared(path);if(text!=last){last=text;status=JsonSerializer.Deserialize<JsonElement>(text);var value=status.Value;string state=value.GetProperty("state").GetString()??"Unknown";int? step=value.TryGetProperty("step",out var st)?st.GetInt32():null;int? total=state=="Training"?spec.Steps:null;string message=state+(value.TryGetProperty("loss",out var loss)?" \u00b7 loss "+loss.GetDouble().ToString("0.0000"):value.TryGetProperty("detail",out var detail)?" \u00b7 "+detail.GetString():"");progress(message,total==null?null:step,total);}}}catch(IOException){}catch(JsonException){}
            if(!owner.IsRunning)break;
            if(cancelled!=null&&DateTimeOffset.UtcNow-cancelled>TimeSpan.FromSeconds(30))throw new OperationCanceledException("Training stop deadline reached; last committed checkpoint retained.",ct);
            await Task.Delay(200,CancellationToken.None);
        }
        // Consume the final atomically written observation after process exit.
        status=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(spec.Output,"status.json")));
        string final=status.Value.GetProperty("state").GetString()!;
        if(final=="Interrupted")throw new OperationCanceledException("Stopped at a training boundary; resume the retained run.",ct);
        if(owner.ExitCode!=0||final!=(probe?"Ready":"Completed"))throw new InvalidOperationException(status.Value.TryGetProperty("error",out var error)?error.GetString():"Training runtime exited without a completed observation. Inspect the retained log/checkpoints.");
        return status.Value.Clone();
    }
    public static object Evidence(string root,Guid id)
    {
        var folder=DirectoryFor(root,id);var status=File.Exists(Path.Combine(folder,"status.json"))?JsonSerializer.Deserialize<JsonElement>(ReadShared(Path.Combine(folder,"status.json"))):JsonSerializer.SerializeToElement(new{state="Not started"});
        var metrics=File.Exists(Path.Combine(folder,"events.jsonl"))?TailShared(Path.Combine(folder,"events.jsonl")).Select(x=>{try{return JsonSerializer.Deserialize<JsonElement>(x);}catch{return default;}}).Where(x=>x.ValueKind==JsonValueKind.Object).ToArray():[];
        var validations=metrics.Where(x=>x.TryGetProperty("event",out var kind)&&kind.GetString()=="validation"&&x.TryGetProperty("evaluationLoss",out _)).GroupBy(x=>x.GetProperty("step").GetInt32()).Select(g=>new{step=g.Key,loss=g.Last().GetProperty("evaluationLoss").GetDouble()}).ToArray();
        var checkpoints=new List<LocalTrainingCheckpoint>();
        if(Directory.Exists(folder))foreach(var path in Directory.GetDirectories(folder,"checkpoint-*")){
            try{using var metadata=JsonDocument.Parse(ReadShared(Path.Combine(path,"checkpoint.json")));checkpoints.Add(new(Path.GetFileName(path),Directory.GetLastWriteTimeUtc(path),metadata.RootElement.GetProperty("step").GetInt32(),"Committed metadata observed"));}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            catch(Exception error)when(error is IOException or JsonException or InvalidOperationException){checkpoints.Add(new(Path.GetFileName(path),Directory.GetLastWriteTimeUtc(path),null,"Metadata unavailable \u00b7 "+error.GetType().Name));}
        }
        return new{Id=id,Folder=folder,Status=status,ValidationMetrics=validations,Checkpoints=checkpoints.OrderByDescending(x=>x.SavedAt).Take(3).ToArray(),MetricsWindow="Latest 2 MiB / up to 2,000 runtime events, deduplicated by optimizer step; full journal retained on disk.",Metrics=metrics.Where(x=>x.TryGetProperty("loss",out _)&&(!x.TryGetProperty("event",out var kind)||kind.GetString()=="optimizer")).GroupBy(x=>x.GetProperty("step").GetInt32()).Select(g=>{var x=g.Last();return new{step=x.GetProperty("step").GetInt32(),loss=x.GetProperty("loss").GetDouble(),elapsedSeconds=x.GetProperty("elapsedSeconds").GetDouble()};}).ToArray(),Checkpoint=File.Exists(Path.Combine(folder,"latest.json"))?JsonSerializer.Deserialize<JsonElement>(ReadShared(Path.Combine(folder,"latest.json"))):(JsonElement?)null};
    }
}
