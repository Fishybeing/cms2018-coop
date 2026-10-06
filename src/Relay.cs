using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;

namespace CMS2018Coop
{
    internal static class Relay
    {
        private const string Dll = "coop_steam_api64";
        private const int ConfigIceEnable = 104;
        private const int SendUnreliable = 1 | 32;
        private const int SendReliable = 8 | 1 | 32;
        private const int CallbackSessionRequest = 1251;
        private const int CallbackSessionFailed = 1252;
        private const int IdentityTypeSteamId = 16;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport(Dll)] private static extern void SteamAPI_ManualDispatch_Init();
        [DllImport(Dll)] private static extern int SteamAPI_InitFlat(byte[] errMsg);
        [DllImport(Dll)] private static extern int SteamAPI_GetHSteamPipe();
        [DllImport(Dll)] private static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ManualDispatch_GetNextCallback(int pipe, IntPtr callbackMsg);
        [DllImport(Dll)] private static extern void SteamAPI_ManualDispatch_FreeLastCallback(int pipe);
        [DllImport(Dll)] private static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        [DllImport(Dll)] private static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int key, int value);
        [DllImport(Dll)] private static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
        [DllImport(Dll)] private static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(IntPtr identity, ulong steamId);
        [DllImport(Dll)] private static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, IntPtr identity, IntPtr data, uint size, int flags, int channel);
        [DllImport(Dll)] private static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr[] messages, int max);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, IntPtr identity);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, IntPtr identity);
        [DllImport(Dll)] private static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);

        public static bool Ready;
        public static string Error = "";

        private static IntPtr messages;
        private static IntPtr identity;
        private static IntPtr callbackMsg;
        private static int pipe;
        private static readonly IntPtr[] incoming = new IntPtr[32];
        private static int incomingCount;
        private static int incomingIndex;
        private static ManualLogSource log;
        private static Func<ulong, bool> acceptPeer;

        public static bool TryStart(ManualLogSource logger, Func<ulong, bool> accept)
        {
            log = logger;
            acceptPeer = accept;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(Relay).Assembly.Location), Dll + ".dll");
                if (!File.Exists(path))
                {
                    Error = "relay file missing";
                    return false;
                }
                if (LoadLibraryW(path) == IntPtr.Zero)
                {
                    Error = "could not load relay file (" + Marshal.GetLastWin32Error() + ")";
                    return false;
                }
                SteamAPI_ManualDispatch_Init();
                byte[] err = new byte[1024];
                int result = SteamAPI_InitFlat(err);
                if (result != 0)
                {
                    Error = "Steam relay init failed (" + result + "): " + Encoding.UTF8.GetString(err).TrimEnd('\0');
                    return false;
                }
                pipe = SteamAPI_GetHSteamPipe();
                messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
                IntPtr utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                if (messages == IntPtr.Zero || utils == IntPtr.Zero)
                {
                    Error = "Steam relay interfaces missing";
                    return false;
                }
                if (!SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, ConfigIceEnable, 0))
                {
                    Error = "could not turn off direct connections";
                    return false;
                }
                SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(utils);
                identity = Marshal.AllocHGlobal(136);
                callbackMsg = Marshal.AllocHGlobal(32);
                Ready = true;
                log.LogInfo("Steam relay ready, direct connections off");
                return true;
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static IntPtr Identity(ulong steamId)
        {
            for (int i = 0; i < 136; i++) Marshal.WriteByte(identity, i, 0);
            SteamAPI_SteamNetworkingIdentity_SetSteamID64(identity, steamId);
            return identity;
        }

        public static void Send(ulong to, byte[] data, int length, bool reliable)
        {
            if (!Ready) return;
            GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                SteamAPI_ISteamNetworkingMessages_SendMessageToUser(messages, Identity(to), h.AddrOfPinnedObject(), (uint)length, reliable ? SendReliable : SendUnreliable, 0);
            }
            finally
            {
                h.Free();
            }
        }

        public static bool TryReceive(ref byte[] buffer, out int length, out ulong from)
        {
            length = 0;
            from = 0;
            if (!Ready) return false;
            while (true)
            {
                if (incomingIndex >= incomingCount)
                {
                    incomingIndex = 0;
                    incomingCount = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(messages, 0, incoming, incoming.Length);
                    if (incomingCount <= 0)
                    {
                        incomingCount = 0;
                        return false;
                    }
                }
                IntPtr msg = incoming[incomingIndex++];
                try
                {
                    IntPtr data = Marshal.ReadIntPtr(msg, 0);
                    int size = Marshal.ReadInt32(msg, 8);
                    int type = Marshal.ReadInt32(msg, 16);
                    if (type != IdentityTypeSteamId || size <= 0) continue;
                    from = (ulong)Marshal.ReadInt64(msg, 24);
                    if (buffer.Length < size) buffer = new byte[size];
                    Marshal.Copy(data, buffer, 0, size);
                    length = size;
                    return true;
                }
                finally
                {
                    SteamAPI_SteamNetworkingMessage_t_Release(msg);
                }
            }
        }

        public static void Pump()
        {
            if (!Ready) return;
            SteamAPI_ManualDispatch_RunFrame(pipe);
            while (SteamAPI_ManualDispatch_GetNextCallback(pipe, callbackMsg))
            {
                try
                {
                    int id = Marshal.ReadInt32(callbackMsg, 4);
                    IntPtr param = Marshal.ReadIntPtr(callbackMsg, 8);
                    if (id == CallbackSessionRequest)
                    {
                        ulong peer = (ulong)Marshal.ReadInt64(param, 8);
                        if (acceptPeer != null && acceptPeer(peer))
                        {
                            SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(messages, Identity(peer));
                        }
                        else
                        {
                            log.LogWarning("Ignored relay connection from non-member " + peer);
                        }
                    }
                    else if (id == CallbackSessionFailed)
                    {
                        log.LogWarning("Relay session failed");
                    }
                }
                finally
                {
                    SteamAPI_ManualDispatch_FreeLastCallback(pipe);
                }
            }
        }

        public static void Close(ulong peer)
        {
            if (!Ready) return;
            SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(messages, Identity(peer));
        }
    }
}
