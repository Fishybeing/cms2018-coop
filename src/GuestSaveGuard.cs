using System;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace CMS2018Coop
{
    internal static class GuestSaveGuard
    {
        private const string MarkerName = "coop_guest_backup.txt";

        private static string Root
        {
            get { return Application.persistentDataPath; }
        }

        public static bool Active
        {
            get { return File.Exists(Path.Combine(Root, MarkerName)); }
        }

        public static void Protect(ManualLogSource log)
        {
            if (Active) return;
            int profile = ProfileManager.Get().GetSelectedProfile();
            string source = Path.Combine(Root, "profile" + profile);
            string backup = Path.Combine(Root, "coop_backup_profile" + profile);
            if (!Directory.Exists(source)) return;
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            CopyDir(source, backup);
            File.WriteAllText(Path.Combine(Root, MarkerName), profile.ToString());
            log.LogInfo("Backed up your save (profile " + profile + ") while you are a guest");
        }

        public static void RestoreIfNeeded(ManualLogSource log)
        {
            string marker = Path.Combine(Root, MarkerName);
            if (!File.Exists(marker)) return;
            try
            {
                int profile = int.Parse(File.ReadAllText(marker).Trim());
                string target = Path.Combine(Root, "profile" + profile);
                string backup = Path.Combine(Root, "coop_backup_profile" + profile);
                if (Directory.Exists(backup))
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    CopyDir(backup, target);
                    Directory.Delete(backup, true);
                    log.LogInfo("Restored your own save (profile " + profile + ") from before the co-op session");
                }
                File.Delete(marker);
            }
            catch (Exception e)
            {
                log.LogError("Could not restore your save backup: " + e);
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            }
            foreach (string dir in Directory.GetDirectories(from))
            {
                CopyDir(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }
}
