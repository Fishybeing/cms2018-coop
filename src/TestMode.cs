using System.Collections.Generic;
using UnityEngine;

namespace CMS2018Coop
{
    internal class TestMode
    {
        private readonly Session session;
        private readonly bool autoLoad;
        private readonly bool walk;
        private readonly string screenshotPath;
        private bool loadRequested;
        private float nextMenuCheck;
        private float menuSeenAt = -1f;
        private float visibleSince = -1f;
        private int shotsTaken;
        private float nextShot;
        private bool hasWalkCenter;
        private string walkScene;
        private Vector3 walkCenter;
        private readonly bool clearCars;
        private readonly bool testPart;
        private bool cleared;
        private float garageSince = -1f;
        private float syncedSince = -1f;
        private bool partTested;
        private readonly bool lowGfx;
        private readonly bool testLift;
        private bool liftTested;
        private bool gfxApplied;

        public static bool HoldSync;

        public TestMode(Session session)
        {
            this.session = session;
            autoLoad = CommandLine.Has("-coop-autoload");
            walk = CommandLine.Has("-coop-testwalk");
            screenshotPath = CommandLine.Value("-coop-screenshot");
            clearCars = CommandLine.Has("-coop-testclear");
            testPart = CommandLine.Has("-coop-testpart");
            lowGfx = CommandLine.Has("-coop-lowgfx");
            testLift = CommandLine.Has("-coop-testlift");
            HoldSync = clearCars;
            if (autoLoad || walk || screenshotPath != null || clearCars || testPart)
            {
                session.Log.LogInfo("Test mode: autoload=" + autoLoad + " walk=" + walk + " clear=" + clearCars + " part=" + testPart + " screenshot=" + screenshotPath);
            }
        }

        public void Update()
        {
            if (autoLoad && !loadRequested) TryAutoLoad();
            if (screenshotPath != null && shotsTaken < 3) TryScreenshot();
            if (!CarSync.InGarage())
            {
                garageSince = -1f;
                syncedSince = -1f;
                return;
            }
            if (garageSince < 0f) garageSince = Time.unscaledTime;
            if (lowGfx && !gfxApplied)
            {
                gfxApplied = true;
                QualitySettings.SetQualityLevel(0, true);
                Application.targetFrameRate = 30;
                session.Log.LogInfo("Test low graphics on");
            }
            if (testLift && !liftTested) TryLift();
            if (clearCars && !cleared) TryClear();
            if (testPart && !partTested) TryPart();
        }

        private void TryLift()
        {
            if (!session.IsHost || Time.unscaledTime - garageSince < 40f) return;
            liftTested = true;
            foreach (CarLifter lift in Object.FindObjectsOfType<CarLifter>())
            {
                if (lift.GetConnectedCarLoader() == null || lift.isMoving) continue;
                session.Log.LogInfo("Test lift: raising " + lift.name + " from position " + lift.pos);
                lift.Action(0);
                return;
            }
            session.Log.LogWarning("Test lift: no lift with a car on it");
        }

        private void TryClear()
        {
            CarLoader[] all = Object.FindObjectsOfType<CarLoader>();
            foreach (CarLoader l in all)
            {
                if (!l.IsCarLoaded()) return;
            }
            int n = 0;
            foreach (CarLoader l in all)
            {
                if (l.GetRoot() == null) continue;
                l.DeleteCar(false);
                n++;
            }
            cleared = true;
            HoldSync = false;
            session.Log.LogInfo("Test clear: deleted " + n + " local cars before syncing");
        }

