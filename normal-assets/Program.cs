using System.Text.Json;
using System.Security.Cryptography;
using Penumbra.GameData.Files;
using Lumina.Data.Files;
var options=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
if(args.Length!=2)throw new ArgumentException("Supply sqpack directory and local output directory.");
using var game=new Lumina.GameData(args[0]);string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
var modelHashes=new Dictionary<string,string>();
foreach(string slot in new[]{"top","dwn"}){
 string path=$"chara/equipment/e0000/model/c0201e0000_{slot}.mdl";
 byte[] data=game.GetFile(path)?.Data??throw new Exception("Missing source model "+path);
 modelHashes[slot]=Convert.ToHexStringLower(SHA256.HashData(data));
}
File.WriteAllText(Path.Combine(root,"model-hashes.json"),JsonSerializer.Serialize(modelHashes,options));
var paths=new[]{
 "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl",
 "chara/equipment/e0000/material/v0001/mt_c0201e0000_top_a.mtrl",
 "chara/equipment/e0000/material/v0001/mt_c0201e0000_dwn_a.mtrl"};
var rows=new List<object>();
foreach(var path in paths){
 var raw=game.GetFile(path)?.Data??throw new Exception("Missing original material "+path);
 var m=new Penumbra.GameData.Files.MtrlFile(raw);if(!m.Valid)throw new Exception("Invalid material "+path);
 File.WriteAllBytes(Path.Combine(root,Path.GetFileName(path)),raw);
 string sp="shader/sm5/shpk/"+m.ShaderPackage.Name;
 var shader=game.GetFile(sp)?.Data;
 if(shader!=null){
  File.WriteAllBytes(Path.Combine(root,m.ShaderPackage.Name),shader);
  var shpk=new ShpkFile(shader,false);
  var defaults=shpk.MaterialParamsDefaults?.ToArray()??throw new Exception("Missing material parameter defaults "+sp);
  foreach(var constant in m.ShaderPackage.Constants){
   var param=shpk.GetMaterialParamById(constant.Id);
   if(param is {} pp)m.ShaderPackage.ShaderValues.AsSpan(constant.ByteOffset,constant.ByteSize).CopyTo(defaults.AsSpan(pp.ByteOffset));
  }
  File.WriteAllBytes(Path.Combine(root,Path.GetFileNameWithoutExtension(path)+".constants"),defaults);
  File.WriteAllText(Path.Combine(root,m.ShaderPackage.Name+".json"),JsonSerializer.Serialize(new{shpk.MaterialParams,shpk.Constants},options));
 }
 var textures=new List<object>();
 foreach(var tex in m.Textures){
  string tp=tex.Path;
  var f=game.GetFile<TexFile>(tp)??throw new Exception("Missing original texture "+tp);
  string stem=Path.GetFileNameWithoutExtension(tp);
  File.WriteAllBytes(Path.Combine(root,stem+".tex"),f.Data);
  byte[] bgra=f.TextureBuffer.Filter(mip:0,z:0,format:TexFile.TextureFormat.B8G8R8A8).RawData;
  File.WriteAllBytes(Path.Combine(root,stem+".bgra"),bgra);
  textures.Add(new{path=tp,width=f.Header.Width,height=f.Header.Height,format=f.Header.Format,
   sha256=Convert.ToHexStringLower(SHA256.HashData(f.Data)),decoded=stem+".bgra",descriptor=tex});
 }
 rows.Add(new{path,sha256=Convert.ToHexStringLower(SHA256.HashData(raw)),material=new{m.ShaderPackage,m.UvSets,m.Textures},textures});
}
File.WriteAllText(Path.Combine(root,"materials.json"),JsonSerializer.Serialize(rows,options));
