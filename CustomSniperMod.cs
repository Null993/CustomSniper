using Duckov.Modding;
using Duckov.Options;
using Duckov.Utilities;

using ItemStatsSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using Debug = UnityEngine.Debug;
using HarmonyLib;
namespace CustomSniper
{
    [Serializable]
    public class SniperConfig
    {
        public float BulletDistanceMultiplier = 1.0f;  // 射程倍率（额外系数：基于原始值）
        public float AdsTimeMultiplier = 1.0f;         // 开镜时间倍率
        public float ScatterFactorMultiplier = 1.0f;   // 开镜散射倍率
        public bool Penetration = false;               // 穿墙
        public string BL_SniperIDs = "";
        public string WL_SniperIDs = ""; // 白名单ID列表

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

        private static readonly string MOD_NAME = "CustomSniper";
        public static ModBehaviour Instance { get; private set; }

        private SniperConfig config = new SniperConfig();

        // 狙击枪ID列表（按需修改）
        //private int[] sniperIDs = { 246, 407, 780, 781, 782 };
        private HashSet<int> sniperIDs = new HashSet<int>();
        private HashSet<int> bl_sniperIDs = new HashSet<int>();
        private HashSet<int> wl_sniperIDs = new HashSet<int>();
        private HashSet<int> real_sniperIDs = new HashSet<int>();

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
        //private static HashSet<string> AllowedWeaponNames = new HashSet<string>();
        private static bool _configRegistered = false;
        private static List<string> targetCarlibers = new List<string> { "SNP", "MAG" };
        private static int distHash;
        private static int adsHash;
        private static int scatterHash;




