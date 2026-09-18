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

        public static string MainLeavesName = "UnityTempFile";
        public static string RankedLeavesName = "UnityTempFile";

        public static async void FetchLeaves()
        {
            try
            {
                using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                {
                    string json = await client.GetStringAsync("https://gtag.website/leafs/");
                    System.Text.RegularExpressions.Match mainMatch = System.Text.RegularExpressions.Regex.Match(json, @"""mainForest""\s*:\s*""([^""]+)""");
                    if (mainMatch.Success) MainLeavesName = mainMatch.Groups[1].Value;
                    
                    System.Text.RegularExpressions.Match rankedMatch = System.Text.RegularExpressions.Regex.Match(json, @"""rankedForest""\s*:\s*""([^""]+)""");
                    if (rankedMatch.Success) RankedLeavesName = rankedMatch.Groups[1].Value;
                }
            }
            catch { }
        }

        public static bool LeavesRemoved { get; private set; } = true;
        private Coroutine removeLeavesCoroutine;

        public static void Toggle()
        {
            LeavesRemoved = !LeavesRemoved;
            foreach (GameObject obj in GetLeaves())
            {
                if (obj != null)
                {
                    obj.SetActive(!LeavesRemoved);
                }
            }
        }

        private void Awake()
        {
            new HarmonyLib.Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;
            FetchLeaves();
            RemoveLeaves();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (removeLeavesCoroutine != null)
            {
                StopCoroutine(removeLeavesCoroutine);
                removeLeavesCoroutine = null;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RemoveLeaves();
        }

        private void RemoveLeaves()
        {
            if (removeLeavesCoroutine != null)
            {
                StopCoroutine(removeLeavesCoroutine);
            }

            removeLeavesCoroutine = StartCoroutine(RemoveLeavesLater());
        }

        private IEnumerator RemoveLeavesLater()
        {
            const int attempts = 12;
            const float delaySeconds = 0.5f;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                RemoveLeavesPass();

                if (attempt < attempts - 1)
                {
                    yield return new WaitForSeconds(delaySeconds);
                }
            }

            removeLeavesCoroutine = null;
        }

        private int RemoveLeavesPass()
        {
            int count = 0;

            foreach (GameObject obj in GetLeaves())
            {
                if (obj != null)
                {
                    obj.SetActive(!LeavesRemoved);
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<GameObject> GetLeaves()
        {
            HashSet<GameObject> foundObjs = new HashSet<GameObject>();

            GameObject forest = GameObject.Find(forestPath);
            if (forest != null)
            {
                for (int i = 0; i < forest.transform.childCount; i++)
                {
                    GameObject v = forest.transform.GetChild(i).gameObject;
                    if (v.name.Contains(MainLeavesName))
                    {
                        foundObjs.Add(v);
                    }
                }
            }

            GameObject rankedForest = GameObject.Find(rankedForestPath);
            if (rankedForest != null)
            {
                for (int i = 0; i < rankedForest.transform.childCount; i++)
                {
                    GameObject v = rankedForest.transform.GetChild(i).gameObject;
                    if (v.name.Contains(RankedLeavesName))
                    {
                        foundObjs.Add(v);
                    }
                }
            }

            return foundObjs;
        }
    }
}
