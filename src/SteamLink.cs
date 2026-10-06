using System;
using System.Collections.Generic;
using System.Reflection;
using Steamworks;

namespace CMS2018Coop
{
    internal class SteamLink : ILink
    {
        private const string LobbyKey = "cms2018coop";
        private const int MaxPlayers = 4;

        private readonly Session session;
        private readonly CSteamID me;
        private CSteamID lobby = CSteamID.Nil;
        private bool creatingLobby;
        private readonly bool relayOk;
        private readonly Dictionary<ulong, string> peerRelay = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, float> peerSeen = new Dictionary<ulong, float>();
        private float relayRefresh;

        private readonly Callback<LobbyCreated_t> cbLobbyCreated;
        private readonly Callback<LobbyEnter_t> cbLobbyEnter;
        private readonly Callback<GameLobbyJoinRequested_t> cbJoinRequested;
        private readonly Callback<LobbyChatUpdate_t> cbChatUpdate;
        private readonly Callback<P2PSessionRequest_t> cbSessionRequest;
        private readonly Callback<P2PSessionConnectFail_t> cbConnectFail;

        public bool Active
        {
            get { return lobby != CSteamID.Nil; }
        }

        public bool IsHost
        {
            get { return Active && SteamMatchmaking.GetLobbyOwner(lobby) == me; }
        }

        public ulong HostId
        {
            get { return Active ? SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID : 0; }
        }

        public SteamLink(Session session)
        {
            this.session = session;
            me = SteamUser.GetSteamID();
            cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            cbChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            cbSessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            cbConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
            SteamNetworking.AllowP2PPacketRelay(true);
            if (Plugin.Instance == null || Plugin.Instance.HideIp)
            {
                relayOk = Relay.TryStart(session.Log, id => IsLobbyMember(new CSteamID(id)));
                if (!relayOk) session.Log.LogWarning("Steam relay not available, IP may be visible: " + Relay.Error);
            }
            session.Log.LogInfo("Steam ready as " + PersonaName() + " (" + me.m_SteamID + ")");
        }

        public static bool SteamIsUp()
        {
            Type t = typeof(SteamManager);
            FieldInfo inst = t.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo init = t.GetField("m_bInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
            if (inst == null || init == null) return false;
            object manager = inst.GetValue(null);
            if (manager == null || (manager as UnityEngine.Object) == null) return false;
            return (bool)init.GetValue(manager);
        }

        public static string PersonaName()
        {
            return SteamFriends.GetPersonaName();
        }

        public void JoinFromCommandLine()
        {
            string id = CommandLine.Value("+connect_lobby");
            if (id != null && ulong.TryParse(id, out ulong lobbyId))
            {
                session.Log.LogInfo("Joining lobby from launch invite " + lobbyId);
                Join(new CSteamID(lobbyId));
            }
        }

        public void Host()
        {
            if (Active)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(lobby);
                return;
            }
            if (creatingLobby) return;
            creatingLobby = true;
            session.Status = "Creating session...";
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxPlayers);
        }

        public void Join(CSteamID target)
        {
            if (Active)
            {
                if (target == lobby) return;
                session.Leave();
            }
            session.Status = "Joining...";
            SteamMatchmaking.JoinLobby(target);
        }

        public string Privacy
        {
            get
            {
                if (!relayOk) return "IP may be visible (relay off)";
                foreach (ulong id in session.Remotes.Keys)
                {
                    string flag;
                    if (!peerRelay.TryGetValue(id, out flag) || flag != "1") return "IP may be visible to " + session.NameOf(id);
                }
                return "IP hidden (Steam relay)";
            }
        }

