using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NekoFishpower
{
    /// <summary>
    /// 渔力全开（How to Fish）遥测 mod：hook 游戏事件 → TCP 9877 推给插件。
    /// 只回报事件/状态，不注入任何输入（AI 控制是后续阶段）。
    /// 游戏序列化字段多为 private，统一走 Harmony Traverse 读取。
    /// </summary>
    [BepInPlugin("neko_fishpower", "Neko Fishpower Telemetry", "0.1.0")]
    public class FishPowerMod : BaseUnityPlugin
    {
        internal static FishPowerMod Instance;
        internal static ManualLogSource Log;

        private TelemetryServer _server;
        private Harmony _harmony;
        private float _lastStatePush = 0f;
        private string _lastPhase = "";
        private bool _lastBite = false;
        private float _lastAchCheck = 0f;
        private readonly Dictionary<string, bool> _achState = new Dictionary<string, bool>();
        // 已发现鱼种集合：caught 事件 + OnInspectedCreature 累计（图鉴计数数据源）
        private readonly HashSet<string> _discoveredFish = new HashSet<string>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _server = new TelemetryServer(9877);
            _server.OnClientConnected = () => { _registryResendPending = true; };
            _server.Start();
            _harmony = new Harmony("neko_fishpower");
            _harmony.PatchAll();
            Logger.LogInfo("Neko Fishpower mod loaded, telemetry on :9877");
            // 注册表延迟推送：GameInfo._allCreatures 要等场景加载后才填充
            StartCoroutine(PushRegistryLater());
        }

        // 新客户端连接 → 主线程重推 registry（含重试，场景可能未加载完）
        private volatile bool _registryResendPending = false;
        private void ResendRegistryIfPending()
        {
            if (_registryResendPending)
            {
                _registryResendPending = false;
                StartCoroutine(PushRegistryLater());
            }
        }

        private System.Collections.IEnumerator PushRegistryLater()
        {
            for (int i = 0; i < 10; i++)
            {
                yield return new WaitForSeconds(3f);
                int n = PushRegistry();
                if (n > 0) yield break;
            }
            Log.LogWarning("registry: 多次尝试仍为空（图鉴数据可能延迟）");
        }

        private void Update()
        {
            ResendRegistryIfPending();
            if (Time.time - _lastStatePush >= 1f)
            {
                _lastStatePush = Time.time;
                _server.PushState(BuildState());
            }
            DetectFishingPhase();
            if (Time.time - _lastAchCheck >= 2f)
            {
                _lastAchCheck = Time.time;
                DetectAchievements();
            }
        }

        private void OnDestroy()
        {
            try { _server?.Stop(); } catch { }
            _harmony?.UnpatchSelf();
        }

        // ── 上钩 hook：FishingUI.OnNewFishCaught(Creature) ──
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FishingUI), "OnNewFishCaught")]
        private static void OnCaughtPostfix(Creature __0)
        {
            try { Instance?.HandleCaught(__0); } catch (Exception e) { Log?.LogWarning($"caught hook: {e.Message}"); }
        }

        // ── 抛竿 hook：PrimaryInput（抛竿/提竿共用）→ 非收线中按=抛竿 ──
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FishingRod), "PrimaryInput")]
        private static void OnCastPostfix()
        {
            try
            {
                if (Instance == null || Instance._lastPhase == "reeling") return;  // 收线中按=提竿
                var bait = Instance?.CurrentBaitName() ?? "";
                Instance?._server.PushEvent(new Dictionary<string, object> { ["event"] = "cast", ["bait"] = bait });
            }
            catch { }
        }

        // ── 图鉴发现 hook：Creature.OnInspectedCreature（查看/发现新鱼）→ discovered ──
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Creature), "OnInspectedCreature")]
        private static void OnInspectedPostfix(Creature __0)
        {
            try
            {
                var d = new Dictionary<string, object> { ["event"] = "discovered" };
                if (__0 != null)
                {
                    string fname = __0.GetName();
                    if (!string.IsNullOrEmpty(fname))
                    {
                        Instance._discoveredFish.Add(fname);
                        d["fish"] = fname;
                        d["new"] = true;
                    }
                }
                d["count"] = Instance?._discoveredFish.Count ?? 0;
                d["total"] = Instance?.RegistryCreatureCount() ?? 0;
                Instance?._server.PushEvent(d);
            }
            catch { }
        }

        // ── 玩家死亡 hook：PlayerManager.OnPlayerDied → player_death（大事件互动） ──
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerManager), "OnPlayerDied")]
        private static void OnPlayerDiedPostfix()
        {
            try
            {
                Instance?._server.PushEvent(new Dictionary<string, object> { ["event"] = "player_death" });
            }
            catch { }
        }

        private string CurrentBaitName()
        {
            try
            {
                var p = Player.LocalPlayer;
                if (p == null) return "";
                var inv = Get<PlayerInventory>(Traverse.Create(p).Field("_inventory"), null);
                if (inv == null) return "";
                var bait = Get<BaitInfo>(Traverse.Create(inv).Field("_baitSlot"), null);
                return bait != null ? Get<string>(Traverse.Create(bait), "_name") ?? "" : "";
            }
            catch { return ""; }
        }

        // ── 游戏成就检测：反射轮询 AchievementManager 静态解锁字段 ──
        private void DetectAchievements()
        {
            try
            {
                var t = typeof(AchievementManager);
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(bool)) continue;
                    if (!f.Name.Contains("Unlocked") && !f.Name.Contains("Achievement")
                        && !f.Name.EndsWith("ed") && !f.Name.StartsWith("_has")) continue;
                    bool val;
                    try { val = (bool)f.GetValue(null); } catch { continue; }
                    if (!_achState.TryGetValue(f.Name, out bool prev))
                    {
                        _achState[f.Name] = val;
                        continue;
                    }
                    if (val && !prev)
                    {
                        _achState[f.Name] = val;
                        _server.PushEvent(new Dictionary<string, object>
                        {
                            ["event"] = "achievement",
                            ["key"] = f.Name,
                        });
                        Log.LogInfo($"achievement unlocked: {f.Name}");
                    }
                }
            }
            catch { }
        }

        private void HandleCaught(Creature c)
        {
            if (c == null) return;
            var t = Traverse.Create(c);
            string fname = c.GetName();
            bool isNew = !string.IsNullOrEmpty(fname) && _discoveredFish.Add(fname);
            var d = new Dictionary<string, object>
            {
                ["event"] = "caught",
                ["fish"] = fname,
                ["new"] = isNew,
                ["type"] = c.GetType().Name,
                ["weight"] = (double)Get<float>(t, "_syncedRandomWeight"),
                ["worth"] = Get<int>(t, "_worth"),
                ["drip"] = Get<bool>(t, "_isDrip"),
                ["endangered"] = Get<bool>(t, "_isEndangered"),
                ["boss"] = Get<string>(t, "_bossType"),
            };
            try
            {
                var skinIdx = Get<byte>(t, "_curSkin");
                var preset = Get<SkinPreset>(t, "_skinPreset");
                var skins = preset != null ? Traverse.Create(preset).Field("_skins").GetValue<List<ItemSkin>>() : null;
                if (skins != null && skinIdx < skins.Count)
                {
                    var skin = skins[skinIdx];
                    var skinT = Traverse.Create(skin);
                    d["rarity"] = Get<string>(skinT, "_rarity");
                    d["shiny"] = Get<bool>(skinT, "_isRainbowSkin");
                }
            }
            catch { }
            _server.PushEvent(d);
        }

        // ── 抛竿/咬钩检测（轮询 FishingRod 状态） ──
        private float _lastBitePush = 0f;
        private const float BITE_MIN_INTERVAL = 60f;  // 60s 内只推一次咬钩：连续钓鱼时咬钩是常态，不每竿都喊
        private const int BITE_DEBOUNCE_FRAMES = 5;   // 杆弯连续 5 帧才算咬钩（防抖动）
        private int _bendFrames = 0;

        private void DetectFishingPhase()
        {
            var player = Player.LocalPlayer;
            if (player == null) return;
            var held = Get<Item>(Traverse.Create(player).Field("_holding"), "_heldItem");
            var rod = held != null ? Get<FishingRod>(Traverse.Create(held), "_fishingRod") : null;
            if (rod == null)
            {
                _lastPhase = "idle";
                return;
            }
            var rodT = Traverse.Create(rod);
            bool reeling = Get<bool>(rodT, "_isReelingIn") || Get<bool>(rodT, "_isReelingOut");
            _lastPhase = reeling ? "reeling" : "idle";
            // 杆弯去抖：_curRodBendForce 是连续模拟量，瞬时超阈值会抖动；
            // 连续 BITE_DEBOUNCE_FRAMES 帧超阈值才算咬钩
            bool rodBent = Get<float>(rodT, "_curRodBendForce") > 0.5f;
            _bendFrames = rodBent ? _bendFrames + 1 : 0;
            bool biting = rodBent && _bendFrames >= BITE_DEBOUNCE_FRAMES;
            if (biting && !_lastBite && Time.time - _lastBitePush >= BITE_MIN_INTERVAL)
            {
                _lastBite = true;
                _lastBitePush = Time.time;
                _server.PushEvent(new Dictionary<string, object> { ["event"] = "bite" });
            }
            else if (!biting && _lastBite)
            {
                _lastBite = false;
            }
        }

        // ── 状态快照 ──
        private Dictionary<string, object> BuildState()
        {
            var s = new Dictionary<string, object> { ["type"] = "state" };
            try
            {
                var p = Player.LocalPlayer;
                s["phase"] = _lastPhase;
                s["connected"] = p != null;
                if (p == null) return s;

                // 岛屿：Island.CurIsland.name 是类型名（IslandManager），
                // 真实岛名取 IslandManager._islandInfos[_curIsland]._spawnPosition.name
                var island = Island.CurIsland;
                if (island != null) s["island"] = island.name;
                var im = Traverse.Create(typeof(IslandManager)).Field("_instance").GetValue<IslandManager>();
                if (im != null)
                {
                    byte idx = Traverse.Create(im).Field("_curIsland").GetValue<byte>();
                    s["island_index"] = idx;
                    var infos = Traverse.Create(im).Field("_islandInfos").GetValue<IslandInfo[]>();
                    if (infos != null && idx < infos.Length && infos[idx] != null)
                    {
                        var spawn = Get<GameObject>(Traverse.Create(infos[idx]), "_spawnPosition");
                        if (spawn != null) s["island"] = spawn.name;
                    }
                }

                var inv = Get<PlayerInventory>(Traverse.Create(p).Field("_inventory"), null);
                if (inv != null)
                {
                    var mm = MoneyManager.Instance;
                    s["money"] = mm != null ? Get<int>(Traverse.Create(mm), "_money") : 0;
                    // 当前饵：优先 _baitSlot 的 BaitInfo 名，兜底索引字符串
                    string baitName = CurrentBaitName();
                    s["bait"] = !string.IsNullOrEmpty(baitName) ? baitName
                        : Get<byte>(Traverse.Create(inv), "_curBait").ToString();
                    s["owned_baits"] = Get<List<int>>(Traverse.Create(inv), "_ownedBaits") ?? new List<int>();
                    s["held"] = inv.SyncedCurItem?.GetName() ?? "";
                }
                s["on_boat"] = Get<bool>(Traverse.Create(p).Field("_movement"), null);

                s["betting"] = CasinoManager.IsBetting;
                s["bet_color"] = Get<string>(Traverse.Create(typeof(CasinoManager)), "_curBetColor");

                var boss = BossManager.Boss;
                s["boss_active"] = boss != null;
                if (boss != null)
                {
                    var bt = Traverse.Create(boss);
                    s["boss_hp"] = Get<int>(bt, "_hp");
                    s["boss_max_hp"] = Get<int>(bt, "_maxHp");
                }

                s["journal_count"] = _discoveredFish.Count;
                var all = Get<List<Creature>>(Traverse.Create(typeof(GameInfo)), "_allCreatures");
                s["journal_total"] = all?.Count ?? 0;
            }
            catch (Exception e)
            {
                Log?.LogWarning($"state: {e.Message}");
            }
            return s;
        }

        // ── 图鉴注册表导出（GameInfo._allCreatures） ──
        private int RegistryCreatureCount()
        {
            try
            {
                var list = Get<List<Creature>>(Traverse.Create(typeof(GameInfo)), "_allCreatures");
                return list?.Count ?? 0;
            }
            catch { return 0; }
        }

        private int PushRegistry()
        {
            int total = 0;
            try
            {
                var gt = Traverse.Create(typeof(GameInfo));
                var creatures = Get<List<Creature>>(gt, "_allCreatures") ?? new List<Creature>();
                var clist = new List<object>();
                foreach (var c in creatures)
                {
                    if (c == null) continue;
                    var ct = Traverse.Create(c);
                    clist.Add(new Dictionary<string, object>
                    {
                        ["name"] = c.GetName(),
                        ["type"] = c.GetType().Name,
                        ["worth"] = Get<int>(ct, "_worth"),
                        ["cost"] = Get<int>(ct, "_cost"),
                        ["weight"] = Get<float>(ct, "_weight"),
                        ["max_hp"] = Get<int>(ct, "_maxHp"),
                        ["endangered"] = Get<bool>(ct, "_isEndangered"),
                        ["boss"] = Get<string>(ct, "_bossType"),
                    });
                }
                var baits = Get<List<BaitInfo>>(gt, "_allBaits") ?? new List<BaitInfo>();
                var blist = new List<object>();
                foreach (var b in baits)
                {
                    if (b == null) continue;
                    var bt = Traverse.Create(b);
                    var weights = new List<object>();
                    var iws = Get<List<ItemInfoWeight>>(bt, "_itemWeights");
                    if (iws != null)
                    {
                        foreach (var iw in iws)
                        {
                            if (iw == null) continue;
                            var iwt = Traverse.Create(iw);
                            var item = Get<Item>(iwt, "_item");
                            var w = Get<float>(iwt, "_weight");
                            if (item == null) continue;
                            weights.Add(new Dictionary<string, object>
                            {
                                ["item"] = item.GetName(),
                                ["weight"] = w,
                            });
                        }
                    }
                    blist.Add(new Dictionary<string, object>
                    {
                        ["name"] = bt.Field("_name").GetValue<string>() ?? "",
                        ["cost"] = bt.Field("_cost").GetValue<int>(),
                        ["lost_chance"] = Get<float>(bt, "_lostOnBaitChance"),
                        ["catch_minmax"] = Get<Vector2>(bt, "_catchTimeMinMax"),
                        ["require_reel"] = Get<bool>(bt, "_requireReelingToCatch"),
                        ["weights"] = weights,
                    });
                }
                _server.Push(new Dictionary<string, object>
                {
                    ["type"] = "registry",
                    ["creatures"] = clist,
                    ["baits"] = blist,
                });
                total = clist.Count;
                Log.LogInfo($"registry pushed: {clist.Count} creatures, {blist.Count} baits");
            }
            catch (Exception e)
            {
                Log?.LogWarning($"registry: {e.Message}");
            }
            return total;
        }

        // ── 私有字段读取辅助（Harmony Traverse） ──
        // FishNet 的 SyncVar<T>/SyncList<T> 字段：先取 .Value / 按 IEnumerable 转 List
        private static T Get<T>(Traverse t, string field)
        {
            if (t == null) return default;
            object o;
            try { o = field == null ? t.GetValue() : t.Field(field).GetValue(); }
            catch { return default; }
            if (o == null) return default;
            try
            {
                if (o is T tv) return tv;
                var valProp = o.GetType().GetProperty("Value");
                if (valProp != null)
                {
                    var v = valProp.GetValue(o);
                    if (v is T vv) return vv;
                    // SyncList<T> → List<T>
                    if (v is System.Collections.IEnumerable en && typeof(T).IsGenericType
                        && typeof(T).GetGenericTypeDefinition() == typeof(List<>))
                    {
                        var itemT = typeof(T).GetGenericArguments()[0];
                        var list = (System.Collections.IList)Activator.CreateInstance(
                            typeof(List<>).MakeGenericType(itemT));
                        foreach (var it in en) list.Add(it);
                        if (list is T lst) return lst;
                    }
                    return (T)Convert.ChangeType(v, typeof(T));
                }
                return (T)o;
            }
            catch { return default; }
        }

        private static T Get<T>(Traverse t, string field, object ignored) => Get<T>(t, field);

        internal void PushEvent(Dictionary<string, object> d)
        {
            _server.PushEvent(d);
        }
    }

    /// <summary>TCP 服务端：监听 9877，JSON 行推送（mod 是服务端，Python 插件连接）。</summary>
    public class TelemetryServer
    {
        private readonly int _port;
        private System.Net.Sockets.TcpListener _listener;
        private System.Threading.Thread _acceptThread;
        private volatile bool _running;
        private readonly object _lock = new object();
        private readonly List<System.Net.Sockets.TcpClient> _clients = new List<System.Net.Sockets.TcpClient>();
        /// <summary>新客户端连接回调：重推图鉴注册表（插件可能后于 mod 启动）。</summary>
        public System.Action OnClientConnected;

        public TelemetryServer(int port) { _port = port; }

        public void Start()
        {
            _running = true;
            _acceptThread = new System.Threading.Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            try
            {
                _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, _port);
                _listener.Start();
                while (_running)
                {
                    var c = _listener.AcceptTcpClient();
                    c.NoDelay = true;
                    lock (_lock) _clients.Add(c);
                    try { OnClientConnected?.Invoke(); } catch { }
                }
            }
            catch { }
        }

        public void PushEvent(Dictionary<string, object> d)
        {
            var msg = new Dictionary<string, object>(d) { ["type"] = "event" };
            Push(msg);
        }

        public void PushState(Dictionary<string, object> s)
        {
            if (s.ContainsKey("type")) s["type"] = "state";
            else s.Add("type", "state");
            Push(s);
        }

        public void Push(Dictionary<string, object> msg)
        {
            string line;
            try { line = Newtonsoft.Json.JsonConvert.SerializeObject(msg) + "\n"; }
            catch { return; }
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (_lock)
            {
                for (int i = _clients.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var st = _clients[i].GetStream();
                        st.Write(bytes, 0, bytes.Length);
                    }
                    catch
                    {
                        try { _clients[i].Close(); } catch { }
                        _clients.RemoveAt(i);
                    }
                }
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            lock (_lock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
        }
    }
}
