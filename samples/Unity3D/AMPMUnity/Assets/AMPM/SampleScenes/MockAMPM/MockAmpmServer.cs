using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OscJack;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Debug = UnityEngine.Debug;

namespace AmpmLib.Samples
{
	/// <summary>
	/// A stand-in for AMPM that runs inside the app, so the AMPM prefab can be tested without
	/// installing AMPM. Like AMPM it receives heartbeats, logs and events, serves /config,
	/// can send messages to the app, and restarts the app when heartbeats stop or it crashes.
	/// MockAmpmPanel shows its state. It registers as AMPMTestToolsManager's stand-in, so the AMPM Test
	/// button's Crash and Freeze Forever work in the Editor too. Don't run it while real AMPM is
	/// running: both use the same ports.
	/// </summary>
	[DefaultExecutionOrder(-20000)] // Listen before AMPMManager connects and requests the config.
	public class MockAmpmServer : MonoBehaviour, IAmpmStandIn
	{
		[Tooltip("JSON file served at /config, standing in for the config real AMPM builds from its ampm.json (MockAMPM/MockAmpmConfig.json by default). Its \"network\" ports also decide where the mock listens. Edit it before pressing Play.")]
		[SerializeField]
		private TextAsset configFile;

		[Tooltip("Off simulates AMPM being down.")]
		[SerializeField]
		private bool listenForApp = true;

		[Tooltip("Off: the app can't get its config and keeps retrying.")]
		[SerializeField]
		private bool serveConfig = true;

		[Tooltip("Seconds to wait before answering a config request.")]
		[Range(0f, 20f)]
		[SerializeField]
		private float configDelaySeconds = 0f;

		[Tooltip("Seconds without a heartbeat before the mock restarts the app, like heartbeatTimeout in ampm.json. 0 = never.")]
		[Range(0f, 30f)]
		[SerializeField]
		private float restartTimeoutSeconds = 5f;

		[Tooltip("Relaunch the app when it crashes, like restartOnProcessExit in ampm.json.")]
		[SerializeField]
		private bool restartOnProcessExit = true;

		[Header("Optional test buttons")]
		[Tooltip("Show the \"Send message to the app\" button. The AMPM server doesn't send messages to apps today, so it's hidden by default.")]
		[SerializeField]
		private bool enableSendMessageToApp = false;

		[Tooltip("Show the \"Send bad JSON to the app\" button, to test how the app handles a malformed message.")]
		[SerializeField]
		private bool enableSendBadJsonToApp = false;

		/// <summary>A snapshot of the heartbeats received, for display.</summary>
		public struct HeartbeatStats
		{
			public long Count;
			public double SecondsSinceLast; // -1 before the first heartbeat
			public double LongestGap;
			public double Fps;
		}

		private const string Prefix = "[Mock AMPM] ";
		private const string RestartReasonArg = "-ampmMockRestartReason";
#if UNITY_EDITOR
		private const string RestartReasonKey = "AmpmLib.Samples.MockAmpmServer.RestartReason";
#endif
		private const int MaxReceived = 50;
		private const int FpsSamples = 180; // AMPM averages the spacing of its last 180 heartbeats.

		private readonly object _lock = new object();
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();
		private readonly List<string> _received = new List<string>();
		private readonly double[] _intervals = new double[FpsSamples];

		private int _listenPort, _appPort, _configPort;
		private string _servedConfig;
		private string _configError, _listenError, _httpError;

		private OscServer _osc;
		private TcpListener _http;
		private Thread _watchdog;
		private volatile bool _running;
		private bool _isEditor;

		// Heartbeat tracking, guarded by _lock.
		private long _heartbeats;
		private double _lastHeartSeconds = -1;
		private double _longestGap;
		private int _intervalCount, _intervalIndex;
		private double _intervalSum;

