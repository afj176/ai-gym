using AiGym;
using System.Text.Json;

var root=Path.Combine(Path.GetFullPath(args[0]),"gym-lab-"+Guid.NewGuid());Directory.CreateDirectory(root);
int passed=0,calls=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
async Task Reject(Func<Task> action,string name){try{await action();}catch(Exception e)when(e is InvalidDataException or InvalidOperationException or LabSetupException or IOException or JsonException){Check(true,name);return;}throw new Exception("Expected rejection: "+name);}
try{
    var engine=new LabEngine();engine.Register(new("fixture","Read",LabEffect.Read,"{}","Test fixture"),(s,p,ct)=>{calls++;return Task.FromResult(new LabResult("text","Read fixture"));});
    const string plan="{\"steps\":[{\"tool\":\"fixture\",\"title\":\"Read\",\"arguments\":{}}]}";
    await Reject(()=>Task.Run(()=>engine.ParsePlan(plan,"length")),"truncated plans rejected");
    await Reject(()=>Task.Run(()=>engine.ParsePlan(plan.Replace("fixture","shell.exec"),"stop")),"unregistered shell tools rejected");
    await Reject(()=>Task.Run(()=>engine.ParsePlan(plan.Replace("\"steps\":","\"extra\":true,\"steps\":"),"stop")),"unexpected plan fields rejected");
    var run=new LabRun{Prompt="Inspect",Scope="gym",Steps=engine.ParsePlan(plan,"stop")};
    await engine.Execute(run,run.Steps[0],"gym",false,()=>{},default);Check(calls==0&&run.State==LabState.AwaitingReview,"review gate performs no effects");
    await Reject(()=>engine.Execute(run,run.Steps[0],"other",true,()=>{},default),"changed scope blocked");
    await engine.Execute(run,run.Steps[0],"gym",true,()=>{},default);Check(calls==1&&run.State==LabState.Completed,"approved tool executes once");
    await Reject(()=>engine.Execute(run,run.Steps[0],"gym",true,()=>{},default),"completed effects cannot replay");
    var store=new LabStore(root);store.Save(run);var stale=store.Load().Single();run.Title="New writer";store.Save(run);stale.Title="Stale writer";
    await Reject(()=>Task.Run(()=>store.Save(stale)),"stale writer preserves newer checkpoint");
    var interrupted=new LabRun{Prompt="Interrupted",Scope="gym",State=LabState.Running,Steps=[new(){Tool="fixture",Title="Read",State=LabState.Running}]};store.Save(interrupted);
    var recovered=store.Load().Single(x=>x.Id==interrupted.Id);Check(recovered.State==LabState.Interrupted&&recovered.Steps[0].State==LabState.Interrupted,"restart never resumes in-flight effects");
    engine.Register(new("cancel","Cancel",LabEffect.Read,"{}","Cancellation fixture"),async(s,p,ct)=>{await Task.Delay(10000,ct);return new("text","Unexpected");});
    using(var ct=new CancellationTokenSource(20)){var cancelled=new LabRun{Prompt="Cancel",Scope="gym",Steps=[new(){Tool="cancel",Title="Cancel"}]};await engine.Execute(cancelled,cancelled.Steps[0],"gym",true,()=>{},ct.Token);Check(cancelled.State==LabState.Cancelled&&cancelled.Steps[0].Result==null,"cancellation retains truthful state");}
    var ordered=new LabRun{Prompt="Order",Scope="gym",Steps=[new(){Tool="fixture",Title="First"},new(){Tool="fixture",Title="Second"}]};await Reject(()=>engine.Execute(ordered,ordered.Steps[1],"gym",true,()=>{},default),"out-of-order effects rejected");
    Console.WriteLine($"{passed} lab checks passed");
}finally{Directory.Delete(root,true);}