        public static bool EvaluateFilter(ItemMetaData metaData, ItemFilter filter)
        {

            // 口径匹配
            if (!string.IsNullOrEmpty(filter.caliber))
            {
                if (!string.Equals(metaData.caliber, filter.caliber, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }


        public static int[] GetAllTypeIds(ItemFilter filter)
        {
            if (ItemAssetsCollection.Instance == null)
                return null;

            IEnumerable<int> collection = from e in ItemAssetsCollection.Instance.entries
                                          where EvaluateFilter(e.metaData, filter)
                                          select e.typeID;

            var traverse = Traverse.Create(ItemAssetsCollection.Instance);
            var dynamicDic = traverse.Field("dynamicDic").GetValue<IDictionary<int, ItemAssetsCollection.DynamicEntry>>();

            IEnumerable<int> range = from e in dynamicDic
                                     where e.Value?.prefab != null && EvaluateFilter(e.Value.MetaData, filter)
                                     select e.Key;

            HashSet<int> hashSet = new HashSet<int>(collection);
            hashSet.UnionWith(range);
            return hashSet.ToArray();
        }
        public static void Log(string message)
        {
            if (_enablePenetrationDebugLog)
            {
                Debug.Log("[自定义狙击枪穿墙组件] " + message);
            }
        }

        public static void LogWarning(string message)
        {
            if (_enablePenetrationDebugLog)
            {
                Debug.LogWarning("[自定义狙击枪穿墙组件] " + message);
            }
        }

        public static void LogError(string message)
        {
            if (_enablePenetrationDebugLog)
            {
                Debug.LogError("[自定义狙击枪穿墙组件] " + message);
            }
        }


        public static string GetCaliber(Item item)
        {
            if (item == null)
            {
                return null;
            }
            CustomDataCollection constants = item.Constants;
            if (constants == null)
            {
                return null;
            }
            return constants.GetString("Caliber".GetHashCode(), null);
        }

        public Item find_gun(int id)
        {
            Item prefab = ItemAssetsCollection.GetPrefab(id);
            if (prefab == null)
            {
                var traverse = Traverse.Create(ItemAssetsCollection.Instance);
                var dynamicDic = traverse.Field("dynamicDic").GetValue<IDictionary<int, ItemAssetsCollection.DynamicEntry>>();

                if (dynamicDic.TryGetValue(id, out var entry))
                {
                    ItemMetaData metaData = Traverse.Create(entry).Field("MetaData").GetValue<ItemMetaData>();

                    if (entry.prefab == null)
                    {
                        Log($"entry.prefab == null");
                    }
                    else
                    {
                        prefab = entry.prefab;
                    }
                }
            }
            return prefab;
        }
        public void updata_weapons(int distHash, int adsHash, int scatterHash)
        {

            foreach (int id in real_sniperIDs)
            {
                Item prefab = find_gun(id);
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
        public void updata_Real_SniperIDs(HashSet<int> sniperIDs, HashSet<int> bl_sniperIDs, HashSet<int> wl_sniperIDs)
        {
            real_sniperIDs = new HashSet<int>(sniperIDs.Except(bl_sniperIDs));
            // 添加白名单中的武器（无视类型和黑名单）
            foreach (int id in wl_sniperIDs)
            {
                if (!real_sniperIDs.Contains(id))
                {
                    real_sniperIDs.Add(id);
                    Log($"[白名单] 强制允许武器 ID={id}");
                }
            }
            foreach (int id in real_sniperIDs)
            {
                Item prefab = find_gun(id);
                if (prefab == null)
                {
                    LogWarning($"[RealSniper] 找不到ID={id}的Prefab");
                    continue;
                }
                CustomDataCollection constants = prefab.Constants;
                Log($"{prefab.DisplayName}:{constants.GetString("Caliber".GetHashCode(), null)}");

                if (!real_sniperIDs.Contains(prefab.TypeID))
                {

                    real_sniperIDs.Add(prefab.TypeID);
                    Log($"新增允许穿透的武器: {prefab.DisplayName} (ID={id})");
                }
      

            }
        }

        private void Awake()
        {
            Log("初始化中...");
            Instance = this;
            
            _projectileHitLayersField = typeof(Projectile).GetField("hitLayers", BindingFlags.Instance | BindingFlags.NonPublic);
            _projectileContextField = typeof(Projectile).GetField("context", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (_projectileHitLayersField == null)
                LogWarning("未找到 Projectile.hitLayers 字段。");
            if (_projectileContextField == null)
                LogWarning("未找到 Projectile.context 字段。");



            //// 构建CSV
            //StringBuilder sb = new StringBuilder();
            //sb.AppendLine("ID,名称,口径");

            //foreach (int id in tempids)
            //{
            //    string carliber = GetCaliber(ItemAssetsCollection.GetPrefab(id));
            //    Debug.Log($"[{id}] {ItemAssetsCollection.GetPrefab(id).DisplayName}: {carliber}");

            //    //string name = .Replace(",", "，");
            //    //string caliber = carliber.Replace(",", "，");

            //    //sb.AppendLine($"{id},{ItemAssetsCollection.GetPrefab(id).DisplayName},{carliber}");
            //}
            //// 导出为 CSV
            //string savePath = Path.Combine(Application.persistentDataPath, "WeaponList.csv");
            //File.WriteAllText(savePath, sb.ToString(), new UTF8Encoding(true));

            // 通过反射拿到哈希（如果类型/字段不存在会抛异常）

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

            foreach (string targetCaliber in targetCarlibers)
            {
                try
                {
                    //Log("调试点1");

                    ItemFilter filter = new ItemFilter();
                    filter.caliber = targetCaliber;




                    // 获取所有匹配类型 id
                    int[] tempids = GetAllTypeIds(filter);
                   
                    
                    if (tempids == null)
                    {
                        LogWarning($"GetAllTypeIds returned null for caliber {targetCaliber}");
                        continue;
                    }
                    foreach (int id in tempids)

                    {
                        try
                        {
                            var prefab = ItemAssetsCollection.GetPrefab(id);
                            if (prefab == null)
                            {
                                var traverse = Traverse.Create(ItemAssetsCollection.Instance);
                                var dynamicDic = traverse.Field("dynamicDic").GetValue<IDictionary<int, ItemAssetsCollection.DynamicEntry>>();

                                if (dynamicDic.TryGetValue(id, out var entry))
                                {
                                    ItemMetaData metaData = Traverse.Create(entry).Field("MetaData").GetValue<ItemMetaData>();
     
                                    if (entry.prefab == null)
                                    {
                                        Log($"entry.prefab == null");
                                    }
                                    else {
                                        prefab = entry.prefab;
                                        Log($"发现可能不是原版的武器: {entry.prefab.DisplayName}");                                    
                                    }
                        
                                }
                            }
     

                            if (prefab == null)
                            {
                                LogWarning($"ItemAssetsCollection.GetPrefab({id}) 返回 null");
                                continue;
                            }

                            if (prefab.Tags == null)
                            {
                                LogWarning($"Prefab [{id}] {prefab.DisplayName} 的 Tags 为 null");
                                continue;
                            }

                            
                            
                            bool _isgun = false;
                            foreach (Tag tag in prefab.Tags)
                            {
                                try
                                {
                                    if (tag == null)
                                    {
                                        LogWarning($"Prefab [{id}] {prefab.DisplayName} 的某个 tag 为 null");
                                        continue;
                                    }

                                    
                                    if (prefab.Tags.Contains("Gun"))
                                    {
                                        _isgun = true;
                                        sniperIDs.Add(id);
                                    }
                                }
                                catch (Exception e)
                                {
                                    LogWarning($"遍历 tag 时出错 id={id}, tag={tag?.ToString() ?? "null"}: {e.Message}");
                                }
                            }
                            if (_isgun)
                            {
                                Log($"Find Gun: [{id}] {prefab.DisplayName}");
                            }
                            

                        }
                        catch (Exception e)
                        {
                            LogWarning($"遍历 prefab id={id} 出错: {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    LogWarning($"处理 caliber={targetCaliber} 时出错: {e.Message}\n{e.StackTrace}");
                }


            }


        }




        private void OnEnable()
        {
            ModManager.OnModActivated += OnModActivated;

            LevelManager.OnAfterLevelInitialized += this.OnLevelInitialized;
            //if (ModConfigAPI.IsAvailable())
            //{
            //    SetupConfigUI();
            //    LoadConfig();
            //    ApplyTweaks();

            //}
        }

        protected override void OnAfterSetup()
        {
            if (ModConfigAPI.IsAvailable())
            {
                SetupConfigUI();
                LoadConfig();
                ApplyTweaks();

            }
        }


        private void OnDisable()
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



            if (!config.Penetration || !real_sniperIDs.Contains(_trackedGun.Item.TypeID))
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
            if (!ModConfigAPI.IsAvailable() || _configRegistered) return;

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
            // 武器黑名单（逗号分隔ID）
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME,
                "BlacklistIDs",
                zh ? "武器黑名单（英文逗号分隔ID）" : "Weapon Blacklist (comma-separated IDs)",
                typeof(string),
                "" // 默认值为空
            );
            // 武器白名单（逗号分隔ID）
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME,
                "WhitelistIDs",
                zh ? "武器白名单（英文逗号分隔ID）" : "Weapon Whitelist (comma-separated IDs)",
                typeof(string),
                "" // 默认值为空
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

            _configRegistered = true;
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
            config.BL_SniperIDs = ModConfigAPI.SafeLoad<string>(MOD_NAME, "BlacklistIDs", config.BL_SniperIDs);
            config.WL_SniperIDs = ModConfigAPI.SafeLoad<string>(MOD_NAME, "WhitelistIDs", config.WL_SniperIDs);


            // ✅ 读取黑名单并解析
            bl_sniperIDs.Clear();

            if (!string.IsNullOrWhiteSpace(config.BL_SniperIDs))
            {
                string[] parts = config.BL_SniperIDs.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string p in parts)
                {
                    if (int.TryParse(p.Trim(), out int id))
                        bl_sniperIDs.Add(id);
                }
                Log($"已加载黑名单，共 {bl_sniperIDs.Count} 个ID: {string.Join(", ", bl_sniperIDs)}");
            }
            else
            {
                Log("未配置武器黑名单。");
            }
            // ✅ 白名单加载
            wl_sniperIDs.Clear();
            if (!string.IsNullOrWhiteSpace(config.WL_SniperIDs))
            {
                string[] parts = config.WL_SniperIDs.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string p in parts)
                {
                    if (int.TryParse(p.Trim(), out int id))
                        wl_sniperIDs.Add(id);
                }
                Log($"已加载白名单，共 {wl_sniperIDs.Count} 个ID: {string.Join(", ", wl_sniperIDs)}");
            }
            else
            {
                Log("未配置武器白名单。");
            }

            updata_Real_SniperIDs(sniperIDs,bl_sniperIDs,wl_sniperIDs);
            updata_weapons(distHash, adsHash, scatterHash);
        }

        private void ApplyTweaks()
        {
            updata_weapons(distHash,adsHash ,scatterHash);
        }

    }

}