		private volatile bool _restartRequested;
		private volatile bool _editorPaused;
		private string _restartReason;
		private bool _restartStarted;

#if UNITY_EDITOR
		// After a restart, keep trying to focus the Game view until this time (see Update).
		private const float RefocusSeconds = 3f;
		private const float RefocusInterval = 0.5f;
		private float _refocusUntil;
		private float _nextRefocus;
#endif

		public bool ListenForApp
		{
			get { return listenForApp; }
			set
			{
				if (value == listenForApp)
					return;
				listenForApp = value;
				if (value)
					StartListening();
				else
					StopListening();
			}
		}

		public bool ServeConfig
		{
			get { return serveConfig; }
			set
			{
				if (value == serveConfig)
					return;
				serveConfig = value;
				if (value)
					StartConfigServer();
				else
					StopConfigServer();
			}
		}

		public float ConfigDelaySeconds
		{
			get { return configDelaySeconds; }
			set { configDelaySeconds = Mathf.Max(0f, value); }
		}

		public float RestartTimeoutSeconds
		{
			get { return restartTimeoutSeconds; }
			set { restartTimeoutSeconds = Mathf.Max(0f, value); }
		}

		public bool RestartOnProcessExit
		{
			get { return restartOnProcessExit; }
			set { restartOnProcessExit = value; }
		}

		public bool EnableSendMessageToApp { get { return enableSendMessageToApp; } }
		public bool EnableSendBadJsonToApp { get { return enableSendBadJsonToApp; } }

		public bool IsListening { get { return _osc != null; } }
		public bool IsServingConfig { get { return _http != null; } }
		public int ListenPort { get { return _listenPort; } }
		public int AppPort { get { return _appPort; } }
		public int ConfigPort { get { return _configPort; } }
		public string ServedConfig { get { return _servedConfig; } }

		/// <summary>What the mock received from the app, newest first.</summary>
		public IReadOnlyList<string> Received { get { return _received; } }

		/// <summary>Changes whenever <see cref="Received"/> changes.</summary>
		public int ReceivedVersion { get; private set; }

		/// <summary>Problems starting the mock, e.g. a port already in use by real AMPM.</summary>
		public IEnumerable<string> Errors
		{
			get
			{
				foreach (string error in new[] { _configError, _listenError, _httpError })
				{
					if (error != null)
						yield return error;
				}
			}
		}

		public string RestartStatus
		{
			get
			{
				if (_restartRequested)
					return "Restarting: " + _restartReason;
				if (restartTimeoutSeconds <= 0f)
					return "Off (restart timeout is 0)";
				if (_osc == null)
					return "Not watching while the mock isn't listening";
				lock (_lock)
				{
					if (_lastHeartSeconds < 0)
						return "Starts watching at the first heartbeat";
				}
				return "After " + AmpmSampleText.Seconds(restartTimeoutSeconds) + " without a heartbeat";
			}
		}

		public HeartbeatStats GetHeartbeatStats()
		{
			lock (_lock)
			{
				return new HeartbeatStats
				{
					Count = _heartbeats,
					SecondsSinceLast = _lastHeartSeconds < 0 ? -1 : _clock.Elapsed.TotalSeconds - _lastHeartSeconds,
					LongestGap = _longestGap,
					Fps = _intervalCount > 0 && _intervalSum > 0 ? _intervalCount / _intervalSum : 0
				};
			}
		}

		public void SendMessageToApp()
		{
			SendToApp("{\"text\": \"Hello from Mock AMPM\"}");
		}

		public void SendBadJsonToApp()
		{
			SendToApp("{not valid json");
		}

		private void Awake()
		{
			_isEditor = Application.isEditor;
			AMPMTestToolsManager.StandIn = this;

			// A test harness: keep updating when the window loses focus, so clicking another window
			// doesn't stop the heartbeat and make the mock restart the app.
			Application.runInBackground = true;

			LoadConfig();
			_running = true;
			if (listenForApp)
				StartListening();
			if (serveConfig)
				StartConfigServer();

			_watchdog = new Thread(Watchdog) { IsBackground = true, Name = "Mock AMPM watchdog" };
			_watchdog.Start();

#if UNITY_EDITOR
			EditorApplication.pauseStateChanged += OnPauseStateChanged;
#endif
		}

