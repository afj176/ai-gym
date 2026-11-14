using AiGym;
using System.Net;
using System.Text;
using System.Text.Json;

var root=Path.Combine(Path.GetFullPath(args[0]),"gym-models-"+Guid.NewGuid());Directory.CreateDirectory(root);
int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
async Task Reject(Func<Task> action,string name){try{await action();}catch(Exception e)when(e is ArgumentException or InvalidDataException or IOException){Check(true,name);return;}throw new Exception("Expected rejection: "+name);}
try{
    Environment.SetEnvironmentVariable("AIGYM_DATA_HOME",root);Check(Store.Root==root,"profile folder override");
    Store.Write("appearance.json",new AppPreferences("Dark"));Check(Store.Read<AppPreferences>("appearance.json")?.Theme=="Dark","profile persistence");
    var quantized=Path.Combine(root,"runtimes","quantized-text");Directory.CreateDirectory(Path.Combine(quantized,"Scripts"));File.WriteAllText(QuantizedRuntime.ManagedPython,"");
    bool noMarker=QuantizedRuntime.IsPrepared;File.WriteAllText(Path.Combine(quantized,QuantizedRuntime.LegacyMarker),"{\"ready\":true}");bool legacy=QuantizedRuntime.IsPrepared;
    File.WriteAllText(Path.Combine(quantized,QuantizedRuntime.Marker),"{}");bool unfinished=QuantizedRuntime.IsPrepared;File.WriteAllText(Path.Combine(quantized,QuantizedRuntime.Marker),"{\"ready\":true}");
    Check(!noMarker&&legacy&&!unfinished&&QuantizedRuntime.IsPrepared,"quantized runtime prepared under the earlier marker name still counts; the current marker wins");
    var weights=Path.Combine(root,"model.gguf");File.WriteAllBytes(weights,"GGUFtest-fixture"u8.ToArray());
    var model=new LocalModel("Fixture",weights,"Fixture","GGUF","Chat",16,"Supported GGUF");
    var argv=ModelServing.Arguments(model,"exact-model",18210,true,root,"manifest",new(4096));
    Check(argv.Contains("CUDA0")&&argv.Contains("4096")&&argv.Contains("exact-model"),"GPU launch retains model identity and selected context");
    Check(ModelServing.Arguments(model,"cpu",18210,false,root,"manifest").Contains("none"),"CPU launch remains available");
    await Reject(()=>Task.Run(()=>ModelServing.Arguments(model with{Task="Embeddings"},"bad",18210,false,root,"manifest")),"embedding models are not served");
    await Reject(()=>Task.Run(()=>ModelServing.Arguments(model with{Task="Reranking"},"bad",18210,false,root,"manifest")),"reranking models are not served");
    await Reject(()=>Task.Run(()=>ModelServing.Arguments(model,"bad",22,false,root,"manifest")),"invalid launch port rejected");
    await Reject(()=>Task.Run(()=>ModelServing.Arguments(model,"bad",18210,false,root,"manifest",new(0))),"invalid context rejected");
    foreach(var url in new[]{"https://example.com/","http://127.0.0.1/?key=secret","http://user:secret@127.0.0.1/","file:///D:/model"})await Reject(()=>Task.Run(()=>LocalAiClient.ValidateEndpoint(url)),"endpoint rejects external or unsafe address");
    Check(LocalAiClient.ValidateEndpoint("http://127.0.0.1:18210/").IsLoopback,"loopback endpoint accepted");
    var handler=new Fixture();using var client=new LocalAiClient(handler);var profile=new AiEndpointProfile{Endpoint="http://127.0.0.1:18210/",Model="fixture-model"};
    handler.Body="{\"data\":[{\"id\":\"fixture-model\"}]}";var listing=await client.Check(profile,AiCapability.Models,"fixture-key",default);Check(listing.Models?.Single()=="fixture-model"&&handler.Path=="/v1/models","model discovery uses correct route");
    Check(handler.Authorization=="Bearer fixture-key","selected endpoint key used");
    handler.Body="{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}";Check((await client.Check(profile,AiCapability.Chat,"",default)).State=="Passed","synthetic chat capability check");
    using(var sent=JsonDocument.Parse(handler.RequestBody!))Check(sent.RootElement.GetProperty("model").GetString()==profile.Model,"capability request binds exact model");
    await Reject(()=>client.Check(profile,AiCapability.Embeddings,"",default),"embedding transport unavailable in AI Gym");
    handler.Body="{\"choices\":[]}";Check((await client.Check(profile,AiCapability.Chat,"",default)).State=="Invalid response","malformed capability response rejected");
    handler.Status=HttpStatusCode.Unauthorized;Check((await client.Check(profile,AiCapability.Chat,"",default)).State=="Failed","authentication failure is not readiness");handler.Status=HttpStatusCode.OK;
    handler.ContentType="text/event-stream";handler.Body="data: {\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\ndata: {\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    var output=new StringBuilder();var result=await client.StreamChat(profile,"",[new("user","Fixture prompt")],64,x=>output.Append(x),default);Check(output.ToString()=="Hello"&&result.FinishReason=="stop"&&result.ObservedModel==profile.Model,"streaming preserves observed model and finish reason");
    Check(handler.Path=="/v1/chat/completions","stream uses chat route");
    AiMessage[] prompt=[new("user","Fixture prompt")];
    var adapter=new TransformersAdapter{Context=4096,Prompt=100};using var adapterClient=new LocalAiClient(adapter);
    Check(await adapterClient.FitOutput(profile,"",prompt,512,default)==512,"output limit kept when it fits the loaded context");
    adapter.Context=512;Check(await adapterClient.FitOutput(profile,"",prompt,512,default)==412,"output limit lowered to the room left beside the prompt");
    adapter.Prompt=512;await Reject(()=>adapterClient.FitOutput(profile,"",prompt,512,default),"prompt filling the context is refused before sending");
    handler.ContentType="application/json";handler.Body="{\"data\":[{\"id\":\"fixture-model\"}]}";Check(await client.FitOutput(profile,"",prompt,512,default)==512,"unmeasurable server keeps the requested limit");
    Console.WriteLine($"{passed} focused model checks passed");
}finally{Directory.Delete(root,true);}

/// <summary>The metadata and token-count routes of AI Gym's Transformers text adapter (scripts/Serve-Transformers.py).</summary>
sealed class TransformersAdapter:HttpMessageHandler{
    public int Context,Prompt;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        string? body=request.RequestUri!.AbsolutePath switch{
            "/v1/models"=>$"{{\"data\":[{{\"id\":\"fixture-model\",\"owned_by\":\"ai-gym-transformers\",\"meta\":{{\"n_ctx\":{Context},\"n_ctx_train\":{Context},\"chat_token_count\":\"v1\",\"n_ctx_output\":null}}}}]}}",
            "/v1/chat/token-count"=>$"{{\"model\":\"fixture-model\",\"prompt_tokens\":{Prompt},\"context_tokens\":{Context},\"output_context_tokens\":null}}",
            _=>null};
        return Task.FromResult(body==null?new HttpResponseMessage(HttpStatusCode.NotFound):new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
    }
}
sealed class Fixture:HttpMessageHandler{
    public string Body="{}",ContentType="application/json";public HttpStatusCode Status=HttpStatusCode.OK;public string? Path,Authorization,RequestBody;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Path=request.RequestUri!.AbsolutePath;Authorization=request.Headers.Authorization?.ToString();RequestBody=request.Content==null?null:await request.Content.ReadAsStringAsync(ct);return new(Status){Content=new StringContent(Body,Encoding.UTF8,ContentType)};}
}
