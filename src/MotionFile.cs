using System.Text;
using System.Security.Cryptography;

namespace Aetherform;

// PHYB pointers inside simulators are relative to the first simulator header.
internal sealed class MotionFile
{
    private static readonly int[] Sizes = [36, 36, 48, 32, 52, 48, 16, 36];
    private static readonly int[] ShapeSizes = [124, 116, 92, 168, 80];
    private readonly byte[] prefix, extended;
    private readonly List<byte[]>[] shapes = [[], [], [], [], []];
    private readonly List<Simulator> simulations = [];

    private sealed record Chain(byte[] Head, byte[] Collisions, byte[] Nodes);
    private sealed record Simulator(byte[] Head, byte[][] Lists, Chain[] Chains)
    {
        public int Area
        {
            get
            {
                var bone = Chains.Length == 0 ? "" : Name(Chains[0].Nodes);
                if (bone.StartsWith("j_mune_")) return 0;
                if (bone.Contains("shiri")) return 3;
                if (bone.Contains("fukubu")) return 4;
                if (bone.Contains("daitai")) return 5;
                if (bone.StartsWith("iv_") && new[]{"ochinko","kougan","funyachin","kintama"}.Any(bone.Contains)) return 6;
                return bone switch { "j_mimi_l" => 7, "n_sippo_a" or "n_sippo_b" => 8, _ => -1 };
            }
        }
    }

    private static string Name(byte[] bytes, int pos = 0) =>
        Encoding.ASCII.GetString(bytes.AsSpan(pos, 32)).Split('\0')[0];
    private static int Int(byte[] b, int pos) => BitConverter.ToInt32(b, pos);
    private static void Put(byte[] b, int pos, int value) => BitConverter.TryWriteBytes(b.AsSpan(pos), value);

    public MotionFile(byte[] b)
    {
        originalEndAlignment = b.Length % 8;
        var col = Int(b, 8);
        var sim = Int(b, 12);
        prefix = b[..col];
        if (col < sim)
        {
            int pos = col + 8;
            for (int k = 0; k < shapes.Length; k++)
                for (int j = 0; j < b[col+k]; j++, pos += ShapeSizes[k])
                    shapes[k].Add(b.AsSpan(pos, ShapeSizes[k]).ToArray());
            if (pos != sim) throw new InvalidDataException("Unknown physics collision layout.");
        }
        extended = b.AsSpan(b.Length-24, 4).SequenceEqual("PACK"u8)
            ? b[^checked((int)BitConverter.ToInt64(b, b.Length-8))..] : [];
        var origin = sim + 4;
        byte[] List(byte[] h, int offset, int count, int size) => count == 0 ? [] :
            b.AsSpan(checked(origin + Int(h, offset)), checked(size * count)).ToArray();
        for (int i = 0; i < Int(b, sim); i++)
        {
            var h = b.AsSpan(origin + 72*i, 72).ToArray();
            var lists = Sizes.Select((size,k) => List(h, 40+4*k, h[k], size)).ToArray();
            var chains = new Chain[h[2]];
            for (int j = 0; j < chains.Length; j++)
            {
                var c = lists[2].AsSpan(48*j, 48).ToArray();
                chains[j] = new(c, List(c,40,BitConverter.ToUInt16(c),36),
                    List(c,44,BitConverter.ToUInt16(c,2),84));
            }
            simulations.Add(new(h,lists,chains));
        }
    }

    public void Assemble((MotionFile Source, int Index)[] components)
    {
        var selected = components.Select(c => c.Source.simulations[c.Index]).ToArray();
        simulations.Clear();
        simulations.AddRange(selected);
    }

