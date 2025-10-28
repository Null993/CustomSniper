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
        public int PenetrationCount = 0;               // 穿墙次数（0=关闭，1-10=穿墙次数）
     
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
        private static ModBehaviour Instance;

        private SniperConfig config = new SniperConfig();

        // 狙击枪ID列表（按需修改）
        private readonly int[] sniperIDs = { 246, 407, 780, 781, 782 };

        // 缓存每个武器的原始参数（避免重复叠加）
        private Dictionary<int, (float dist, float ads, float scatter)> _originalValues
            = new Dictionary<int, (float dist, float ads, float scatter)>();

        private TextMeshProUGUI _text;

        // ===== 穿墙功能相关字段 =====
        private static readonly FieldInfo ProjectileHitLayersField =
            typeof(Projectile).GetField("hitLayers", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ProjectileDamagedObjectsField =
            typeof(Projectile).GetField("damagedObjects", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ProjectileDeadField =
            typeof(Projectile).GetField("dead", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly HashSet<int> _penetratingProjectiles = new HashSet<int>();
        private readonly Dictionary<int, Projectile> _penetratingProjectileRefs = new Dictionary<int, Projectile>();
        private readonly Dictionary<int, LayerMask> _originalProjectileMasks = new Dictionary<int, LayerMask>();
        private readonly List<Projectile> _activeProjectiles = new List<Projectile>(128);
        private readonly HashSet<int> _activeProjectileIds = new HashSet<int>();

        private CharacterMainControl _trackedCharacter;
        private ItemAgent_Gun _trackedGun;
        private bool _penetrationActive = false;

        void Awake()
        {
            Instance = this;
        }

        void OnEnable()
        {
            ModManager.OnModActivated += OnModActivated;
            LevelManager.OnAfterLevelInitialized += OnLevelInitialized;

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
            LevelManager.OnAfterLevelInitialized -= OnLevelInitialized;
            ModConfigAPI.SafeRemoveOnOptionsChangedDelegate(OnConfigChanged);

            // 清理穿墙功能
            SetPenetrationActive(false);
            UnhookCharacterEvents();
            RestorePenetratingProjectiles();

            _activeProjectiles.Clear();
            _activeProjectileIds.Clear();
            _penetratingProjectiles.Clear();
            _penetratingProjectileRefs.Clear();
            _originalProjectileMasks.Clear();

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

        void Update()
        {
            // 确保角色追踪
            EnsureCharacterTracking();

            // 维护穿透弹药
            if (config.PenetrationCount > 0 && _penetrationActive)
            {
                MaintainPenetratingProjectiles();
            }
        }

        private void OnLevelInitialized()
        {
            TryHookCharacter();
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
                typeof(float), config.AdsTimeMultiplier, new Vector2(0.5f, 2f)
            );

            // 开镜散射倍率
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "ScatterFactorMultiplier",
                zh ? "开镜散射倍率" : "ADS Scatter Multiplier",
                typeof(float), config.ScatterFactorMultiplier, new Vector2(0.5f, 2f)
            );

            // 穿墙次数
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME, "PenetrationCount",
                zh ? "穿墙次数" : "Wall Penetration Count",
                typeof(int), config.PenetrationCount, new Vector2(0, 10)
            );

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
            config.PenetrationCount = ModConfigAPI.SafeLoad<int>(MOD_NAME, "PenetrationCount", config.PenetrationCount);
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

        // ===== 穿墙功能实现 =====

        private void EnsureCharacterTracking()
        {
            CharacterMainControl main = CharacterMainControl.Main;
            if (main == null)
            {
                if (_trackedCharacter != null)
                {
                    UnhookCharacterEvents();
                }
                return;
            }

            if (_trackedCharacter != main)
            {
                TryHookCharacter();
            }
        }

        private void TryHookCharacter()
        {
            CharacterMainControl main = CharacterMainControl.Main;
            if (main == null) return;

            if (_trackedCharacter != main)
            {
                UnhookCharacterEvents();
                _trackedCharacter = main;
            }

            HookGunEvents(main);
        }

        private void HookGunEvents(CharacterMainControl character)
        {
            if (character == null || character.agentHolder == null) return;

            ItemAgent_Gun gun = character.agentHolder.CurrentHoldGun;
            if (gun != _trackedGun)
            {
                if (_trackedGun != null)
                {
                    _trackedGun.OnShootEvent -= OnGunShoot;
                }

                _trackedGun = gun;

                if (_trackedGun != null)
                {
                    _trackedGun.OnShootEvent += OnGunShoot;
                }
            }
        }

        private void UnhookCharacterEvents()
        {
            if (_trackedGun != null)
            {
                _trackedGun.OnShootEvent -= OnGunShoot;
            }
            _trackedGun = null;
            _trackedCharacter = null;
        }

        private void OnGunShoot()
        {
            if (config.PenetrationCount <= 0 || _trackedGun == null) return;

            // 检查是否是狙击枪
            if (_trackedGun.Item != null)
            {
                bool isSniper = Array.Exists(sniperIDs, id => id == _trackedGun.Item.TypeID);
                if (!isSniper) return;
            }

            bool hasPenetratingProjectiles = _penetratingProjectiles.Count > 0;
            SetPenetrationActive(hasPenetratingProjectiles || config.PenetrationCount > 0);

            if (_penetrationActive)
            {
                EnsureShotPenetration(_trackedCharacter);
            }
        }

        private void EnsureShotPenetration(CharacterMainControl shooter)
        {
            if (config.PenetrationCount <= 0 || !_penetrationActive) return;
            if (shooter == null || _activeProjectiles.Count == 0) return;

            for (int i = 0; i < _activeProjectiles.Count; i++)
            {
                Projectile projectile = _activeProjectiles[i];
                if (projectile != null && projectile.context.fromCharacter == shooter)
                {
                    TryApplyObstaclePenetration(projectile);
                }
            }
        }

        private void TryApplyObstaclePenetration(Projectile projectile)
        {
            if (config.PenetrationCount <= 0 || !_penetrationActive) return;

            try
            {
                int instanceID = projectile.GetInstanceID();
                bool isAlreadyPenetrating = _penetratingProjectiles.Contains(instanceID);

                if (config.PenetrationCount > 0 || isAlreadyPenetrating)
                {
                    bool isExplosive = IsExplosiveProjectile(projectile);
                    LayerMask? originalMask = null;

                    // 保存原始遮罩
                    if (ProjectileHitLayersField != null && !_originalProjectileMasks.ContainsKey(instanceID))
                    {
                        object value = ProjectileHitLayersField.GetValue(projectile);
                        if (value is LayerMask)
                        {
                            originalMask = (LayerMask)value;
                        }
                    }

                    // 设置穿透属性
                    projectile.context.ignoreHalfObsticle = true;

                    if (isExplosive)
                    {
                        projectile.context.penetrate = Math.Min(projectile.context.penetrate, 0);
                    }
                    else
                    {
                        projectile.context.penetrate = Math.Max(projectile.context.penetrate, config.PenetrationCount);
                    }

                    ConfigureProjectileHitMask(projectile);
                    RegisterPenetratingProjectile(projectile, originalMask);
                    EnsureDamagedObjectsInitialized(projectile);

                    if (isExplosive)
                    {
                        ClampExplosiveProjectileState(projectile);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{MOD_NAME}] 应用穿透失败: {ex.Message}");
            }
        }

        private void ConfigureProjectileHitMask(Projectile projectile)
        {
            if (ProjectileHitLayersField == null) return;

            try
            {
                int mask = GameplayDataSettings.Layers.damageReceiverLayerMask.value;
                int obstacleMask = GameplayDataSettings.Layers.wallLayerMask.value |
                                   GameplayDataSettings.Layers.groundLayerMask.value |
                                   GameplayDataSettings.Layers.halfObsticleLayer.value;

                mask &= ~obstacleMask;

                if (mask == 0)
                {
                    int damageReceiverLayer = LayerMask.NameToLayer("DamageReceiver");
                    if (damageReceiverLayer >= 0) mask |= 1 << damageReceiverLayer;

                    int headColliderLayer = LayerMask.NameToLayer("HeadCollider");
                    if (headColliderLayer >= 0) mask |= 1 << headColliderLayer;
                }

                LayerMask layerMask = new LayerMask { value = mask };
                ProjectileHitLayersField.SetValue(projectile, layerMask);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{MOD_NAME}] 配置遮罩失败: {ex.Message}");
            }
        }

        private static bool IsExplosiveProjectile(Projectile projectile)
        {
            if (projectile == null) return false;

            ProjectileContext context = projectile.context;
            if (context.explosionRange > 0.01f || context.explosionDamage > 0.01f)
                return true;

            string name = projectile.name;
            return !string.IsNullOrEmpty(name) &&
                   name.IndexOf("rocket", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ClampExplosiveProjectileState(Projectile projectile)
        {
            if (projectile != null && projectile.context.penetrate > 0)
            {
                projectile.context.penetrate = 0;
            }
        }

        private void RegisterPenetratingProjectile(Projectile projectile, LayerMask? originalMask)
        {
            if (projectile == null) return;

            int instanceID = projectile.GetInstanceID();
            _penetratingProjectiles.Add(instanceID);
            _penetratingProjectileRefs[instanceID] = projectile;

            if (originalMask.HasValue && !_originalProjectileMasks.ContainsKey(instanceID))
            {
                _originalProjectileMasks[instanceID] = originalMask.Value;
            }
        }

        private void EnsureDamagedObjectsInitialized(Projectile projectile)
        {
            if (ProjectileDamagedObjectsField == null) return;

            try
            {
                var list = ProjectileDamagedObjectsField.GetValue(projectile) as List<GameObject>;
                if (list != null)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        GameObject obj = list[i];
                        if (obj == null || obj.GetComponent<DamageReceiver>() == null)
                        {
                            list.RemoveAt(i);
                        }
                    }
                }
            }
            catch { }
        }

        private void MaintainPenetratingProjectiles()
        {
            if (_penetratingProjectiles.Count == 0) return;

            foreach (int id in _penetratingProjectiles.ToArray())
            {
                if (!_penetratingProjectileRefs.TryGetValue(id, out Projectile projectile) ||
                    !IsProjectileActive(projectile))
                {
                    _penetratingProjectiles.Remove(id);
                    _penetratingProjectileRefs.Remove(id);
                    _originalProjectileMasks.Remove(id);
                }
            }
        }

        private void SetPenetrationActive(bool active)
        {
            if (_penetrationActive == active) return;

            _penetrationActive = active;

            if (!active)
            {
                RestorePenetratingProjectiles();
            }
        }

        private void RestorePenetratingProjectiles()
        {
            if (_penetratingProjectiles.Count == 0)
            {
                _penetratingProjectileRefs.Clear();
                _originalProjectileMasks.Clear();
                return;
            }

            foreach (int id in _penetratingProjectiles.ToArray())
            {
                if (!_penetratingProjectileRefs.TryGetValue(id, out Projectile projectile) ||
                    projectile == null)
                {
                    _penetratingProjectiles.Remove(id);
                    _penetratingProjectileRefs.Remove(id);
                    _originalProjectileMasks.Remove(id);
                }
                else
                {
                    try
                    {
                        if (_originalProjectileMasks.TryGetValue(id, out LayerMask mask) &&
                            ProjectileHitLayersField != null)
                        {
                            ProjectileHitLayersField.SetValue(projectile, mask);
                        }
                        projectile.context.ignoreHalfObsticle = false;
                    }
                    catch { }
                }
            }

            _penetratingProjectiles.Clear();
            _originalProjectileMasks.Clear();
            _penetratingProjectileRefs.Clear();
        }

        private static bool IsProjectileActive(Projectile projectile)
        {
            if (projectile == null) return false;
            if (projectile.gameObject == null || !projectile.gameObject.activeInHierarchy)
                return false;

            if (ProjectileDeadField != null)
            {
                try
                {
                    if ((bool)ProjectileDeadField.GetValue(projectile))
                        return false;
                }
                catch { }
            }

            return true;
        }

        public void RegisterActiveProjectile(Projectile projectile)
        {
            if (projectile == null) return;

            int id = projectile.GetInstanceID();
            if (_activeProjectileIds.Add(id))
            {
                _activeProjectiles.Add(projectile);
            }
        }

        public void UnregisterActiveProjectile(Projectile projectile)
        {
            if (projectile == null) return;

            int id = projectile.GetInstanceID();
            if (_activeProjectileIds.Remove(id))
            {
                _activeProjectiles.Remove(projectile);
            }
        }

        // ProjectileTracker 组件
        public class ProjectileTracker : MonoBehaviour
        {
            private Projectile _projectile;

            private void Awake()
            {
                _projectile = GetComponent<Projectile>();
            }

            private void OnEnable()
            {
                if (_projectile == null)
                    _projectile = GetComponent<Projectile>();

                ModBehaviour instance = Instance;
                if (instance != null && _projectile != null)
                {
                    instance.RegisterActiveProjectile(_projectile);
                }
            }

            private void OnDisable()
            {
                if (_projectile == null)
                    _projectile = GetComponent<Projectile>();

                ModBehaviour instance = Instance;
                if (instance != null && _projectile != null)
                {
                    instance.UnregisterActiveProjectile(_projectile);
                }
            }
        }
    }
}