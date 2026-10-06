using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace CMS2018Coop
{
    internal interface ILink
    {
        bool Active { get; }
        bool IsHost { get; }
        ulong HostId { get; }
        void Update();
        void Send(ulong to, byte[] data, int length, bool reliable);
        bool TryReceive(ref byte[] buffer, out int length, out ulong from);
        bool Admit(ulong from, byte messageType);
        void Disconnect(ulong peer);
        void Shutdown();
    }

    internal class Session
    {
        public const byte ProtocolVersion = 3;
        public const byte MsgHello = 1;
        public const byte MsgState = 2;
        public const byte MsgBye = 3;

        public bool Ready;
        public string Status = "Waiting for Steam...";
        public string MyName = "Player";
        public readonly Dictionary<ulong, RemotePlayer> Remotes = new Dictionary<ulong, RemotePlayer>();

        public bool Connected
        {
            get { return link != null && link.Active; }
        }

        public bool IsDirect
        {
            get { return link is DirectLink; }
        }

        public bool IsHost
        {
            get { return Connected && link.IsHost; }
        }

        public string Privacy
        {
            get
            {
                SteamLink s = link as SteamLink;
                if (s != null) return s.Privacy;
                return link is DirectLink ? "Direct connect: IP visible" : "";
            }
        }

        public readonly ManualLogSource Log;
        public readonly CarSync Cars;
        private ILink link;
        private SteamLink steam;
        private byte[] readBuffer = new byte[2048];
        private readonly MemoryStream writeStream = new MemoryStream(256);
        private readonly BinaryWriter writer;

        public Session(ManualLogSource log)
        {
            Log = log;
            writer = new BinaryWriter(writeStream, Encoding.UTF8);
            Cars = new CarSync(this);
        }

        public void BecameGuest()
        {
            try
            {
                GuestSaveGuard.Protect(Log);
            }
            catch (Exception e)
            {
                Log.LogError("Could not back up your save: " + e);
            }
        }

        public string NameOf(ulong id)
        {
            RemotePlayer r;
            return Remotes.TryGetValue(id, out r) ? r.Name : id.ToString();
        }

        public BinaryWriter Begin(byte type)
        {
            writeStream.SetLength(0);
            writer.Write(type);
            return writer;
        }

        public void SendTo(ulong id, bool reliable)
        {
            writer.Flush();
            link.Send(id, writeStream.GetBuffer(), (int)writeStream.Length, reliable);
        }

        public void SendToHost(bool reliable)
        {
            if (link.IsHost) return;
            SendTo(link.HostId, reliable);
        }

        public void SendToAllExcept(ulong except, bool reliable)
        {
            writer.Flush();
            byte[] data = writeStream.GetBuffer();
            int len = (int)writeStream.Length;
            foreach (ulong id in Remotes.Keys)
            {
                if (id != except) link.Send(id, data, len, reliable);
            }
        }

        public void TryInit()
        {
            string hostPort = CommandLine.Value("-coop-host");
            string joinAddress = CommandLine.Value("-coop-join");
            string name = CommandLine.Value("-coop-name");

            if (hostPort != null || joinAddress != null)
            {
                MyName = name ?? (SteamLink.SteamIsUp() ? SteamLink.PersonaName() : "Player");
                DirectLink direct = new DirectLink(this);
                link = direct;
                Ready = true;
                try
                {
                    if (hostPort != null) direct.Listen(int.Parse(hostPort));
                    else direct.Connect(joinAddress);
                }
                catch (Exception e)
                {
                    Status = "Direct connect failed: " + e.Message;
                    Log.LogError(e);
                }
                return;
            }

            if (!SteamLink.SteamIsUp()) return;
            try
            {
                steam = new SteamLink(this);
                link = steam;
                MyName = name ?? SteamLink.PersonaName();
                Ready = true;
                Status = "Not connected";
                steam.JoinFromCommandLine();
            }
            catch (Exception e)
            {
                Status = "Steam error, see BepInEx log";
                Log.LogError(e);
            }
        }

        public void Host()
        {
            if (steam != null) steam.Host();
        }

        public void Leave()
        {
            if (!Connected) return;
            writeStream.SetLength(0);
            writer.Write(MsgBye);
            SendToAll(true);
            foreach (ulong id in new List<ulong>(Remotes.Keys))
            {
                RemoveRemote(id);
            }
            link.Shutdown();
            Status = "Not connected";
            Log.LogInfo("Left session");
        }

        public void OnLinkUp(string status)
        {
            Status = status;
            Log.LogInfo(status);
        }

        public void AddRemote(ulong id, string name)
        {
            if (Remotes.ContainsKey(id)) return;
            Remotes[id] = new RemotePlayer(id, name);
            Log.LogInfo("Player joined: " + name);
        }

        public void RemoveRemote(ulong id)
        {
            if (!Remotes.TryGetValue(id, out RemotePlayer r)) return;
            Log.LogInfo("Player left: " + r.Name);
            r.Destroy();
            Remotes.Remove(id);
            if (link != null) link.Disconnect(id);
        }

        public void SyncMembers(Dictionary<ulong, string> members)
        {
            foreach (KeyValuePair<ulong, string> m in members)
            {
                AddRemote(m.Key, m.Value);
            }
            List<ulong> gone = new List<ulong>();
            foreach (ulong id in Remotes.Keys)
            {
                if (!members.ContainsKey(id)) gone.Add(id);
            }
            foreach (ulong id in gone)
            {
                RemoveRemote(id);
            }
        }

        public void SendHello()
        {
            writeStream.SetLength(0);
            writer.Write(MsgHello);
            writer.Write(ProtocolVersion);
            writer.Write(MyName ?? "Player");
            SendToAll(true);
        }

        public void SendHelloTo(ulong id)
        {
            writeStream.SetLength(0);
            writer.Write(MsgHello);
            writer.Write(ProtocolVersion);
            writer.Write(MyName ?? "Player");
            writer.Flush();
            link.Send(id, writeStream.GetBuffer(), (int)writeStream.Length, true);
        }

        public void BroadcastState(PlayerState s)
        {
            if (Remotes.Count == 0) return;
            writeStream.SetLength(0);
            writer.Write(MsgState);
            writer.Write(s.Scene ?? "");
            writer.Write(s.Feet.x);
            writer.Write(s.Feet.y);
            writer.Write(s.Feet.z);
            writer.Write(s.Yaw);
            writer.Write(s.Pitch);
            writer.Write(s.Sprinting);
            SendToAll(false);
        }

        public void SendToAll(bool reliable)
        {
            writer.Flush();
            byte[] data = writeStream.GetBuffer();
            int len = (int)writeStream.Length;
            foreach (ulong id in Remotes.Keys)
            {
                link.Send(id, data, len, reliable);
            }
        }

        public void Poll()
        {
            if (link == null) return;
            link.Update();
            while (link.TryReceive(ref readBuffer, out int length, out ulong from))
            {
                try
                {
                    Handle(from, readBuffer, length);
                }
                catch (Exception e)
                {
                    Log.LogWarning("Bad packet from " + from + ": " + e.Message);
                }
            }
        }

        private void Handle(ulong from, byte[] data, int length)
        {
            if (!Connected || length < 1) return;
            if (!Remotes.TryGetValue(from, out RemotePlayer remote))
            {
                if (!link.Admit(from, data[0])) return;
                if (!Remotes.TryGetValue(from, out remote)) return;
            }

            using (BinaryReader reader = new BinaryReader(new MemoryStream(data, 0, length, false), Encoding.UTF8))
            {
                byte type = reader.ReadByte();
                switch (type)
                {
                    case MsgHello:
                        byte version = reader.ReadByte();
                        string name = reader.ReadString();
                        if (remote.Name != name) Log.LogInfo("Hello from " + name);
                        remote.Name = name;
                        if (version != ProtocolVersion) Log.LogWarning(name + " runs co-op protocol " + version + ", you run " + ProtocolVersion);
                        if (!remote.GreetedBack)
                        {
                            remote.GreetedBack = true;
                            SendHelloTo(from);
                        }
                        break;
                    case MsgState:
                        PlayerState s;
                        s.Scene = reader.ReadString();
                        s.Feet = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        s.Yaw = reader.ReadSingle();
                        s.Pitch = reader.ReadSingle();
                        s.Sprinting = reader.ReadBoolean();
                        if (!remote.HasState) Log.LogInfo("First position from " + remote.Name + " in " + s.Scene + " at " + s.Feet);
                        remote.Apply(s);
                        break;
                    case MsgBye:
                        RemoveRemote(from);
                        break;
                    default:
                        Cars.Handle(from, type, reader);
                        break;
                }
            }
        }

        public void TickRemotes(string localScene)
        {
            foreach (RemotePlayer r in Remotes.Values)
            {
                r.Tick(localScene);
            }
        }
    }

    internal static class CommandLine
    {
        public static string Value(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        public static bool Has(string key)
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
