using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CMS2018Coop
{
    internal class MechanicModel
    {
        private static MechanicModel cached;
        private static bool tried;

        public string[] BoneNames;
        public int[] Parents;
        public Vector3[] RestPos;
        public Quaternion[] RestRot;
        public int HipsIndex;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public BoneWeight[] Weights;
        public Color[] SubColors;
        public int[][] SubIndices;
        public byte[] TexturePng;
        private Texture2D texture;
        public readonly Dictionary<string, Clip> Clips = new Dictionary<string, Clip>();

        internal class Clip
        {
            public float Fps;
            public int Frames;
            public Vector3[] Hips;
            public Quaternion[][] Rots;

            public float Length
            {
                get { return Frames / Fps; }
            }

            public void Sample(float time, Quaternion[] outRots, out Vector3 hips)
            {
                float f = time * Fps;
                f -= Mathf.Floor(f / Frames) * Frames;
                int a = (int)f % Frames;
                int b = (a + 1) % Frames;
                float t = f - Mathf.Floor(f);
                hips = Vector3.Lerp(Hips[a], Hips[b], t);
                Quaternion[] ra = Rots[a];
                Quaternion[] rb = Rots[b];
                for (int i = 0; i < outRots.Length; i++) outRots[i] = Quaternion.Slerp(ra[i], rb[i], t);
            }
        }

        public static MechanicModel Get()
        {
            if (tried) return cached;
            tried = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(MechanicModel).Assembly.Location), "mechanic.cmsm");
            try
            {
                if (File.Exists(path)) cached = Load(path);
            }
            catch (Exception e)
            {
                if (Plugin.Instance != null && Plugin.Instance.Session != null) Plugin.Instance.Session.Log.LogError("Could not load mechanic model: " + e);
            }
            return cached;
        }

        private static Vector3 V3(BinaryReader r)
        {
            return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        private static Quaternion Q(BinaryReader r)
        {
            return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        private static MechanicModel Load(string path)
        {
            MechanicModel m = new MechanicModel();
            using (BinaryReader r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (new string(r.ReadChars(4)) != "CMSM") throw new Exception("not a mechanic model");
                int version = r.ReadInt32();
                int bones = r.ReadInt32();
                m.BoneNames = new string[bones];
                m.Parents = new int[bones];
                m.RestPos = new Vector3[bones];
                m.RestRot = new Quaternion[bones];
                for (int i = 0; i < bones; i++)
                {
                    m.BoneNames[i] = r.ReadString();
                    m.Parents[i] = r.ReadInt32();
                    m.RestPos[i] = V3(r);
                    m.RestRot[i] = Q(r);
                }
                m.HipsIndex = r.ReadInt32();

                int verts = r.ReadInt32();
                m.Vertices = new Vector3[verts];
                m.Normals = new Vector3[verts];
                m.Uvs = new Vector2[verts];
                m.Weights = new BoneWeight[verts];
                for (int i = 0; i < verts; i++) m.Vertices[i] = V3(r);
                for (int i = 0; i < verts; i++) m.Normals[i] = V3(r);
                for (int i = 0; i < verts; i++) m.Uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < verts; i++)
                {
                    BoneWeight w = new BoneWeight();
                    w.boneIndex0 = r.ReadInt32();
                    w.boneIndex1 = r.ReadInt32();
                    w.boneIndex2 = r.ReadInt32();
                    w.boneIndex3 = r.ReadInt32();
                    w.weight0 = r.ReadSingle();
                    w.weight1 = r.ReadSingle();
                    w.weight2 = r.ReadSingle();
                    w.weight3 = r.ReadSingle();
                    m.Weights[i] = w;
                }

                int subs = r.ReadInt32();
                m.SubColors = new Color[subs];
                m.SubIndices = new int[subs][];
                for (int s = 0; s < subs; s++)
                {
                    m.SubColors[s] = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    int count = r.ReadInt32();
                    int[] idx = new int[count];
                    for (int i = 0; i < count; i++) idx[i] = r.ReadInt32();
                    m.SubIndices[s] = idx;
                }

                int clips = r.ReadInt32();
                for (int c = 0; c < clips; c++)
                {
                    Clip clip = new Clip();
                    string name = r.ReadString();
                    clip.Fps = r.ReadSingle();
                    clip.Frames = r.ReadInt32();
                    clip.Hips = new Vector3[clip.Frames];
                    clip.Rots = new Quaternion[clip.Frames][];
                    for (int f = 0; f < clip.Frames; f++)
                    {
                        clip.Hips[f] = V3(r);
                        Quaternion[] rots = new Quaternion[bones];
                        for (int i = 0; i < bones; i++) rots[i] = Q(r);
                        clip.Rots[f] = rots;
                    }
                    m.Clips[name] = clip;
                }
                if (version >= 2)
                {
                    int len = r.ReadInt32();
                    if (len > 0) m.TexturePng = r.ReadBytes(len);
                }
            }
            return m;
        }

        public MechanicAvatar Build(Transform parent, Color tint)
        {
            GameObject root = new GameObject("Mechanic");
            root.transform.SetParent(parent, false);

            Transform[] bones = new Transform[BoneNames.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = new GameObject(BoneNames[i]).transform;
            }
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(Parents[i] >= 0 ? bones[Parents[i]] : root.transform, false);
                bones[i].localPosition = RestPos[i];
                bones[i].localRotation = RestRot[i];
            }

            Vector3 savedPos = root.transform.position;
            Quaternion savedRot = root.transform.rotation;
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            Matrix4x4[] bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) bindposes[i] = bones[i].worldToLocalMatrix * root.transform.localToWorldMatrix;
            root.transform.position = savedPos;
            root.transform.rotation = savedRot;

            Mesh mesh = new Mesh();
            mesh.name = "Mechanic";
            mesh.vertices = Vertices;
            mesh.normals = Normals;
            mesh.uv = Uvs;
            mesh.boneWeights = Weights;
            mesh.bindposes = bindposes;
            mesh.subMeshCount = SubIndices.Length;
            for (int s = 0; s < SubIndices.Length; s++) mesh.SetTriangles(SubIndices[s], s);
            mesh.RecalculateBounds();

            GameObject skin = new GameObject("Body");
            skin.transform.SetParent(root.transform, false);
            SkinnedMeshRenderer smr = skin.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[HipsIndex];
            smr.updateWhenOffscreen = true;

            Material baseMat = BaseMaterial();
            Texture2D tex = Texture();
            Material[] mats = new Material[SubColors.Length];
            for (int s = 0; s < mats.Length; s++)
            {
                Material mat = new Material(baseMat);
                Color c = SubColors[s];
                if (tex != null) mat.mainTexture = tex;
                else if (s == 1) c = Color.Lerp(c, tint, 0.35f);
                mat.color = c;
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                mats[s] = mat;
            }
            smr.sharedMaterials = mats;

            return new MechanicAvatar(this, root.transform, bones);
        }

        private Texture2D Texture()
        {
            if (texture != null || TexturePng == null) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!texture.LoadImage(TexturePng))
            {
                texture = null;
                return null;
            }
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 4;
            texture.wrapMode = TextureWrapMode.Repeat;
            return texture;
        }

        private static Material baseMaterial;

        private static Material BaseMaterial()
        {
            if (baseMaterial != null) return baseMaterial;
            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseMaterial = new Material(probe.GetComponent<Renderer>().sharedMaterial);
            UnityEngine.Object.Destroy(probe);
            return baseMaterial;
        }
    }

    internal class MechanicAvatar
    {
        private readonly MechanicModel model;
        private readonly Transform root;
        private readonly Transform[] bones;
        private readonly Transform neck;
        private readonly Transform head;
        private readonly MechanicModel.Clip idle;
        private readonly MechanicModel.Clip walk;
        private readonly MechanicModel.Clip run;
        private readonly Quaternion[] poseA;
        private readonly Quaternion[] poseB;
        private readonly Quaternion[] poseC;
        private float idleTime;
        private float phase;
        private float walkWeight;
        private float runWeight;

        public MechanicAvatar(MechanicModel model, Transform root, Transform[] bones)
        {
            this.model = model;
            this.root = root;
            this.bones = bones;
            for (int i = 0; i < bones.Length; i++)
            {
                if (model.BoneNames[i] == "Neck") neck = bones[i];
                if (model.BoneNames[i] == "Head") head = bones[i];
            }
            model.Clips.TryGetValue("Idle", out idle);
            model.Clips.TryGetValue("Walk", out walk);
            model.Clips.TryGetValue("Run", out run);
            poseA = new Quaternion[bones.Length];
            poseB = new Quaternion[bones.Length];
            poseC = new Quaternion[bones.Length];
        }

        public void Animate(float dt, float speed, float walkRef, bool sprinting, float pitch)
        {
            float targetWalk = Mathf.Clamp01((speed - 0.15f) / 0.5f);
            float targetRun = sprinting && speed > 0.5f ? 1f : 0f;
            float k = 1f - Mathf.Exp(-10f * dt);
            walkWeight = Mathf.Lerp(walkWeight, targetWalk, k);
            runWeight = Mathf.Lerp(runWeight, targetRun, k);

            idleTime += dt;
            float rate = walkRef > 0.1f ? speed / walkRef : 1f;
            rate = Mathf.Clamp(rate, 0.5f, 2.5f);
            float walkCycle = walk != null ? walk.Length : 1f;
            float runCycle = run != null ? run.Length : 0.7f;
            float cycle = Mathf.Lerp(walkCycle, runCycle * 1.25f, runWeight);
            phase += dt * rate / cycle;
            phase -= Mathf.Floor(phase);

            Vector3 hipsA = model.RestPos[model.HipsIndex];
            Vector3 hipsB = hipsA;
            Vector3 hipsC = hipsA;
            if (idle != null) idle.Sample(idleTime, poseA, out hipsA);
            else Array.Copy(model.RestRot, poseA, poseA.Length);

            if (walk != null && walkWeight > 0.001f)
            {
                walk.Sample(phase * walk.Length, poseB, out hipsB);
                if (run != null && runWeight > 0.001f)
                {
                    run.Sample(phase * run.Length, poseC, out hipsC);
                    for (int i = 0; i < poseB.Length; i++) poseB[i] = Quaternion.Slerp(poseB[i], poseC[i], runWeight);
                    hipsB = Vector3.Lerp(hipsB, hipsC, runWeight);
                }
                for (int i = 0; i < poseA.Length; i++) poseA[i] = Quaternion.Slerp(poseA[i], poseB[i], walkWeight);
                hipsA = Vector3.Lerp(hipsA, hipsB, walkWeight);
            }

            for (int i = 0; i < bones.Length; i++) bones[i].localRotation = poseA[i];
            bones[model.HipsIndex].localPosition = hipsA;

            if (Mathf.Abs(pitch) > 0.5f)
            {
                Vector3 axis = root.right;
                if (neck != null) neck.rotation = Quaternion.AngleAxis(pitch * 0.4f, axis) * neck.rotation;
                if (head != null) head.rotation = Quaternion.AngleAxis(pitch * 0.6f, axis) * head.rotation;
            }
        }
    }
}
