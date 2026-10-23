#!/usr/bin/env python3
"""Convert the supplied rigged FBX into the tiny runtime format read by ImperialTrooper.dll.
Requires: python -m pip install assimp_py
No Unity Editor is required. Assimp's left-handed conversion matches Unity's coordinate system.
"""
import math, struct, sys
from pathlib import Path
import assimp_py

src = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('Stormtrooper.fbx')
out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path('Stormtrooper.imodel')
flags = (assimp_py.Process_Triangulate | assimp_py.Process_GenSmoothNormals |
         assimp_py.Process_LimitBoneWeights | assimp_py.Process_MakeLeftHanded |
         assimp_py.Process_FlipWindingOrder | assimp_py.Process_FlipUVs |
         assimp_py.Process_ValidateDataStructure)
scene = assimp_py.import_file(str(src), flags)
if scene.num_meshes < 1:
    raise RuntimeError('FBX contains no meshes')
mesh = scene.meshes[0]

nodes=[]
node_indices={}
node_object_indices={}
IDENTITY = tuple(tuple(1.0 if r == c else 0.0 for c in range(4)) for r in range(4))

def matmul(a, b):
    return tuple(tuple(sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)) for r in range(4))

def walk(node, parent, accumulated):
    local = tuple(tuple(float(v) for v in row) for row in node.transformation)
    combined = matmul(accumulated, local)
    # Assimp exposes FBX pre-rotation/rotation/translation helpers as extra nodes.
    # Preserve their transforms but collapse the helpers into the real bone nodes.
    is_helper = "_$AssimpFbx$_" in str(node.name)
    if is_helper:
        next_parent = parent
        next_accumulated = combined
    else:
        idx = len(nodes)
        node_indices[node.name] = idx
        node_object_indices[id(node)] = idx
        nodes.append({'name': node.name, 'parent': parent, 'matrix': combined})
        next_parent = idx
        next_accumulated = IDENTITY
    for child in node.children:
        walk(child, next_parent, next_accumulated)

walk(scene.root_node, -1, IDENTITY)

def quat_from_rot(m):
    tr=m[0][0]+m[1][1]+m[2][2]
    if tr>0:
        s=math.sqrt(max(1e-12,tr+1.0))*2
        w=.25*s; x=(m[2][1]-m[1][2])/s; y=(m[0][2]-m[2][0])/s; z=(m[1][0]-m[0][1])/s
    elif m[0][0]>m[1][1] and m[0][0]>m[2][2]:
        s=math.sqrt(max(1e-12,1+m[0][0]-m[1][1]-m[2][2]))*2
        w=(m[2][1]-m[1][2])/s; x=.25*s; y=(m[0][1]+m[1][0])/s; z=(m[0][2]+m[2][0])/s
    elif m[1][1]>m[2][2]:
        s=math.sqrt(max(1e-12,1+m[1][1]-m[0][0]-m[2][2]))*2
        w=(m[0][2]-m[2][0])/s; x=(m[0][1]+m[1][0])/s; y=.25*s; z=(m[1][2]+m[2][1])/s
    else:
        s=math.sqrt(max(1e-12,1+m[2][2]-m[0][0]-m[1][1]))*2
        w=(m[1][0]-m[0][1])/s; x=(m[0][2]+m[2][0])/s; y=(m[1][2]+m[2][1])/s; z=.25*s
    l=math.sqrt(x*x+y*y+z*z+w*w) or 1
    return x/l,y/l,z/l,w/l

def decompose(m):
    # Assimp's matrices are row-addressed and store translation in the fourth column.
    cols=[[m[0][c],m[1][c],m[2][c]] for c in range(3)]
    scale=[math.sqrt(sum(x*x for x in col)) for col in cols]
    scale=[s if s>1e-8 else 1.0 for s in scale]
    det=(m[0][0]*(m[1][1]*m[2][2]-m[1][2]*m[2][1]) - m[0][1]*(m[1][0]*m[2][2]-m[1][2]*m[2][0]) + m[0][2]*(m[1][0]*m[2][1]-m[1][1]*m[2][0]))
    if det<0: scale[0]*=-1
    rot=tuple(tuple(m[r][c]/scale[c] for c in range(3)) for r in range(3))
    pos=(m[0][3],m[1][3],m[2][3])
    q=quat_from_rot(rot)
    return pos,q,tuple(scale)