        private void TryPart()
        {
            bool synced = session.IsHost ? Time.unscaledTime - garageSince > 25f : session.Cars.Summary.StartsWith("Garage synced");
            if (!synced)
            {
                syncedSince = -1f;
                return;
            }
            if (syncedSince < 0f) syncedSince = Time.unscaledTime;
            if (Time.unscaledTime - syncedSince < 6f) return;
            partTested = true;

            foreach (CarLoader l in Object.FindObjectsOfType<CarLoader>())
            {
                GameObject root = l.GetRoot();
                if (root == null) continue;
                PartScript pick = null;
                foreach (PartScript ps in root.GetComponentsInChildren<PartScript>())
                {
                    if (ps.IsUnmounted() || ps.IsBlocked() || !ps.IscanBeUnmount() || ps.ShouldUnmountWith()) continue;
                    if (ps.gameObject.layer != 16) continue;
                    pick = ps;
                    break;
                }
                if (pick != null)
                {
                    session.Log.LogInfo("Test part: unmounting " + l.name + "/" + pick.GetGameObjectPathWithoutRoot());
                    pick.FastUnmount();
                }
                foreach (carPart cp in l.GetCarParts())
                {
                    if (cp == null || cp.handle == null || cp.unmounted || cp.inprogress) continue;
                    if (!cp.name.StartsWith("door") && !cp.name.StartsWith("hood") && !cp.name.StartsWith("trunk")) continue;
                    session.Log.LogInfo("Test part: opening " + l.name + "/" + cp.name);
                    l.SwitchCarPart(cp.name, false);
                    break;
                }
                return;
            }
            session.Log.LogWarning("Test part: no car to test on");
        }

        public PlayerState Adjust(PlayerState s)
        {
            if (!walk) return s;
            if (!hasWalkCenter || s.Scene != walkScene)
            {
                hasWalkCenter = true;
                walkScene = s.Scene;
                walkCenter = s.Feet + Quaternion.Euler(0f, s.Yaw, 0f) * Vector3.forward * 4f;
            }
            float t = Time.unscaledTime * 0.6f;
            Vector3 offset = new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t)) * 1.5f;
            Vector3 tangent = new Vector3(-Mathf.Sin(t), 0f, Mathf.Cos(t));
            s.Feet = new Vector3(walkCenter.x + offset.x, s.Feet.y, walkCenter.z + offset.z);
            s.Yaw = Quaternion.LookRotation(tangent).eulerAngles.y;
            s.Pitch = Mathf.Sin(t * 2f) * 25f;
            s.Sprinting = Mathf.Sin(Time.unscaledTime * 0.4f) > 0.4f;
            return s;
        }

        private void TryAutoLoad()
        {
            if (Time.unscaledTime < nextMenuCheck) return;
            nextMenuCheck = Time.unscaledTime + 1f;
            MainMenuManager menu = Object.FindObjectOfType<MainMenuManager>();
            if (menu == null)
            {
                menuSeenAt = -1f;
                return;
            }
            if (menuSeenAt < 0f) menuSeenAt = Time.unscaledTime;
            if (Time.unscaledTime - menuSeenAt < 6f) return;
            if (string.IsNullOrEmpty(ProfileManager.Get().GetSelectedProfileLastSave()))
            {
                session.Log.LogWarning("Test autoload: no save in the selected profile");
                loadRequested = true;
                return;
            }
            session.Log.LogInfo("Test autoload: continuing career");
            loadRequested = true;
            menu.ContinueCareer();
        }

        private void TryScreenshot()
        {
            bool anyVisible = false;
            foreach (RemotePlayer r in session.Remotes.Values)
            {
                if (r.Visible) anyVisible = true;
            }
            if (!anyVisible)
            {
                visibleSince = -1f;
                return;
            }
            if (visibleSince < 0f)
            {
                visibleSince = Time.unscaledTime;
                nextShot = visibleSince + 4f;
            }
            if (Time.unscaledTime < nextShot) return;
            nextShot = Time.unscaledTime + 1.5f;
            shotsTaken++;
            string path = screenshotPath.Replace("{n}", shotsTaken.ToString());
            ScreenCapture.CaptureScreenshot(path);
            List<string> where = new List<string>();
            foreach (RemotePlayer r in session.Remotes.Values) where.Add(r.Name + "@" + r.LabelPosition);
            session.Log.LogInfo("Test screenshot " + path + " remotes " + string.Join(", ", where.ToArray()));
        }
    }
}
