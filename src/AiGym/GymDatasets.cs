using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiGym;

public sealed record GymMessage(string role, string content);
public sealed record GymExample(string entity, GymMessage[] messages);
public sealed record GymDataset(Guid Id, string Name, DateTimeOffset Imported, string SourceHash, GymExample[] Examples, string ContentHash = "");
public sealed record SkippedDataset(string File, string Reason);
public static class GymFiles
{
    public static void Atomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid()+".tmp";
        try { File.WriteAllText(temp,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false)); File.Move(temp,path,true); }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
public static class GymDatasets
{
    public static GymDataset Import(string path)
    {
        if(new FileInfo(path).Length>16*1024*1024)throw new InvalidDataException("Dataset exceeds 16 MiB.");
        var text=File.ReadAllText(path);var examples=new List<GymExample>();
        foreach(var line in text.Split('\n').Where(x=>!string.IsNullOrWhiteSpace(x)))
        {
            if(examples.Count>=10000)throw new InvalidDataException("Use at most 10,000 examples.");
            GymExample row;
            try { row=JsonSerializer.Deserialize<GymExample>(line)??throw new InvalidDataException("Empty example."); }
            // A raw parser message ("'M' is an invalid start of a value") does not tell the user which file or line is wrong.
            catch(JsonException error) { throw new InvalidDataException($"Row {examples.Count+1} of {Path.GetFileName(path)} is not valid JSON. Choose a JSONL file with one conversation per line. ({error.Message})",error); }
            if(string.IsNullOrWhiteSpace(row.entity)||row.entity.Length>500||row.messages is not {Length:>1 and <=64}||row.messages.Any(m=>m is null)||row.messages[^1].role!="assistant"||!row.messages.Any(m=>m.role=="user")||row.messages.Any(m=>m.role is not ("system" or "user" or "assistant")||string.IsNullOrWhiteSpace(m.content)||m.content.Length>32000))
                throw new InvalidDataException($"Invalid example at row {examples.Count+1}. Each needs an entity and bounded user/assistant messages, ending with assistant.");
            examples.Add(row);
        }
        if(examples.Count<2||examples.Select(x=>x.entity).Distinct(StringComparer.Ordinal).Count()<2)throw new InvalidDataException("Use at least two examples from distinct entities.");
        var rows=examples.ToArray();
        var dataset=new GymDataset(Guid.NewGuid(),Path.GetFileNameWithoutExtension(path),DateTimeOffset.UtcNow,GymFiles.Hash(text),rows,GymFiles.Hash(JsonSerializer.Serialize(rows)));
        return dataset;
    }
    public static void Validate(GymDataset dataset)
    {
        if(dataset.Id==Guid.Empty||dataset.Name is null||dataset.SourceHash is null||dataset.Examples is not {Length:>=2 and <=10000}||dataset.Examples.Any(x=>x?.entity is null||x.messages is null||x.messages.Any(m=>m?.role is null||m.content is null))||dataset.ContentHash!=GymFiles.Hash(JsonSerializer.Serialize(dataset.Examples)))throw new InvalidDataException("Saved dataset content changed or is invalid. Re-import the reviewed source.");
    }
    public static void Save(string root,GymDataset dataset){Validate(dataset);GymFiles.Atomic(Path.Combine(root,"datasets",dataset.Id+".json"),dataset);}
    /// <summary>Valid saved datasets. Files that cannot be read are skipped and stay on disk; see <see cref="Load"/>.</summary>
    public static IEnumerable<GymDataset> List(string root)=>Load(root).Datasets;
    /// <summary>Reads every saved dataset. One unreadable file never hides the others: it is left untouched and reported with its reason.</summary>
    public static (GymDataset[] Datasets,SkippedDataset[] Skipped) Load(string root)
    {
        var folder=Path.Combine(root,"datasets");if(!Directory.Exists(folder))return([],[]);
        var datasets=new List<GymDataset>();var skipped=new List<SkippedDataset>();
        foreach(var file in Directory.EnumerateFiles(folder,"*.json"))
        {
            try
            {
                if(new FileInfo(file).Length>32*1024*1024)throw new InvalidDataException("Saved dataset exceeds its 32 MiB size bound.");
                var dataset=JsonSerializer.Deserialize<GymDataset>(File.ReadAllText(file))??throw new InvalidDataException("The file holds no dataset.");
                Validate(dataset);datasets.Add(dataset);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                skipped.Add(new(Path.GetFileName(file),error.Message));
            }
        }
        return(datasets.ToArray(),skipped.ToArray());
    }
    public static HashSet<string> HeldOut(GymDataset data)=>data.Examples.Select(x=>x.entity).Distinct(StringComparer.Ordinal).OrderBy(GymFiles.Hash,StringComparer.Ordinal).Take(Math.Max(1,(int)Math.Ceiling(data.Examples.Select(x=>x.entity).Distinct(StringComparer.Ordinal).Count()*.2))).ToHashSet(StringComparer.Ordinal);
    public static void Export(GymDataset data,string destination)
    {
        Validate(data);
        destination=Path.GetFullPath(destination);
        if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("Export needs a new folder; existing bundles are preserved.");
        var parent=Path.GetDirectoryName(destination)!;Directory.CreateDirectory(parent);
        var stage=Path.Combine(parent,".aigym-"+Guid.NewGuid());Directory.CreateDirectory(stage);
        try
        {
            var held=HeldOut(data);
            foreach(var split in new[]{"train","evaluation"})File.WriteAllLines(Path.Combine(stage,split+".jsonl"),data.Examples.Where(x=>held.Contains(x.entity)==(split=="evaluation")).Select(x=>JsonSerializer.Serialize(new{messages=x.messages})),new UTF8Encoding(false));
            GymFiles.Atomic(Path.Combine(stage,"manifest.json"),new{Version=1,Kind="ai-gym-training-handoff",data.Id,data.Name,data.SourceHash,CreatedUtc=DateTimeOffset.UtcNow,SplitPolicy="Exact entity SHA-256 order; first ceil(20%) held out. Semantic duplicates require review.",Examples=data.Examples.Select(x=>new{EntityHash=GymFiles.Hash(x.entity),Split=held.Contains(x.entity)?"evaluation":"train"}),Files=new{TrainSha256=GymFiles.Hash(File.ReadAllText(Path.Combine(stage,"train.jsonl"))),EvaluationSha256=GymFiles.Hash(File.ReadAllText(Path.Combine(stage,"evaluation.jsonl")))}});
            Directory.Move(stage,destination);
        }
        finally { if(Directory.Exists(stage))Directory.Delete(stage,true); }
    }
}