		private void Start()
		{
			string reason = TakeRestartReason();
			if (reason == null)
				return;

			Debug.Log(Prefix + "The app was restarted by Mock AMPM: " + reason + ".");
#if UNITY_EDITOR
			// Play mode started from code leaves the Game view unfocused, and the UI ignores clicks
			// while the app has no focus. Focus it, as pressing Play does. A slow domain reload can
			// swallow the first attempt, so Update keeps trying for a few seconds.
			FocusGameView();
			_refocusUntil = Time.realtimeSinceStartup + RefocusSeconds;
			_nextRefocus = Time.realtimeSinceStartup + RefocusInterval;
#endif
		}

		private void Update()
		{
			while (_incoming.TryDequeue(out string item))
			{
				_received.Insert(0, item);
				if (_received.Count > MaxReceived)
					_received.RemoveAt(_received.Count - 1);
				ReceivedVersion++;
			}

#if UNITY_EDITOR
			if (_refocusUntil > 0f)
			{
				if (Application.isFocused || Time.realtimeSinceStartup > _refocusUntil)
				{
					if (!Application.isFocused)
						Debug.LogWarning(Prefix + "The Game view still isn't focused after the restart, so the UI ignores clicks. Click the Game view once. (Focused window: "
							+ (EditorWindow.focusedWindow != null ? EditorWindow.focusedWindow.GetType().Name : "none") + ")");
					_refocusUntil = 0f;
				}
				else if (Time.realtimeSinceStartup >= _nextRefocus)
				{
					_nextRefocus = Time.realtimeSinceStartup + RefocusInterval;
					FocusGameView();
				}
			}

			// In the Editor the restart happens here, on the main thread, once it's responsive again.
			if (_restartRequested && !_restartStarted)
			{
				_restartStarted = true;
				RestartPlayMode(_restartReason);
			}
#endif
		}

		private void OnDestroy()
		{
			_running = false;
			if (ReferenceEquals(AMPMTestToolsManager.StandIn, this))
				AMPMTestToolsManager.StandIn = null;
			StopListening();
			StopConfigServer();
#if UNITY_EDITOR
			EditorApplication.pauseStateChanged -= OnPauseStateChanged;
#endif
		}

		// ---- IAmpmStandIn: called by AMPMTestToolsManager's Crash ----

		/// <summary>
		/// Editor only: the app "crashed" without taking Unity down. Restart (or stop) Play mode,
		/// like AMPM reacting to the app's process exiting.
		/// </summary>
		public void SimulateCrash()
		{
			Debug.Log(Prefix + "The app's process exited (simulated crash).");
			if (restartOnProcessExit)
			{
				RequestRestart("the app's process exited (crash)");
				return;
			}

			Debug.Log(Prefix + "Restart On Process Exit is off, so the app stays closed.");
#if UNITY_EDITOR
			EditorApplication.isPlaying = false;
#endif
		}

		/// <summary>
		/// Build only: the app is about to crash for real and the mock dies with it, so arrange the
		/// relaunch now, like AMPM's restartOnProcessExit.
		/// </summary>
		public void PrepareForCrash()
		{
			lock (_lock)
				_restartRequested = true; // Stop the watchdog from restarting it a second time.

			if (!restartOnProcessExit)
			{
				Debug.Log(Prefix + "Restart On Process Exit is off, so nothing will relaunch the app after the crash.");
				return;
			}

			const string reason = "the app's process exited (crash)";
			Debug.Log(Prefix + "AMPM is restarting the app: " + reason + ".");
			ScheduleRelaunch(reason);
		}

		// ---- Config ----

