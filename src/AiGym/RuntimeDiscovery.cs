namespace AiGym;

/// <summary>Find existing executables; never installs, executes or infers package capability.</summary>
public static class RuntimeDiscovery
{
    public static (string LlamaServer,string Python) Find() => Find(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),File.Exists);

    public static (string LlamaServer,string Python) Find(Func<string,string?> env,string home,string local,Func<string,bool> exists)
    {
        string First(IEnumerable<string?> candidates) => candidates.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!.Trim().Trim('"'))
            .FirstOrDefault(x=>Path.IsPathFullyQualified(x)&&exists(x)&&!x.Contains(Path.DirectorySeparatorChar+"WindowsApps"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))??"";
        IEnumerable<string> OnPath(string executable)=>(env("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries).Select(x=>Path.Combine(x.Trim().Trim('"'),executable));
        string? In(string variable,string executable)=>env(variable) is {Length:>0} folder?Path.Combine(folder,executable):null;
        // DBCOWBOY_* is accepted for profiles set up before AI Gym was its own app; AIGYM_* is the documented name and wins.
        var llama=First(new[]{env("AIGYM_LLAMA_SERVER"),env("DBCOWBOY_LLAMA_SERVER")}.Concat(OnPath("llama-server.exe")).Concat(new[]{Path.Combine(local,"llama.cpp","llama-server.exe"),Path.Combine(home,"llama.cpp","build","bin","Release","llama-server.exe"),Path.Combine(home,".unsloth","llama.cpp","build","bin","Release","llama-server.exe")}));
        var python=First(new[]{env("AIGYM_PYTHON"),env("DBCOWBOY_PYTHON"),In("VIRTUAL_ENV",Path.Combine("Scripts","python.exe")),In("CONDA_PREFIX","python.exe")}.Concat(OnPath("python.exe")).Concat(OnPath("python3.exe")).Concat(new[]{Path.Combine(home,".unsloth","studio","unsloth_studio","Scripts","python.exe")}));
        return(llama,python);
    }
}
