using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

// Shared by JuanBerta's skill mods (Haggling, Stealth). Each mod compiles its own copy in its own namespace.
namespace BriberyPlusMod
{
	/// <summary>
	/// Hidden save object for a custom skill. It extends the game's Storage only so the game saves it
	/// in Globals.tnmt with each save. It never holds items and has no UI.
	/// </summary>
	public abstract class SkillSaveBase : Storage
	{
		public string dayKey = "";
		public float skillXp = 0f;
		public string ownerCharacter = "";
		// Per-day counters as "key=value|key=value", cleared each in-game day.
		public string daily = "";

		[NonSerialized] public bool loadedFromSave;
		[NonSerialized] private Dictionary<string, float> parsed;
		[NonSerialized] private string parsedFrom;

		protected abstract string SaveGuid { get; }
		protected abstract string SaveTitle { get; }

		public void ResetSkill(string owner)
		{
			skillXp = 0f;
			daily = "";
			dayKey = "";
			ownerCharacter = owner ?? "";
		}

		public void Setup()
		{
			GUID = SaveGuid;
			global = true;
			title = SaveTitle;
			storageSlots = new StorageSlot[0];
			Slots = new SlotController[0];
			spawnSettings = new SpawnSettings();
			FieldInfo startItems = typeof(Storage).GetField("startItems", BindingFlags.NonPublic | BindingFlags.Instance);
			if (startItems != null) startItems.SetValue(this, new ItemStack[0]);
			FieldInfo startDone = typeof(Storage).GetField("startDone", BindingFlags.NonPublic | BindingFlags.Instance);
			if (startDone != null) startDone.SetValue(this, true);
			initialized = true;
		}

		public override bool OnSavingGameSpecial()
		{
			storageSlots = new StorageSlot[0];
			return SaveController.SerializeData(this, global);
		}

		public override bool OnLoadingGameSpecial()
		{
			bool ok = SaveController.DeSerializeData(this, global);
			storageSlots = new StorageSlot[0];
			Slots = new SlotController[0];
			if (!ok) ResetSkill(SkillHost.CurrentCharacter());
			loadedFromSave = true;
			parsed = null;
			return ok;
		}

