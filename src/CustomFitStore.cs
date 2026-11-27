using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetherform.Geometry;

namespace Aetherform;

internal sealed class CustomFitStore : IDisposable
{
    internal sealed record Entry(string Id,string GamePath,string SourceKind,string SourceHash,string Recipe,string Sha256);
    internal sealed record Index(int Schema,Entry[] Entries);
    private static readonly Uri Root=new("https://raw.githubusercontent.com/kaldigo/FFXIVAetherform/custom-fits/");
    private readonly string directory;
    private readonly Action<string> warn;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop=new();
    private readonly HashSet<string> wanted=new(StringComparer.Ordinal);
    private Task<CustomFitLibrary>? work;
    private bool dirty;
    private long nextCheck;
    internal CustomFitLibrary Current { get; private set; }=CustomFitLibrary.Empty;
    internal CustomFitStore(string config,Action<string> warn,HttpMessageHandler? handler=null)
    {
        this.warn=warn;directory=Path.Combine(config,"custom-fits");Directory.CreateDirectory(directory);
        http=handler==null?new HttpClient():new HttpClient(handler);
        http.Timeout=TimeSpan.FromSeconds(30);
        try
        {
            string ready=Path.Combine(directory,"cached-index.json"),index=Path.Combine(directory,"index.json");
            if(File.Exists(ready)||File.Exists(index))
                Current=ReadCached(ReadIndex(File.ReadAllBytes(File.Exists(ready)?ready:index)));
        }
        catch(Exception ex) { warn("Could not read cached custom fits: "+ex.Message); }
    }
    internal static Index ReadIndex(byte[] bytes)
    {
        var index=JsonSerializer.Deserialize<Index>(bytes) ?? throw new InvalidDataException("Empty custom fit index.");
        if(index.Schema!=1 || index.Entries==null || index.Entries.Length>10000 || index.Entries.Any(e=>
            !CustomFitRecipe.IdValid(e.Id) || !CustomFitRecipe.PathValid(e.Recipe) || !CustomFitRecipe.HashValid(e.Sha256) ||
            !CustomFitRecipe.HashValid(e.SourceHash) || e.SourceKind is not ("vanilla" or "family") || !CustomFitRecipe.GameValid(e.GamePath)) ||
            index.Entries.Select(e=>e.Id).Distinct().Count()!=index.Entries.Length ||
            index.Entries.Select(e=>(e.GamePath,e.SourceKind,e.SourceHash)).Distinct().Count()!=index.Entries.Length)
            throw new InvalidDataException("Invalid custom fit index.");
        return index;
    }
    private string Blob(string hash)=>Path.Combine(directory,hash);
    private byte[] Cached(string hash)
    {
        var bytes=File.ReadAllBytes(Blob(hash));
        if(CustomFitRecipe.Hash(bytes)!=hash) throw new InvalidDataException("Cached custom fit checksum mismatch.");
        return bytes;
    }
    private static IEnumerable<CustomFitAsset> Assets(CustomFitRecipe r)=>r.Variants.Concat(r.Default==null?[]:new[]{r.Default})
        .SelectMany(v=>new[]{v.Mesh,v.Classification}.OfType<CustomFitAsset>().Concat(v.Baked.Select(f=>f.Mesh))).DistinctBy(a=>a.Sha256);
    private CustomFitBundle Bundle(Entry e)
    {
        var recipe=CustomFitRecipe.Read(Cached(e.Sha256));
        if(recipe.Id!=e.Id || recipe.GamePath!=e.GamePath || recipe.SourceKind!=e.SourceKind || recipe.SourceHash!=e.SourceHash)
            throw new InvalidDataException("Custom fit index and recipe disagree.");
        return new(recipe,e.Sha256,Assets(recipe).ToDictionary(a=>a.Sha256,a=>Cached(a.Sha256)));
    }
    private CustomFitLibrary ReadCached(Index index)
    {
        var bundles=new List<CustomFitBundle>();
        foreach(var entry in index.Entries)
            try {bundles.Add(Bundle(entry));} catch(IOException) {} catch(InvalidDataException) {}
        return new(bundles);
    }
    internal void Request(string path) {if(wanted.Add(path)) dirty=true;}
    internal void Poll()
    {
        if(work is {IsCompleted:true})
        {
            try {Current=work.GetAwaiter().GetResult();}
            catch(OperationCanceledException) when(stop.IsCancellationRequested) {}
            catch(Exception ex) {warn("Keeping cached custom fits; update failed: "+ex.GetBaseException().Message);}
            work=null;
        }
        if(work!=null || stop.IsCancellationRequested || (!dirty && Environment.TickCount64<nextCheck)) return;
        bool refreshIndex=Environment.TickCount64>=nextCheck;
        dirty=false;if(refreshIndex) nextCheck=Environment.TickCount64+600000;
        var paths=wanted.ToHashSet(StringComparer.Ordinal);var previous=Current;
        work=Task.Run(()=>Refresh(paths,previous,refreshIndex,stop.Token),stop.Token);
    }
    private static void Write(string path,byte[] bytes)
    {
        string temp=path+".tmp";
        File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);
    }
    private async Task<byte[]> Download(string path,int maximum,CancellationToken token,string? etag=null)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));token=timeout.Token;
        if(!CustomFitRecipe.PathValid(path)) throw new InvalidDataException("Invalid custom fit download path.");
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(Root,path));
        if(etag!=null) request.Headers.TryAddWithoutValidation("If-None-Match",etag);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.NotModified) return [];
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>maximum) throw new InvalidDataException("Custom fit download is too large.");
        using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();
        var buffer=new byte[65536];int read;
        while((read=await input.ReadAsync(buffer,token))!=0)
        {
            if(output.Length+read>maximum) throw new InvalidDataException("Custom fit download is too large.");
            output.Write(buffer,0,read);
        }
        if(path=="index.json") latestEtag=response.Headers.ETag?.ToString();
        return output.ToArray();
    }
    private string? latestEtag;
    private async Task<CustomFitLibrary> Refresh(HashSet<string> paths,CustomFitLibrary previous,bool refreshIndex,CancellationToken token)
    {
        string indexFile=Path.Combine(directory,"index.json"),tagFile=Path.Combine(directory,"etag.txt");
        var bytes=refreshIndex||!File.Exists(indexFile)?await Download("index.json",4*1024*1024,token,File.Exists(indexFile)&&File.Exists(tagFile)?File.ReadAllText(tagFile):null):[];
        bool changed=bytes.Length!=0;
        if(!changed) bytes=File.ReadAllBytes(indexFile);
        var index=ReadIndex(bytes);
        var ready=previous.Bundles.Where(b=>index.Entries.Any(e=>e.Id==b.Recipe.Id)).ToDictionary(b=>b.Recipe.Id);
        foreach(var entry in index.Entries.Where(e=>paths.Contains(e.GamePath)))
        {
            try
            {
                await Fetch(entry.Recipe,entry.Sha256,1024*1024,token);
                var recipe=CustomFitRecipe.Read(Cached(entry.Sha256));
                foreach(var a in Assets(recipe)) await Fetch(a.Path,a.Sha256,64*1024*1024,token);
                ready[entry.Id]=Bundle(entry);
            }
            catch(Exception ex) when(ex is not OperationCanceledException || !token.IsCancellationRequested)
            {warn("Keeping the previous custom fit for "+entry.Id+": "+ex.GetBaseException().Message);}
        }
        if(changed)
        {
            Write(indexFile,bytes);
            if(latestEtag!=null) File.WriteAllText(tagFile,latestEtag);else if(File.Exists(tagFile)) File.Delete(tagFile);
        }
        var library=new CustomFitLibrary(ready.Values.GroupBy(b=>(b.Recipe.GamePath,b.Recipe.SourceKind,b.Recipe.SourceHash))
            .Select(g=>g.OrderByDescending(b=>index.Entries.Any(e=>e.Sha256==b.Revision)).ThenBy(b=>b.Recipe.Id,StringComparer.Ordinal).First()).ToArray());
        Write(Path.Combine(directory,"cached-index.json"),JsonSerializer.SerializeToUtf8Bytes(new Index(1,library.Bundles.Select(b=>new Entry(
            b.Recipe.Id,b.Recipe.GamePath,b.Recipe.SourceKind,b.Recipe.SourceHash,"cached/"+b.Revision+".json",b.Revision)).ToArray())));
        return library;
    }
    private async Task Fetch(string path,string hash,int maximum,CancellationToken token)
    {
        if(File.Exists(Blob(hash)))
        {
            try {Cached(hash);return;} catch(InvalidDataException) {}
        }
        var bytes=await Download(path,maximum,token);
        if(CustomFitRecipe.Hash(bytes)!=hash) throw new InvalidDataException("Downloaded custom fit checksum mismatch.");
        Write(Blob(hash),bytes);
    }
    public void Dispose()
    {
        stop.Cancel();
        try {work?.GetAwaiter().GetResult();} catch(OperationCanceledException) {} catch(Exception) {}
        http.Dispose();stop.Dispose();
    }
}
