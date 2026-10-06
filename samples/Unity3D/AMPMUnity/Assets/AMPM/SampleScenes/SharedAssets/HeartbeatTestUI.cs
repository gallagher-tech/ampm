using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace AmpmLib.Samples
{
	/// <summary>
	/// The app side of the AMPM heartbeat test scenes (the HeartbeatTestUI Canvas prefab): shows a clock
	/// that stops while the app is frozen, the heartbeats sent, AMPM's status, its config, messages
	/// received from AMPM and the app's own log. The tests themselves (restart, crash, freeze) are on
	/// the AMPMTestToolsManager prefab's AMPM Test button. With a MockAmpmServer in the scene the panel
	/// takes the right half of the screen and the real-AMPM build instructions are hidden.
	/// </summary>
	[DefaultExecutionOrder(-30000)] // Start capturing the app log before anything else logs.
	public class HeartbeatTestUI : MonoBehaviour
	{
		[Header("Layout")]
		[Tooltip("The scrolling panel. It takes the right half of the screen next to Mock AMPM, or the full width without it.")]
		[SerializeField]
		private RectTransform panel;

		[Tooltip("Shown only with real AMPM (no MockAmpmServer in the scene), e.g. the build instructions.")]
		[SerializeField]
		private GameObject[] realAmpmOnly;

		[Header("Live text")]
		[SerializeField] private Text titleText;
		[SerializeField] private Text subtitleText;
		[Tooltip("Updated every frame, so it visibly stops while the app is frozen.")]
		[SerializeField] private Text clockText;
		[SerializeField] private Text heartbeatCounterText;
		[SerializeField] private Text statusText;
		[SerializeField] private Text configText;
		[SerializeField] private Text messagesText;
		[SerializeField] private Text appLogText;

		private const float RefreshInterval = 0.25f;
		private const int MaxLogLines = 100;
		private const int MaxLineLength = 300;
		private const int MaxMessages = 20;

		private readonly List<string> _log = new List<string>();
		private readonly List<string> _messages = new List<string>();
		private volatile bool _logChanged = true;
		private bool _messagesChanged = true;
		private bool _hasMock;
		private float _nextRefresh;
		private long _lastHeartbeatCount;
		private float _lastRateTime;
		private float _heartbeatRate;

		private void Awake()
		{
			Application.logMessageReceivedThreaded += OnLogMessage;
			_hasMock = FindAnyObjectByType<MockAmpmServer>() != null;
			AmpmSampleLayout.FillParent(transform);

			// The right half of the screen next to Mock AMPM (which takes the left half), or the whole screen.
			if (panel != null)
			{
				const float margin = 10f;
				panel.anchorMin = new Vector2(_hasMock ? 0.5f : 0f, panel.anchorMin.y);
				panel.anchorMax = new Vector2(1f, panel.anchorMax.y);
				panel.offsetMin = new Vector2(_hasMock ? margin / 2f : margin, panel.offsetMin.y);
				panel.offsetMax = new Vector2(-margin, panel.offsetMax.y);
			}

			if (realAmpmOnly != null)
			{
				foreach (GameObject item in realAmpmOnly)
				{
					if (item != null)
						item.SetActive(!_hasMock);
				}
			}
		}

		private void Start()
		{
			if (titleText != null)
				titleText.text = _hasMock ? "Unity App - Mock AMPM Heartbeat Test" : "Unity App - Real AMPM Heartbeat Test";
			subtitleText.text = (_hasMock ? "Talking to Mock AMPM (left)." : "Talking to real AMPM.")
				+ (Application.isEditor ? " Running in the Editor." : " Running in a build.")
				+ " Use the AMPM Test button to restart, crash or freeze the app.";
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

		// ---- Display ----

		private void Refresh()
		{
			_nextRefresh = Time.realtimeSinceStartup + RefreshInterval;

			statusText.text = BuildStatus();

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
