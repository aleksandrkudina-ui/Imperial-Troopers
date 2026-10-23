using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ImperialTrooperMod
{
    internal sealed class ImperialTrooperRuntimeModelData
    {
        private sealed class NodeRecord
        {
            internal string Name;
            internal int Parent;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Scale;
        }

        private NodeRecord[] nodes;
        private Vector3[] vertices;
        private Vector3[] normals;
        private Vector2[] uvs;
        private BoneWeight[] weights;
        private int[] triangles;
        private int[] boneNodeIndices;
        private int meshNodeIndex;

        internal int NodeCount => nodes == null ? 0 : nodes.Length;
        internal int VertexCount => vertices == null ? 0 : vertices.Length;
        internal int SkinBoneCount => boneNodeIndices == null ? 0 : boneNodeIndices.Length;

        internal static ImperialTrooperRuntimeModelData Load(string path)
        {
            if (!File.Exists(path)) return null;
            var result = new ImperialTrooperRuntimeModelData();
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
            {
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != (byte)'I' || magic[1] != (byte)'T' || magic[2] != (byte)'M' || magic[3] != (byte)'D')
                    throw new InvalidDataException("Invalid Stormtrooper model file header.");
                int version = reader.ReadInt32();
                if (version != 1) throw new InvalidDataException("Unsupported Stormtrooper model file version " + version + ".");

                int nodeCount = ReadCount(reader, "node", 4096);
                result.nodes = new NodeRecord[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    int stringLength = ReadCount(reader, "node-name byte", 4096);
                    byte[] nameBytes = reader.ReadBytes(stringLength);
                    if (nameBytes.Length != stringLength) throw new EndOfStreamException();
                    var n = new NodeRecord
                    {
                        Name = Encoding.UTF8.GetString(nameBytes),
                        Parent = reader.ReadInt32(),
                        Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                    };
                    float qLen = Mathf.Sqrt(n.Rotation.x * n.Rotation.x + n.Rotation.y * n.Rotation.y + n.Rotation.z * n.Rotation.z + n.Rotation.w * n.Rotation.w);
                    if (qLen > 0.0001f)
                        n.Rotation = new Quaternion(n.Rotation.x / qLen, n.Rotation.y / qLen, n.Rotation.z / qLen, n.Rotation.w / qLen);
                    else n.Rotation = Quaternion.identity;
                    if (n.Parent >= i || n.Parent < -1)
                        throw new InvalidDataException("Invalid parent index on model node " + i + ".");
                    result.nodes[i] = n;
                }

                result.meshNodeIndex = reader.ReadInt32();
                if (result.meshNodeIndex < 0 || result.meshNodeIndex >= nodeCount)
                    throw new InvalidDataException("Invalid mesh node index.");
                int vertexCount = ReadCount(reader, "vertex", 1000000);
                result.vertices = new Vector3[vertexCount];
                result.normals = new Vector3[vertexCount];
                result.uvs = new Vector2[vertexCount];
                result.weights = new BoneWeight[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    result.vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    result.normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    result.uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    ushort i0 = reader.ReadUInt16();
                    ushort i1 = reader.ReadUInt16();
                    ushort i2 = reader.ReadUInt16();
                    ushort i3 = reader.ReadUInt16();
                    float w0 = reader.ReadSingle();
                    float w1 = reader.ReadSingle();
                    float w2 = reader.ReadSingle();
                    float w3 = reader.ReadSingle();
                    result.weights[i] = new BoneWeight
                    {
                        boneIndex0 = i0, boneIndex1 = i1, boneIndex2 = i2, boneIndex3 = i3,
                        weight0 = w0, weight1 = w1, weight2 = w2, weight3 = w3
                    };
                }

                int indexCount = ReadCount(reader, "index", 5000000);
                result.triangles = new int[indexCount];
                for (int i = 0; i < indexCount; i++)
                {
                    result.triangles[i] = reader.ReadInt32();
                    if (result.triangles[i] < 0 || result.triangles[i] >= vertexCount)
                        throw new InvalidDataException("Triangle index is outside the vertex array.");
                }
                int boneCount = ReadCount(reader, "skin bone", 512);
                result.boneNodeIndices = new int[boneCount];
                for (int i = 0; i < boneCount; i++)
                {
                    int nodeIndex = reader.ReadInt32();
                    if (nodeIndex < 0 || nodeIndex >= nodeCount) throw new InvalidDataException("Invalid skin bone node index.");
                    result.boneNodeIndices[i] = nodeIndex;
                }
                if (stream.Position != stream.Length)
                    ImperialTrooperPlugin.LogSource?.LogWarning("Stormtrooper model file contains " + (stream.Length - stream.Position) + " trailing bytes.");
            }
            return result;
        }

        private static int ReadCount(BinaryReader reader, string label, int maximum)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > maximum) throw new InvalidDataException("Invalid " + label + " count " + value + ".");
            return value;
        }

        internal GameObject Instantiate(Transform parent, Material material)
        {
            if (nodes == null || nodes.Length == 0 || vertices == null || vertices.Length == 0 || boneNodeIndices == null || boneNodeIndices.Length == 0)
                throw new InvalidDataException("Stormtrooper model data is incomplete.");

            GameObject root = new GameObject("ImperialTrooperImportedVisuals");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            // The source mesh is about 3.70 Unity units tall; half scale gives roughly 1.85 m.
            root.transform.localScale = Vector3.one * 0.5f;

            Transform[] transforms = new Transform[nodes.Length];
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    NodeRecord record = nodes[i];
                    GameObject nodeObject = new GameObject(string.IsNullOrEmpty(record.Name) ? "ImportedNode_" + i : record.Name);
                    Transform nodeTransform = nodeObject.transform;
                    nodeTransform.SetParent(record.Parent < 0 ? root.transform : transforms[record.Parent], false);
                    nodeTransform.localPosition = record.Position;
                    nodeTransform.localRotation = record.Rotation;
                    nodeTransform.localScale = record.Scale;
                    transforms[i] = nodeTransform;
                }

                Mesh mesh = new Mesh { name = "ImperialTrooperRiggedMesh" };
                if (vertices.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.uv = uvs;
                mesh.boneWeights = weights;
                mesh.triangles = triangles;
                mesh.RecalculateBounds();

                Transform meshTransform = transforms[meshNodeIndex];
                SkinnedMeshRenderer renderer = meshTransform.gameObject.AddComponent<SkinnedMeshRenderer>();
                Transform[] bones = new Transform[boneNodeIndices.Length];
                for (int i = 0; i < bones.Length; i++) bones[i] = transforms[boneNodeIndices[i]];
                Transform hips = null;
                for (int i = 0; i < transforms.Length; i++)
                {
                    string name = nodes[i].Name ?? string.Empty;
                    if (name.EndsWith(":Hips", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Hips", StringComparison.OrdinalIgnoreCase))
                    { hips = transforms[i]; break; }
                }
                if (hips == null) hips = bones[0];

                Matrix4x4[] bindposes = new Matrix4x4[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    bindposes[i] = bones[i].worldToLocalMatrix * meshTransform.localToWorldMatrix;
                mesh.bindposes = bindposes;

                renderer.sharedMesh = mesh;
                renderer.bones = bones;
                renderer.rootBone = hips;
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.updateWhenOffscreen = false;
                renderer.localBounds = mesh.bounds;
                return root;
            }
            catch
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                throw;
            }
        }
    }
}