		private Dictionary<string, float> Daily()
		{
			if (parsed != null && parsedFrom == daily) return parsed;
			parsed = new Dictionary<string, float>();
			parsedFrom = daily;
			foreach (string pair in (daily ?? "").Split('|'))
			{
				int eq = pair.LastIndexOf('=');
				float v;
				if (eq > 0 && float.TryParse(pair.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
					parsed[pair.Substring(0, eq)] = v;
			}
			return parsed;
		}

		public float GetDaily(string key)
		{
			float v;
			return Daily().TryGetValue(key, out v) ? v : 0f;
		}

		public void SetDaily(string key, float value)
		{
			var d = Daily();
			d[key.Replace("|", "/").Replace("=", "-")] = value;
			var parts = new List<string>();
			foreach (var kv in d) parts.Add(kv.Key + "=" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
			daily = string.Join("|", parts.ToArray());
			if (daily.Length > 60000) daily = "";
			parsedFrom = daily;
			parsed = null;
		}

		public float AddDaily(string key, float amount)
		{
			float v = GetDaily(key) + amount;
			SetDaily(key, v);
			return v;
		}

		public void ClearDaily() { daily = ""; parsed = null; }
	}

	/// <summary>One custom skill: XP, level, the game's state effects, and its card on the Character tab.</summary>
	internal sealed class CustomSkill
	{
		internal const int MaxLevel = 10;

		internal readonly string Name;
		internal ConfigEntry<bool> Enabled;
		internal ConfigEntry<float> XpMultiplier;
		internal ConfigEntry<bool> ShowXpNotifications;
		internal ConfigEntry<bool> StateAffectsSkill;
		internal ConfigEntry<bool> DrinkingSkillHelps;
		internal ConfigEntry<bool> MushroomsAffectLevel;
		internal Func<string> CardNote;   // extra text on the card after the XP

		internal CustomSkill(string name) { Name = name; }

		internal static ConfigDescription D(string text, int order, AcceptableValueBase range = null, bool advanced = false)
		{
			return new ConfigDescription(text, range, new ConfigurationManagerAttributes { Order = order, IsAdvanced = advanced });
		}

		internal void BindCommon(ConfigFile cfg)
		{
			Enabled = cfg.Bind("Skill", "Enabled", true, D("Turn the " + Name + " skill on or off.", 100));
			XpMultiplier = cfg.Bind("Skill", "XpMultiplier", 1f, D("Multiplies all " + Name + " XP. Like the game's skills, a level is 1000 XP, up to level 10.", 80, new AcceptableValueRange<float>(0.1f, 10f)));
			ShowXpNotifications = cfg.Bind("Skill", "ShowXpNotifications", true, D("Show the game's \"Skill increased!\" notification each time you gain " + Name + " XP.", 70));
			StateAffectsSkill = cfg.Bind("State", "StateAffectsSkill", true, D("Alcohol and mushrooms affect " + Name + " the way they affect the game's own skills.", 100));
			DrinkingSkillHelps = cfg.Bind("State", "DrinkingSkillHelps", true, D("Your Drinking skill makes the drunk penalty smaller: -8% per Drinking level, down to 20% of the penalty (the game's own numbers).", 70));
			MushroomsAffectLevel = cfg.Bind("State", "MushroomsAffectLevel", true, D("Mushroom effects change your effective " + Name + " level, using the game's own formula for skills.", 60));
		}

		internal float Xp
		{
			get { return SkillHost.Save != null ? SkillHost.Save.skillXp : 0f; }
			set { if (SkillHost.Save != null) SkillHost.Save.skillXp = Mathf.Max(0f, value); }
		}

		internal bool Learned { get { return Xp > 0f; } }
		internal float SkillValue { get { return Learned ? Mathf.Min(MaxLevel, 1f + Xp / 1000f) : 0f; } }
		internal int Level { get { return Enabled.Value ? Mathf.FloorToInt(SkillValue) : 0; } }

		internal void AddXp(float amount, string note)
		{
			if (!Enabled.Value || amount <= 0f || SkillHost.Save == null || SkillValue >= MaxLevel) return;
			bool wasLearned = Learned;
			int before = Level;
			Xp += amount * XpMultiplier.Value;
			int after = Level;
			if (!wasLearned) ShowPopupOr("New skill\n" + Name + "\nlearned!", "New skill learned!", Name);
			else if (after > before) ShowPopupOr(Name + "\nincreased to\nlevel " + after + "!", "Skill increased!", Name + " increased to level " + after + "!");
			else if (ShowXpNotifications.Value) SkillHost.Notify("Skill increased!", string.IsNullOrEmpty(note) ? Name : Name + " (" + note + ")");
		}

		// ---- State (the same inputs the game uses for its skills) ----

		internal float Alcohol
		{
			get
			{
				if (!StateAffectsSkill.Value || PlayerStats.instance == null || PlayerStats.instance.alcohol == null) return 0f;
				return Mathf.Clamp(PlayerStats.instance.alcohol.AlcoholInBlood, 0f, 100f);
			}
		}

		internal int DrinkingLevel
		{
			get
			{
				try { return PlayerStats.instance != null ? PlayerStats.instance.GetSkill(PlayerStats.PlayerSkillType.Drinking) : 0; }
				catch (Exception) { return 0; }
			}
		}

		// PlayerStatsAlcohol.SetAlcohol: a drink's effect is scaled by 1 - Drinking * 0.08, never below 0.2.
		internal float DrinkingTolerance { get { return DrinkingSkillHelps.Value ? Mathf.Clamp(1f - DrinkingLevel * 0.08f, 0.2f, 1f) : 1f; } }

		// 0 = sober, 1 = completely drunk (after the Drinking skill).
		internal float Drunkenness { get { return Alcohol / 100f * DrinkingTolerance; } }

		// PlayerStats.GetSkillWithEffects: (skill + MushroomsHigh / 10) * (1 - SMVProgression / 200).
		internal float EffectiveLevel
		{
			get
			{
				float level = Level;
				if (level <= 0f) return 0f;
				if (StateAffectsSkill.Value && MushroomsAffectLevel.Value && PlayerStats.instance != null && PlayerStats.instance.mushrooms != null)
				{
					var m = PlayerStats.instance.mushrooms;
					level = (level + m.MushroomsHigh / 10f) * (1f - m.SMVProgression / 200f);
				}
				return Mathf.Clamp(level, 0f, MaxLevel);
			}
		}

		// ---- Character tab card ----

		private readonly Dictionary<CharacterPanel, CharacterPanelSkill> cards = new Dictionary<CharacterPanel, CharacterPanelSkill>();
		private bool hidEmptyText;

		internal void AddCard(CharacterPanel panel)
		{
			if (panel == null || cards.ContainsKey(panel)) return;
			Transform prefab = Traverse.Create(panel).Field("skillPrefab").GetValue<Transform>();
			Transform list = Traverse.Create(panel).Field("skillList").GetValue<Transform>();
			if (prefab == null || list == null) return;
			Transform t = UnityEngine.Object.Instantiate(prefab, list);
			t.name = "Skill_" + Name;
			CharacterPanelSkill card = t.GetComponent<CharacterPanelSkill>();
			if (card != null) cards[panel] = card;
		}

		internal void UpdateCard(CharacterPanel panel)
		{
			CharacterPanelSkill card;
			if (panel == null || !cards.TryGetValue(panel, out card) || card == null) return;
			bool show = Enabled.Value && Learned;
			card.gameObject.SetActive(show);
			GameObject list = Traverse.Create(panel).Field("skillPanel").GetValue<GameObject>();
			Component empty = Traverse.Create(panel).Field("emptyPanelText").GetValue<Component>();
			if (show && list != null && !list.activeSelf)
			{
				list.SetActive(true);
				if (empty != null && empty.gameObject.activeSelf) { empty.gameObject.SetActive(false); hidEmptyText = true; }
			}
			else if (!show && hidEmptyText && empty != null)
			{
				empty.gameObject.SetActive(true);
				hidEmptyText = false;
			}
			if (!show) return;
			card.UpdateSkill(new PlayerStats.PlayerSkill { Type = PlayerStats.PlayerSkillType.None, Skill = SkillValue });
			SetText(Traverse.Create(card).Field("title").GetValue<Component>(), Name);
			string note = null;
			try { note = CardNote != null ? CardNote() : null; } catch (Exception) { }
			if (!string.IsNullOrEmpty(note))
				SetText(Traverse.Create(card).Field("xp").GetValue<Component>(), Mathf.FloorToInt(SkillValue * 1000f) + " XP · " + note);
			card.transform.SetAsLastSibling();
		}

		private static void ShowPopupOr(string popup, string title, string text)
		{
			try
			{
				if (PlayerSkillIncreaseUI.instance != null)
				{
					Traverse.Create(PlayerSkillIncreaseUI.instance).Method("ShowNotification", new object[] { popup }).GetValue();
					return;
				}
			}
			catch (Exception) { }
			SkillHost.Notify(title, text);
		}

		internal static void SetText(Component tmp, string text)
		{
			if (tmp == null) return;
			var prop = tmp.GetType().GetProperty("text");
			if (prop != null) prop.SetValue(tmp, text, null);
		}
	}

	/// <summary>Save object lifetime, owner checks and day changes for one skill mod.</summary>
	internal static class SkillHost
	{
		private static BaseUnityPlugin plugin;
		private static BepInEx.Logging.ManualLogSource log;
		private static Func<GameObject, SkillSaveBase> factory;
		private static SkillSaveBase save;
		private static string objectName;
		private static float nextOwnerCheck;
		internal static CustomSkill Skill;
		internal static event Action DayChanged;

		internal static SkillSaveBase Save { get { return save; } }

		internal static void Init(BaseUnityPlugin owner, BepInEx.Logging.ManualLogSource logger, CustomSkill skill, string saveObjectName, Func<GameObject, SkillSaveBase> make)
		{
			plugin = owner;
			log = logger;
			Skill = skill;
			objectName = saveObjectName;
			factory = make;
			SceneManager.sceneLoaded += (s, m) => EnsureSaveObject();
			TimeOfDayAzure.DayChanged += OnDayChanged;
			SaveController.LoadingDone += OnGameLoaded;
			EnsureSaveObject();
			Harmony.CreateAndPatchAll(typeof(Hooks));
		}

		// Call from the plugin's Update.
		internal static void Tick()
		{
			EnsureSaveObject();
			if (Time.unscaledTime >= nextOwnerCheck)
			{
				nextOwnerCheck = Time.unscaledTime + 1f;
				CheckOwner();
			}
		}

		internal static bool InGame
		{
			get { return save != null && PlayerStats.instance != null && SceneManager.GetActiveScene().name != "Main Menu" && !GameIsLoading(); }
		}

		internal static string CurrentCharacter()
		{
			try { return NormalizeCharacter(Traverse.Create(typeof(SaveController)).Field("CharacterName").GetValue<string>()); }
			catch (Exception) { return null; }
		}

		internal static string NormalizeCharacter(string name)
		{
			if (string.IsNullOrEmpty(name)) return name;
			name = name.Replace('\\', '/').TrimEnd('/');
			int slash = name.LastIndexOf('/');
			return slash >= 0 ? name.Substring(slash + 1) : name;
		}

		private static bool GameIsLoading()
		{
			try { return SaveController.instance != null && Traverse.Create(SaveController.instance).Property("Loading").GetValue<bool>(); }
			catch (Exception) { return false; }
		}

		private static void CheckOwner()
		{
			if (save == null) return;
			if (PlayerStats.instance == null || SceneManager.GetActiveScene().name == "Main Menu")
			{
				save.loadedFromSave = false;
				return;
			}
			if (GameIsLoading()) return;
			string current = CurrentCharacter();
			if (string.IsNullOrEmpty(current) || NormalizeCharacter(save.ownerCharacter) == current) return;
			if (save.loadedFromSave)
			{
				save.ownerCharacter = current;
				return;
			}
			save.ResetSkill(current);
			save.dayKey = CurrentDayKey();
			LogInfo("New character '" + current + "': " + (Skill != null ? Skill.Name : "mod data") + " starts at 0.");
		}

		internal static string CurrentDayKey()
		{
			if (TimeOfDayAzure.instance == null) return "";
			TimeAndDay now = TimeOfDayAzure.instance.currentTimeAndDay;
			return now.year + "-" + now.month + "-" + now.day;
		}

		private static void OnDayChanged(int day)
		{
			if (save == null) return;
			save.ClearDaily();
			save.dayKey = CurrentDayKey();
			if (DayChanged != null) DayChanged();
		}

		private static void OnGameLoaded()
		{
			EnsureSaveObject();
			if (save.loadedFromSave && string.IsNullOrEmpty(save.ownerCharacter)) save.ownerCharacter = CurrentCharacter() ?? "";
			if (save.dayKey != CurrentDayKey())
			{
				save.ClearDaily();
				save.dayKey = CurrentDayKey();
			}
		}

		private static void EnsureSaveObject()
		{
			if (save == null)
			{
				GameObject go = new GameObject(objectName);
				go.SetActive(false);
				save = factory(go);
				save.Setup();
				UnityEngine.Object.DontDestroyOnLoad(go);
				SaveController.DontDestroyOnLoadObjects.Add(go);
			}
			if (!save.gameObject.activeSelf && Inventory.instance != null) save.gameObject.SetActive(true);
		}

		internal static void LogInfo(string text) { if (log != null) log.LogInfo(text); }
		internal static void LogWarning(string text) { if (log != null) log.LogWarning(text); }

		internal static void Notify(string title, string text)
		{
			if (Notifications.instance != null) Notifications.instance.CreateNotification(title, text);
			else LogInfo(text);
		}

		// ---- Night and light (shared by skills that care about darkness) ----

		internal static bool IsNight(int start, int end)
		{
			if (TimeOfDayAzure.instance == null || TimeOfDayAzure.instance.currentTimeAndDay == null) return false;
			float h = TimeOfDayAzure.instance.currentTimeAndDay.hours;
			return start > end ? (h >= start || h < end) : (h >= start && h < end);
		}

		internal static IEnumerable<string> EquippedItemNames()
		{
			var inv = Inventory.instance;
			if (inv == null || inv.CharacterSlots == null) yield break;
			foreach (var sc in inv.CharacterSlots)
			{
				if (sc == null || sc.itemStack == null || sc.itemStack.itemId < 0 || sc.itemStack.itemAmount <= 0) continue;
				Item item = ItemDatabase.instance != null ? ItemDatabase.instance.FetchItemByID(sc.itemStack.itemId) : null;
				if (item != null && !string.IsNullOrEmpty(item.Title)) yield return item.Title;
			}
		}

		internal static bool HasLight()
		{
			try
			{
				foreach (var f in UnityEngine.Object.FindObjectsByType<WieldableItem.Flashlight>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
					if (f != null && f.isActiveAndEnabled && Traverse.Create(f).Field("on").GetValue<bool>()) return true;
				foreach (string name in EquippedItemNames())
					if (name.IndexOf("Headlamp", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			catch (Exception) { }
			return false;
		}

		internal static class Hooks
		{
			[HarmonyPatch(typeof(CharacterPanel), "Init")]
			[HarmonyPostfix]
			private static void Init_Postfix(CharacterPanel __instance)
			{
				try { if (Skill != null) Skill.AddCard(__instance); }
				catch (Exception e) { LogWarning("Skill card: " + e.Message); }
			}

			[HarmonyPatch(typeof(CharacterPanel), "UpdateSkills")]
			[HarmonyPostfix]
			private static void UpdateSkills_Postfix(CharacterPanel __instance)
			{
				try { if (Skill != null) Skill.UpdateCard(__instance); }
				catch (Exception e) { LogWarning("Skill card: " + e.Message); }
			}

			[HarmonyPatch(typeof(SaveController), "ExecuteDontDestroyOnLoadSaveLoadFunctions")]
			[HarmonyPrefix]
			private static void SaveLoad_Prefix()
			{
				if (save != null && !save.gameObject.activeSelf) save.gameObject.SetActive(true);
			}
		}
	}

	internal sealed class ConfigurationManagerAttributes
	{
		public int? Order;
		public bool? IsAdvanced;
	}
}
