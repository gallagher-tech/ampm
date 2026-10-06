using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace AmpmLib.Samples
{
	/// <summary>
	/// The Mock AMPM panel (left half of the screen): shows what the mock receives and lets you
	/// change how it behaves. The prefab's toggles, sliders and buttons call the methods below.
	/// </summary>
	public class MockAmpmPanel : MonoBehaviour
	{
		[SerializeField] private MockAmpmServer server;

		[Header("Live text")]
		[SerializeField] private Text statusText;
		[SerializeField] private Text receivedText;
		[SerializeField] private Text servedConfigText;
		[SerializeField] private Text listenLabel;
		[SerializeField] private Text serveLabel;
		[SerializeField] private Text delayLabel;
		[SerializeField] private Text sendMessageExplanation;

		[Header("Optional buttons (shown by the flags on MockAmpmServer)")]
		[SerializeField] private GameObject sendMessageButton;
		[SerializeField] private GameObject badJsonButton;
		[SerializeField] private Text badJsonExplanation;

		[Header("Controls")]
		[SerializeField] private Toggle listenToggle;
		[SerializeField] private Toggle serveToggle;
		[SerializeField] private Toggle restartOnExitToggle;
		[SerializeField] private Toggle showConfigToggle;
		[SerializeField] private Slider delaySlider;

		private const float RefreshInterval = 0.25f;
		private float _nextRefresh;
		private int _shownReceivedVersion = -1;

		private void Awake()
		{
			AmpmSampleLayout.FillParent(transform);
		}

		private void Start()
		{
			// Match the controls to the server's settings without triggering their callbacks.
			listenToggle.SetIsOnWithoutNotify(server.ListenForApp);
			serveToggle.SetIsOnWithoutNotify(server.ServeConfig);
			restartOnExitToggle.SetIsOnWithoutNotify(server.RestartOnProcessExit);
			delaySlider.SetValueWithoutNotify(server.ConfigDelaySeconds);

			servedConfigText.text = server.ServedConfig;
			servedConfigText.gameObject.SetActive(showConfigToggle.isOn);
			sendMessageExplanation.text = "Sends {\"text\": \"Hello from Mock AMPM\"} to the app on port " + server.AppPort
				+ ". It appears under Messages from AMPM on the right (needs Listen For Messages on the AMPM object).";
			Refresh();
		}

		private void Update()
		{
			if (Time.realtimeSinceStartup >= _nextRefresh)
				Refresh();
		}

		// ---- Called by the prefab's controls ----

		public void SetListen(bool on)
		{
			server.ListenForApp = on;
			Refresh();
		}

		public void SetServeConfig(bool on)
		{
			server.ServeConfig = on;
			Refresh();
		}

		public void SetConfigDelay(float seconds)
		{
			server.ConfigDelaySeconds = Mathf.Round(seconds);
			Refresh();
		}

		public void SetRestartOnProcessExit(bool on)
		{
			server.RestartOnProcessExit = on;
		}

		public void SetShowConfig(bool on)
		{
			servedConfigText.gameObject.SetActive(on);
		}

		public void SendMessageToApp()
		{
			server.SendMessageToApp();
		}

		public void SendBadJsonToApp()
		{
			server.SendBadJsonToApp();
		}

		// ---- Display ----

		private void Refresh()
		{
			_nextRefresh = Time.realtimeSinceStartup + RefreshInterval;

			listenLabel.text = "Listen for the app on port " + server.ListenPort;
			serveLabel.text = "Serve the config on port " + server.ConfigPort;
			delayLabel.text = "Config delay: " + AmpmSampleText.Seconds(server.ConfigDelaySeconds);

			// Checked on every refresh, so changing the flags in the Inspector during Play shows or hides them.
			SetVisible(sendMessageButton, sendMessageExplanation, server.EnableSendMessageToApp);
			SetVisible(badJsonButton, badJsonExplanation, server.EnableSendBadJsonToApp);

			statusText.text = BuildStatus();

			if (_shownReceivedVersion != server.ReceivedVersion)
			{
				_shownReceivedVersion = server.ReceivedVersion;
				receivedText.text = server.Received.Count == 0
					? "Nothing yet. Heartbeats are counted above, not listed."
					: string.Join("\n", server.Received);
			}
		}

		private static void SetVisible(GameObject button, Text explanation, bool visible)
		{
			if (button.activeSelf != visible)
				button.SetActive(visible);
			if (explanation.gameObject.activeSelf != visible)
				explanation.gameObject.SetActive(visible);
		}

		private string BuildStatus()
		{
			MockAmpmServer.HeartbeatStats stats = server.GetHeartbeatStats();
			var builder = new StringBuilder();

			foreach (string error in server.Errors)
				builder.Append(AmpmSampleText.Yellow(error)).Append('\n');

			builder.Append(AmpmSampleText.Line("Listening", server.IsListening ? "Port " + server.ListenPort : AmpmSampleText.Red("No"))).Append('\n');
			builder.Append(AmpmSampleText.Line("Config server", server.IsServingConfig ? "http://127.0.0.1:" + server.ConfigPort + "/config" : AmpmSampleText.Red("Off"))).Append('\n');
			builder.Append(AmpmSampleText.Line("Heartbeats", stats.Count + "  (app FPS " + stats.Fps.ToString("0") + ", worked out like AMPM's console)")).Append('\n');
			builder.Append(AmpmSampleText.Line("Since last", stats.SecondsSinceLast < 0 ? "Waiting for the first heartbeat" : stats.SecondsSinceLast.ToString("0.00") + " s")).Append('\n');
			builder.Append(AmpmSampleText.Line("Longest gap", stats.LongestGap.ToString("0.00") + " s")).Append('\n');

			string restart = server.RestartStatus;
			builder.Append(AmpmSampleText.Line("Restart", restart.StartsWith("Restarting") ? AmpmSampleText.Yellow(restart) : restart)).Append('\n');
			builder.Append(AmpmSampleText.Line("Restart timeout", AmpmSampleText.Seconds(server.RestartTimeoutSeconds) + "  (set in the Mock Unity App, next to the freeze length)"));
			return builder.ToString();
		}
	}
}
