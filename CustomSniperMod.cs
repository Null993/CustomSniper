using Duckov.Modding;
using Duckov.Options;
using Duckov.Utilities;
using ItemStatsSystem;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CustomSniper
{
    [Serializable]
    public class SniperConfig
    {
        public float BulletDistanceMultiplier = 1.0f;  // 射程倍率（额外系数：基于原始值）
        public float AdsTimeMultiplier = 1.0f;         // 开镜时间倍率
        public float ScatterFactorMultiplier = 1.0f;   // 开镜散射倍率
        //public int FontSize = 14;                      // 字体大小
    }

    public static class ReflectionHelper
    {
        public static T GetStaticFieldValue<T>(Type type, string fieldName)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrEmpty(fieldName)) throw new ArgumentNullException(nameof(fieldName));

            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException($"ReflectionHelper: 未找到静态字段 '{fieldName}' 于类型 {type.FullName}");

            object value = field.GetValue(null);
            return (T)value;
        }
    }

    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private static readonly string MOD_NAME = "SniperTweaks";
        private SniperConfig config = new SniperConfig();

        // 狙击枪ID列表（按需修改）
        private readonly int[] sniperIDs = { 246, 407, 780, 781, 782 };

        // 缓存每个武器的原始参数（避免重复叠加）
        private Dictionary<int, (float dist, float ads, float scatter)> _originalValues
            = new Dictionary<int, (float dist, float ads, float scatter)>();

        private TextMeshProUGUI _text;

        void OnEnable()
        {
            ModManager.OnModActivated += OnModActivated;
            if (ModConfigAPI.IsAvailable())
            {
                SetupConfigUI();
                LoadConfig();
                ApplyTweaks();
                //ApplyFontSize();
            }
        }

        void OnDisable()
        {
            ModManager.OnModActivated -= OnModActivated;
            ModConfigAPI.SafeRemoveOnOptionsChangedDelegate(OnConfigChanged);
            if (_text != null)
            {
                try { GameObject.Destroy(_text.gameObject); }
                catch { /* ignore */ }
                _text = null;
            }
        }

        private void OnModActivated(ModInfo info, Duckov.Modding.ModBehaviour behaviour)
        {
            if (info.name == ModConfigAPI.ModConfigName)
            {
                SetupConfigUI();
                LoadConfig();
                ApplyTweaks();
                //ApplyFontSize();
            }
        }

        private void SetupConfigUI()
        {
            if (!ModConfigAPI.IsAvailable()) return;

            ModConfigAPI.SafeAddOnOptionsChangedDelegate(OnConfigChanged);

            bool zh = Application.systemLanguage.ToString().StartsWith("Chinese");

            //// 字体大小控制
            //ModConfigAPI.SafeAddInputWithSlider(
            //    MOD_NAME, "FontSize",
            //    zh ? "字体大小" : "Font Size",
            //    typeof(int), config.FontSize, new Vector2(5, 24)
            //);

            // 子弹射程倍率（额外系数）
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "BulletDistanceMultiplier",
                zh ? "子弹射程倍率" : "Bullet Distance Multiplier",
                typeof(float), config.BulletDistanceMultiplier, new Vector2(0.1f, 3f)
            );

            // 开镜时间倍率
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "AdsTimeMultiplier",
                zh ? "开镜时间倍率" : "ADS Time Multiplier",
                typeof(float), config.AdsTimeMultiplier, new Vector2(0.1f, 3f)
            );

            // 开镜散射倍率
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "ScatterFactorMultiplier",
                zh ? "开镜散射倍率" : "ADS Scatter Multiplier",
                typeof(float), config.ScatterFactorMultiplier, new Vector2(0.1f, 3f)
            );

            Debug.Log($"[{MOD_NAME}] Config UI ready.");
        }

        private void OnConfigChanged(string key)
        {
            if (!key.StartsWith(MOD_NAME + "_")) return;
            LoadConfig();
            ApplyTweaks();
            //ApplyFontSize();
            Debug.Log($"[{MOD_NAME}] Config changed: {key}");
        }

        private void LoadConfig()
        {
            //config.FontSize = ModConfigAPI.SafeLoad<int>(MOD_NAME, "FontSize", config.FontSize);
            config.BulletDistanceMultiplier = ModConfigAPI.SafeLoad<float>(MOD_NAME, "BulletDistanceMultiplier", config.BulletDistanceMultiplier);
            config.AdsTimeMultiplier = ModConfigAPI.SafeLoad<float>(MOD_NAME, "AdsTimeMultiplier", config.AdsTimeMultiplier);
            config.ScatterFactorMultiplier = ModConfigAPI.SafeLoad<float>(MOD_NAME, "ScatterFactorMultiplier", config.ScatterFactorMultiplier);
        }

        private void ApplyTweaks()
        {
            // 通过反射拿到哈希（如果类型/字段不存在会抛异常）
            int distHash, adsHash, scatterHash;
            try
            {
                distHash = ReflectionHelper.GetStaticFieldValue<int>(typeof(ItemAgent_Gun), "BulletDistanceHash");
                adsHash = ReflectionHelper.GetStaticFieldValue<int>(typeof(ItemAgent_Gun), "AdsTimeHash");
                scatterHash = ReflectionHelper.GetStaticFieldValue<int>(typeof(ItemAgent_Gun), "ScatterFactorHashADS");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{MOD_NAME}] 反射读取哈希失败: {e.Message}");
                return;
            }

            foreach (int id in sniperIDs)
            {
                Item prefab = ItemAssetsCollection.GetPrefab(id);
                if (prefab == null)
                {
                    Debug.LogWarning($"[{MOD_NAME}] Missing item prefab: {id}");
                    continue;
                }

                try
                {
                    // 如果未缓存原始值，先读取并缓存（以便未来任何时候都能以原始值为基准）
                    if (!_originalValues.ContainsKey(id))
                    {
                        //float originalDist = prefab.Constants.GetFloat(distHash, 0f);
                        //float originalAds = prefab.Constants.GetFloat(adsHash, 0f);
                        //float originalScatter = prefab.Constants.GetFloat(scatterHash, 0f);
                        float originalDist = prefab.GetStat(distHash).BaseValue;
                        float originalAds = prefab.GetStat(adsHash).BaseValue;
                        float originalScatter = prefab.GetStat(scatterHash).BaseValue;


                        _originalValues[id] = (originalDist, originalAds, originalScatter);

                        Debug.Log($"[{MOD_NAME}] 缓存原始值 ID={id}: Dist={originalDist}, ADS={originalAds}, Scatter={originalScatter}");
                    }

                    var orig = _originalValues[id];

                    // 始终以缓存的原始值作为基准，乘以配置里的系数（避免重复叠加）
                    float newDist = orig.dist * config.BulletDistanceMultiplier;
                    float newAds = orig.ads * config.AdsTimeMultiplier;
                    float newScatter = orig.scatter * config.ScatterFactorMultiplier;

                    //prefab.Constants.SetFloat(distHash, newDist);
                    //prefab.Constants.SetFloat(adsHash, newAds);
                    //prefab.Constants.SetFloat(scatterHash, newScatter);
                    prefab.GetStat(distHash).BaseValue = newDist;
                    prefab.GetStat(adsHash).BaseValue = newAds;
                    prefab.GetStat(scatterHash).BaseValue = newScatter;

                    Debug.Log($"[{MOD_NAME}] ID={id} updated: Dist={orig.dist}->{newDist}, ADS={orig.ads}->{newAds}, Scatter={orig.scatter}->{newScatter}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{MOD_NAME}] Update failed for item {id}: {e.Message}");
                }
            }
        }

        //private void ApplyFontSize()
        //{
        //    try
        //    {
        //        // 只实例化一次文本，避免重复创建
        //        if (_text == null)
        //        {
        //            // 说明：TemplateTextUGUI 是一个现成的 TextMeshProUGUI 模板；根据你的 UI 层级可调整 parent
        //            var template = GameplayDataSettings.UIStyle.TemplateTextUGUI;
        //            if (template != null)
        //            {
        //                _text = Instantiate(template);
        //                _text.gameObject.SetActive(true);
        //                // 可选：设置父物体或位置，比如放到某个 UI 面板下
        //                // _text.transform.SetParent(someParentTransform, false);
        //            }
        //            else
        //            {
        //                Debug.LogWarning($"[{MOD_NAME}] 无法取得 UI 模板 (GameplayDataSettings.UIStyle.TemplateTextUGUI 为 null)。");
        //                return;
        //            }
        //        }

        //        _text.fontSize = config.FontSize;
        //        _text.text =
        //            $"<b>Sniper Tuning</b>\n" +
        //            $"Bullet Distance Multiplier: {config.BulletDistanceMultiplier:F2}\n" +
        //            $"ADS Time Multiplier: {config.AdsTimeMultiplier:F2}\n" +
        //            $"ADS Scatter Multiplier: {config.ScatterFactorMultiplier:F2}";

        //        Debug.Log($"[{MOD_NAME}] Font size & text applied: size={config.FontSize}");
        //    }
        //    catch (Exception e)
        //    {
        //        Debug.LogWarning($"[{MOD_NAME}] Font size apply failed: {e.Message}");
        //    }
        //}

        protected override void OnAfterSetup()
        {
            ApplyTweaks();
            //ApplyFontSize();
        }
    }
}