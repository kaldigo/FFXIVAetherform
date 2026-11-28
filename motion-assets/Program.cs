using System.Security.Cryptography;
using System.Text.Json;
using XivLiveMesh;

if(args.Length!=2) throw new ArgumentException("Supply installed sqpack directory and local output directory.");
using var game=new Lumina.GameData(args[0]);
var path="chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb";
var data=game.GetFile(path)?.Data??throw new Exception("Missing vanilla skeleton: "+path);
var skeleton=HavokSkeleton.Read(data,out var reason)??throw new Exception(reason);
var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
File.WriteAllBytes(Path.Combine(output,"vanilla-c0201.sklb"),data);
File.WriteAllText(Path.Combine(output,"vanilla-c0201-rig.json"),JsonSerializer.Serialize(new{
    skeleton.Names,skeleton.Parents,skeleton.Pose,source=path,
    sha256=Convert.ToHexStringLower(SHA256.HashData(data)),
    scope="Installed vanilla skeleton and reference pose. No game animation tracks or physics simulation are extracted."
},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Extracted {skeleton.Names.Count} vanilla bones and reference transforms.");