		private void LoadConfig()
		{
			JObject config = new JObject();
			if (configFile == null)
			{
				_configError = "No config file is assigned on MockAmpmServer, so an empty config (default ports) is served.";
				Debug.LogWarning(Prefix + _configError);
			}
			else
			{
				try
				{
					config = JObject.Parse(configFile.text);
				}
				catch (JsonReaderException ex)
				{
					_configError = "'" + configFile.name + "' isn't valid JSON, so an empty config (default ports) is served: " + ex.Message;
					Debug.LogError(Prefix + _configError);
				}
			}

			// Like real AMPM, the served config always lists the network ports in use.
			JObject network = config["network"] as JObject ?? new JObject();
			config["network"] = network;
			_listenPort = ReadPort(network, "oscFromAppPort", 3002);
			_appPort = ReadPort(network, "oscToAppPort", 3003);
			_configPort = ReadPort(network, "socketToConsolePort", 8888);
			network["oscFromAppPort"] = _listenPort;
			network["oscToAppPort"] = _appPort;
			network["socketToConsolePort"] = _configPort;

			_servedConfig = config.ToString(Formatting.Indented);
		}

		private static int ReadPort(JObject network, string key, int fallback)
		{
			JToken token = network[key];
			return token != null && token.Type == JTokenType.Integer ? (int)token : fallback;
		}

		private void StartConfigServer()
		{
			try
			{
				var listener = new TcpListener(IPAddress.Loopback, _configPort);
				listener.Start();
				_http = listener;
				_httpError = null;
				new Thread(() => AcceptRequests(listener)) { IsBackground = true, Name = "Mock AMPM config server" }.Start();
			}
			catch (Exception ex)
			{
				_http = null;
				_httpError = "Couldn't serve the config on port " + _configPort + " (" + ex.Message + "). Is real AMPM running? Stop it to use the mock.";
				Debug.LogError(Prefix + _httpError);
			}
		}

		private void StopConfigServer()
		{
			TcpListener listener = _http;
			_http = null;
			if (listener != null)
				listener.Stop();
		}

		private void AcceptRequests(TcpListener listener)
		{
			while (true)
			{
				TcpClient client;
				try
				{
					client = listener.AcceptTcpClient();
				}
				catch (Exception)
				{
					return; // The listener was stopped.
				}
				ThreadPool.QueueUserWorkItem(_ => HandleRequest(client));
			}
		}

		// A minimal HTTP responder: GET /config returns the config, anything else 404.
		private void HandleRequest(TcpClient client)
		{
			try
			{
				using (client)
				{
					NetworkStream stream = client.GetStream();
					stream.ReadTimeout = 2000;
					string requestLine = ReadRequestLine(stream);

					float delay = configDelaySeconds;
					if (delay > 0f)
						Thread.Sleep(TimeSpan.FromSeconds(delay));

					bool isConfig = requestLine != null && requestLine.StartsWith("GET /config", StringComparison.Ordinal);
					byte[] body = Encoding.UTF8.GetBytes(isConfig ? _servedConfig : "Not found");
					string header = (isConfig ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found")
						+ "\r\nContent-Type: " + (isConfig ? "application/json" : "text/plain")
						+ "\r\nContent-Length: " + body.Length
						+ "\r\nConnection: close\r\n\r\n";
					byte[] head = Encoding.ASCII.GetBytes(header);
					stream.Write(head, 0, head.Length);
					stream.Write(body, 0, body.Length);

					if (isConfig)
						Enqueue("Served /config" + (delay > 0f ? " after " + AmpmSampleText.Seconds(delay) : ""));
				}
			}
			catch (Exception)
			{
				// The app gave up waiting (e.g. its 5 s timeout); it will ask again.
			}
		}

		private static string ReadRequestLine(NetworkStream stream)
		{
			var buffer = new byte[8192];
			int total = 0;
			while (total < buffer.Length)
			{
				int read = stream.Read(buffer, total, buffer.Length - total);
				if (read <= 0)
					break;
				total += read;

				string text = Encoding.ASCII.GetString(buffer, 0, total);
				if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
					return text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal));
			}
			return null;
		}

