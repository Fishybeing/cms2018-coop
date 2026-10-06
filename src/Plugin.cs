using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CMS2018Coop
{
    [BepInPlugin("fishybeing.cms2018coop", "CMS 2018 Co-op", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;

        private Session session;
        private TestMode test;
        private float sendTimer;
        private float nextErrorLog;
        private bool showOverlay = true;
        private ConfigEntry<KeyboardShortcut> hostKey;
        private ConfigEntry<KeyboardShortcut> leaveKey;
        private ConfigEntry<KeyboardShortcut> overlayKey;
        private ConfigEntry<int> sendRate;
        private ConfigEntry<int> backgroundFps;
        private ConfigEntry<bool> hideIp;
        private ConfigEntry<KeyboardShortcut> sprintKey;
        private ConfigEntry<float> sprintMultiplier;
        private GUIStyle labelStyle;
        private GUIStyle boxStyle;
        private string overlayText = "";
        private Rect overlayRect;
        private float overlayRefresh;
        private bool focused = true;
        private int savedFrameRate = -1;
        private bool frameRateCapped;

        internal bool HideIp
        {
            get { return hideIp == null || hideIp.Value; }
        }

        internal bool SprintHeld
        {
            get { return sprintKey != null && Input.GetKey(sprintKey.Value.MainKey); }
        }

        internal float SprintMultiplier
        {
            get { return sprintMultiplier != null ? Mathf.Clamp(sprintMultiplier.Value, 1f, 3f) : 1.2f; }
        }

        internal Session Session
        {
            get { return session; }
        }

        private void Awake()
        {
            Instance = this;
            GuestSaveGuard.RestoreIfNeeded(Logger);
            hostKey = Config.Bind("Keys", "HostAndInvite", new KeyboardShortcut(KeyCode.F6), "Create a co-op session and open the Steam invite window");
            leaveKey = Config.Bind("Keys", "Leave", new KeyboardShortcut(KeyCode.F7), "Leave the current co-op session");
            overlayKey = Config.Bind("Keys", "ToggleOverlay", new KeyboardShortcut(KeyCode.F4), "Show or hide the co-op status box");
            sendRate = Config.Bind("Network", "SendRate", 20, "Position updates sent per second");
            hideIp = Config.Bind("Network", "HideIP", true, "Send everything through Valve relay servers so players never see each other's IP. Falls back to normal Steam P2P if the relay cannot start");
            sprintKey = Config.Bind("Movement", "SprintKey", new KeyboardShortcut(KeyCode.LeftShift), "Hold to sprint");
            sprintMultiplier = Config.Bind("Movement", "SprintMultiplier", 1.2f, "How much faster you move while sprinting");
            backgroundFps = Config.Bind("Performance", "BackgroundFps", 30, "Frame cap while the game window is not focused during co-op, 0 for no cap");
            session = new Session(Logger);
            test = new TestMode(session);
            try
            {
                Patches.Apply();
            }
            catch (System.Exception e)
            {
                Logger.LogError("Could not apply game hooks: " + e);
            }
            Logger.LogInfo("CMS 2018 Co-op loaded");
        }

        private void Update()
        {
            test.Update();
            if (!session.Ready)
            {
                session.TryInit();
                return;
            }

            if (hostKey.Value.IsDown() && !session.IsDirect) session.Host();
            if (leaveKey.Value.IsDown()) session.Leave();
            if (overlayKey.Value.IsDown()) showOverlay = !showOverlay;

            if (session.Connected && !Application.runInBackground) Application.runInBackground = true;
            UpdateFrameCap();

            session.Poll();
            try
            {
                session.Cars.Update();
            }
            catch (System.Exception e)
            {
                if (Time.unscaledTime > nextErrorLog)
                {
                    nextErrorLog = Time.unscaledTime + 5f;
                    Logger.LogError("Car sync error: " + e);
                }
            }

            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer <= 0f)
            {
                sendTimer = 1f / Mathf.Max(1, sendRate.Value);
                if (session.Connected && LocalPlayer.TryRead(out PlayerState state))
                {
                    session.BroadcastState(test.Adjust(state));
                }
            }

            session.TickRemotes(GameManager.IsGameReady ? LocalPlayer.SceneName() : "");
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            focused = hasFocus;
        }

        private void UpdateFrameCap()
        {
            bool want = !focused && session.Connected && backgroundFps.Value > 0;
            if (want && !frameRateCapped)
            {
                savedFrameRate = Application.targetFrameRate;
                Application.targetFrameRate = backgroundFps.Value;
                frameRateCapped = true;
            }
            else if (!want && frameRateCapped)
            {
                Application.targetFrameRate = savedFrameRate;
                frameRateCapped = false;
            }
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold };
                labelStyle.normal.textColor = Color.white;
                boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
                boxStyle.normal.textColor = Color.white;
            }

            DrawNameTags();

            if (!showOverlay) return;
            if (Time.unscaledTime >= overlayRefresh)
            {
                overlayRefresh = Time.unscaledTime + 0.25f;
                overlayText = BuildOverlay();
                overlayRect = new Rect(10, 10, 300, 12 + 17 * CountLines(overlayText));
            }
            GUI.Box(overlayRect, overlayText, boxStyle);
        }

        private string BuildOverlay()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            sb.Append("CMS 2018 Co-op\n").Append(session.Status);
            if (session.Ready)
            {
                if (session.Connected)
                {
                    sb.Append('\n').Append(session.Privacy);
                    if (!string.IsNullOrEmpty(session.Cars.Summary)) sb.Append('\n').Append(session.Cars.Summary);
                    if (!session.IsHost && GuestSaveGuard.Active) sb.Append("\nYour own save is backed up");
                    sb.Append("\nPlayers: ").Append(session.Remotes.Count + 1);
                    foreach (RemotePlayer r in session.Remotes.Values)
                    {
                        sb.Append("\n  ").Append(r.Name).Append(r.HasState ? " (" + r.Scene + ")" : " (connecting)");
                    }
                    if (session.IsDirect) sb.Append('\n').Append(leaveKey.Value).Append(": leave");
                    else sb.Append('\n').Append(hostKey.Value).Append(": invite   ").Append(leaveKey.Value).Append(": leave");
                }
                else
                {
                    sb.Append('\n').Append(hostKey.Value).Append(": host and invite a friend");
                }
                sb.Append('\n').Append(overlayKey.Value).Append(": hide this");
            }
            return sb.ToString();
        }

        private void DrawNameTags()
        {
            if (!session.Connected || session.Remotes.Count == 0) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            foreach (RemotePlayer r in session.Remotes.Values)
            {
                if (!r.Visible) continue;
                Vector3 screen = cam.WorldToScreenPoint(r.LabelPosition);
                if (screen.z <= 0f || screen.z > 60f) continue;
                Rect rect = new Rect(screen.x - 100f, Screen.height - screen.y - 12f, 200f, 24f);
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), r.Name, labelStyle);
                GUI.color = Color.white;
                GUI.Label(rect, r.Name, labelStyle);
            }
        }

        private static int CountLines(string s)
        {
            int n = 1;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        private void OnApplicationQuit()
        {
            session.Leave();
        }
    }
}
