using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace AmpmLib
{
	/// <summary>
	/// Connects the app to AMPM. Put the AMPM prefab in the first scene: it initializes AMPM,
	/// sends a heartbeat every frame, downloads AMPM's config, delivers AMPM's messages on the
	/// main thread and applies kiosk-friendly player settings. It persists across scene loads,
	/// and only one can exist.
	/// </summary>
	[DefaultExecutionOrder(-10000)] // Initialize AMPM before other scripts' Awake can call it.
	[DisallowMultipleComponent]
	public class AMPMManager : MonoBehaviour
	{
		[Serializable]
		public class ConfigLoadedEvent : UnityEvent<JObject> { }

		[Header("Connection")]
		[SerializeField]
		private AmpmSettings connection = new AmpmSettings();

		[Header("Config")]
		[Tooltip("Download AMPM's config when the app starts, retrying every 5 seconds until it arrives.")]
		[SerializeField]
		private bool loadConfig = true;

		[Tooltip("If AMPM's config lists different Send / Listen ports than the Connection settings, switch to AMPM's ports.")]
		[SerializeField]
		private bool usePortsFromConfig = true;

		[Tooltip("Hold the heartbeat until the config arrives (or Max Wait passes). Off: the heartbeat starts immediately.")]
		[SerializeField]
		private bool waitForConfigBeforeHeartbeat = false;

		[Tooltip("With Wait For Config Before Heartbeat on, start the heartbeat anyway after this many seconds. 0 waits forever.")]
		[Min(0f)]
		[SerializeField]
		private float maxWaitSeconds = 10f;

		[Header("Editor")]
		[Tooltip("Connect to AMPM in Play mode in the Editor. Off by default, so working in the Editor doesn't send heartbeats or request the config.")]
		[SerializeField]
		private bool runInEditor = false;

		[Header("Events")]
		[Tooltip("Called on the main thread when AMPM's config arrives. Code can use AMPM.Config or AMPM.ConfigLoaded instead.")]
		[SerializeField]
		private ConfigLoadedEvent onConfigLoaded = new ConfigLoadedEvent();

		private const float ConfigRetrySeconds = 5f;
		private static readonly TimeSpan ConfigRequestTimeout = TimeSpan.FromSeconds(5);
		private static readonly TimeSpan ConfigWarningInterval = TimeSpan.FromMinutes(1);

		// HttpClient rather than UnityWebRequest: UnityWebRequest refuses plain http:// unless the
		// project allows insecure HTTP, which the importing project may not.
		private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false })
		{
			Timeout = ConfigRequestTimeout
		};

		private static AMPMManager _instance;

		private bool _heartbeatStarted;
		private float _waitStartTime;
		private CancellationTokenSource _configCancellation;

		private void Awake()
		{
			// Only one manager: loading a scene again (e.g. returning to a menu) would otherwise add another.
			if (_instance != null && _instance != this)
			{
				Debug.LogWarning("AMPM: an AMPMManager already exists, so the duplicate on '" + name + "' was removed.");
				enabled = false;
				Destroy(gameObject);
				return;
			}
			_instance = this;

			// DontDestroyOnLoad only works on root objects.
			if (transform.parent != null)
			{
				Debug.LogWarning("AMPM: '" + name + "' was moved to the root of the hierarchy so it can persist across scenes.");
				transform.SetParent(null);
			}
			DontDestroyOnLoad(gameObject);

			if (Application.isEditor && !runInEditor)
			{
				Debug.Log("AMPM: not connecting in the Editor. Turn on Run In Editor on the AMPM prefab to test with AMPM.");
				enabled = false;
				return;
			}

			WarnIfNotRunningInBackground();
			AMPM.Initialize(connection);

			_heartbeatStarted = !(loadConfig && waitForConfigBeforeHeartbeat);
			_waitStartTime = Time.realtimeSinceStartup;

			if (loadConfig)
			{
				_configCancellation = new CancellationTokenSource();
				_ = LoadConfigAsync(_configCancellation.Token);
			}
		}

		// Runs every frame while enabled, so the heartbeat stops automatically when the component is disabled.
		private void Update()
		{
			AMPM.ProcessMessages();

			if (!_heartbeatStarted)
			{
				if (AMPM.Config != null)
				{
					_heartbeatStarted = true;
				}
				else if (maxWaitSeconds > 0f && Time.realtimeSinceStartup - _waitStartTime >= maxWaitSeconds)
				{
					Debug.LogWarning("AMPM: the config hasn't arrived after " + maxWaitSeconds + " seconds, so the heartbeat is starting without it.");
					_heartbeatStarted = true;
				}
				else
				{
					return;
				}
			}

			// One heartbeat per rendered frame: AMPM shows the app's FPS from the spacing between heartbeats.
			AMPM.Heart();
		}

		private void OnDestroy()
		{
			if (_configCancellation != null)
				_configCancellation.Cancel();

			if (_instance == this)
				_instance = null;
		}

		/// <summary>
		/// Asks AMPM to restart the app. Public so a UI Button's On Click can call it.
		/// </summary>
		public void RequestRestart()
		{
			AMPM.Restart();
		}

		// Downloads AMPM's config, retrying until it arrives. Awaits resume on Unity's main thread,
		// so the config is applied and its events are raised on the main thread.
		private async Task LoadConfigAsync(CancellationToken cancellation)
		{
			string url = "http://" + connection.host + ":" + connection.configPort + "/config";
			DateTime lastWarning = DateTime.MinValue;

			while (!cancellation.IsCancellationRequested)
			{
				JObject config = null;
				string error = null;

				try
				{
					using (HttpResponseMessage response = await Http.GetAsync(url, cancellation))
					{
						response.EnsureSuccessStatusCode();
						config = JObject.Parse(await response.Content.ReadAsStringAsync());
					}
				}
				catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
				{
					return;
				}
				catch (TaskCanceledException)
				{
					error = "no response within " + ConfigRequestTimeout.TotalSeconds + " seconds";
				}
				catch (Exception ex)
				{
					error = ex.GetBaseException().Message;
				}

				if (cancellation.IsCancellationRequested)
					return;

				if (config != null)
				{
					// Outside the try, so an exception in a ConfigLoaded handler isn't treated as a failed download.
					ApplyConfig(config);
					return;
				}

				// Warn on the first failure, then at most once a minute.
				if (DateTime.UtcNow - lastWarning >= ConfigWarningInterval)
				{
					lastWarning = DateTime.UtcNow;
					Debug.LogWarning("AMPM: couldn't load the config from " + url + " (" + error + "). Retrying every " + ConfigRetrySeconds + " seconds.");
				}

				try
				{
					await Task.Delay(TimeSpan.FromSeconds(ConfigRetrySeconds), cancellation);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		}

		private void ApplyConfig(JObject config)
		{
			if (usePortsFromConfig)
				ApplyPortsFromConfig(config);

			AMPM.SetConfig(config);
			onConfigLoaded.Invoke(config);
		}

		// AMPM's /config always includes the "network" ports it is actually using.
		private void ApplyPortsFromConfig(JObject config)
		{
			AmpmSettings current = AMPM.CurrentSettings;
			if (current == null)
				return;

			int sendPort = ReadPort(config, "network.oscFromAppPort", current.sendPort);
			int listenPort = ReadPort(config, "network.oscToAppPort", current.listenPort);

			var changes = new List<string>();
			if (sendPort != current.sendPort)
				changes.Add("Send Port " + current.sendPort + " -> " + sendPort);
			if (listenPort != current.listenPort && current.listenForMessages)
				changes.Add("Listen Port " + current.listenPort + " -> " + listenPort);

			if (changes.Count == 0)
				return;

			Debug.LogWarning("AMPM: AMPM's config overrides " + string.Join(", ", changes) + ". Update the AMPM prefab's Connection settings to match.");
			AMPM.ChangePorts(sendPort, listenPort);
		}

		private static int ReadPort(JObject config, string path, int fallback)
		{
			JToken token = config.SelectToken(path);
			if (token == null)
				return fallback;

			try
			{
				return (int)token;
			}
			catch (Exception)
			{
				Debug.LogWarning("AMPM: ignoring '" + path + "' in AMPM's config because it isn't a port number: " + token);
				return fallback;
			}
		}

		// Without Run In Background, Unity pauses whenever the window loses focus, the heartbeat stops,
		// and AMPM restarts an app that was working fine. The project's Player Settings decide; this
		// only makes sure nobody misses it.
		private static void WarnIfNotRunningInBackground()
		{
			if (Application.runInBackground)
				return;

			Debug.LogError("AMPM WARNING: Player Setting \"Run In Background\" is OFF. AMPM may restart the app whenever it loses focus, "
				+ "because Unity pauses and stops sending heartbeats. Turn it on in Edit > Project Settings > Player > Resolution and Presentation > Run In Background.");
		}

		// Statics survive between play sessions when the domain reload is skipped.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetInstance()
		{
			_instance = null;
		}
	}
}