		// ---- Messages from the app ----

		private void StartListening()
		{
			try
			{
				var server = new OscServer(_listenPort);
				server.MessageDispatcher.AddCallback(string.Empty, OnAppMessage);
				lock (_lock)
				{
					_osc = server;
					_lastHeartSeconds = -1; // Wait for a fresh heartbeat before timing out.
				}
				_listenError = null;
			}
			catch (Exception ex)
			{
				_listenError = "Couldn't listen on port " + _listenPort + " (" + ex.Message + "). Is real AMPM running? Stop it to use the mock.";
				Debug.LogError(Prefix + _listenError);
			}
		}

		private void StopListening()
		{
			OscServer server;
			lock (_lock)
			{
				server = _osc;
				_osc = null;
			}

			// Dispose outside the lock: it waits for OscJack's thread, which may be waiting for the lock.
			if (server != null)
				server.Dispose();
		}

		// Runs on OscJack's background thread.
		private void OnAppMessage(string address, OscDataHandle data)
		{
			string payload = data.GetElementCount() > 0 ? data.GetElementAsString(0) : "";
			switch (address)
			{
				case "/heart":
					RecordHeartbeat();
					break;
				case "/restart":
					Enqueue("Restart requested by the app");
					RequestRestart("the app asked AMPM to restart it");
					break;
				case "/log":
					Enqueue(DescribeLog(payload));
					break;
				default:
					Enqueue(address + " " + payload);
					break;
			}
		}

		private static string DescribeLog(string payload)
		{
			try
			{
				JObject log = JObject.Parse(payload);
				return "Log [" + (string)log["level"] + "] " + (string)log["message"];
			}
			catch (JsonReaderException)
			{
				return "Log (not JSON) " + payload;
			}
		}

		private void RecordHeartbeat()
		{
			lock (_lock)
			{
				double now = _clock.Elapsed.TotalSeconds;
				if (_lastHeartSeconds >= 0)
				{
					double gap = now - _lastHeartSeconds;
					if (gap > _longestGap)
						_longestGap = gap;

					if (_intervalCount == FpsSamples)
						_intervalSum -= _intervals[_intervalIndex];
					else
						_intervalCount++;
					_intervals[_intervalIndex] = gap;
					_intervalSum += gap;
					_intervalIndex = (_intervalIndex + 1) % FpsSamples;
				}
				_lastHeartSeconds = now;
				_heartbeats++;
			}
		}

		private void Enqueue(string text)
		{
			_incoming.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
		}

		private void SendToApp(string json)
		{
			try
			{
				using (var client = new OscClient("127.0.0.1", _appPort))
					client.Send("/test", json);
				Enqueue("Sent to the app on port " + _appPort + ": " + json);
			}
			catch (Exception ex)
			{
				Enqueue("Couldn't send to the app: " + ex.Message);
			}
		}

		// ---- Restarting the app ----

		// Runs on its own thread, so it keeps watching while the app's main thread is frozen.
		private void Watchdog()
		{
			while (_running)
			{
				Thread.Sleep(100);

				float timeout = restartTimeoutSeconds;
				if (_restartRequested || _editorPaused || timeout <= 0f)
					continue;

				double gap;
				lock (_lock)
				{
					if (_osc == null || _lastHeartSeconds < 0)
						continue;
					gap = _clock.Elapsed.TotalSeconds - _lastHeartSeconds;
				}

				if (gap >= timeout)
					RequestRestart("no heartbeat for " + gap.ToString("0.0") + " s");
			}
		}

		// Can be called from any thread.
		private void RequestRestart(string reason)
		{
			lock (_lock)
			{
				if (_restartRequested)
					return;
				_restartRequested = true;
				_restartReason = reason;
			}

			Debug.Log(Prefix + "AMPM is restarting the app: " + reason + ".");

			if (_isEditor)
			{
				// End a freeze so the main thread can restart Play mode (see Update).
				AMPMTestToolsManager.EndFreeze();
				return;
			}

			// Like AMPM: close the app (even if it's frozen) and launch it again.
			ScheduleRelaunch(reason);
			Thread.Sleep(200); // Give the log line time to reach Player.log.
			Process.GetCurrentProcess().Kill();
		}

