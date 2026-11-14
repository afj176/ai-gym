using System.Text.Json;
using AiGym;

var root=Path.Combine(args.Length>0?Path.GetFullPath(args[0]):Path.GetTempPath(),"aigym-dataset-checks-"+Guid.NewGuid());Directory.CreateDirectory(root);
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);checks++;}
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is InvalidDataException or IOException){Check(true,name);return;}throw new Exception("Expected rejection: "+name);}
try
{
    var source=Path.Combine(root,"examples.jsonl");
    var rows=Enumerable.Range(0,12).Select(i=>new GymExample("entity-"+(i/2),[new("user","Question "+i),new("assistant","Answer "+i)])).ToArray();
    File.WriteAllLines(source,rows.Select(x=>JsonSerializer.Serialize(x)));
    var data=GymDatasets.Import(source);GymDatasets.Save(root,data);
    Check(GymDatasets.List(root).Single().SourceHash==data.SourceHash,"saved dataset retains source identity");
    var held=GymDatasets.HeldOut(data);Check(held.Count==2,"ceil 20 percent split uses distinct entities");
    Check(GymDatasets.HeldOut(data with{Examples=rows.Reverse().ToArray()}).SetEquals(held),"split is independent of row order");
    var bundle=Path.Combine(root,"bundle");GymDatasets.Export(data,bundle);
    using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle,"manifest.json")));
    var m=manifest.RootElement;var provenance=m.GetProperty("Examples").EnumerateArray().ToArray();
    var train=provenance.Where(x=>x.GetProperty("Split").GetString()=="train").Select(x=>x.GetProperty("EntityHash").GetString()).ToHashSet();
    var eval=provenance.Where(x=>x.GetProperty("Split").GetString()=="evaluation").Select(x=>x.GetProperty("EntityHash").GetString()).ToHashSet();
    Check(!train.Overlaps(eval),"entity identities never cross train/evaluation boundary");
    Check(File.ReadAllLines(Path.Combine(bundle,"train.jsonl")).Length==8&&File.ReadAllLines(Path.Combine(bundle,"evaluation.jsonl")).Length==4,"all examples retained in correct split");
    Check(m.GetProperty("Files").GetProperty("TrainSha256").GetString()==GymFiles.Hash(File.ReadAllText(Path.Combine(bundle,"train.jsonl"))),"manifest binds exact training bytes");
    Check(m.GetProperty("Files").GetProperty("EvaluationSha256").GetString()==GymFiles.Hash(File.ReadAllText(Path.Combine(bundle,"evaluation.jsonl"))),"manifest binds exact evaluation bytes");
    Reject(()=>GymDatasets.Export(data,bundle),"existing bundle cannot be overwritten");
    File.WriteAllText(source,JsonSerializer.Serialize(rows[0]));Reject(()=>GymDatasets.Import(source),"single entity cannot become training bundle");
    File.WriteAllLines(source,[JsonSerializer.Serialize(new{entity="a",messages=new[]{new{role="user",content="Q"}}}),JsonSerializer.Serialize(rows[2])]);Reject(()=>GymDatasets.Import(source),"missing assistant target rejected");
    File.WriteAllText(source,"null\nnull");Reject(()=>GymDatasets.Import(source),"null records rejected");
    File.WriteAllText(source,"{\"entity\":\"a\",\"messages\":[null,null]}\n");Reject(()=>GymDatasets.Import(source),"null messages rejected without a null dereference");
    var altered=data with{Examples=rows.Select((x,i)=>i==0?x with{messages=[new("user","changed"),new("assistant","changed")]}:x).ToArray()};
    Reject(()=>GymDatasets.Save(root,altered),"changed saved content cannot retain the old integrity hash");
    Check(!Directory.EnumerateDirectories(root,".aigym-*").Any(),"export staging cleaned");
    var broken=Path.Combine(root,"datasets","broken.json");File.WriteAllText(broken,"{ not json");
    var tampered=Path.Combine(root,"datasets","tampered.json");File.WriteAllText(tampered,JsonSerializer.Serialize(altered));
    var loaded=GymDatasets.Load(root);
    Check(loaded.Datasets.Single().Id==data.Id&&loaded.Skipped.Select(x=>x.File).Order().SequenceEqual(["broken.json","tampered.json"])&&loaded.Skipped.All(x=>x.Reason.Length>0),"unreadable dataset files are skipped with a reason, not hiding valid ones");
    Check(File.Exists(broken)&&File.Exists(tampered),"skipped dataset files stay on disk");
    Console.WriteLine($"{checks} dataset checks passed");
}
finally { Directory.Delete(root,true); }
