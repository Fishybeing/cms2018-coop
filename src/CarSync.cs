using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CMS2018Coop
{
    internal class CarSync
    {
        public const byte MsgManifest = 10;
        public const byte MsgCarRequest = 11;
        public const byte MsgCarChunk = 12;
        public const byte MsgPart = 13;
        public const byte MsgBody = 14;
        public const byte MsgLift = 15;
        public const byte MsgCarProps = 16;

        public static bool Applying;

        private const int ChunkSize = 1000;
        private const float ScanInterval = 0.25f;
        private const float ManifestInterval = 1f;
        private const float RequestRetry = 5f;

        private static readonly FieldInfo UnmountedField = typeof(PartScript).GetField("unmounted", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo TakeOnFromSave = typeof(CarLoader).GetMethod("TakeOnCarPartFromSave", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly Session session;
        private readonly Dictionary<string, int> hostSerials = new Dictionary<string, int>();
        private readonly Dictionary<string, string> hostSigs = new Dictionary<string, string>();
        private readonly Dictionary<string, int> applied = new Dictionary<string, int>();
        private readonly Dictionary<string, float> requestedAt = new Dictionary<string, float>();
        private readonly Dictionary<string, Transfer> transfers = new Dictionary<string, Transfer>();
        private readonly HashSet<string> loading = new HashSet<string>();
        private readonly Dictionary<string, CarState> known = new Dictionary<string, CarState>();
        private readonly Dictionary<string, CarLoader> loaders = new Dictionary<string, CarLoader>();
        private readonly Dictionary<string, CarLifter> lifters = new Dictionary<string, CarLifter>();
        private readonly Dictionary<string, int> pendingLifts = new Dictionary<string, int>();
        private readonly Dictionary<string, float> liftMismatchSince = new Dictionary<string, float>();
        private readonly Dictionary<string, float> placeMismatchSince = new Dictionary<string, float>();
        private int nextSerial = 1;
        private string lastScene = "";
        private float manifestTimer;
        private float scanTimer;
        private float loaderRefresh;

        public string Summary = "";

        private class Transfer
        {
            public int Serial;
            public byte[] Data;
            public bool[] Got;
            public int Remaining;
        }

        private class CarState
        {
            public int RootId;
            public readonly Dictionary<string, PartScript> Index = new Dictionary<string, PartScript>();
            public readonly Dictionary<string, PartState> Parts = new Dictionary<string, PartState>();
            public readonly Dictionary<string, BodyState> Body = new Dictionary<string, BodyState>();
            public CarProps Props;
        }

        private struct PartState
        {
            public bool Unmounted;
            public int Condition;
            public int Quality;
            public string Tuned;
            public bool Painted;
            public int PaintType;
            public uint Color;

            public bool Same(PartState o)
            {
                return Unmounted == o.Unmounted && Condition == o.Condition && Quality == o.Quality && Tuned == o.Tuned
                    && Painted == o.Painted && PaintType == o.PaintType && Color == o.Color;
            }
        }

        private struct BodyState
        {
            public bool Unmounted;
            public bool Switched;
            public int Condition;
            public uint Color;
            public int PaintType;
            public string Livery;
            public int LiveryStrength;

            public bool Same(BodyState o)
            {
                return Unmounted == o.Unmounted && Switched == o.Switched && Condition == o.Condition
                    && Color == o.Color && PaintType == o.PaintType && Livery == o.Livery && LiveryStrength == o.LiveryStrength;
            }
        }

        private struct CarProps
        {
            public int Place;
            public int Oil;
            public int OilCondition;

            public bool Same(CarProps o)
            {
                return Place == o.Place && Oil == o.Oil && OilCondition == o.OilCondition;
            }
        }

        private static uint Pack(Color c)
        {
            uint r = (uint)Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f);
            uint g = (uint)Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f);
            uint b = (uint)Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f);
            uint a = (uint)Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255f);
            return r | (g << 8) | (b << 16) | (a << 24);
        }

        private static Color Unpack(uint v)
        {
            return new Color((v & 0xff) / 255f, ((v >> 8) & 0xff) / 255f, ((v >> 16) & 0xff) / 255f, ((v >> 24) & 0xff) / 255f);
        }

        public CarSync(Session session)
        {
            this.session = session;
        }

        public static bool InGarage()
        {
            if (!GameManager.IsGameReady) return false;
            string scene = LocalPlayer.SceneName();
            return scene != null && scene.StartsWith("garage", StringComparison.OrdinalIgnoreCase);
        }

        public void Reset()
        {
            applied.Clear();
            requestedAt.Clear();
            transfers.Clear();
            known.Clear();
            loaders.Clear();
            lifters.Clear();
            pendingLifts.Clear();
            liftMismatchSince.Clear();
            placeMismatchSince.Clear();
            Summary = "";
        }

        public void Update()
        {
            if (!session.Connected || session.Remotes.Count == 0)
            {
                if (lastScene != "") Reset();
                lastScene = "";
                return;
            }

            string scene = LocalPlayer.SceneName();
            if (scene != lastScene)
            {
                Reset();
                lastScene = scene;
            }
            if (!InGarage()) return;

            RefreshLoaders();
            UpdatePendingLifts();

            if (session.IsHost)
            {
                manifestTimer -= Time.unscaledDeltaTime;
                if (manifestTimer <= 0f)
                {
                    manifestTimer = ManifestInterval;
                    SendManifest();
                }
            }

            scanTimer -= Time.unscaledDeltaTime;
            if (scanTimer <= 0f)
            {
                scanTimer = ScanInterval;
                ScanAll();
            }
        }

        private void RefreshLoaders()
        {
            if (Time.unscaledTime < loaderRefresh && loaders.Count > 0) return;
            loaderRefresh = Time.unscaledTime + 2f;
            loaders.Clear();
            foreach (CarLoader l in UnityEngine.Object.FindObjectsOfType<CarLoader>())
            {
                if (!loaders.ContainsKey(l.name)) loaders[l.name] = l;
            }
            lifters.Clear();
            foreach (CarLifter lift in UnityEngine.Object.FindObjectsOfType<CarLifter>())
            {
                string key = PathOf(lift.transform);
                if (!lifters.ContainsKey(key)) lifters[key] = lift;
            }
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private string KeyOf(CarLifter lift)
        {
            foreach (KeyValuePair<string, CarLifter> kv in lifters)
            {
                if (kv.Value == lift) return kv.Key;
            }
            loaderRefresh = 0f;
            RefreshLoaders();
            return PathOf(lift.transform);
        }

        public void OnLocalLift(CarLifter lift, int actionType)
        {
            if (!InGarage() || session.Remotes.Count == 0) return;
            string key = KeyOf(lift);
            BinaryWriter w = session.Begin(MsgLift);
            w.Write(key);
            w.Write(actionType);
            Route(0);
        }

        private void OnLift(ulong from, BinaryReader r)
        {
            string key = r.ReadString();
            int actionType = r.ReadInt32();
            if (!InGarage()) return;
            CarLifter lift;
            if (!lifters.TryGetValue(key, out lift) || lift == null) return;
            if (lift.isMoving) pendingLifts[key] = actionType;
            else RunLift(lift, actionType);
            session.Log.LogInfo(session.NameOf(from) + " used lift " + lift.name + " (action " + actionType + ")");
            if (session.IsHost)
            {
                BinaryWriter w = session.Begin(MsgLift);
                w.Write(key);
                w.Write(actionType);
                session.SendToAllExcept(from, true);
            }
        }

        private static void RunLift(CarLifter lift, int actionType)
        {
            Applying = true;
            try
            {
                lift.Action(actionType);
            }
            finally
            {
                Applying = false;
            }
        }

        private void UpdatePendingLifts()
        {
            if (pendingLifts.Count == 0) return;
            foreach (string key in new List<string>(pendingLifts.Keys))
            {
                CarLifter lift;
                if (!lifters.TryGetValue(key, out lift) || lift == null)
                {
                    pendingLifts.Remove(key);
                    continue;
                }
                if (lift.isMoving) continue;
                RunLift(lift, pendingLifts[key]);
                pendingLifts.Remove(key);
            }
        }

        private void ReconcileLift(string key, int hostPos, bool hostMoving)
        {
            CarLifter lift;
            if (!lifters.TryGetValue(key, out lift) || lift == null) return;
            if (hostMoving || lift.isMoving || pendingLifts.ContainsKey(key) || lift.GetConnectedCarLoader() == null || lift.pos == hostPos)
            {
                liftMismatchSince.Remove(key);
                return;
            }
            float since;
            if (!liftMismatchSince.TryGetValue(key, out since))
            {
                liftMismatchSince[key] = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - since < 2f) return;
            liftMismatchSince.Remove(key);
            Applying = true;
            try
            {
                lift.InstantSet(hostPos);
            }
            finally
            {
                Applying = false;
            }
            session.Log.LogInfo("Moved lift " + lift.name + " to match host (position " + hostPos + ")");
        }

        private CarLoader Loader(string name)
        {
            CarLoader l;
            if (loaders.TryGetValue(name, out l) && l != null) return l;
            loaderRefresh = 0f;
            RefreshLoaders();
            return loaders.TryGetValue(name, out l) ? l : null;
        }

        private static string Signature(CarLoader l)
        {
            GameObject root = l.GetRoot();
            if (root == null || string.IsNullOrEmpty(l.carToLoad)) return "";
            return l.carToLoad + "|" + root.GetInstanceID();
        }

        private void SendManifest()
        {
            List<CarLoader> entries = new List<CarLoader>();
            foreach (CarLoader l in loaders.Values)
            {
                if (l == null || !l.IsCarLoaded()) continue;
                string sig = Signature(l);
                string old;
                if (!hostSigs.TryGetValue(l.name, out old) || old != sig)
                {
                    hostSigs[l.name] = sig;
                    hostSerials[l.name] = nextSerial++;
                    known.Remove(l.name);
                }
                entries.Add(l);
            }

            BinaryWriter w = session.Begin(MsgManifest);
            w.Write(LocalPlayer.SceneName());
            w.Write(entries.Count);
            foreach (CarLoader l in entries)
            {
                w.Write(l.name);
                w.Write(hostSerials[l.name]);
                w.Write(l.GetPlaceNo());
            }
            w.Write(lifters.Count);
            foreach (KeyValuePair<string, CarLifter> kv in lifters)
            {
                w.Write(kv.Key);
                w.Write(kv.Value != null ? kv.Value.pos : -1);
                w.Write(kv.Value != null && kv.Value.isMoving);
            }
            session.SendToAll(true);

            int synced = 0;
            foreach (CarLoader l in loaders.Values)
            {
                if (l != null && l.GetRoot() != null) synced++;
            }
            Summary = "Sharing " + synced + " car" + (synced == 1 ? "" : "s");
        }

        public void Handle(ulong from, byte type, BinaryReader r)
        {
            switch (type)
            {
                case MsgManifest:
                    OnManifest(r);
                    break;
                case MsgCarRequest:
                    OnCarRequest(from, r);
                    break;
                case MsgCarChunk:
                    OnCarChunk(r);
                    break;
                case MsgPart:
                    OnPart(from, r);
                    break;
                case MsgBody:
                    OnBody(from, r);
                    break;
                case MsgLift:
                    OnLift(from, r);
                    break;
                case MsgCarProps:
                    OnCarProps(from, r);
                    break;
            }
        }

        private void OnManifest(BinaryReader r)
        {
            if (session.IsHost) return;
            string scene = r.ReadString();
            int count = r.ReadInt32();
            Dictionary<string, int> entries = new Dictionary<string, int>();
            Dictionary<string, int> places = new Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                entries[name] = r.ReadInt32();
                places[name] = r.ReadInt32();
            }
            int liftCount = r.ReadInt32();
            List<KeyValuePair<string, int>> liftPos = new List<KeyValuePair<string, int>>();
            List<bool> liftMoving = new List<bool>();
            for (int i = 0; i < liftCount; i++)
            {
                liftPos.Add(new KeyValuePair<string, int>(r.ReadString(), r.ReadInt32()));
                liftMoving.Add(r.ReadBoolean());
            }
            if (!InGarage() || scene != LocalPlayer.SceneName() || TestMode.HoldSync) return;

            foreach (CarLoader l in loaders.Values)
            {
                if (l != null && !l.IsCarLoaded()) return;
            }

            if (loading.Count == 0)
            {
                for (int i = 0; i < liftPos.Count; i++)
                {
                    if (liftPos[i].Value >= 0) ReconcileLift(liftPos[i].Key, liftPos[i].Value, liftMoving[i]);
                }
            }

            int done = 0;
            foreach (KeyValuePair<string, int> e in entries)
            {
                int have;
                if (applied.TryGetValue(e.Key, out have) && have == e.Value)
                {
                    done++;
                    if (!loading.Contains(e.Key)) ReconcilePlace(e.Key, places[e.Key]);
                    continue;
                }
                if (loading.Contains(e.Key) || Loader(e.Key) == null) continue;
                float at;
                if (requestedAt.TryGetValue(e.Key, out at) && Time.unscaledTime - at < RequestRetry) continue;
                requestedAt[e.Key] = Time.unscaledTime;
                BinaryWriter w = session.Begin(MsgCarRequest);
                w.Write(e.Key);
                session.SendToHost(true);
            }
            Summary = done == entries.Count ? "Garage synced (" + done + " spots)" : "Syncing garage " + done + "/" + entries.Count;
        }

        private void OnCarRequest(ulong from, BinaryReader r)
        {
            if (!session.IsHost || !InGarage()) return;
            string name = r.ReadString();
            CarLoader l = Loader(name);
            int serial;
            if (l == null || !l.IsCarLoaded() || !hostSerials.TryGetValue(name, out serial)) return;

            byte[] data = new byte[0];
            if (l.GetRoot() != null && !string.IsNullOrEmpty(l.carToLoad))
            {
                string file = "coop_snap_" + name;
                string path = ProfilePath(file);
                l.SaveCarToFile(file, false);
                data = File.ReadAllBytes(path);
                File.Delete(path);
            }

            int chunks = Math.Max(1, (data.Length + ChunkSize - 1) / ChunkSize);
            for (int i = 0; i < chunks; i++)
            {
                int offset = i * ChunkSize;
                int len = Math.Min(ChunkSize, data.Length - offset);
                BinaryWriter w = session.Begin(MsgCarChunk);
                w.Write(name);
                w.Write(serial);
                w.Write(data.Length);
                w.Write(i);
                w.Write(Math.Max(0, len));
                if (len > 0) w.Write(data, offset, len);
                session.SendTo(from, true);
            }
            session.Log.LogInfo("Sent " + name + " (" + l.carToLoad + ", " + data.Length + " bytes) to " + session.NameOf(from));
        }

        private void OnCarChunk(BinaryReader r)
        {
            if (session.IsHost) return;
            string name = r.ReadString();
            int serial = r.ReadInt32();
            int total = r.ReadInt32();
            int index = r.ReadInt32();
            int len = r.ReadInt32();
            byte[] bytes = r.ReadBytes(len);

            Transfer t;
            if (!transfers.TryGetValue(name, out t) || t.Serial != serial || t.Data.Length != total)
            {
                int chunks = Math.Max(1, (total + ChunkSize - 1) / ChunkSize);
                t = new Transfer { Serial = serial, Data = new byte[total], Got = new bool[chunks], Remaining = chunks };
                transfers[name] = t;
            }
            if (index < 0 || index >= t.Got.Length || t.Got[index]) return;
            Buffer.BlockCopy(bytes, 0, t.Data, index * ChunkSize, len);
            t.Got[index] = true;
            t.Remaining--;
            if (t.Remaining > 0) return;

            transfers.Remove(name);
            if (!InGarage()) return;
            Plugin.Instance.StartCoroutine(ApplySnapshot(name, serial, t.Data));
        }

        private IEnumerator ApplySnapshot(string name, int serial, byte[] data)
        {
            CarLoader l = Loader(name);
            if (l == null || loading.Contains(name)) yield break;
            loading.Add(name);
            known.Remove(name);
            while (l != null && !l.IsCarLoaded()) yield return null;
            if (l == null)
            {
                loading.Remove(name);
                yield break;
            }

            if (data.Length == 0)
            {
                if (l.GetRoot() != null) l.DeleteCar(false);
                session.Log.LogInfo("Cleared " + name + " to match host");
            }
            else
            {
                string file = "coop_recv_" + name;
                string path = ProfilePath(file);
                File.WriteAllBytes(path, data);
                yield return l.StartCoroutine(l.LoadCarFromFile(file));
                try { File.Delete(path); } catch (Exception) { }
                session.Log.LogInfo("Loaded " + name + " (" + l.carToLoad + ") from host");
            }
            applied[name] = serial;
            requestedAt.Remove(name);
            known.Remove(name);
            loading.Remove(name);
        }

        private static string ProfilePath(string file)
        {
            return Application.persistentDataPath + "/profile" + ProfileManager.Get().GetSelectedProfile() + "/" + file + ".dat";
        }

        private int SerialFor(string name)
        {
            int s;
            if (session.IsHost) return hostSerials.TryGetValue(name, out s) ? s : -1;
            return applied.TryGetValue(name, out s) ? s : -1;
        }

        private bool Syncable(string name)
        {
            if (loading.Contains(name)) return false;
            if (session.IsHost) return hostSerials.ContainsKey(name);
            return applied.ContainsKey(name);
        }

        private void ScanAll()
        {
            foreach (CarLoader l in loaders.Values)
            {
                if (l == null || !l.IsCarLoaded() || !Syncable(l.name)) continue;
                Scan(l);
            }
        }

        private CarState StateFor(CarLoader l)
        {
            GameObject root = l.GetRoot();
            if (root == null)
            {
                known.Remove(l.name);
                return null;
            }
            CarState cs;
            if (known.TryGetValue(l.name, out cs) && cs.RootId == root.GetInstanceID()) return cs;

            cs = new CarState { RootId = root.GetInstanceID() };
            foreach (PartScript ps in root.GetComponentsInChildren<PartScript>(true))
            {
                string path = ps.GetGameObjectPathWithoutRoot();
                if (cs.Index.ContainsKey(path)) continue;
                cs.Index[path] = ps;
                cs.Parts[path] = Read(ps);
            }
            foreach (carPart cp in l.GetCarParts())
            {
                if (cp == null || cp.handle == null || cs.Body.ContainsKey(cp.name)) continue;
                cs.Body[cp.name] = Read(cp);
            }
            cs.Props = ReadProps(l);
            known[l.name] = cs;
            return cs;
        }

        private void Scan(CarLoader l)
        {
            bool fresh = !known.ContainsKey(l.name);
            CarState cs = StateFor(l);
            if (cs == null || fresh) return;

            foreach (KeyValuePair<string, PartScript> kv in cs.Index)
            {
                if (kv.Value == null) continue;
                PartState now = Read(kv.Value);
                if (now.Same(cs.Parts[kv.Key])) continue;
                cs.Parts[kv.Key] = now;
                SendPart(l.name, kv.Key, now, 0);
            }
            foreach (carPart cp in l.GetCarParts())
            {
                if (cp == null || cp.handle == null || cp.inprogress) continue;
                BodyState old;
                if (!cs.Body.TryGetValue(cp.name, out old)) continue;
                BodyState now = Read(cp);
                if (now.Same(old)) continue;
                cs.Body[cp.name] = now;
                SendBody(l.name, cp.name, now, 0);
            }
            CarProps props = ReadProps(l);
            if (!props.Same(cs.Props))
            {
                cs.Props = props;
                SendProps(l.name, props, 0);
            }
        }

        private static PartState Read(PartScript ps)
        {
            return new PartState
            {
                Unmounted = ps.IsUnmounted(),
                Condition = Mathf.RoundToInt(ps.GetCondition() * 1000f),
                Quality = ps.GetQuality(),
                Tuned = ps.GetTunedID() ?? "",
                Painted = ps.IsPainted,
                PaintType = (int)ps.CurrentPaintType,
                Color = ps.IsPainted ? Pack(ps.GetColor()) : 0u
            };
        }

        private static BodyState Read(carPart cp)
        {
            return new BodyState
            {
                Unmounted = cp.unmounted,
                Switched = cp.switched,
                Condition = Mathf.RoundToInt(cp.condition * 1000f),
                Color = Pack(cp.color),
                PaintType = cp.paintType,
                Livery = cp.livery ?? "",
                LiveryStrength = Mathf.RoundToInt(cp.liveryStrength * 1000f)
            };
        }

        private static CarProps ReadProps(CarLoader l)
        {
            return new CarProps
            {
                Place = l.GetPlaceNo(),
                Oil = Mathf.RoundToInt(l.GetOilLevel() * 1000f),
                OilCondition = Mathf.RoundToInt(l.GetOilCondition() * 1000f)
            };
        }

        private void SendProps(string car, CarProps p, ulong except)
        {
            BinaryWriter w = session.Begin(MsgCarProps);
            w.Write(car);
            w.Write(SerialFor(car));
            w.Write(p.Place);
            w.Write(p.Oil);
            w.Write(p.OilCondition);
            Route(except);
        }

        private void OnCarProps(ulong from, BinaryReader r)
        {
            string car = r.ReadString();
            int serial = r.ReadInt32();
            CarProps p = new CarProps { Place = r.ReadInt32(), Oil = r.ReadInt32(), OilCondition = r.ReadInt32() };

            if (!InGarage() || !Syncable(car) || SerialFor(car) != serial) return;
            CarLoader l = Loader(car);
            if (l == null) return;
            CarState cs = StateFor(l);
            if (cs == null) return;

            try
            {
                if (p.Place >= 0 && p.Place != l.GetPlaceNo()) MoveCar(l, p.Place);
                if (p.Oil != Mathf.RoundToInt(l.GetOilLevel() * 1000f) || p.OilCondition != Mathf.RoundToInt(l.GetOilCondition() * 1000f))
                {
                    l.SetOilLevelAndCondition(p.Oil / 1000f, p.OilCondition / 1000f);
                }
            }
            catch (Exception e)
            {
                session.Log.LogWarning("Could not apply car state for " + car + ": " + e.Message);
            }
            cs.Props = ReadProps(l);
            if (session.IsHost) SendProps(car, p, from);
        }

        private void MoveCar(CarLoader l, int place)
        {
            CarLoaderPlaces places = CarLoaderPlaces.Get();
            if (places == null) return;
            int before = l.GetPlaceNo();
            if (!session.IsHost && places.GetOccupied((CarPlace)place)) ClearSpot(places, l, place);
            if (place >= 2 && place <= 4) l.CloseCar();
            l.ChangePosition(place);
            RememberProps(l);
            if (l.GetPlaceNo() == place) session.Log.LogInfo("Moved " + l.name + " from spot " + before + " to " + place);
            else session.Log.LogWarning("Could not move " + l.name + " to spot " + place + ", it looks occupied here");
        }

        private void ClearSpot(CarLoaderPlaces places, CarLoader mover, int place)
        {
            CarLoader blocker = places.GetCarLoaderForPlace((CarPlace)place);
            if (blocker == null || blocker == mover) return;
            try
            {
                for (int spot = 0; spot < 8; spot++)
                {
                    if (spot == place || places.GetOccupied((CarPlace)spot)) continue;
                    blocker.ChangePosition(spot);
                    RememberProps(blocker);
                    session.Log.LogInfo("Moved " + blocker.name + " out of spot " + place + " to spot " + spot + " to make room");
                    return;
                }
            }
            catch (Exception e)
            {
                session.Log.LogWarning("Could not make room in spot " + place + ": " + e.Message);
            }
        }

        private void RememberProps(CarLoader l)
        {
            CarState cs;
            if (known.TryGetValue(l.name, out cs)) cs.Props = ReadProps(l);
        }

        private void ReconcilePlace(string car, int hostPlace)
        {
            CarLoader l = Loader(car);
            if (l == null || l.GetRoot() == null || hostPlace < 0 || l.GetPlaceNo() == hostPlace)
            {
                placeMismatchSince.Remove(car);
                return;
            }
            float since;
            if (!placeMismatchSince.TryGetValue(car, out since))
            {
                placeMismatchSince[car] = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - since < 1.5f) return;
            placeMismatchSince[car] = Time.unscaledTime + 5f;
            MoveCar(l, hostPlace);
        }

        private void SendPart(string car, string path, PartState s, ulong except)
        {
            BinaryWriter w = session.Begin(MsgPart);
            w.Write(car);
            w.Write(SerialFor(car));
            w.Write(path);
            w.Write(s.Unmounted);
            w.Write(s.Condition);
            w.Write(s.Quality);
            w.Write(s.Tuned);
            w.Write(s.Painted);
            w.Write(s.PaintType);
            w.Write(s.Color);
            Route(except);
        }

        private void SendBody(string car, string name, BodyState s, ulong except)
        {
            BinaryWriter w = session.Begin(MsgBody);
            w.Write(car);
            w.Write(SerialFor(car));
            w.Write(name);
            w.Write(s.Unmounted);
            w.Write(s.Switched);
            w.Write(s.Condition);
            w.Write(s.Color);
            w.Write(s.PaintType);
            w.Write(s.Livery ?? "");
            w.Write(s.LiveryStrength);
            Route(except);
        }

        private void Route(ulong except)
        {
            if (session.IsHost) session.SendToAllExcept(except, true);
            else session.SendToHost(true);
        }

        private void OnPart(ulong from, BinaryReader r)
        {
            string car = r.ReadString();
            int serial = r.ReadInt32();
            string path = r.ReadString();
            PartState s = new PartState { Unmounted = r.ReadBoolean(), Condition = r.ReadInt32(), Quality = r.ReadInt32(), Tuned = r.ReadString(), Painted = r.ReadBoolean(), PaintType = r.ReadInt32(), Color = r.ReadUInt32() };

            if (!InGarage() || !Syncable(car) || SerialFor(car) != serial) return;
            CarLoader l = Loader(car);
            if (l == null) return;
            CarState cs = StateFor(l);
            PartScript ps;
            if (cs == null || !cs.Index.TryGetValue(path, out ps) || ps == null) return;

            try
            {
                ApplyPart(ps, s);
            }
            catch (Exception e)
            {
                session.Log.LogWarning("Could not apply " + path + ": " + e.Message);
            }
            cs.Parts[path] = s;
            session.Log.LogInfo(session.NameOf(from) + " changed " + car + "/" + path + (s.Unmounted ? " (off)" : " (on)") + " condition " + s.Condition / 10f + "%");
            if (session.IsHost) SendPart(car, path, s, from);
        }

        private void ApplyPart(PartScript ps, PartState s)
        {
            if (!string.IsNullOrEmpty(s.Tuned) && s.Tuned != (ps.GetTunedID() ?? "")) ps.TunePart(s.Tuned);
            if (s.Unmounted && !ps.IsUnmounted())
            {
                ps.HideBySavegame(false);
                if (!ps.IsUnmounted())
                {
                    UnmountedField.SetValue(ps, true);
                    ps.SetLayerRecursively(16);
                    ps.ReplaceShader(true);
                    ps.Alpha0();
                }
            }
            else if (!s.Unmounted && ps.IsUnmounted())
            {
                ps.MountByGroup(true);
                if (ps.enableOnUnmount != null)
                {
                    foreach (GameObject go in ps.enableOnUnmount)
                    {
                        if (go != null) go.SetActive(false);
                    }
                }
            }
            if (Mathf.RoundToInt(ps.GetCondition() * 1000f) != s.Condition) ps.SetCondition(s.Condition / 1000f);
            if (ps.GetQuality() != s.Quality) ps.SetQuality(s.Quality);
            bool paintChanged = s.Painted != ps.IsPainted || s.PaintType != (int)ps.CurrentPaintType || (s.Painted && s.Color != Pack(ps.GetColor()));
            if (paintChanged)
            {
                ps.IsPainted = s.Painted;
                ps.CurrentPaintType = (PaintType)s.PaintType;
                if (s.Painted)
                {
                    Color c = Unpack(s.Color);
                    ps.SetColor(c);
                    CarHelper.SetPartPaintType(ps.gameObject, ps.CurrentPaintType, c);
                }
            }
        }

        private void OnBody(ulong from, BinaryReader r)
        {
            string car = r.ReadString();
            int serial = r.ReadInt32();
            string name = r.ReadString();
            BodyState s = new BodyState { Unmounted = r.ReadBoolean(), Switched = r.ReadBoolean(), Condition = r.ReadInt32(), Color = r.ReadUInt32(), PaintType = r.ReadInt32(), Livery = r.ReadString(), LiveryStrength = r.ReadInt32() };

            if (!InGarage() || !Syncable(car) || SerialFor(car) != serial) return;
            CarLoader l = Loader(car);
            if (l == null) return;
            CarState cs = StateFor(l);
            carPart cp = l.GetCarPart(name);
            if (cs == null || cp == null || cp.handle == null) return;

            try
            {
                if (s.Unmounted && !cp.unmounted) l.TakeOffCarPartFromSave(name);
                else if (!s.Unmounted && cp.unmounted && TakeOnFromSave != null) TakeOnFromSave.Invoke(l, new object[] { name });
                if (s.Switched != cp.switched && !cp.inprogress) l.SwitchCarPart(name, false);
                if (Mathf.RoundToInt(cp.condition * 1000f) != s.Condition)
                {
                    l.SetCondition(cp, s.Condition / 1000f);
                    l.UpdateCarBodyPart(cp);
                }
                if (s.PaintType != cp.paintType || s.Color != Pack(cp.color))
                {
                    Color c = Unpack(s.Color);
                    cp.paintType = s.PaintType;
                    if (!l.SetCarColor(cp, c)) cp.color = c;
                }
                if (s.Livery != (cp.livery ?? "") || s.LiveryStrength != Mathf.RoundToInt(cp.liveryStrength * 1000f))
                {
                    l.SetCarLivery(cp, s.Livery, s.LiveryStrength / 1000f);
                    cp.livery = s.Livery;
                    cp.liveryStrength = s.LiveryStrength / 1000f;
                }
            }
            catch (Exception e)
            {
                session.Log.LogWarning("Could not apply body part " + name + ": " + e.Message);
            }
            cs.Body[name] = s;
            session.Log.LogInfo(session.NameOf(from) + " changed body " + car + "/" + name + (s.Unmounted ? " (off)" : "") + (s.Switched ? " (open)" : " (closed)"));
            if (session.IsHost) SendBody(car, name, s, from);
        }
    }
}