        public void Update()
        {
            if (!relayOk) return;
            Relay.Pump();
            if (!Active || UnityEngine.Time.unscaledTime < relayRefresh) return;
            relayRefresh = UnityEngine.Time.unscaledTime + 1f;
            foreach (ulong id in new List<ulong>(session.Remotes.Keys))
            {
                if (!peerSeen.ContainsKey(id)) peerSeen[id] = UnityEngine.Time.unscaledTime;
                string flag = SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(id), "relay") ?? "";
                string old;
                peerRelay.TryGetValue(id, out old);
                if (old == flag) continue;
                peerRelay[id] = flag;
                session.Log.LogInfo(session.NameOf(id) + (flag == "1" ? " uses the Steam relay" : flag == "0" ? " has no Steam relay, using normal Steam P2P" : " relay status unknown"));
                if (flag != "") session.SendHelloTo(id);
            }
        }

        private bool UseRelay(ulong to, out bool drop)
        {
            drop = false;
            if (!relayOk) return false;
            string flag;
            peerRelay.TryGetValue(to, out flag);
            if (flag == "1") return true;
            if (flag == "0") return false;
            float seen;
            if (!peerSeen.TryGetValue(to, out seen)) peerSeen[to] = seen = UnityEngine.Time.unscaledTime;
            drop = UnityEngine.Time.unscaledTime - seen < 8f;
            return false;
        }

        public void Send(ulong to, byte[] data, int length, bool reliable)
        {
            bool drop;
            if (UseRelay(to, out drop))
            {
                Relay.Send(to, data, length, reliable);
                return;
            }
            if (drop) return;
            SteamNetworking.SendP2PPacket(new CSteamID(to), data, (uint)length, reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay);
        }

        public bool TryReceive(ref byte[] buffer, out int length, out ulong from)
        {
            if (relayOk && Relay.TryReceive(ref buffer, out length, out from)) return true;
            length = 0;
            from = 0;
            if (!SteamNetworking.IsP2PPacketAvailable(out uint size)) return false;
            if (size > buffer.Length) buffer = new byte[size];
            if (!SteamNetworking.ReadP2PPacket(buffer, (uint)buffer.Length, out uint read, out CSteamID sender)) return false;
            length = (int)read;
            from = sender.m_SteamID;
            return true;
        }

        public bool Admit(ulong from, byte messageType)
        {
            RefreshMembers();
            return session.Remotes.ContainsKey(from);
        }

        public void Disconnect(ulong peer)
        {
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));
            if (relayOk) Relay.Close(peer);
            peerRelay.Remove(peer);
            peerSeen.Remove(peer);
        }

        public void Shutdown()
        {
            if (!Active) return;
            SteamMatchmaking.LeaveLobby(lobby);
            SteamFriends.SetRichPresence("connect", null);
            lobby = CSteamID.Nil;
        }

        private void OnLobbyCreated(LobbyCreated_t e)
        {
            creatingLobby = false;
            if (e.m_eResult != EResult.k_EResultOK)
            {
                session.Status = "Could not create session (" + e.m_eResult + ")";
                session.Log.LogWarning(session.Status);
                return;
            }
            CSteamID created = new CSteamID(e.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(created, LobbyKey, Session.ProtocolVersion.ToString());
            SteamMatchmaking.SetLobbyData(created, "host", session.MyName);
            session.Log.LogInfo("Created session " + created.m_SteamID);
            SteamFriends.ActivateGameOverlayInviteDialog(created);
        }

        private void OnLobbyEnter(LobbyEnter_t e)
        {
            if (e.m_EChatRoomEnterResponse != 1)
            {
                session.Status = "Could not join session (code " + e.m_EChatRoomEnterResponse + ")";
                session.Log.LogWarning(session.Status);
                return;
            }
            lobby = new CSteamID(e.m_ulSteamIDLobby);
            string version = SteamMatchmaking.GetLobbyData(lobby, LobbyKey);
            if (!string.IsNullOrEmpty(version) && version != Session.ProtocolVersion.ToString())
            {
                session.Log.LogWarning("Host runs a different co-op mod version: " + version);
            }
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + lobby.m_SteamID);
            SteamMatchmaking.SetLobbyMemberData(lobby, "relay", relayOk ? "1" : "0");
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
            bool host = owner == me;
            session.OnLinkUp(host ? "Hosting. Waiting for friends..." : "Connected to " + SteamFriends.GetFriendPersonaName(owner));
            if (!host) session.BecameGuest();
            RefreshMembers();
            session.SendHello();
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t e)
        {
            session.Log.LogInfo("Accepted invite from " + SteamFriends.GetFriendPersonaName(e.m_steamIDFriend));
            Join(e.m_steamIDLobby);
        }

        private void OnChatUpdate(LobbyChatUpdate_t e)
        {
            if (!Active || e.m_ulSteamIDLobby != lobby.m_SteamID) return;
            RefreshMembers();
            const uint entered = 1;
            if ((e.m_rgfChatMemberStateChange & entered) != 0) session.SendHelloTo(e.m_ulSteamIDUserChanged);
            if (SteamMatchmaking.GetLobbyOwner(lobby) == me)
            {
                session.Status = session.Remotes.Count > 0 ? "Hosting" : "Hosting. Waiting for friends...";
            }
        }

        private void OnSessionRequest(P2PSessionRequest_t e)
        {
            if (IsLobbyMember(e.m_steamIDRemote))
            {
                SteamNetworking.AcceptP2PSessionWithUser(e.m_steamIDRemote);
            }
            else
            {
                session.Log.LogWarning("Ignored connection from non-member " + e.m_steamIDRemote.m_SteamID);
            }
        }

        private void OnConnectFail(P2PSessionConnectFail_t e)
        {
            session.Log.LogWarning("Connection to " + e.m_steamIDRemote.m_SteamID + " failed, error " + e.m_eP2PSessionError);
        }

        private bool IsLobbyMember(CSteamID id)
        {
            if (!Active) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id) return true;
            }
            return false;
        }

        private void RefreshMembers()
        {
            if (!Active) return;
            Dictionary<ulong, string> members = new Dictionary<ulong, string>();
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (id == me) continue;
                members[id.m_SteamID] = SteamFriends.GetFriendPersonaName(id);
            }
            session.SyncMembers(members);
        }
    }
}