		// Launches the app again about 2 s from now, from a separate process. The delay lets this
		// process exit first: with Force Single Instance on, a copy started while it's alive would just close.
		private static void ScheduleRelaunch(string reason)
		{
#if UNITY_STANDALONE_WIN
			try
			{
				string exe = Process.GetCurrentProcess().MainModule.FileName;
				string command = "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe + "\" " + RestartReasonArg + " \"" + Sanitize(reason) + "\"";
				Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false });
			}
			catch (Exception ex)
			{
				Debug.LogError(Prefix + "Couldn't relaunch the app: " + ex.Message);
			}
#else
			Debug.LogWarning(Prefix + "Relaunching is only implemented for Windows builds.");
#endif
		}

		// Keeps the reason safe to pass through cmd.exe.
		private static string Sanitize(string text)
		{
			var builder = new StringBuilder();
			foreach (char c in text)
			{
				if (char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == ',' || c == ':' || c == '\'' || c == '(' || c == ')')
					builder.Append(c);
			}
			return builder.ToString();
		}

		private static string TakeRestartReason()
		{
#if UNITY_EDITOR
			string reason = SessionState.GetString(RestartReasonKey, null);
			SessionState.EraseString(RestartReasonKey);
			return string.IsNullOrEmpty(reason) ? null : reason;
#else
			string[] args = Environment.GetCommandLineArgs();
			for (int i = 0; i < args.Length - 1; i++)
			{
				if (args[i] == RestartReasonArg)
					return args[i + 1];
			}
			return null;
#endif
		}

#if UNITY_EDITOR
		// Exits Play mode, then enters it again: the Editor's equivalent of relaunching the app.
		private static void RestartPlayMode(string reason)
		{
			SessionState.SetString(RestartReasonKey, reason);
			EditorApplication.playModeStateChanged += EnterPlayModeAgain;
			EditorApplication.isPlaying = false;
		}

		private static void EnterPlayModeAgain(PlayModeStateChange change)
		{
			if (change != PlayModeStateChange.EnteredEditMode)
				return;
			EditorApplication.playModeStateChanged -= EnterPlayModeAgain;
			EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
		}

		// Gives the Game view a fresh focus, as clicking it does. If the Editor still counts it as
		// focused from before the restart, focusing it again does nothing and Play mode stays
		// unfocused, so focus another open window first and come back on the next Editor update.
		private static void FocusGameView()
		{
			Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
			if (gameView == null)
				return;

			EditorWindow focused = EditorWindow.focusedWindow;
			if (focused == null || focused.GetType() != gameView)
			{
				EditorWindow.FocusWindowIfItsOpen(gameView);
				return;
			}

			foreach (string name in new[] { "UnityEditor.InspectorWindow", "UnityEditor.SceneHierarchyWindow", "UnityEditor.ProjectBrowser", "UnityEditor.ConsoleWindow" })
			{
				Type other = typeof(EditorWindow).Assembly.GetType(name);
				if (other != null && Resources.FindObjectsOfTypeAll(other).Length > 0)
				{
					EditorWindow.FocusWindowIfItsOpen(other);
					break;
				}
			}
			EditorApplication.delayCall += () => EditorWindow.FocusWindowIfItsOpen(gameView);
		}

		private void OnPauseStateChanged(PauseState state)
		{
			_editorPaused = state == PauseState.Paused;

			// Time spent paused in the Editor isn't a missing heartbeat.
			lock (_lock)
			{
				if (_lastHeartSeconds >= 0)
					_lastHeartSeconds = _clock.Elapsed.TotalSeconds;
			}
		}
#endif
	}
}
