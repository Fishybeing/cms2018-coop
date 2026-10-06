using UnityEngine;
using UnityEngine.SceneManagement;

namespace CMS2018Coop
{
    internal struct PlayerState
    {
        public string Scene;
        public Vector3 Feet;
        public float Yaw;
        public float Pitch;
        public bool Sprinting;
    }

    internal static class LocalPlayer
    {
        private static GameObject controller;
        private static CharacterController characterController;
        private static FPSCamera fpsCamera;
        private static float nextSearch;
        private static float lastYaw;
        private static float lastPitch;
        private static PlayerState last;
        private static bool hasLast;

        public static string SceneName()
        {
            return SceneManager.GetActiveScene().name;
        }

        public static bool TryRead(out PlayerState state)
        {
            state = default(PlayerState);
            if (!GameManager.IsGameReady) return false;
            if (!Find())
            {
                if (!hasLast || last.Scene != SceneName()) return false;
                state = last;
                return true;
            }

            Transform t = controller.transform;
            Vector3 center = t.position;
            float halfHeight = 1f;
            if (characterController != null)
            {
                center = t.TransformPoint(characterController.center);
                halfHeight = characterController.height * 0.5f * t.lossyScale.y;
            }

            if (fpsCamera != null && fpsCamera.enabled)
            {
                Vector3 e = fpsCamera.transform.eulerAngles;
                lastYaw = e.y;
                lastPitch = e.x > 180f ? e.x - 360f : e.x;
            }
            else
            {
                lastYaw = t.eulerAngles.y;
            }

            state.Scene = SceneName();
            state.Feet = center - Vector3.up * halfHeight;
            state.Yaw = lastYaw;
            state.Pitch = lastPitch;
            state.Sprinting = Sprint.Active;
            last = state;
            hasLast = true;
            return true;
        }

        private static bool Find()
        {
            if (controller != null && controller.activeInHierarchy) return true;
            if (Time.unscaledTime < nextSearch) return false;
            nextSearch = Time.unscaledTime + 1f;

            controller = GameObject.Find("First Person Controller");
            if (controller == null)
            {
                CharacterMotor motor = Object.FindObjectOfType<CharacterMotor>();
                if (motor != null) controller = motor.gameObject;
            }
            if (controller == null) return false;

            characterController = controller.GetComponent<CharacterController>();
            fpsCamera = Object.FindObjectOfType<FPSCamera>();
            return true;
        }
    }
}
