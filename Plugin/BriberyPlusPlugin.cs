using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BriberyPlusMod
{
	public class BriberyPlusSave : SkillSaveBase
	{
		protected override string SaveGuid { get { return "JuanBerta_BriberyPlus_Save_001"; } }
		protected override string SaveTitle { get { return "Bribery+"; } }

		// NPCs who won't talk money with you for a while: "npcId=until day|...". Kept across days.
		public string offended = "";

		public int OffendedUntil(int npc)
		{
			foreach (string pair in (offended ?? "").Split('|'))
			{
				int eq = pair.IndexOf('=');
				int id, day;
				if (eq > 0 && int.TryParse(pair.Substring(0, eq), out id) && id == npc && int.TryParse(pair.Substring(eq + 1), out day)) return day;
			}
			return -1;
		}

		public void Offend(int npc, int untilDay, int today)
		{
			var keep = new List<string>();
			foreach (string pair in (offended ?? "").Split('|'))
			{
				int eq = pair.IndexOf('=');
				int id, day;
				if (eq > 0 && int.TryParse(pair.Substring(0, eq), out id) && int.TryParse(pair.Substring(eq + 1), out day) && id != npc && day >= today)
					keep.Add(pair);
			}
			keep.Add(npc + "=" + untilDay);
			offended = string.Join("|", keep.ToArray());
		}
	}

	/// <summary>
	/// Bribery+ adds to the game's own Bribery skill: ask the people you meet for money. Your Bribery level
	/// sets the odds and the size of the reward, and the game gives Bribery XP for each try, more when the
	/// odds were worse (PlayerStats.SkillSuccess, the same call the game uses for police bribes).
	/// </summary>
	[BepInPlugin("JuanBerta.Obenseuer.BriberyPlus", "Bribery+", "1.0.1")]
	public class BriberyPlusPlugin : BaseUnityPlugin
	{
		internal static BriberyPlusSave Save { get { return SkillHost.Save as BriberyPlusSave; } }

		internal static ConfigEntry<bool> Enabled;
		internal static ConfigEntry<KeyboardShortcut> AskKey;
		internal static ConfigEntry<bool> ShowPrompt;
		internal static ConfigEntry<bool> ShowChance;
		internal static ConfigEntry<float> MaxDistance;
		internal static ConfigEntry<float> BaseFailChance;
		internal static ConfigEntry<float> PoliceFailChance;
		internal static ConfigEntry<string> PoliceJobs;
		internal static ConfigEntry<bool> PeopleDiffer;
		internal static ConfigEntry<float> MinReward;
		internal static ConfigEntry<float> MaxReward;
		internal static ConfigEntry<float> RewardPerLevel;
		internal static ConfigEntry<float> PoliceRewardFactor;
		internal static ConfigEntry<float> RmChancePerLevel;
		internal static ConfigEntry<float> RmBonus;
		internal static ConfigEntry<float> XpFactor;
		internal static ConfigEntry<float> FailureXpFactor;
		internal static ConfigEntry<float> MaxDrunkXpBonus;
		internal static ConfigEntry<float> OffendChance;
		internal static ConfigEntry<int> OffendedDays;
		internal static ConfigEntry<float> ReportChance;
		internal static ConfigEntry<float> PoliceReportChance;
		internal static ConfigEntry<int> ReportSeverity;

		private void Awake()
		{
			Bind(Config);
			SkillHost.Init(this, Logger, null, "BriberyPlus_Save", go => go.AddComponent<BriberyPlusSave>());
			Logger.LogInfo("Bribery+ loaded.");
		}

		private static ConfigDescription D(string text, int order, AcceptableValueBase range = null) { return CustomSkill.D(text, order, range); }

		private static void Bind(ConfigFile cfg)
		{
			Enabled = cfg.Bind("General", "Enabled", true, D("Turn Bribery+ on or off.", 100));
			AskKey = cfg.Bind("General", "AskKey", new KeyboardShortcut(KeyCode.B), D("Key to ask the person you're looking at for money.", 90));
			ShowPrompt = cfg.Bind("General", "ShowPrompt", true, D("Show a hint under the crosshair when you look at someone you can ask.", 80));
			ShowChance = cfg.Bind("General", "ShowChance", true, D("Show your chance of success in the hint.", 70));
			MaxDistance = cfg.Bind("General", "MaxDistance", 3f, D("How close you must be, in meters.", 60, new AcceptableValueRange<float>(1f, 10f)));

			BaseFailChance = cfg.Bind("Odds", "BaseFailChance", 0.65f, D("Chance that a stranger says no before your Bribery level, alcohol and mushrooms are counted (the game takes 3% off per level).", 100, new AcceptableValueRange<float>(0f, 1f)));
			PoliceFailChance = cfg.Bind("Odds", "PoliceFailChance", 0.85f, D("The same for police and militia: harder, but they pay more.", 90, new AcceptableValueRange<float>(0f, 1f)));
			PoliceJobs = cfg.Bind("Odds", "PoliceJobs", "Police,Militia,Guard,Officer,Cop", D("Words in a person's job that mark them as police, comma separated.", 85));
			PeopleDiffer = cfg.Bind("Odds", "PeopleDiffer", true, D("Some people are generous and some are stingy: each person has their own odds and reward size (always the same for that person).", 80));

			MinReward = cfg.Bind("Rewards", "MinReward", 3f, D("Smallest reward in OC at level 1.", 100, new AcceptableValueRange<float>(0f, 1000f)));
			MaxReward = cfg.Bind("Rewards", "MaxReward", 12f, D("Biggest reward in OC at level 1.", 95, new AcceptableValueRange<float>(0f, 1000f)));
			RewardPerLevel = cfg.Bind("Rewards", "RewardPerLevel", 0.4f, D("Rewards grow by this much per Bribery level above 1 (0.4 = level 10 gives 4.6 times as much).", 90, new AcceptableValueRange<float>(0f, 3f)));
			PoliceRewardFactor = cfg.Bind("Rewards", "PoliceRewardFactor", 2f, D("Reward multiplier when police pay up.", 85, new AcceptableValueRange<float>(0f, 10f)));
			RmChancePerLevel = cfg.Bind("Rewards", "RmChancePerLevel", 0.04f, D("Chance per Bribery level that you're paid in RM instead of OC (0.04 = 40% at level 10).", 80, new AcceptableValueRange<float>(0f, 0.1f)));
			RmBonus = cfg.Bind("Rewards", "RmBonus", 1.25f, D("RM rewards are worth this much more than the same OC reward.", 75, new AcceptableValueRange<float>(0.1f, 5f)));
			XpFactor = cfg.Bind("Rewards", "XpFactor", 1f, D("Multiplies the Bribery XP the game gives for each try.", 70, new AcceptableValueRange<float>(0f, 10f)));
			FailureXpFactor = cfg.Bind("Rewards", "FailureXpFactor", 2.5f, D("A refusal gives this many times the XP the game normally gives for a failure (the game gives half of a success; 2.5 = a bit more than a success).", 65, new AcceptableValueRange<float>(1f, 10f)));
			MaxDrunkXpBonus = cfg.Bind("Rewards", "MaxDrunkXpBonus", 1f, D("Extra Bribery XP when asking drunk, scaled by alcohol in blood (1 = up to +100%), whether they pay or not.", 60, new AcceptableValueRange<float>(0f, 3f)));

			OffendChance = cfg.Bind("Failure", "OffendChance", 0.25f, D("When someone says no, the chance they're offended and won't talk money with you for a few days.", 100, new AcceptableValueRange<float>(0f, 1f)));
			OffendedDays = cfg.Bind("Failure", "OffendedDays", 3, D("How many in-game days an offended person stays offended.", 90, new AcceptableValueRange<int>(1, 30)));
			ReportChance = cfg.Bind("Failure", "ReportChance", 0.1f, D("When someone says no, the chance they report you for harassment.", 80, new AcceptableValueRange<float>(0f, 1f)));
			PoliceReportChance = cfg.Bind("Failure", "PoliceReportChance", 0.5f, D("The same when police say no.", 75, new AcceptableValueRange<float>(0f, 1f)));
			ReportSeverity = cfg.Bind("Failure", "ReportSeverity", 30, D("How serious a harassment report is (the game's crime severity).", 70, new AcceptableValueRange<int>(1, 500)));
		}

		// ---- Who you're looking at ----

		private struct Target
		{
			public NPCInfo npc;
			public int id;
			public string name;
			public bool police;
			public string blocked;   // why you can't ask, or null
		}

		private static Target? Current()
		{
			if (!Enabled.Value || !SkillHost.InGame || PlayerStats.instance == null) return null;
			if (Time.timeScale <= 0f || Cursor.lockState != CursorLockMode.Locked) return null;
			if (TradePanel.instance != null && TradePanel.instance.activeTrade != null) return null;
			GameObject go = InteractObjects.instance != null ? InteractObjects.instance.currentGameObject : null;
			if (go == null) return null;
			NPCInfo npc = go.GetComponentInParent<NPCInfo>();
			if (npc == null || npc.character == null || !npc.isActiveAndEnabled) return null;
			if (PlayerLocator.instance != null && Vector3.Distance(PlayerLocator.instance.transform.position, npc.transform.position) > MaxDistance.Value) return null;
			var t = new Target { npc = npc, id = npc.character.ID, name = NameOf(npc.character), police = IsPolice(npc.character) };
			try { if (npc.Sleeping()) t.blocked = "asleep"; } catch (Exception) { }
			if (t.blocked == null && Save != null)
			{
				if (Save.OffendedUntil(t.id) >= Today) t.blocked = "still offended";
				else if (Save.GetDaily("ask@" + t.id) > 0f) t.blocked = "already asked today";
			}
			return t;
		}

		private static string NameOf(Character c)
		{
			try { if (!string.IsNullOrEmpty(c.FullName)) return c.FullName; } catch (Exception) { }
			return string.IsNullOrEmpty(c.Name) ? "They" : c.Name;
		}

		private static bool IsPolice(Character c)
		{
			string job = c.Job ?? "";
			foreach (string w in PoliceJobs.Value.Split(','))
				if (w.Trim().Length > 0 && job.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		private static int Today
		{
			get
			{
				try { return TimeOfDayAzure.instance.currentTimeAndDay.currentDay; }
				catch (Exception) { return 0; }
			}
		}

		// A fixed number from 0 to 1 for each person: how generous they are.
		private static float Generosity(int id)
		{
			if (!PeopleDiffer.Value) return 0.5f;
			unchecked
			{
				uint h = (uint)id * 2654435761u;
				h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
				return (h % 1000) / 999f;
			}
		}

		private static float RawFail(Target t)
		{
			float fail = t.police ? PoliceFailChance.Value : BaseFailChance.Value;
			fail += (0.5f - Generosity(t.id)) * 0.2f;   // +-10% by person
			return Mathf.Clamp(fail, 0.01f, 0.99f);
		}

		private static float FailChance(Target t)
		{
			return Recipe.CurrentFailChance(RawFail(t), PlayerStats.PlayerSkillType.Bribery);
		}

		private static int BriberyLevel
		{
			get
			{
				try { return Mathf.Max(1, PlayerStats.instance.GetSkill(PlayerStats.PlayerSkillType.Bribery)); }
				catch (Exception) { return 1; }
			}
		}

		// ---- Asking ----

		private void Update()
		{
			SkillHost.Tick();
			try
			{
				if (!AskKey.Value.IsDown()) return;
				Target? t = Current();
				if (t == null) return;
				Ask(t.Value);
			}
			catch (Exception e) { Logger.LogWarning("Bribery+: " + e.Message); }
		}

		private static readonly string[] YesLines =
		{
			"{0} sighs and hands over {1}.",
			"{0} looks you over and gives you {1}.",
			"\"Fine, just go away.\" {0} gives you {1}.",
			"{0} digs through their pockets: {1}.",
			"{0} takes pity on you: {1}.",
		};

		private static readonly string[] PoliceYesLines =
		{
			"{0} glances around and slips you {1}. \"You never saw me.\"",
			"{0} pays {1} to make you disappear.",
		};

		private static readonly string[] NoLines =
		{
			"{0} shakes their head.",
			"\"Get a job.\" {0} walks off.",
			"{0} pretends not to hear you.",
			"\"I've got nothing for you.\"",
			"{0} laughs at you.",
		};

		private static string Pick(string[] lines) { return lines[UnityEngine.Random.Range(0, lines.Length)]; }

		private static void Ask(Target t)
		{
			if (t.blocked != null)
			{
				SkillHost.Notify("Bribery+", t.name + ": " + t.blocked + ".");
				return;
			}
			if (Save == null) return;
			Save.SetDaily("ask@" + t.id, 1f);
			try { CharacterHeadController.PlayerMadeNoise(); } catch (Exception) { }

			float fail = FailChance(t);
			bool ok = PlayerStats.instance.SkillSuccess(PlayerStats.PlayerSkillType.Bribery, fail, false, XpFactor.Value);
			ExtraXp(ok, fail);
			if (ok) Pay(t, fail);
			else Refuse(t);
		}

		// The game's SkillSuccess gave 0.1 x fail chance for a success and 0.05 x fail chance for a failure.
		// Top that up: failures count FailureXpFactor times as much, and being drunk adds its bonus to both.
		private static void ExtraXp(bool ok, float fail)
		{
			try
			{
				float given = XpFactor.Value * (ok ? 0.1f : 0.05f) * (fail == 0f ? 0.01f : fail);
				float alcohol = PlayerStats.instance.alcohol != null ? Mathf.Clamp(PlayerStats.instance.alcohol.AlcoholInBlood, 0f, 100f) : 0f;
				float target = given * (ok ? 1f : FailureXpFactor.Value) * (1f + MaxDrunkXpBonus.Value * alcohol / 100f);
				if (target > given) PlayerStats.instance.IncreaseSkill(PlayerStats.PlayerSkillType.Bribery, target - given);
			}
			catch (Exception) { }
		}

		private static void Pay(Target t, float fail)
		{
			int level = BriberyLevel;
			float quality = 1f;
			try { quality = Mathf.Lerp(0.7f, 1.3f, Mathf.Clamp01(PlayerStats.SkillQuality(PlayerStats.PlayerSkillType.Bribery, fail) / 2f)); }
			catch (Exception) { }
			float oc = UnityEngine.Random.Range(MinReward.Value, Mathf.Max(MinReward.Value, MaxReward.Value));
			oc *= 1f + RewardPerLevel.Value * (level - 1);
			oc *= Mathf.Lerp(0.6f, 1.4f, Generosity(t.id)) * quality;
			if (t.police) oc *= PoliceRewardFactor.Value;
			oc = Mathf.Max(1f, Mathf.Round(oc));

			string paid;
			if (UnityEngine.Random.value < RmChancePerLevel.Value * level)
			{
				float rm = Money.instance.OcToRmConvert(oc * RmBonus.Value);
				if (rm < 0.01f) rm = 0.01f;
				Money.instance.AddMoney(rm, Money.Currency.RM);
				paid = rm.ToString("0.00", CultureInfo.InvariantCulture) + " RM";
			}
			else
			{
				Money.instance.AddMoney(oc, Money.Currency.OC);
				paid = oc.ToString("0", CultureInfo.InvariantCulture) + " OC";
			}
			SkillHost.Notify("Bribery+", string.Format(t.police ? Pick(PoliceYesLines) : Pick(YesLines), t.name, paid));
		}

		private static void Refuse(Target t)
		{
			string text = string.Format(Pick(NoLines), t.name);
			if (UnityEngine.Random.value < (t.police ? PoliceReportChance.Value : ReportChance.Value))
			{
				try
				{
					Crime.instance.CommitCrime(new NPC.NPCReference(t.id), ReportSeverity.Value, Crime.CrimeType.Harassment);
					text += " They report you for harassment!";
				}
				catch (Exception) { }
			}
			else if (UnityEngine.Random.value < OffendChance.Value)
			{
				Save.Offend(t.id, Today + OffendedDays.Value, Today);
				text += " They're offended and won't talk money with you for a while.";
			}
			SkillHost.Notify("Bribery+", text);
		}

		// ---- Hint under the crosshair ----

		private static GUIStyle style;
		private static Target? shown;
		private static float nextLook;

		private void OnGUI()
		{
			if (!ShowPrompt.Value || Event.current.type != EventType.Repaint) return;
			if (Time.unscaledTime >= nextLook)
			{
				nextLook = Time.unscaledTime + 0.2f;
				try { shown = Current(); } catch (Exception) { shown = null; }
			}
			if (shown == null) return;
			Target t = shown.Value;
			if (t.npc == null) { shown = null; return; }
			if (style == null)
			{
				style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold };
			}
			string text;
			if (t.blocked != null) text = t.name + " (" + t.blocked + ")";
			else
			{
				text = "[" + AskKey.Value + "] Ask " + t.name + " for money";
				if (ShowChance.Value)
				{
					float chance = 0f;
					try { chance = 1f - FailChance(t); } catch (Exception) { }
					text += " (" + Mathf.RoundToInt(chance * 100f) + "%)";
				}
			}
			Rect r = new Rect(0f, Screen.height * 0.5f + 60f, Screen.width, 30f);
			Color old = GUI.color;
			GUI.color = new Color(0f, 0f, 0f, 0.8f);
			GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
			GUI.color = t.blocked != null ? new Color(0.75f, 0.75f, 0.75f) : (t.police ? new Color(1f, 0.75f, 0.4f) : Color.white);
			GUI.Label(r, text, style);
			GUI.color = old;
		}
	}
}