mesh_node = None
for i,n in enumerate(nodes):
    # Match the first scene node referencing the selected mesh.
    # We lookup the actual node below using a second deterministic traversal.
    pass
def find_mesh_node(node):
    if 0 in node.mesh_indices:
        idx = node_object_indices.get(id(node))
        if idx is None:
            raise RuntimeError('Mesh is attached directly to an FBX helper node; an explicit mesh transform node is required.')
        return idx
    for child in node.children:
        got = find_mesh_node(child)
        if got is not None: return got
    return None
mesh_node=find_mesh_node(scene.root_node)
if mesh_node is None: raise RuntimeError('could not find mesh node in node hierarchy')

bone_node_indices=[]
for b in mesh.bones:
    if b.name not in node_indices: raise RuntimeError('bone node not found: '+b.name)
    bone_node_indices.append(node_indices[b.name])

vertices=list(mesh.vertices); normals=list(mesh.normals); indices=list(mesh.indices)
if len(vertices)!=mesh.num_vertices*3 or len(normals)!=mesh.num_vertices*3:
    raise RuntimeError('unexpected vertex/normal buffer size')
uv=list(mesh.texcoords[0]) if mesh.texcoords else []
if len(uv)<mesh.num_vertices*2: uv=[0.0]*(mesh.num_vertices*2)

influences=[[] for _ in range(mesh.num_vertices)]
for bone_idx,b in enumerate(mesh.bones):
    ids=list(b.weight_vertex_ids); ws=list(b.weights)
    for vertex_id,weight in zip(ids,ws):
        if 0<=int(vertex_id)<mesh.num_vertices and float(weight)>0:
            influences[int(vertex_id)].append((bone_idx,float(weight)))

out.parent.mkdir(parents=True,exist_ok=True)
with out.open('wb') as f:
    f.write(b'ITMD')
    f.write(struct.pack('<i',1))
    f.write(struct.pack('<i',len(nodes)))
    for n in nodes:
        name=n['name'].encode('utf-8')
        pos,q,scale=decompose(n['matrix'])
        f.write(struct.pack('<i',len(name))); f.write(name)
        f.write(struct.pack('<i',n['parent']))
        f.write(struct.pack('<3f4f3f',*pos,*q,*scale))
    f.write(struct.pack('<ii',mesh_node,mesh.num_vertices))
    # Vertex block: position, normal, uv, four 16-bit bone indexes, four normalized weights.
    for i in range(mesh.num_vertices):
        ps=vertices[i*3:i*3+3]; ns=normals[i*3:i*3+3]; u=uv[i*2:i*2+2]
        inf=sorted(influences[i],key=lambda x:x[1],reverse=True)[:4]
        total=sum(w for _,w in inf)
        if total<=1e-8: inf=[(0,1.0)]; total=1.0
        ids=[idx for idx,_ in inf]+[0]*(4-len(inf))
        weights=[w/total for _,w in inf]+[0.0]*(4-len(inf))
        f.write(struct.pack('<8f4H4f',*ps,*ns,*u,*ids,*weights))
    f.write(struct.pack('<i',len(indices)))
    for i in range(0,len(indices),2048):
        chunk=indices[i:i+2048]
        f.write(struct.pack('<'+'I'*len(chunk),*chunk))
    f.write(struct.pack('<i',len(bone_node_indices)))
    f.write(struct.pack('<'+'i'*len(bone_node_indices),*bone_node_indices))
print(f'Wrote {out} ({out.stat().st_size:,} bytes); {len(nodes)} nodes, {mesh.num_vertices} verts, {len(indices)//3} triangles, {len(bone_node_indices)} skin bones')
