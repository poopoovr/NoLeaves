using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NoLeaves
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private const string forestPath = "Environment Objects/LocalObjects_Prefab/Forest";
        private const string rankedForestPath = "RankedMain/Ranked_Layout/Ranked_Forest_prefab";
        public static string mainName = "";
        public static string rankedName = "";
        public static bool isRemoved { get; private set; } = true;
        private Coroutine hideCoroutine;

        public static bool streamerMode = false;

        public static void Toggle()
        {
            isRemoved = !isRemoved;
            foreach (GameObject obj in FindLeaves())
                if (obj != null) obj.SetActive(!isRemoved);
        }

        public static void StreamerOn()
        {
            foreach (GameObject v in FindLeaves())
            {
                if (v == null) continue;
                v.SetActive(true);
                v.layer = 21; 
            }
        }

        public static void StreamerOff()
        {
            foreach (GameObject v in FindLeaves())
            {
                if (v == null) continue;
                v.layer = 0;
                v.SetActive(!isRemoved);
            }
        }

        private void Update()
        {
            if (UnityEngine.InputSystem.Keyboard.current == null) return;
            if (UnityEngine.InputSystem.Keyboard.current.f5Key.wasPressedThisFrame) Toggle();
            
            if (UnityEngine.InputSystem.Keyboard.current.f6Key.wasPressedThisFrame)
            {
                streamerMode = !streamerMode;
                if (streamerMode) StreamerOn();
                else StreamerOff();
            }
        }

        private void Awake()
        {
            StartupLog.PrintThePoop(Logger);
            new HarmonyLib.Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            SceneManager.sceneLoaded += (scene, mode) => HideLeaves();
            HideLeaves();
        }

        private void OnDestroy()
        {
            if (hideCoroutine != null) StopCoroutine(hideCoroutine);
        }

        private void HideLeaves()
        {
            if (hideCoroutine != null) StopCoroutine(hideCoroutine);
            hideCoroutine = StartCoroutine(HideWait());
        }

        private IEnumerator HideWait()
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                foreach (GameObject obj in FindLeaves())
                    if (obj != null) obj.SetActive(!isRemoved);
                
                if (attempt < 11) yield return new WaitForSeconds(0.5f);
            }
            hideCoroutine = null;
        }

        private static string FindName(GameObject parent)
        {
            if (parent == null) return "";
            int max = Math.Min(29, parent.transform.childCount), count = 1;
            string lastName = "";
            for (int i = 15; i < max; i++)
            {
                string n = parent.transform.GetChild(i).name;
                count = n == lastName ? count + 1 : 1;
                if (count >= 3) return n;
                lastName = n;
            }
            return "";
        }

        private static IEnumerable<GameObject> FindLeaves()
        {
            HashSet<GameObject> foundObjs = new HashSet<GameObject>();
            
            GameObject forest = GameObject.Find(forestPath);
            if (forest != null)
            {
                if (string.IsNullOrEmpty(mainName)) mainName = FindName(forest);
                if (!string.IsNullOrEmpty(mainName))
                    for (int i = 0; i < forest.transform.childCount; i++)
                    {
                        GameObject v = forest.transform.GetChild(i).gameObject;
                        if (v.name == mainName) foundObjs.Add(v);
                    }
            }

            GameObject rankedForest = GameObject.Find(rankedForestPath);
            if (rankedForest != null)
            {
                if (string.IsNullOrEmpty(rankedName)) rankedName = FindName(rankedForest);
                if (!string.IsNullOrEmpty(rankedName))
                    for (int i = 0; i < rankedForest.transform.childCount; i++)
                    {
                        GameObject v = rankedForest.transform.GetChild(i).gameObject;
                        if (v.name == rankedName) foundObjs.Add(v);
                    }
            }

            return foundObjs;
        }
    }
}