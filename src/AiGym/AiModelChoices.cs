namespace AiGym;

/// <summary>The saved external and app-managed connections a page may offer for one capability.</summary>
public static class AiModelChoices
{
    public static AiEndpointProfile[] For(IEnumerable<AiEndpointProfile> saved,AiCapability capability)=>saved.Where(p=>
    {
        var purpose=p.ManagedCapability;
        if(purpose is { } task && task!=capability)return false;
        var check=p.Checks.LastOrDefault(x=>x.Capability==capability);
        if(check?.State=="Passed")return true;
        if(check?.State=="Unavailable")return false;
        // Unchecked external connections remain selectable; verified different-purpose endpoints do not.
        return purpose==capability || !p.Checks.Any(x=>x.Capability!=AiCapability.Models&&x.State=="Passed");
    }).ToArray();
}
