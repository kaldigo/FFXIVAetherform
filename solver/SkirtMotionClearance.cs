using System.Numerics;
using Dalamud.Plugin.Services;
using Proteus.Services;
using Group=Aetherform.Geometry.ThighMotionEnvelope.Group;

namespace Aetherform.Geometry;

// Reconcile body-driven motion with the emitted skin while retaining the skirt
// rig, then reserve clearance for the additional motion introduced by refitting.
internal static class SkirtMotionClearance
{
    private sealed record Site(Vector3 Point,Dictionary<string,float> Weights,int Mesh,int Vertex);
    private sealed record Reference(Vector3 Point,Group[] Groups,Dictionary<string,float> Weights);
    private sealed record Constraint(int[] Nodes,float[] Bary,Vector3 Host,Vector3 Normal,Group[] Body,Group[] CurrentBody,
        Group[] OldCloth,float Margin,float AbsoluteMargin);

    internal static byte[] Apply(byte[] original,byte[] fit,IReadOnlyDictionary<string,byte[]> targets,
        IEnumerable<byte[]> sourceBodies,IPluginLog log,Dictionary<int,ClothSubdivision.Origins>? origins=null,
        IReadOnlyDictionary<int,IReadOnlySet<int>>? held=null)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var layout=SecondSkinWriter.Parse(fit);
        if(!layout.BoneNames.Any(ThighMotionEnvelope.IsSkirt)) return fit;
        var sites=new List<Site>();
        void Skin(byte[] bytes,List<Site>? destination=null)
        {
            destination ??= sites;
            var source=SecondSkinWriter.Parse(bytes);
            for(int m=source.Lod0MeshIndex;m<source.Lod0MeshIndex+source.Lod0MeshCount;m++)
            {
                if(!SecondSkinWriter.IsBodySkinMaterial(source.MatNames[BitConverter.ToUInt16(bytes,source.MeshStart+36*m+8)])) continue;
                var mesh=new GarmentClearance.Mesh(source,m);
                var weights=new Dictionary<int,Dictionary<string,float>>();
                foreach(int v in mesh.Indices)
                {
                    if(!weights.TryGetValue(v,out var w)) weights[v]=w=SurfaceSkinning.Read(bytes,source,m,v);
                    destination.Add(new(mesh.P[v],w,m-source.Lod0MeshIndex,v));
                }
            }
        }
        Skin(fit);
        int drawnSites=sites.Count;
        bool Thigh(Site site)=>site.Weights.Where(p=>ThighMotionEnvelope.IsThigh(p.Key)).Sum(p=>p.Value)>.25f;
        bool drawnThigh=sites.Any(Thigh);
        // A skin cut can remove the neutral host under a skirt's edge. Include
        // the selected leg guide so those gaps do not hide a motion constraint.
        if(targets.TryGetValue("_dwn",out var legs)) Skin(legs);
        if(!sites.Any(Thigh)) return fit;
        var oldSites=new List<Site>();
        Skin(original,oldSites);
        int oldDrawnSites=oldSites.Count;
        foreach(var body in sourceBodies) Skin(body,oldSites);
        var oldDrawn=oldDrawnSites==0 ? null : new ClearanceSurface(oldSites.Take(oldDrawnSites).Select(site=>site.Point).ToArray());
        var oldSurface=new ClearanceSurface(oldSites.Skip(oldDrawnSites).Select(site=>site.Point).ToArray());
        var referenceLayout=SecondSkinWriter.Parse(original);
        bool sameSkin=drawnSites==oldDrawnSites && Enumerable.Range(0,drawnSites)
            .All(i=>sites[i].Mesh==oldSites[i].Mesh && sites[i].Vertex==oldSites[i].Vertex);
        var bodyReferences=new Dictionary<(Vector3,bool),Reference>();
        Reference BodyReference(Vector3 point,bool preferGuide=false)
        {
            var key=(point,preferGuide);
            if(bodyReferences.TryGetValue(key,out var cached)) return cached;
            var hit=oldSurface.Nearest(point);int at=oldDrawnSites+3*hit.Face;
            if(oldDrawn!=null && !preferGuide)
            {
                var actual=oldDrawn.Nearest(point);
                if(actual.Distance<=hit.Distance+.01f) {hit=actual;at=3*hit.Face;}
            }
            BrushTransfer.ClosestOnTriangle(point,oldSites[at].Point,oldSites[at+1].Point,oldSites[at+2].Point,out float a,out float b,out float c);
            float[] bary=[a,b,c];var groups=new Group[2];var weights=new Dictionary<string,float>();
            for(int k=0;k<3;k++) foreach(var (bone,weight) in oldSites[at+k].Weights)
            {
                float mass=bary[k]*weight;
                weights[bone]=weights.GetValueOrDefault(bone)+mass;
                int side=ThighMotionEnvelope.Side(bone);if(side<0) continue;
                groups[side]=new(groups[side].Mass+mass,groups[side].Moment+mass*oldSites[at+k].Point);
            }
            var reference=new Reference(hit.Point,groups,weights);bodyReferences.Add(key,reference);return reference;
        }
        Group[] SiteGroups(Site site)=>Enumerable.Range(0,2).Select(side=>
        {
            float mass=site.Weights.Where(p=>ThighMotionEnvelope.Side(p.Key)==side).Sum(p=>p.Value);
            return new Group(mass,mass*site.Point);
        }).ToArray();
        var oldBodyGroups=oldSites.Select(SiteGroups).ToArray();
        var pivots=new Vector3[2];
        for(int side=0;side<2;side++)
        {
            var region=sites.Where(s=>s.Weights.Where(p=>ThighMotionEnvelope.Side(p.Key)==side && ThighMotionEnvelope.IsThigh(p.Key)).Sum(p=>p.Value)>.25f).ToArray();
            if(region.Length==0) return fit;
            float top=region.Select(s=>s.Point.Y).Order().ElementAt((int)(.95f*(region.Length-1)));
            var upper=region.Where(s=>s.Point.Y>=top).Select(s=>s.Point).ToArray();
            pivots[side]=upper.Aggregate(Vector3.Zero,(a,b)=>a+b)/upper.Length;
        }
        var surface=drawnSites==0 ? null : new ClearanceSurface(sites.Take(drawnSites).Select(s=>s.Point).ToArray());
        var guide=drawnSites==sites.Count ? null : new ClearanceSurface(sites.Skip(drawnSites).Select(s=>s.Point).ToArray());
        var bodyGroups=sites.Select(site=>Enumerable.Range(0,2).Select(side=>
        {
            float mass=site.Weights.Where(p=>ThighMotionEnvelope.Side(p.Key)==side).Sum(p=>p.Value);
            return new Group(mass,mass*site.Point);
        }).ToArray()).ToArray();
        var primary=sites.Select(site=>site.Weights.Where(p=>ThighMotionEnvelope.IsThigh(p.Key)).Sum(p=>p.Value)).ToArray();
        bool Contact(Vector3 point,out ClearanceSurface.Hit hit,out int offset)
        {
            hit=drawnThigh ? surface!.Nearest(point) : guide!.Nearest(point);offset=drawnThigh?0:drawnSites;
            bool Exterior(ClearanceSurface.Hit candidate,bool cutFallback=false)
            {
                var normal=candidate.Distance>1e-5f ? (point-candidate.Point)/candidate.Distance : candidate.Normal;
                var radial=candidate.Point-(pivots[0]+pivots[1])*.5f;radial.Y=0;
                return ThighMotionEnvelope.Exterior(normal,candidate.Point,(pivots[0]+pivots[1])*.5f) &&
                    (!cutFallback || Vector3.Dot(normal,radial)>.65f*radial.Length() &&
                        ThighMotionEnvelope.Exterior(candidate.Normal,candidate.Point,(pivots[0]+pivots[1])*.5f) &&
                        Vector3.Dot(normal,candidate.Normal)>.5f);
            }
            if(guide!=null && (hit.Distance>40 || !Exterior(hit)))
            {
                // The emitted skin may end at a sewn cut. Its closest point
                // then lies along the cut instead of beneath the cloth. Use
                // the complete selected leg surface to recover that host.
                hit=guide.Nearest(point);offset=drawnSites;
            }
            return hit.Distance<=40 && Exterior(hit,offset>=drawnSites);
        }
        var bodyAdjacency=new Dictionary<(bool Guide,Vector3 Point),HashSet<int>>();
        for(int t=0;t<sites.Count;t+=3)
        {
            for(int k=0;k<3;k++)
            {
                var key=(t>=drawnSites,sites[t+k].Point);
                if(!bodyAdjacency.TryGetValue(key,out var faces)) bodyAdjacency.Add(key,faces=[]);
                faces.Add(t/3);
            }
        }
        var sewn=ClothSubdivision.IdentifyJoins(layout).Keys.ToLookup(e=>e.Mesh);
        var bodyBones=sites.Concat(oldSites).SelectMany(s=>s.Weights.Keys).ToHashSet(StringComparer.Ordinal);
        var owned=layout.BoneNames.Where(n=>ThighMotionEnvelope.IsSkirt(n) ||
            !bodyBones.Contains(n) && ThighMotionEnvelope.Side(n)<0 && n is not ("n_root" or "j_kosi") && !n.StartsWith("j_sebo_",StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var transfers=new Dictionary<int,Dictionary<int,Dictionary<string,float>>>();
        byte[]? output=null;int moved=0;float worstMove=0;
        for(int m=layout.Lod0MeshIndex;m<layout.Lod0MeshIndex+layout.Lod0MeshCount;m++)
        {
            if(SecondSkinWriter.IsBodySkinMaterial(layout.MatNames[BitConverter.ToUInt16(fit,layout.MeshStart+36*m+8)])) continue;
            var draw=new GarmentClearance.Mesh(layout,m);
            var used=draw.Indices.Distinct().ToArray();
            var weights=used.ToDictionary(v=>v,v=>SurfaceSkinning.Read(fit,layout,m,v));
            if(!weights.Values.Any(w=>w.Keys.Any(ThighMotionEnvelope.IsSkirt))) continue;
            int oldMesh=referenceLayout.Lod0MeshIndex+m-layout.Lod0MeshIndex;
            var oldDraw=new GarmentClearance.Mesh(referenceLayout,oldMesh);
            var oldWeights=new Dictionary<int,Group[]>();
            var authoredWeights=new Dictionary<int,Dictionary<string,float>>();
            Group[] OriginalWeights(int vertex)
            {
                if(oldWeights.TryGetValue(vertex,out var cached)) return cached;
                var w=SurfaceSkinning.Read(original,referenceLayout,oldMesh,vertex);
                authoredWeights[vertex]=w;
                var groups=Enumerable.Range(0,2).Select(side=>
                {
                    float mass=w.Where(p=>ThighMotionEnvelope.Side(p.Key)==side).Sum(p=>p.Value);
                    return new Group(mass,mass*oldDraw.P[vertex]);
                }).ToArray();
                oldWeights.Add(vertex,groups);return groups;
            }
            var references=new Dictionary<int,Reference>();
            if(origins!=null && origins.TryGetValue(m,out var lineage))
            {
                var parents=new Dictionary<int,int>();
                for(int f=0;f<lineage.Parents.Length;f++) for(int k=0;k<3;k++) parents.TryAdd(draw.Indices[3*f+k],lineage.Parents[f]);
                foreach(int vertex in used)
                {
                    var rest=1000*lineage.Positions[vertex];int parent=parents[vertex];
                    int[] corners=oldDraw.Indices[(3*parent)..(3*parent+3)];
                    var referencePoint=BrushTransfer.ClosestOnTriangle(rest,oldDraw.P[corners[0]],oldDraw.P[corners[1]],oldDraw.P[corners[2]],out float a,out float b,out float c);
                    if(Vector3.Distance(rest,referencePoint)>.01f)
                        throw new InvalidDataException($"Invalid cloth source lineage in mesh {m}, vertex {vertex}: source {rest}, parent {parent}, error {Vector3.Distance(rest,referencePoint):F3} mm.");
                    float[] bary=[a,b,c];var groups=new Group[2];var authored=new Dictionary<string,float>();
                    for(int k=0;k<3;k++) for(int side=0;side<2;side++)
                    {
                        var w=OriginalWeights(corners[k])[side];
                        groups[side]=new(groups[side].Mass+bary[k]*w.Mass,groups[side].Moment+bary[k]*w.Moment);
                    }
                    for(int k=0;k<3;k++) foreach(var (bone,w) in authoredWeights[corners[k]])
                        authored[bone]=authored.GetValueOrDefault(bone)+bary[k]*w;
                    references.Add(vertex,new(rest,groups,authored));
                }
            }
            else foreach(int vertex in used) references.Add(vertex,new(oldDraw.P[vertex],OriginalWeights(vertex),authoredWeights[vertex]));
            var pinned=sewn[m].SelectMany(e=>new[]{e.A,e.B}).ToHashSet();
            pinned.UnionWith(held?.GetValueOrDefault(m) ?? new HashSet<int>());
            pinned.UnionWith(draw.AmbiguousShapeBases);
            foreach(var (basis,replacements) in draw.ShapeTargets)
                if(replacements.Any(v=>held?.GetValueOrDefault(m)?.Contains(v)==true)) pinned.Add(basis);
            var aliases=new Dictionary<(Vector3,Vector3,string),int>();
            var nodes=new Dictionary<int,int>();var points=new List<Vector3>();var members=new List<List<int>>();
            foreach(int v in used)
            {
                string motion=string.Join('|',weights[v].OrderBy(p=>p.Key).Select(p=>p.Key+"="+MathF.Round(255*p.Value)));
                var key=(draw.P[v],references[v].Point,motion);
                if(!aliases.TryGetValue(key,out int id))
                { aliases.Add(key,id=points.Count);points.Add(draw.P[v]);members.Add([]); }
                nodes.Add(v,id);members[id].Add(v);
            }
            var fixedNodes=members.Select(list=>list.Any(pinned.Contains)).ToArray();
            // A draw can contain both the animated skirt and sewn underwear.
            // Only the skirt-driven region needs this independent-motion pass.
            var skirtMass=members.Select(list=>weights[list[0]].Where(p=>ThighMotionEnvelope.IsSkirt(p.Key)).Sum(p=>p.Value)).ToArray();
            for(int i=0;i<fixedNodes.Length;i++) fixedNodes[i]|=skirtMass[i]<=0;
            var adapted=new Dictionary<int,Dictionary<string,float>>();
            foreach(var list in members)
            {
                if(list.Any(pinned.Contains)) continue;
                int vertex=list[0];var point=draw.P[vertex];
                if(!weights[vertex].Keys.Any(ThighMotionEnvelope.IsSkirt)) continue;
                if(!Contact(point,out var hit,out int offset)) continue;
                int first=offset+3*hit.Face;
                BrushTransfer.ClosestOnTriangle(point,sites[first].Point,sites[first+1].Point,sites[first+2].Point,out float a,out float b,out float c);
                if(a*primary[first]+b*primary[first+1]+c*primary[first+2]<.001f) continue;
                var target=new Dictionary<string,float>();float[] bary=[a,b,c];
                for(int k=0;k<3;k++) foreach(var (bone,w) in sites[first+k].Weights)
                    target[bone]=target.GetValueOrDefault(bone)+bary[k]*w;
                var reference=references[vertex];var oldHost=BodyReference(reference.Point,offset>=drawnSites);
                var fitted=SkirtMotionWeights.Fit(weights[vertex],reference.Weights,oldHost.Weights,target,owned);
                foreach(int v in list)
                {
                    var prior=weights[v];adapted[v]=weights[v]=fitted;
                    foreach(int shape in draw.ShapeTargets.GetValueOrDefault(v) ?? [])
                    {
                        if(used.Contains(shape) || held?.GetValueOrDefault(m)?.Contains(shape)==true) continue;
                        var variant=SurfaceSkinning.Read(fit,layout,m,shape);
                        adapted[shape]=SkirtMotionWeights.Fit(variant,variant,prior,fitted,owned);
                    }
                }
            }
            if(adapted.Count>0) transfers[m]=adapted;
            var neighbours=points.Select(_=>new HashSet<int>()).ToArray();
            var mass=members.Select(list=>Enumerable.Range(0,2).Select(side=>weights[list[0]].Where(p=>ThighMotionEnvelope.Side(p.Key)==side).Sum(p=>p.Value)).ToArray()).ToArray();
            var sourcePoints=members.Select(list=>references[list[0]].Point).ToArray();
            var sourceGroups=members.Select(list=>references[list[0]].Groups).ToArray();
            var constraints=new List<Constraint>();
            var faces=new List<(int A,int B,int C,Vector3 Normal,float Area)>();
            void Sample(int[] ids,float[] bary)
            {
                if(ids.Distinct().Count()!=ids.Length)
                {
                    var merged=Enumerable.Range(0,ids.Length).GroupBy(j=>ids[j]).Select(g=>(Id:g.Key,Weight:g.Sum(j=>bary[j]))).ToArray();
                    ids=merged.Select(p=>p.Id).ToArray();bary=merged.Select(p=>p.Weight).ToArray();
                }
                if(ids.All(i=>skirtMass[i]<=0)) return;
                var point=Vector3.Zero;var oldPoint=Vector3.Zero;var oldCloth=new Group[2];
                for(int j=0;j<ids.Length;j++)
                {
                    point+=bary[j]*points[ids[j]];oldPoint+=bary[j]*sourcePoints[ids[j]];
                    for(int side=0;side<2;side++)
                    {
                        var g=sourceGroups[ids[j]][side];
                        oldCloth[side]=new(oldCloth[side].Mass+bary[j]*g.Mass,oldCloth[side].Moment+bary[j]*g.Moment);
                    }
                }
                if(ids.All(i=>fixedNodes[i])) return;
                if(!Contact(point,out var hit,out int offset)) return;
                // The closest-feature direction also works at open cut edges,
                // where either adjacent face can win the nearest-point tie and
                // its infinite face plane is not a reliable inside/outside test.
                var normal=hit.Distance>1e-5f ? Vector3.Normalize(point-hit.Point) : hit.Normal;
                if(hit.Distance<=1e-5f && !ThighMotionEnvelope.Exterior(normal,hit.Point,(pivots[0]+pivots[1])*.5f)) normal=-normal;
                // A skirt surrounds both legs. Inner thigh planes face into
                // the inter-leg gap and must not become opposing constraints
                // that squeeze the same cloth point in incompatible directions.
                if(!ThighMotionEnvelope.Exterior(normal,hit.Point,(pivots[0]+pivots[1])*.5f)) return;
                int t=offset+3*hit.Face;
                var candidates=new HashSet<int>{t/3};
                for(int k=0;k<3;k++) candidates.UnionWith(bodyAdjacency[(t>=drawnSites,sites[t+k].Point)]);
                float free=Enumerable.Range(0,ids.Length).Where(j=>!fixedNodes[ids[j]]).Sum(j=>bary[j]);
                var left=default(Group);var right=default(Group);
                for(int j=0;j<ids.Length;j++)
                {
                    int id=ids[j];float w=bary[j];
                    left=new(left.Mass+w*mass[id][0],left.Moment+w*mass[id][0]*points[id]);
                    right=new(right.Mass+w*mass[id][1],right.Moment+w*mass[id][1]*points[id]);
                }
                foreach(int face in candidates)
                {
                    int first=3*face;
                    var faceNormal=Vector3.Cross(sites[first+1].Point-sites[first].Point,sites[first+2].Point-sites[first].Point);
                    if(faceNormal.LengthSquared()<1e-16f) continue;
                    faceNormal=Vector3.Normalize(faceNormal);
                    BrushTransfer.ClosestOnTriangle(point,sites[first].Point,sites[first+1].Point,sites[first+2].Point,out float a,out float b,out float c);
                    var host=a*sites[first].Point+b*sites[first+1].Point+c*sites[first+2].Point;
                    float distance=Vector3.Distance(point,host);
                    if(distance>hit.Distance+8) continue;
                    var direction=distance>1e-5f ? (point-host)/distance : normal;
                    if(!ThighMotionEnvelope.Exterior(direction,host,(pivots[0]+pivots[1])*.5f)) continue;
                    if(first>=drawnSites)
                    {
                        var radial=host-(pivots[0]+pivots[1])*.5f;radial.Y=0;
                        if(Vector3.Dot(direction,radial)<=.65f*radial.Length() ||
                            !ThighMotionEnvelope.Exterior(faceNormal,host,(pivots[0]+pivots[1])*.5f) || Vector3.Dot(direction,faceNormal)<=.5f) continue;
                    }
                    if(a*primary[first]+b*primary[first+1]+c*primary[first+2]<.001f) continue;
                    var oldHost=BodyReference(host-(point-oldPoint),first>=drawnSites);
                    float authoredGap=Math.Clamp(Vector3.Dot(oldPoint-oldHost.Point,direction),0,40);
                    // Sample the local material point on neighbouring skin
                    // faces too. Remote triangle corners must not turn a finite
                    // skin face into an infinite obstacle under a long drape.
                    var currentBody=new Group[2];var previousBody=new Group[2];float[] hostBary=[a,b,c];
                    for(int k=0;k<3;k++) for(int side=0;side<2;side++)
                    {
                        var now=bodyGroups[first+k][side];
                        currentBody[side]=new(currentBody[side].Mass+hostBary[k]*now.Mass,currentBody[side].Moment+hostBary[k]*now.Moment);
                        if(sameSkin && first<drawnSites)
                        {
                            var prior=oldBodyGroups[first+k][side];
                            previousBody[side]=new(previousBody[side].Mass+hostBary[k]*prior.Mass,previousBody[side].Moment+hostBary[k]*prior.Moment);
                        }
                    }
                    if(!sameSkin || first>=drawnSites) previousBody=oldHost.Groups;
                    var body=new[]{currentBody[0]-previousBody[0],currentBody[1]-previousBody[1]};
                    float extra=ThighMotionEnvelope.Closure(direction,pivots[0],body[0],left-oldCloth[0])+
                        ThighMotionEnvelope.Closure(direction,pivots[1],body[1],right-oldCloth[1]);
                    // Preserve authored clearance and relative movement. The
                    // source rig already supplies the skirt's animation;
                    // reserve only the additional closing from this refit.
                    // Preserve the source's local surface gap, not the
                    // distance to a remote corner of a large skin triangle.
                    float reserve=MathF.Min(authoredGap,MathF.Max(0,Vector3.Dot(point-host,direction)));
                    if(extra>.05f) reserve+=free*ThighMotionEnvelope.MarginMm;
                    float absolute=free*ThighMotionEnvelope.MarginMm+
                        ThighMotionEnvelope.Closure(direction,pivots[0],currentBody[0],left)+
                        ThighMotionEnvelope.Closure(direction,pivots[1],currentBody[1],right);
                    float risk=MathF.Min(reserve+extra,absolute)-Vector3.Dot(point-host,direction);
                    if(risk< -2) continue;
                    constraints.Add(new(ids,bary,host,direction,body,currentBody,oldCloth,reserve,
                        free*ThighMotionEnvelope.MarginMm));
                }
            }
            foreach(int v in Enumerable.Range(0,points.Count)) Sample([v],[1]);
            for(int t=0;t<draw.Indices.Length;t+=3)
            {
                int[] ids=draw.Indices[t..(t+3)].Select(v=>nodes[v]).ToArray();
                var normal=Vector3.Cross(points[ids[1]]-points[ids[0]],points[ids[2]]-points[ids[0]]);
                float area=normal.Length();
                if(area>1e-4f) faces.Add((ids[0],ids[1],ids[2],normal/area,area));
                for(int j=0;j<3;j++) {neighbours[ids[j]].Add(ids[(j+1)%3]);neighbours[ids[(j+1)%3]].Add(ids[j]);}
                Sample(ids,[1f/3,1f/3,1f/3]);
                for(int j=0;j<3;j++) Sample([ids[j],ids[(j+1)%3]],[.5f,.5f]);
            }
            if(constraints.Count==0) continue;
            var shifts=new Vector3[points.Count];
            float Deficit(Constraint constraint)
            {
                var q=Vector3.Zero;var left=default(Group);var right=default(Group);
                for(int j=0;j<constraint.Nodes.Length;j++)
                {
                    int id=constraint.Nodes[j];var point=points[id]+shifts[id];float w=constraint.Bary[j];q+=w*point;
                    left=new(left.Mass+w*mass[id][0],left.Moment+w*mass[id][0]*point);
                    right=new(right.Mass+w*mass[id][1],right.Moment+w*mass[id][1]*point);
                }
                float required=constraint.Margin;
                required+=ThighMotionEnvelope.Closure(constraint.Normal,pivots[0],constraint.Body[0],left-constraint.OldCloth[0]);
                required+=ThighMotionEnvelope.Closure(constraint.Normal,pivots[1],constraint.Body[1],right-constraint.OldCloth[1]);
                // Once the full independent-thigh envelope is clear, retaining
                // a larger source standoff adds no collision protection.
                float absolute=constraint.AbsoluteMargin+ThighMotionEnvelope.Closure(constraint.Normal,pivots[0],constraint.CurrentBody[0],left)+
                    ThighMotionEnvelope.Closure(constraint.Normal,pivots[1],constraint.CurrentBody[1],right);
                required=MathF.Min(required,absolute);
                return required-Vector3.Dot(q-constraint.Host,constraint.Normal);
            }
            for(int iteration=0;iteration<160;iteration++)
            {
                if(iteration<48)
                {
                    var smoothed=(Vector3[])shifts.Clone();
                    for(int i=0;i<shifts.Length;i++) if(!fixedNodes[i] && neighbours[i].Count>0)
                        smoothed[i]=.65f*shifts[i]+.35f*neighbours[i].Aggregate(Vector3.Zero,(v,j)=>v+shifts[j])/neighbours[i].Count;
                    shifts=smoothed;
                }
                float worst=0;
                foreach(var constraint in constraints)
                {
                    float deficit=Deficit(constraint);if(deficit<=.025f) continue;
                    float denom=0;
                    for(int j=0;j<constraint.Nodes.Length;j++) if(!fixedNodes[constraint.Nodes[j]]) denom+=constraint.Bary[j]*constraint.Bary[j];
                    if(denom<1e-8f) continue;
                    worst=Math.Max(worst,deficit);
                    for(int j=0;j<constraint.Nodes.Length;j++) if(!fixedNodes[constraint.Nodes[j]])
                        shifts[constraint.Nodes[j]]+=constraint.Normal*(deficit*constraint.Bary[j]/denom);
                }
                float worstArea=0;
                foreach(var face in faces)
                {
                    var a=points[face.A]+shifts[face.A];var b=points[face.B]+shifts[face.B];var c=points[face.C]+shifts[face.C];
                    float area=Vector3.Dot(Vector3.Cross(b-a,c-a),face.Normal);
                    float deficit=.15f*face.Area-area;if(deficit<=0) continue;
                    var gb=Vector3.Cross(c-a,face.Normal);var gc=Vector3.Cross(face.Normal,b-a);var ga=-gb-gc;
                    float denom=(fixedNodes[face.A]?0:ga.LengthSquared())+(fixedNodes[face.B]?0:gb.LengthSquared())+(fixedNodes[face.C]?0:gc.LengthSquared());
                    if(denom<1e-10f) continue;
                    float scale=deficit/denom;
                    if(!fixedNodes[face.A]) shifts[face.A]+=scale*ga;
                    if(!fixedNodes[face.B]) shifts[face.B]+=scale*gb;
                    if(!fixedNodes[face.C]) shifts[face.C]+=scale*gc;
                    worstArea=Math.Max(worstArea,deficit/face.Area);
                }
                if(iteration>=48 && worst<.05f && worstArea<.01f) break;
            }
            float unresolved=constraints.Max(Deficit);
            if(unresolved>.15f)
            {
                var worst=constraints.MaxBy(Deficit)!;
                var q=Enumerable.Range(0,worst.Nodes.Length).Aggregate(Vector3.Zero,(v,j)=>v+worst.Bary[j]*points[worst.Nodes[j]]);
                throw new InvalidDataException($"Skirt movement clearance did not converge ({unresolved:F2} mm, mesh {m}, point {q}, normal {worst.Normal}, free {worst.Margin/ThighMotionEnvelope.MarginMm:F2}, shift {worst.Nodes.Max(i=>shifts[i].Length()):F2} mm).");
            }
            var moves=used.Where(v=>shifts[nodes[v]].LengthSquared()>1e-8f).ToDictionary(v=>v,v=>shifts[nodes[v]]);
            if(moves.Count==0) continue;
            for(int t=0;t<draw.Indices.Length;t+=3)
            {
                int a=draw.Indices[t],b=draw.Indices[t+1],c=draw.Indices[t+2];
                var old=Vector3.Cross(draw.P[b]-draw.P[a],draw.P[c]-draw.P[a]);
                var p=draw.P[a]+moves.GetValueOrDefault(a);var q=draw.P[b]+moves.GetValueOrDefault(b);var r=draw.P[c]+moves.GetValueOrDefault(c);
                var after=Vector3.Cross(q-p,r-p);
                if(old.LengthSquared()>1e-8f && Vector3.Dot(old,after)<=0)
                    throw new InvalidDataException($"Skirt movement clearance would fold cloth mesh {m}, face {t/3} (area {old.Length():G5}, projected {Vector3.Dot(old,after)/old.Length():G5} mm2).");
            }
            output??=(byte[])fit.Clone();
            ModelPanelEdits.Write(output,layout,m,draw,moves);
            moved+=moves.Count;worstMove=Math.Max(worstMove,moves.Values.Max(v=>v.Length()));
        }
        var result=output ?? fit;
        if(transfers.Count>0) result=SurfaceSkinning.Apply(SurfaceSkinning.PromoteInfluences(result),transfers,targets.Values,owned);
        if(output!=null || transfers.Count>0) log.Information("[Aetherform] Skirt movement clearance: {Vertices} vertices, maximum adjustment {Distance:F2} mm; {Weights} body bindings, skirt animation retained ({Seconds:F2} s).",moved,worstMove,transfers.Values.Sum(v=>v.Count),timer.Elapsed.TotalSeconds);
        return result;
    }
}