    public void Replace(int area, MotionFile donor, bool collisions, bool supplementHeight)
    {
        var insert = simulations.FindIndex(s => s.Area == area);
        var incoming = donor.simulations.Where(s => s.Area == area).ToArray();
        if (incoming.Length == 0) throw new InvalidDataException($"Missing physics for area {area}.");
        if (area == 0 && supplementHeight)
        {
            var source = simulations.First(s => s.Area == 0 && s.Head[4] >= 4);
            var chest = incoming.First();
            chest.Lists[4] = [..chest.Lists[4], ..source.Lists[4][^(4*52)..]];
            chest.Head[4] = checked((byte)(chest.Head[4]+4));
        }
        foreach (var s in incoming)
        {
            if (area == 0 && !collisions)
            {
                // The AF collision switch clears both node contact masks.
                foreach (var c in s.Chains)
                    for (int node = 0; node < c.Nodes.Length; node += 84)
                        Array.Clear(c.Nodes, node + 76, 8);
            }
            void Bind(byte[] records, int stride)
            {
                for (int i = 0; i < records.Length; i += stride)
                {
                    var name = Name(records,i);
                    for (int k = 0; k < shapes.Length; k++)
                    {
                        var shape = donor.shapes[k].Find(v => Name(v) == name);
                        if (shape == null) continue;
                        // Give each donor collider a content-derived name so unrelated AF
                        // chains can keep their original collider dimensions and offsets.
                        var alias = "af_" + Convert.ToHexString(SHA256.HashData(shape))[..24];
                        if (!shapes[k].Any(v => Name(v) == alias))
                        {
                            var copy = (byte[])shape.Clone();
                            WriteName(copy,0,alias);
                            shapes[k].Add(copy);
                        }
                        WriteName(records,i,alias);
                        break;
                    }
                }
            }
            Bind(s.Lists[0],36);
            Bind(s.Lists[1],36);
            Bind(s.Lists[7],36);
            foreach (var c in s.Chains) Bind(c.Collisions,36);
        }
        simulations.RemoveAll(s => s.Area == area);
        simulations.InsertRange(insert < 0 ? simulations.Count : insert, incoming);
    }

    private static void WriteName(byte[] data, int at, string name)
    {
        Array.Clear(data,at,32);
        Encoding.ASCII.GetBytes(name,data.AsSpan(at,32));
    }

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(prefix);
        if (shapes.Any(list => list.Count != 0))
        {
            foreach (var list in shapes) w.Write(checked((byte)list.Count));
            w.Write(new byte[]{0xcc,0xcc,0xcc});
            foreach (var list in shapes) foreach (var shape in list) w.Write(shape);
        }
        int simOffset = (int)stream.Position;
        w.Write(simulations.Count);
        int origin = (int)stream.Position;
        w.Write(new byte[simulations.Count * 72]);
        foreach (var (s,index) in simulations.Select((s,i)=>(s,i)))
        {
            for (int k = 0; k < Sizes.Length; k++)
            {
                Put(s.Head,40+4*k,s.Head[k] == 0 ? 0 : (int)stream.Position-origin);
                if (k != 2) { w.Write(s.Lists[k]); continue; }
                var start = stream.Position;
                w.Write(new byte[48*s.Chains.Length]);
                for (int j = 0; j < s.Chains.Length; j++)
                {
                    var c = s.Chains[j];
                    Put(c.Head,40,c.Head[0] == 0 && c.Head[1] == 0 ? 0 : (int)stream.Position-origin);
                    if (BitConverter.ToUInt16(c.Head) != 0) w.Write(c.Collisions);
                    Put(c.Head,44,(int)stream.Position-origin);
                    w.Write(c.Nodes);
                    var end = stream.Position;
                    stream.Position = start+j*48;
                    w.Write(c.Head);
                    stream.Position=end;
                }
            }
            var endPos=stream.Position;
            stream.Position=origin+index*72;
            w.Write(s.Head);
            stream.Position=endPos;
        }
        if (extended.Length != 0)
        {
            // The trailer locates its table relative to EOF. Preserve its original alignment.
            while ((stream.Position + extended.Length) % 8 != originalEndAlignment) w.Write((byte)0);
            w.Write(extended);
        }
        stream.Position=12;
        w.Write(simOffset);
        return stream.ToArray();
    }
    private readonly int originalEndAlignment;
}
