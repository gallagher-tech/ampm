using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Diagnostics;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace AmpmLib.Samples
{
	/// <summary>
	/// A mock Unity app for testing AMPM's heartbeat monitoring (the MockUnityHeartbeatTestUI Canvas
	/// prefab). Shows AMPM's status, its config, messages received from AMPM and the app's own log. Its
	/// buttons exercise AMPM's heartbeat monitoring (restart, freeze, crash) and each has an
	/// explanation of what should happen. With a MockAmpmServer in the scene the panel takes the
	/// right half of the screen and its explanations describe the mock.
	/// </summary>
	[DefaultExecutionOrder(-30000)] // Start capturing the app log before anything else logs.
	public class MockUnityHeartbeatTestUI : MonoBehaviour
	{
		[Header("Layout")]
		[Tooltip("The scrolling panel. It takes the right half of the screen next to Mock AMPM, or the full width without it.")]
		[SerializeField]
		private RectTransform panel;

		[Header("Live text")]
		[SerializeField] private Text subtitleText;
		[Tooltip("Updated every frame, so it visibly stops while the app is frozen.")]
		[SerializeField] private Text clockText;
		[SerializeField] private Text heartbeatCounterText;
		[SerializeField] private Text statusText;
		[SerializeField] private Text configText;
		[SerializeField] private Text messagesText;
		[SerializeField] private Text appLogText;

		[Header("Explanations under the buttons")]
		[SerializeField] private Text restartExplanation;
		[SerializeField] private Text freezeExplanation;
		[SerializeField] private Text freezeForeverExplanation;
		[SerializeField] private Text crashExplanation;

		[Header("Freeze")]
		[Tooltip("Default length of the timed freeze, in seconds.")]
		[Range(1f, 30f)]
		[SerializeField]
		private float freezeSeconds = 10f;

		[SerializeField] private Slider freezeSlider;
		[SerializeField] private Text freezeLengthText;
		[SerializeField] private Text freezeButtonText;
		[SerializeField] private Button freezeForeverButton;
		[SerializeField] private Button crashButton;

		[Header("Mock AMPM restart timeout (next to the freeze length, since the two decide whether a restart happens)")]
		[SerializeField] private GameObject restartTimeoutRow;
		[SerializeField] private Slider restartTimeoutSlider;
		[SerializeField] private Text restartTimeoutLabel;
		[SerializeField] private Text restartTimeoutExplanation;

		// In the Editor, Freeze Forever ends on its own after this long, so Unity can never lock up for good.
		private const float EditorFreezeSafetySeconds = 60f;
		private const float RefreshInterval = 0.25f;
		private const int MaxLogLines = 100;
		private const int MaxLineLength = 300;
		private const int MaxMessages = 20;
		private const string Prefix = "[Mock Unity App] ";

		// Set by Mock AMPM (from its background thread) to end a freeze so it can restart Play mode.
		private static volatile bool _releaseFreeze;

		// Set at the same time and never cleared during this run: a restart is on its way, so Restart,
		// Freeze and Crash are ignored. Otherwise a click queued while the app was frozen can freeze it
		// again before Play mode restarts, and Unity hangs.
		private static volatile bool _restartPending;

		// Raised so a click whose pointer moves a little isn't taken as a scroll drag and cancelled.
		private const int MinDragThreshold = 20;

		private readonly List<string> _log = new List<string>();
		private readonly List<string> _messages = new List<string>();
		private volatile bool _logChanged = true;
		private bool _messagesChanged = true;
		private MockAmpmServer _mock;
		private float _nextRefresh;
		private long _lastHeartbeatCount;
		private float _lastRateTime;
		private float _heartbeatRate;

		/// <summary>
		/// Ends a running freeze. Mock AMPM calls this so the main thread can restart Play mode.
		/// </summary>
		public static void ReleaseFreeze()
		{
			_restartPending = true;
			_releaseFreeze = true;
		}

		// Statics survive between play sessions when the domain reload is skipped.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetStatics()
		{
			_releaseFreeze = false;
			_restartPending = false;
		}

		private bool IgnoreWhileRestarting(string action)
		{
			if (!_restartPending)
				return false;
			Debug.Log(Prefix + action + " ignored: Mock AMPM is already restarting the app.");
			return true;
		}

		private void Awake()
		{
			Application.logMessageReceivedThreaded += OnLogMessage;
			_mock = FindAnyObjectByType<MockAmpmServer>();
			AmpmSampleLayout.FillParent(transform);

			// Without the mock, use the whole screen.
			if (_mock == null && panel != null)
			{
				const float margin = 10f;
				panel.anchorMin = new Vector2(0f, panel.anchorMin.y);
				panel.anchorMax = new Vector2(1f, panel.anchorMax.y);
				panel.offsetMin = new Vector2(margin, panel.offsetMin.y);
				panel.offsetMax = new Vector2(-margin, panel.offsetMax.y);
			}
		}

		private void Start()
		{
			// Without a mock to end it, Freeze Forever (and a real crash) would take Unity down with the app.
			bool allowedHere = !(Application.isEditor && _mock == null);
			freezeForeverButton.interactable = allowedHere;
			crashButton.interactable = allowedHere;

			EventSystem eventSystem = EventSystem.current;
			if (eventSystem != null && eventSystem.pixelDragThreshold < MinDragThreshold)
				eventSystem.pixelDragThreshold = MinDragThreshold;

			freezeSlider.SetValueWithoutNotify(freezeSeconds);

			// The restart timeout belongs to Mock AMPM; with real AMPM it's heartbeatTimeout in ampm.json.
			restartTimeoutRow.SetActive(_mock != null);
			restartTimeoutExplanation.gameObject.SetActive(_mock != null);
			if (_mock != null)
				restartTimeoutSlider.SetValueWithoutNotify(_mock.RestartTimeoutSeconds);
			subtitleText.text = (_mock != null ? "Talking to Mock AMPM (left)." : "Talking to real AMPM.")
				+ (Application.isEditor ? " Running in the Editor." : " Running in a build.");
			Refresh();
		}

		private void OnEnable()
		{
			// Subscribing after the config arrived still delivers it immediately.
			AMPM.ConfigLoaded += OnConfigLoaded;
			AMPM.OnAmpmMessage += OnAmpmMessage;
		}

		private void OnDisable()
		{
			AMPM.ConfigLoaded -= OnConfigLoaded;
			AMPM.OnAmpmMessage -= OnAmpmMessage;
		}

		private void OnDestroy()
		{
			Application.logMessageReceivedThreaded -= OnLogMessage;
		}

		private void Update()
		{
			// Heartbeats per second, refreshed once a second.
			float now = Time.realtimeSinceStartup;
			if (now - _lastRateTime >= 1f)
			{
				long count = AMPM.HeartbeatsSent;
				_heartbeatRate = (count - _lastHeartbeatCount) / (now - _lastRateTime);
				_lastHeartbeatCount = count;
				_lastRateTime = now;
			}

			// Every frame (not on the slower refresh), so a freeze stops the clock immediately.
			clockText.text = DateTime.Now.ToString("HH:mm:ss.f");
			heartbeatCounterText.text = "Heartbeats sent: " + AMPM.HeartbeatsSent + "  (" + _heartbeatRate.ToString("0") + " per second)";

			if (now >= _nextRefresh)
				Refresh();
		}

		// ---- Called by the prefab's buttons and slider ----

		public void RequestRestart()
		{
			if (IgnoreWhileRestarting("Restart"))
				return;
			Debug.Log(Prefix + "Asking AMPM to restart the app.");
			AMPM.Restart();
		}

		public void SetFreezeSeconds(float seconds)
		{
			freezeSeconds = Mathf.Round(seconds);
			Refresh();
		}

		public void SetRestartTimeout(float seconds)
		{
			if (_mock == null)
				return;
			_mock.RestartTimeoutSeconds = Mathf.Round(seconds);
			Refresh();
		}

		public void FreezeTimed()
		{
			Freeze(freezeSeconds);
		}

		public void FreezeForever()
		{
			Freeze(0f);
		}

		public void Crash()
		{
			if (IgnoreWhileRestarting("Crash"))
				return;
			if (_mock != null && Application.isEditor)
			{
				_mock.SimulateProcessExit();
				return;
			}

			Debug.Log(Prefix + "Crashing the app now." + (_mock == null ? " If restartOnProcessExit is true in ampm.json, AMPM should relaunch it." : ""));
			if (_mock != null)
				_mock.PrepareForCrash();

			Thread.Sleep(200); // Give the log lines time to reach Player.log.
			Utils.ForceCrash(ForcedCrashCategory.AccessViolation);
		}

		// ---- Display ----

		private void Refresh()
		{
			_nextRefresh = Time.realtimeSinceStartup + RefreshInterval;

			statusText.text = BuildStatus();

			restartExplanation.text = ExplainRestart();
			freezeExplanation.text = ExplainFreeze();
			freezeForeverExplanation.text = ExplainFreezeForever();
			crashExplanation.text = ExplainCrash();

			freezeLengthText.text = "Freeze length: " + AmpmSampleText.Seconds(freezeSeconds);
			freezeButtonText.text = "Freeze for " + AmpmSampleText.Seconds(freezeSeconds);

			if (_mock != null)
			{
				restartTimeoutLabel.text = "Mock restart timeout: " + AmpmSampleText.Seconds(_mock.RestartTimeoutSeconds);
				restartTimeoutExplanation.text = "How long Mock AMPM waits without a heartbeat before it restarts the app, like heartbeatTimeout in ampm.json. 0 = never. "
					+ "A freeze longer than this timeout causes a restart.";
			}

			if (configText.text.Length == 0 || !AMPM.IsConfigLoaded)
				configText.text = AMPM.IsConfigLoaded ? AMPM.Config.ToString(Formatting.Indented)
					: AMPM.IsInitialized ? "Not loaded yet. AMPMManager retries every 5 s." : "Not loaded.";

			if (_messagesChanged)
			{
				_messagesChanged = false;
				if (!AMPM.IsListening && _messages.Count == 0)
					messagesText.text = "Listen For Messages is off on the AMPM object, so messages from AMPM aren't received.";
				else
					messagesText.text = _messages.Count == 0 ? "None yet." : string.Join("\n", _messages);
			}

			if (_logChanged)
			{
				_logChanged = false;
				lock (_log)
				{
					var builder = new StringBuilder();
					for (int i = _log.Count - 1; i >= 0; i--)
						builder.Append(_log[i]).Append('\n');
					appLogText.text = builder.ToString();
				}
			}
		}

		private string BuildStatus()
		{
			var lines = new List<string>();
			if (!AMPM.IsInitialized && Application.isEditor)
				lines.Add(AmpmSampleText.Yellow("AMPM isn't initialized. In the Editor, turn on Run In Editor on the AMPM object."));

			lines.Add(AmpmSampleText.Line("Initialized", AmpmSampleText.YesNo(AMPM.IsInitialized)));
			lines.Add(AmpmSampleText.Line("Config loaded", AmpmSampleText.YesNo(AMPM.IsConfigLoaded)));
			lines.Add(AmpmSampleText.Line("Listening", AMPM.IsListening ? "Yes, port " + AMPM.ListenPort : "No"));
			lines.Add(AmpmSampleText.Line("Send port", AMPM.SendPort.ToString()));
			lines.Add(AmpmSampleText.Line("Last send error", AMPM.LastSendError == null ? "None" : AmpmSampleText.Red(AMPM.LastSendError) + "  (" + AmpmSampleText.Ago(AMPM.LastSendErrorTime.Value) + ")"));
			return string.Join("\n", lines);
		}

		private string ExplainRestart()
		{
			if (_mock == null)
				return "Asks AMPM to restart the app: AMPM closes it and runs its launchCommand again. Only works for a build that AMPM launched.";
			return Application.isEditor
				? "Asks AMPM to restart the app. The mock logs \"AMPM is restarting the app\" and restarts Play mode."
				: "Asks AMPM to restart the app. The mock logs \"AMPM is restarting the app\", closes this app and launches it again.";
		}

		private string ExplainFreeze()
		{
			string length = AmpmSampleText.Seconds(freezeSeconds);
			if (_mock == null)
				return "Blocks the app for " + length + ", so heartbeats stop. If AMPM launched this build and its heartbeatTimeout is shorter than " + length + ", AMPM restarts the app.";

			float timeout = _mock.RestartTimeoutSeconds;
			if (timeout <= 0f || timeout >= freezeSeconds)
				return "Blocks the app for " + length + ", so heartbeats stop. The mock's restart timeout (" + AmpmSampleText.Seconds(timeout) + ") isn't shorter than the freeze, so the app should recover without a restart.";

			return "Blocks the app for " + length + ", so heartbeats stop. After " + AmpmSampleText.Seconds(timeout) + " the mock logs \"AMPM is restarting the app\" and "
				+ (Application.isEditor ? "ends the freeze and restarts Play mode." : "closes this app and launches it again.");
		}

		private string ExplainFreezeForever()
		{
			if (_mock == null)
			{
				return Application.isEditor
					? "Disabled in the Editor: it would hang Unity. Try it in a build launched by AMPM."
					: "Blocks the app permanently. AMPM restarts it once its heartbeatTimeout passes (if heartbeatTimeout is set in ampm.json).";
			}

			float timeout = _mock.RestartTimeoutSeconds;
			if (timeout <= 0f)
				return "Blocks the app permanently, and the mock's restart timeout is off, so nothing restarts it." + (Application.isEditor ? " In the Editor it gives up after 60 s." : "");

			return Application.isEditor
				? "Blocks the app (and Unity) until the mock logs \"AMPM is restarting the app\" after " + AmpmSampleText.Seconds(timeout) + " and restarts Play mode. If the mock isn't listening, it gives up after 60 s."
				: "Blocks the app permanently. After " + AmpmSampleText.Seconds(timeout) + " the mock logs \"AMPM is restarting the app\", closes it and launches it again.";
		}

		private string ExplainCrash()
		{
			if (_mock == null)
			{
				return Application.isEditor
					? "Disabled in the Editor: it would crash Unity. Try it in a build launched by AMPM."
					: "Crashes the app for real. AMPM relaunches it if restartOnProcessExit is true in ampm.json.";
			}

			if (Application.isEditor)
			{
				return "Simulates a crash (Unity itself isn't crashed). " + (_mock.RestartOnProcessExit
					? "The mock sees the app exit, logs \"AMPM is restarting the app\" and restarts Play mode."
					: "Restart On Process Exit is off on the mock, so Play mode just stops.");
			}

			return "Crashes the app for real. " + (_mock.RestartOnProcessExit
				? "The mock logs \"AMPM is restarting the app\" and arranges a relaunch first, like AMPM's restartOnProcessExit."
				: "Restart On Process Exit is off on the mock, so nothing relaunches it.");
		}

		// ---- Freeze ----

		// Blocks the main thread like a real hang. seconds <= 0 means forever.
		private void Freeze(float seconds)
		{
			if (IgnoreWhileRestarting(seconds <= 0f ? "Freeze Forever" : "Freeze"))
				return;

			bool forever = seconds <= 0f;
			Debug.Log(Prefix + (forever ? "Freezing the app permanently. " : "Freezing the app for " + AmpmSampleText.Seconds(seconds) + ". ") + FreezeExpectation(forever, seconds));

			_releaseFreeze = false;
			bool editor = Application.isEditor;
			Stopwatch watch = Stopwatch.StartNew();
			while (!_releaseFreeze)
			{
				double elapsed = watch.Elapsed.TotalSeconds;
				if (!forever && elapsed >= seconds)
					break;
				if (forever && editor && elapsed >= EditorFreezeSafetySeconds)
				{
					Debug.LogWarning(Prefix + "Freeze Forever ended after " + EditorFreezeSafetySeconds + " s because nothing restarted the app. In the Editor it never lasts longer.");
					break;
				}
				Thread.Sleep(10);
			}

			if (_releaseFreeze)
				Debug.Log(Prefix + "Mock AMPM ended the freeze so it can restart Play mode.");
			else if (!forever)
				Debug.Log(Prefix + "The freeze ended after " + AmpmSampleText.Seconds(seconds) + " without a restart.");
			_releaseFreeze = false;
		}

		private string FreezeExpectation(bool forever, float seconds)
		{
			if (_mock == null)
				return "If AMPM's heartbeatTimeout is shorter" + (forever ? "" : " than " + AmpmSampleText.Seconds(seconds)) + ", AMPM should restart the app.";

			float timeout = _mock.RestartTimeoutSeconds;
			if (timeout <= 0f || (!forever && timeout >= seconds))
				return "Mock AMPM shouldn't restart the app.";
			return "Mock AMPM should restart the app after " + AmpmSampleText.Seconds(timeout) + " without a heartbeat.";
		}

		// ---- Events ----

		private void OnConfigLoaded(object sender, JObject config)
		{
			if (configText != null)
				configText.text = config.ToString(Formatting.Indented);
		}

		private void OnAmpmMessage(object sender, Tuple<string, JToken> message)
		{
			string data = message.Item2 == null ? "(no data)" : message.Item2.ToString(Formatting.None);
			_messages.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + message.Item1 + ": " + data);
			if (_messages.Count > MaxMessages)
				_messages.RemoveAt(_messages.Count - 1);
			_messagesChanged = true;
		}

		// Called from any thread.
		private void OnLogMessage(string condition, string stackTrace, LogType type)
		{
			string text = condition.Length > MaxLineLength ? condition.Substring(0, MaxLineLength) + "..." : condition;
			text = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
			if (type == LogType.Warning)
				text = AmpmSampleText.Yellow(text);
			else if (type != LogType.Log)
				text = AmpmSampleText.Red(text);

			lock (_log)
			{
				_log.Add(text);
				if (_log.Count > MaxLogLines)
					_log.RemoveAt(0);
			}
			_logChanged = true;
		}
	}
}
