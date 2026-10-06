using UnityEngine;

namespace CMS2018Coop
{
    internal class RemotePlayer
    {
        private const float Timeout = 20f;
        private const float BodyHeight = 1.75f;

        private static readonly Color[] Palette =
        {
            new Color(0.20f, 0.55f, 0.95f),
            new Color(0.95f, 0.45f, 0.15f),
            new Color(0.30f, 0.80f, 0.35f),
            new Color(0.85f, 0.25f, 0.65f)
        };

        public readonly ulong Id;
        public string Name;
        public string Scene = "";
        public bool HasState;
        public bool GreetedBack;
        public bool Visible;
        public float LastSeen;

        private Vector3 targetFeet;
        private float targetYaw;
        private float targetPitch;
        private Vector3 shownFeet;
        private float shownYaw;
        private float shownPitch;
        private GameObject root;
        private Transform head;
        private MechanicAvatar avatar;
        private bool sprinting;
        private float speed;
        private float walkRef = 2f;
        private Vector3 lastShown;
        private readonly Color color;

        public RemotePlayer(ulong id, string name)
        {
            Id = id;
            Name = name;
            color = Palette[(int)(id % (ulong)Palette.Length)];
        }

        public Vector3 LabelPosition
        {
            get { return shownFeet + Vector3.up * (BodyHeight + 0.35f); }
        }

        public void Apply(PlayerState s)
        {
            bool sceneChanged = s.Scene != Scene;
            Scene = s.Scene;
            targetFeet = s.Feet;
            targetYaw = s.Yaw;
            targetPitch = Mathf.Clamp(s.Pitch, -80f, 80f);
            sprinting = s.Sprinting;
            LastSeen = Time.unscaledTime;
            if (!HasState || sceneChanged)
            {
                shownFeet = targetFeet;
                shownYaw = targetYaw;
                shownPitch = targetPitch;
                lastShown = shownFeet;
            }
            HasState = true;
        }

        public void Tick(string localScene)
        {
            Visible = HasState && Scene == localScene && Time.unscaledTime - LastSeen < Timeout;
            if (Visible && root == null) Build();
            if (root == null) return;

            if (root.activeSelf != Visible) root.SetActive(Visible);
            if (!Visible) return;

            float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
            float k = 1f - Mathf.Exp(-15f * dt);
            if ((targetFeet - shownFeet).sqrMagnitude > 25f)
            {
                shownFeet = targetFeet;
                lastShown = shownFeet;
            }
            shownFeet = Vector3.Lerp(shownFeet, targetFeet, k);
            shownYaw = Mathf.LerpAngle(shownYaw, targetYaw, k);
            shownPitch = Mathf.Lerp(shownPitch, targetPitch, k);

            Vector3 move = shownFeet - lastShown;
            move.y = 0f;
            lastShown = shownFeet;
            float instant = move.magnitude / dt;
            speed = Mathf.Lerp(speed, instant, 1f - Mathf.Exp(-8f * dt));
            if (!sprinting && speed > 0.8f) walkRef = Mathf.Lerp(walkRef, speed, 0.5f * dt);

            root.transform.position = shownFeet;
            root.transform.rotation = Quaternion.Euler(0f, shownYaw, 0f);
            if (avatar != null) avatar.Animate(dt, speed, walkRef, sprinting, shownPitch);
            else head.localRotation = Quaternion.Euler(shownPitch, 0f, 0f);
        }

        public void Destroy()
        {
            if (root != null) Object.Destroy(root);
            root = null;
            head = null;
            avatar = null;
            Visible = false;
        }

        private void Build()
        {
            root = new GameObject("CoopPlayer_" + Name);
            Object.DontDestroyOnLoad(root);

            MechanicModel model = MechanicModel.Get();
            if (model != null)
            {
                avatar = model.Build(root.transform, color);
                return;
            }

            GameObject body = Part(PrimitiveType.Capsule, root.transform, new Vector3(0f, 0.75f, 0f), new Vector3(0.55f, 0.75f, 0.4f), color);
            body.name = "Body";

            GameObject neck = new GameObject("Head");
            neck.transform.SetParent(root.transform, false);
            neck.transform.localPosition = new Vector3(0f, BodyHeight - 0.15f, 0f);
            head = neck.transform;

            Part(PrimitiveType.Sphere, head, Vector3.zero, new Vector3(0.32f, 0.34f, 0.32f), new Color(0.93f, 0.78f, 0.62f));
            Part(PrimitiveType.Cube, head, new Vector3(0f, 0.04f, 0.15f), new Vector3(0.26f, 0.08f, 0.06f), new Color(0.08f, 0.08f, 0.1f));
            Part(PrimitiveType.Cube, head, new Vector3(0f, 0.14f, 0f), new Vector3(0.34f, 0.06f, 0.34f), color * 0.7f);
            Part(PrimitiveType.Cube, head, new Vector3(0f, 0.12f, 0.2f), new Vector3(0.3f, 0.025f, 0.14f), color * 0.7f);
        }

        private static GameObject Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Color c)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Collider col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null) r.material.color = c;
            return go;
        }
    }
}
