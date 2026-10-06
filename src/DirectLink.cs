using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace CMS2018Coop
{
    internal class DirectLink : ILink
    {
        private readonly Session session;
        private UdpClient udp;
        private bool hosting;
        private ulong hostId;
        private bool heardFromHost;
        private float helloTimer;
        private readonly Dictionary<ulong, IPEndPoint> peers = new Dictionary<ulong, IPEndPoint>();
        private readonly Dictionary<ulong, IPEndPoint> pending = new Dictionary<ulong, IPEndPoint>();

        public bool Active
        {
            get { return udp != null; }
        }

        public bool IsHost
        {
            get { return hosting; }
        }

        public ulong HostId
        {
            get { return hosting ? 0 : hostId; }
        }

        public DirectLink(Session session)
        {
            this.session = session;
        }

        private static ulong IdOf(IPEndPoint ep)
        {
            return (1UL << 63) | ((ulong)(uint)ep.Address.ToString().GetHashCode() << 16) | (uint)ep.Port;
        }

        public void Listen(int port)
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            hosting = true;
            session.OnLinkUp("Direct host on port " + port + ". Waiting for friends...");
        }

        public void Connect(string address)
        {
            int colon = address.LastIndexOf(':');
            if (colon <= 0) throw new FormatException("Use ip:port, for example 192.168.1.20:27100");
            string host = address.Substring(0, colon);
            int port = int.Parse(address.Substring(colon + 1));
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip)) ip = Dns.GetHostAddresses(host)[0];
            IPEndPoint ep = new IPEndPoint(ip, port);

            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            hostId = IdOf(ep);
            peers[hostId] = ep;
            session.AddRemote(hostId, address);
            session.OnLinkUp("Direct connecting to " + address + "...");
            session.BecameGuest();
            session.SendHello();
        }

        public void Update()
        {
            if (udp == null || hosting || heardFromHost) return;
            helloTimer -= Time.unscaledDeltaTime;
            if (helloTimer > 0f) return;
            helloTimer = 1f;
            session.SendHello();
        }

        public void Send(ulong to, byte[] data, int length, bool reliable)
        {
            if (udp == null || !peers.TryGetValue(to, out IPEndPoint ep)) return;
            try
            {
                udp.Send(data, length, ep);
            }
            catch (SocketException e)
            {
                session.Log.LogWarning("Send failed: " + e.Message);
            }
        }

        public bool TryReceive(ref byte[] buffer, out int length, out ulong from)
        {
            length = 0;
            from = 0;
            while (udp != null && udp.Available > 0)
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try
                {
                    data = udp.Receive(ref ep);
                }
                catch (SocketException)
                {
                    continue;
                }
                from = IdOf(ep);
                if (!peers.ContainsKey(from))
                {
                    if (!hosting) continue;
                    pending[from] = ep;
                }
                if (from == hostId && !heardFromHost)
                {
                    heardFromHost = true;
                    session.OnLinkUp("Direct connected to " + ep);
                }
                buffer = data;
                length = data.Length;
                return true;
            }
            return false;
        }

        public bool Admit(ulong from, byte messageType)
        {
            if (!hosting || messageType != Session.MsgHello) return false;
            if (!pending.TryGetValue(from, out IPEndPoint ep)) return false;
            pending.Remove(from);
            peers[from] = ep;
            session.AddRemote(from, ep.ToString());
            session.Status = "Direct hosting";
            return true;
        }

        public void Disconnect(ulong peer)
        {
            peers.Remove(peer);
        }

        public void Shutdown()
        {
            if (udp != null) udp.Close();
            udp = null;
            peers.Clear();
            pending.Clear();
            heardFromHost = false;
        }
    }
}
