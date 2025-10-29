using Duckov.Modding;
using Duckov.Options;
using Duckov.Utilities;
using ItemStatsSystem;
using System;
using System.Collections.Generic;
using System.Linq;
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
        public bool Penetration = false;               // 穿墙
     
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
        public static ModBehaviour Instance { get; private set; }

        private SniperConfig config = new SniperConfig();

        // 狙击枪ID列表（按需修改）
        private readonly int[] sniperIDs = { 246, 407, 780, 781, 782 };

        // 缓存每个武器的原始参数（避免重复叠加）
        private Dictionary<int, (float dist, float ads, float scatter)> _originalValues
            = new Dictionary<int, (float dist, float ads, float scatter)>();

        private TextMeshProUGUI _text;

        // ===== 穿墙功能相关字段 =====
        private static bool _enablePenetrationDebugLog = true;
        
        private readonly HashSet<int> _penetratingProjectiles = new HashSet<int>();
        private readonly Dictionary<int, WeakReference<Projectile>> _penetratingProjectileRefs = new Dictionary<int, WeakReference<Projectile>>();
        private readonly Dictionary<int, LayerMask> _originalProjectileMasks = new Dictionary<int, LayerMask>();
        private readonly HashSet<int> _activeProjectileIds = new HashSet<int>();
        private static FieldInfo _projectileHitLayersField;
        private static FieldInfo _projectileContextField;
        private readonly List<Projectile> _activeProjectiles = new List<Projectile>(128);
        private ItemAgentHolder _trackedHolder;
        private ItemAgent_Gun _trackedGun;
        private bool _holderEventsHooked;
        private static readonly FieldInfo GunProjectileField = typeof(ItemAgent_Gun).GetField("projInst", BindingFlags.Instance | BindingFlags.NonPublic);
        private static List<string> AllowedWeaponNames = new List<string>();

        private static void Log(string message)
        {
            if (_enablePenetrationDebugLog)
            {
                Debug.Log("[自定义狙击枪穿墙组件] " + message);
            }
        }

        private static void LogWarning(string message)
        {
            if (_enablePenetrationDebugLog)
            {
                Debug.LogWarning("[自定义狙击枪穿墙组件] " + message);
            }
        }

        private static void LogError(string message)
        {
            if (_enablePenetrationDebugLog)
            {
                Debug.LogError("[自定义狙击枪穿墙组件] " + message);
            }
        }

        void Awake()
        {
            Instance = this;
            Log("初始化中...");
            _projectileHitLayersField = typeof(Projectile).GetField("hitLayers", BindingFlags.Instance | BindingFlags.NonPublic);
            _projectileContextField = typeof(Projectile).GetField("context", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (_projectileHitLayersField == null)
                LogWarning("未找到 Projectile.hitLayers 字段。");
            if (_projectileContextField == null)
                LogWarning("未找到 Projectile.context 字段。");

            foreach(int id in sniperIDs) 
            {
                Item prefab = ItemAssetsCollection.GetPrefab(id);

                if (!AllowedWeaponNames.Contains(prefab.DisplayName))
                {
                    AllowedWeaponNames.Add(prefab.DisplayName);
                    Log($"允许穿透的武器: {prefab.DisplayName} (ID={id})");
                }
                else { 
                
                }

            }
  
           
        }


        

        void OnEnable()
        {
            ModManager.OnModActivated += OnModActivated;
            
            LevelManager.OnAfterLevelInitialized += this.OnLevelInitialized;
            if (ModConfigAPI.IsAvailable())
            {
                SetupConfigUI();
                LoadConfig();
                ApplyTweaks();
          
            }
        }

        void OnDisable()
        {
            ModManager.OnModActivated -= OnModActivated;
  
            ModConfigAPI.SafeRemoveOnOptionsChangedDelegate(OnConfigChanged);

            // 清理穿墙功能
            LevelManager.OnAfterLevelInitialized -= this.OnLevelInitialized;

            UnhookAgentHolder();
            RestorePenetratingProjectiles();
            Log("已禁用并恢复所有子弹。");


            if (_text != null)
            {
                try { GameObject.Destroy(_text.gameObject); }
                catch { /* ignore */ }
                _text = null;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }
        private void OnLevelInitialized()
        {

            CharacterMainControl main = CharacterMainControl.Main;
            if (main != null)
            {
                HookAgentHolder(main.agentHolder);
                Log("已启用，等待射击事件...");
            }
            else
            {
                Log("main = null");
            }
        }


        // -------------------- 枪械挂钩逻辑 --------------------
        private void HookAgentHolder(ItemAgentHolder holder)
        {
            if (holder == null)
            {
                Log("holder == null");
                return;
            }
            if (_trackedHolder == holder && _holderEventsHooked)
            {
                Log("_trackedHolder == holder && _holderEventsHooked");
                return;
            }

            UnhookAgentHolder();
            _trackedHolder = holder;
            _trackedHolder.OnHoldAgentChanged += OnHoldAgentChanged;
            _holderEventsHooked = true;

            OnHoldAgentChanged(holder.CurrentHoldGun);
            Log("已挂钩角色持枪事件。");
        }

        private void UnhookAgentHolder()
        {
            if (_trackedHolder != null && _holderEventsHooked)
            {
                _trackedHolder.OnHoldAgentChanged -= OnHoldAgentChanged;
                _holderEventsHooked = false;
                _trackedHolder = null;
            }

            if (_trackedGun != null)
            {
                _trackedGun.OnShootEvent -= OnGunShoot;
                _trackedGun = null;
            }
        }

        private void OnHoldAgentChanged(DuckovItemAgent agent)
        {
            if (_trackedGun != null)
                _trackedGun.OnShootEvent -= OnGunShoot;

            _trackedGun = agent as ItemAgent_Gun;
            if (_trackedGun != null)
            {
                _trackedGun.OnShootEvent += OnGunShoot;
                Log($"当前持枪: {_trackedGun.Item.DisplayName}");
            }
        }

        private sealed class ProjectileTrackerMarker : MonoBehaviour
        {
  
        }

        private void RegisterActiveProjectile(Projectile projectile)
        {
            if (projectile == null)
            {
                return;
            }
            int instanceID = projectile.GetInstanceID();
            if (this._activeProjectileIds.Add(instanceID))
            {
                this._activeProjectiles.Add(projectile);
            }
        }

        // Token: 0x060000B7 RID: 183 RVA: 0x00009114 File Offset: 0x00007314
        private void UnregisterActiveProjectile(Projectile projectile)
        {
            if (projectile == null)
            {
                return;
            }
            int instanceID = projectile.GetInstanceID();
            if (this._activeProjectileIds.Remove(instanceID))
            {
                this._activeProjectiles.Remove(projectile);
            }
        }


        private sealed class ProjectileTracker : MonoBehaviour
        {
            // Token: 0x060000F0 RID: 240 RVA: 0x0000B4E8 File Offset: 0x000096E8
            private void Awake()
            {
                this._projectile = base.GetComponent<Projectile>();
            }

            // Token: 0x060000F1 RID: 241 RVA: 0x0000B4F8 File Offset: 0x000096F8
            private void OnEnable()
            {
                if (this._projectile == null)
                {
                    this._projectile = base.GetComponent<Projectile>();
                }
                ModBehaviour instance = ModBehaviour.Instance;
                if (instance == null || this._projectile == null)
                {
                    return;
                }
                instance.RegisterActiveProjectile(this._projectile);
            }

            // Token: 0x060000F2 RID: 242 RVA: 0x0000B54C File Offset: 0x0000974C
            private void OnDisable()
            {
                if (this._projectile == null)
                {
                    this._projectile = base.GetComponent<Projectile>();
                }
                ModBehaviour instance = ModBehaviour.Instance;
                if (instance == null || this._projectile == null)
                {
                    return;
                }
                instance.UnregisterActiveProjectile(this._projectile);
            }


            private Projectile _projectile;
        }

        // -------------------- 开火事件 --------------------
        private void OnGunShoot()
        {
  

   
            if (!config.Penetration || !AllowedWeaponNames.Contains(_trackedGun.Item.DisplayName))
            {
                return;
            }
 
            try
            {
                if (_trackedGun == null) return;
                Projectile projectile = CaptureImmediateProjectile(_trackedGun);
                if (projectile == null)
                {
                    LogWarning("未捕获到发射的Projectile。");
                    return;
                }

                Log($"捕获发射子弹: {projectile.name}, ID={projectile.GetInstanceID()}");
                ApplyPenetration(projectile);
            }
            catch (Exception e)
            {
                LogError("OnGunShoot 出错: " + e);
            }
        }

        // -------------------- 捕获当前发射的子弹 --------------------
        private Projectile CaptureImmediateProjectile(ItemAgent_Gun gun)
        {
            if (ModBehaviour.GunProjectileField == null)
            {
                return null;
            }
            Projectile result;
            try
            {
                Projectile projectile = ModBehaviour.GunProjectileField.GetValue(gun) as Projectile;
                if (projectile == null)
                {
                    result = null;
                }
                else
                {
                    this.EnsureProjectilePoolTracked(projectile);
                    this.EnsureProjectileTracker(projectile);
                    result = projectile;
                }
            }
            catch
            {
                result = null;
            }
            return result;
        }
        private void EnsureProjectilePoolTracked(Projectile projectile)
        {
            if (projectile == null)
            {
                return;
            }
            Transform parent = projectile.transform.parent;
            if (parent == null)
            {
                return;
            }
            if (parent.GetComponent<ModBehaviour.ProjectileTrackerMarker>() != null)
            {
                return;
            }
            parent.gameObject.AddComponent<ModBehaviour.ProjectileTrackerMarker>();
            Projectile[] componentsInChildren = parent.GetComponentsInChildren<Projectile>(true);
            for (int i = 0; i < componentsInChildren.Length; i++)
            {
                this.EnsureProjectileTracker(componentsInChildren[i]);
            }
        }
        private void EnsureProjectileTracker(Projectile projectile)
        {
            if (projectile == null)
            {
                return;
            }
            if (projectile.GetComponent<ModBehaviour.ProjectileTracker>() != null)
            {
                return;
            }
            projectile.gameObject.AddComponent<ModBehaviour.ProjectileTracker>();
        }


        // -------------------- 应用穿透 --------------------
        private void ApplyPenetration(Projectile projectile)
        {
            if (projectile == null || _projectileHitLayersField == null) return;
            int id = projectile.GetInstanceID();

            try
            {
                // 保存原始掩码
                if (!_originalProjectileMasks.ContainsKey(id))
                {
                    LayerMask mask = (LayerMask)_projectileHitLayersField.GetValue(projectile);
                    _originalProjectileMasks[id] = mask;
                }

                // 获取并修改 context
                object context = _projectileContextField?.GetValue(projectile);
                if (context != null)
                {
                    Type type = context.GetType();
                    FieldInfo ignoreHalf = type.GetField("ignoreHalfObsticle");
                    FieldInfo penetrate = type.GetField("penetrate");

                    if (ignoreHalf != null) ignoreHalf.SetValue(context, true);
                    if (penetrate != null)
                    {
                        int value = (int)penetrate.GetValue(context);
                        penetrate.SetValue(context, Mathf.Max(value, 6));
                    }
                }

                ConfigureProjectileHitMask(projectile);
                _penetratingProjectiles.Add(id);
                _penetratingProjectileRefs[id] = new WeakReference<Projectile>(projectile);

                Log($"子弹 {projectile.name} 已启用穿透。");
            }
            catch (Exception e)
            {
                LogError("ApplyPenetration 出错: " + e);
            }
        }

        // -------------------- 修改掩码 --------------------
        private void ConfigureProjectileHitMask(Projectile projectile)
        {
            try
            {
                int targetMask = GameplayDataSettings.Layers.damageReceiverLayerMask.value;
                int blockMask = GameplayDataSettings.Layers.wallLayerMask.value |
                                GameplayDataSettings.Layers.groundLayerMask.value |
                                GameplayDataSettings.Layers.halfObsticleLayer.value;
                targetMask &= ~blockMask;

                LayerMask newMask = new LayerMask { value = targetMask };
                _projectileHitLayersField?.SetValue(projectile, newMask);
                Log($"修改掩码为: {newMask.value}");
            }
            catch (Exception e)
            {
                LogWarning("设置掩码失败: " + e);
            }
        }

        // -------------------- 恢复逻辑 --------------------
        private void RestorePenetratingProjectiles()
        {
            Log("恢复所有子弹掩码...");
            foreach (int id in _penetratingProjectiles)
            {
                if (_penetratingProjectileRefs.TryGetValue(id, out var weakRef)
                    && weakRef.TryGetTarget(out var proj)
                    && proj != null)
                {
                    if (_originalProjectileMasks.TryGetValue(id, out LayerMask mask))
                    {
                        _projectileHitLayersField?.SetValue(proj, mask);

                        object ctx = _projectileContextField?.GetValue(proj);
                        if (ctx != null)
                        {
                            FieldInfo ignoreHalf = ctx.GetType().GetField("ignoreHalfObsticle");
                            if (ignoreHalf != null) ignoreHalf.SetValue(ctx, false);
                        }

                        Log($"恢复子弹 {proj.name} 掩码。");
                    }
                }
            }

            _penetratingProjectiles.Clear();
            _penetratingProjectileRefs.Clear();
            _originalProjectileMasks.Clear();
        }
    

        private void OnModActivated(ModInfo info, Duckov.Modding.ModBehaviour behaviour)
        {
            if (info.name == ModConfigAPI.ModConfigName)
            {
                SetupConfigUI();
                LoadConfig();
                ApplyTweaks();
         
            }
        }

        private void SetupConfigUI()
        {
            if (!ModConfigAPI.IsAvailable()) return;

            ModConfigAPI.SafeAddOnOptionsChangedDelegate(OnConfigChanged);

            bool zh = Application.systemLanguage.ToString().StartsWith("Chinese");

  

            // 子弹射程倍率（额外系数）
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "BulletDistanceMultiplier",
                zh ? "子弹射程倍率" : "Bullet Distance Multiplier",
                typeof(float), config.BulletDistanceMultiplier, new Vector2(0.5f, 3f)
            );

            // 开镜时间倍率
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "AdsTimeMultiplier",
                zh ? "开镜时间倍率" : "ADS Time Multiplier",
                typeof(float), config.AdsTimeMultiplier, new Vector2(0.2f, 2f)
            );

            // 开镜散射倍率
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "ScatterFactorMultiplier",
                zh ? "开镜散射倍率" : "ADS Scatter Multiplier",
                typeof(float), config.ScatterFactorMultiplier, new Vector2(0.2f, 2f)
            );

            // 穿墙次数
            //ModConfigAPI.SafeAddInputWithSlider(
            //    MOD_NAME, "Penetration",
            //    zh ? "穿墙" : "Wall Penetration",
            //    typeof(bool), config.Penetration, new Vector2(0, 10)
            //);
            //public static bool SafeAddBoolDropdownList(string modName, string key, string description, bool defaultValue)
            ModConfigAPI.SafeAddBoolDropdownList(
                MOD_NAME, "Penetration",
                zh ? "穿墙" : "Wall Penetration",
                config.Penetration);

            Debug.Log($"[{MOD_NAME}] Config UI ready with penetration control.");
        }

        private void OnConfigChanged(string key)
        {
            if (!key.StartsWith(MOD_NAME + "_")) return;
            LoadConfig();
            ApplyTweaks();
    
            Debug.Log($"[{MOD_NAME}] Config changed: {key}");
        }

        private void LoadConfig()
        {

            config.BulletDistanceMultiplier = ModConfigAPI.SafeLoad<float>(MOD_NAME, "BulletDistanceMultiplier", config.BulletDistanceMultiplier);
            config.AdsTimeMultiplier = ModConfigAPI.SafeLoad<float>(MOD_NAME, "AdsTimeMultiplier", config.AdsTimeMultiplier);
            config.ScatterFactorMultiplier = ModConfigAPI.SafeLoad<float>(MOD_NAME, "ScatterFactorMultiplier", config.ScatterFactorMultiplier);
            config.Penetration = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "Penetration", config.Penetration);
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
                    // 如果未缓存原始值，先读取并缓存
                    if (!_originalValues.ContainsKey(id))
                    {
                        float originalDist = prefab.GetStat(distHash).BaseValue;
                        float originalAds = prefab.GetStat(adsHash).BaseValue;
                        float originalScatter = prefab.GetStat(scatterHash).BaseValue;

                        _originalValues[id] = (originalDist, originalAds, originalScatter);

                        Debug.Log($"[{MOD_NAME}] 缓存原始值 ID={id}: Dist={originalDist}, ADS={originalAds}, Scatter={originalScatter}");
                    }

                    var orig = _originalValues[id];

                    // 始终以缓存的原始值作为基准，乘以配置里的系数
                    float newDist = orig.dist * config.BulletDistanceMultiplier;
                    float newAds = orig.ads * config.AdsTimeMultiplier;
                    float newScatter = orig.scatter * config.ScatterFactorMultiplier;

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


        protected override void OnAfterSetup()
        {
            ApplyTweaks();
  
        }



    }
}