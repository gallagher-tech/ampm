using UnityEngine;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using OscJack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;


namespace AmpmLib
{
	/// <summary>
	/// Interface for sending things to ampm.
	/// Call <see cref="Initialize"/> once before anything else; the AMPM prefab (AMPMManager) does this for you.
	/// </summary>
	public static class AMPM
	{
		// Log levels. AMPM's server calls logger[level], so ToServerName must return its exact method names.
		private enum EventLevel { Error, Warn, Info }

		// OscJack builds every packet in a fixed 4096-byte buffer.
		private const int OscBufferSize = 4096;

		// Repeating warnings (e.g. a send failing every frame) are logged at most this often.
		private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(10);

		// Escape non-ASCII characters (é -> é): OscJack writes one byte per character, and
		// AMPM's JSON.parse turns the escapes back into the original characters.
		private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
		{
			StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
		};

		public delegate void ConfigLoadHandler();
		public static event ConfigLoadHandler OnConfigLoaded;

		private static JObject _Config = null;

		public static event EventHandler<JObject> ConfigLoaded; //<JObject>
		public static event EventHandler<Tuple<string, JToken>> OnAmpmMessage;

		// The settings passed to Initialize.
		private static AmpmSettings _Settings;

		// Whether Initialize has completed (cleared again when the app quits).
		private static bool _Initialized;

		// The OSC server to receive OSC messages. Null unless Listen For Messages is on.
		private static OscServer _OscReceive;

		// The OSC client to send OSC messages to the local node.js server.
		private static OscClient _OscSend;

		// OscJack's client isn't thread-safe, so sending and closing are serialized on this lock.
		private static readonly object _SendLock = new object();

		// Messages from AMPM arrive on OscJack's background thread; they wait here until
		// ProcessMessages delivers them on the main thread. Capped so it can't grow forever.
		private const int MaxQueuedMessages = 1000;
		private static readonly Queue<Tuple<string, JToken>> _MessageQueue = new Queue<Tuple<string, JToken>>();

		// Rate limiting for the send-failure warning.
		private static readonly object _WarningLock = new object();
		private static DateTime _LastSendWarning = DateTime.MinValue;
		private static int _SuppressedSendWarnings;

		// Messages sent before Initialize are skipped; only the first one is reported.
		private static bool _WarnedNotInitialized;

		/// <summary>
		/// Opens the connection to AMPM. Calls after the first successful one are ignored.
		/// </summary>
		/// <returns>True if this call initialized AMPM.</returns>
		public static bool Initialize(AmpmSettings settings)
		{
			if (_Initialized)
			{
				Debug.LogWarning("AMPM: Initialize was called again and has been ignored. AMPM is already initialized.");
				return false;
			}

			_Settings = (settings ?? new AmpmSettings()).Clone();

			try
			{
				_OscSend = new OscClient(ResolveHost(_Settings.host), _Settings.sendPort);
			}
			catch (Exception ex)
			{
				Debug.LogError("AMPM: couldn't set up sending to " + _Settings.host + ":" + _Settings.sendPort + ". " + ex.Message);
				return false;
			}

			if (_Settings.listenForMessages)
			{
				try
				{
					// Create a OSC Receiver to receive UDP messages
					_OscReceive = new OscServer(_Settings.listenPort);

					// Handle incoming OSC messages. An empty address receives every message.
					_OscReceive.MessageDispatcher.AddCallback(string.Empty, Server_MessageReceived);
				}
				catch (Exception ex)
				{
					// Keep sending even if listening fails, e.g. because the port is already in use.
					Debug.LogError("AMPM: couldn't listen for messages on port " + _Settings.listenPort + ". " + ex.Message);
					_OscReceive = null;
				}
			}

			_Initialized = true;

			// Close the client and server when the app quits (or play mode stops in the editor).
			Application.quitting -= Close;
			Application.quitting += Close;
			return true;
		}

		// Statics survive between play sessions when Unity's "Enter Play Mode Options" skip the
		// domain reload, so start every run from a clean state.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetState()
		{
			Close();
			_Settings = null;
			_Config = null;
			OnConfigLoaded = null;
			ConfigLoaded = null;
			OnAmpmMessage = null;
			lock (_MessageQueue)
				_MessageQueue.Clear();
			_LastSendWarning = DateTime.MinValue;
			_SuppressedSendWarnings = 0;
			_WarnedNotInitialized = false;
		}

		private static void Close()
		{
			lock (_SendLock)
			{
				if (_OscSend != null)
				{
					_OscSend.Dispose();
					_OscSend = null;
				}
			}

			if (_OscReceive != null)
			{
				_OscReceive.Dispose();
				_OscReceive = null;
			}

			_Initialized = false;
		}

		// OscJack needs an IPv4 address; also accept host names such as "localhost".
		private static string ResolveHost(string host)
		{
			if (IPAddress.TryParse(host, out IPAddress address))
				return address.ToString();

			foreach (IPAddress candidate in Dns.GetHostAddresses(host))
			{
				if (candidate.AddressFamily == AddressFamily.InterNetwork)
					return candidate.ToString();
			}
			throw new Exception("No IPv4 address found for host '" + host + "'.");
		}

        public static void GetConfig(string url = null) {
			if (url == null)
			{
				AmpmSettings settings = _Settings ?? new AmpmSettings();
				url = "http://" + settings.host + ":" + settings.configPort + "/config";
			}

			try
			{
                // load the url
                string strContent;
                var webRequest = WebRequest.Create(@url);
                using (var response = webRequest.GetResponse())
                using (var content = response.GetResponseStream())
                using (var reader = new StreamReader(content))
                {
                    strContent = reader.ReadToEnd();
                }

                // parse it as json
                _Config = JObject.Parse(strContent);

                // fire OnConfigLoaded
                if (OnConfigLoaded != null)
                    OnConfigLoaded();
            }
			catch (Exception e)
			{

				throw;
			}
		}

		/// <summary>
		/// Send a heartbeat message.
		/// </summary>
		public static void Heart()
		{
			UdpEvent("heart");
		}

		/// <summary>
		/// Ask AMPM to restart the app. AMPM closes the app and launches its launchCommand again.
		/// </summary>
		public static void Restart()
		{
			UdpEvent("restart");
		}

		/// <summary>
		/// Log a usage event.
		/// </summary>
		/// <param name="category"></param>
		/// <param name="action"></param>
		/// <param name="label"></param>
		/// <param name="value"></param>
		public static void LogEvent(string category = null, string action = null, string label = null, int value = 0)
		{
			TrackEvent e = new TrackEvent { Category = category, Action = action, Label = label, Value = value };
			SendEvent(e);
		}

		private static void LogMessage(EventLevel eventLevel, string message)
		{
			// Short reference ID that ties the AMPM log entry to the full local copy in Player.log,
			// so the complete message can still be found if the AMPM copy is trimmed.
			// Debug.Log (not LogError) so the local copy doesn't pop the in-game console or get
			// re-forwarded if Unity's own errors are ever routed to AMPM.
			string refId = Guid.NewGuid().ToString("N").Substring(0, 8);
			string level = ToServerName(eventLevel);
			Debug.Log("[AMPM ref " + refId + "] " + level + ": " + message);

			string text = "[ref " + refId + "] " + message;
			int maxLength = MaxPayloadLength("log");
			if (SerializeLog(level, text).Length > maxLength)
				text = AmpmLogTrimmer.Trim(text, candidate => SerializeLog(level, candidate).Length <= maxLength);

			Send("log", SerializeLog(level, text));
		}

		private static string SerializeLog(string level, string message)
		{
			return JsonConvert.SerializeObject(new { level = level, message = message }, JsonSettings);
		}

		private static string ToServerName(EventLevel level)
		{
			switch (level)
			{
				case EventLevel.Error: return "error";
				case EventLevel.Warn: return "warn";
				default: return "info";
			}
		}

		/// <summary>
		/// Log an error.
		/// </summary>
		/// <param name="message"></param>
		public static void Error(string message)
		{
			LogMessage(EventLevel.Error, message);
		}

		/// <summary>
		/// Log a warning.
		/// </summary>
		/// <param name="message"></param>
		public static void Warn(string message)
		{
			LogMessage(EventLevel.Warn, message);
		}

		/// <summary>
		/// Log informational info.
		/// </summary>
		/// <param name="message"></param>
		public static void Info(string message)
		{
			LogMessage(EventLevel.Info, message);
		}

		private static void SendEvent(TrackEvent e)
		{
			UdpEvent("event", e);
		}

		public static void UdpEvent(string name, object data = null)
		{
			string payload = data == null ? "" : JsonConvert.SerializeObject(data, JsonSettings);
			if (payload.Length > MaxPayloadLength(name))
			{
				Debug.LogWarning("AMPM: '" + name + "' message is too large to send (" + payload.Length + " characters) and was skipped.");
				return;
			}

			Send(name, payload);
		}

		private static bool Send(string name, string payload)
		{
			if (!_Initialized)
			{
				if (!_WarnedNotInitialized)
				{
					_WarnedNotInitialized = true;
					Debug.LogWarning("AMPM: '" + name + "' wasn't sent because AMPM isn't initialized. Add the AMPM prefab (AMPMManager) to your first scene, or call AMPM.Initialize. Later messages sent before initialization are skipped without a warning.");
				}
				return false;
			}

			try
			{
				lock (_SendLock)
				{
					// Closed by another thread (e.g. on quit) since the check above.
					if (_OscSend == null)
						return false;
					_OscSend.Send("/" + name, payload);
				}
				return true;
			}
			catch (Exception ex)
			{
				ReportSendFailure(name, ex);
				return false;
			}
		}

		// A failing send can repeat every frame (the heartbeat), so warn at most once per WarningInterval.
		private static void ReportSendFailure(string name, Exception ex)
		{
			string suppressed;
			lock (_WarningLock)
			{
				DateTime now = DateTime.UtcNow;
				if (now - _LastSendWarning < WarningInterval)
				{
					_SuppressedSendWarnings++;
					return;
				}

				suppressed = _SuppressedSendWarnings > 0 ? " (" + _SuppressedSendWarnings + " more failures since the last warning)" : "";
				_LastSendWarning = now;
				_SuppressedSendWarnings = 0;
			}

			Debug.LogWarning("AMPM: couldn't send '" + name + "' to " + _Settings.host + ":" + _Settings.sendPort + ". " + ex.Message + suppressed);
		}

		// The longest payload that fits in one OscJack packet alongside the address and type tag.
		private static int MaxPayloadLength(string name)
		{
			int addressBytes = Align4(name.Length + 2); // "/" + name + null terminator
			const int typeTagBytes = 4;                 // ",s" + null terminator
			return OscBufferSize - addressBytes - typeTagBytes - 1; // - 1 for the payload's null terminator
		}

		private static int Align4(int length)
		{
			return (length + 3) & ~3;
		}

		/// <summary>
		/// Delivers messages received from AMPM to <see cref="OnAmpmMessage"/> on the calling thread.
		/// AMPMManager calls this every frame; call it from Update yourself if you don't use AMPMManager.
		/// </summary>
		public static void ProcessMessages()
		{
			List<Tuple<string, JToken>> messages;
			lock (_MessageQueue)
			{
				if (_MessageQueue.Count == 0)
					return;
				messages = new List<Tuple<string, JToken>>(_MessageQueue);
				_MessageQueue.Clear();
			}

			foreach (Tuple<string, JToken> message in messages)
			{
				try
				{
					OnAmpmMessage?.Invoke(null, message);
				}
				catch (Exception ex)
				{
					// One failing handler shouldn't stop the remaining messages from being delivered.
					Debug.LogException(ex);
				}
			}
		}

		// Runs on OscJack's background thread: parse the message and queue it for ProcessMessages.
		private static void Server_MessageReceived(string address, OscDataHandle oscData)
		{
			if (OnAmpmMessage == null)
			{
				return;
			}

			// Strip only the leading slash so nested addresses like "/app/restart" keep their structure.
			string name = address.StartsWith("/") ? address.Substring(1) : address;
			string json = oscData.GetElementCount() > 0 ? oscData.GetElementAsString(0) : null;
			JToken data = null;
			if (json != null)
			{
				// Json.NET throws on malformed input. Catch it here so one bad message
				// doesn't propagate into the OSC receive thread and stop it listening.
				try
				{
					data = JToken.Parse(json);
				}
				catch (JsonReaderException ex)
				{
					Debug.LogWarning("AMPM: ignoring malformed JSON in '" + name + "' message: " + ex.Message);
				}
			}

			lock (_MessageQueue)
			{
				if (_MessageQueue.Count >= MaxQueuedMessages)
				{
					// Nothing is calling ProcessMessages; drop the oldest message.
					_MessageQueue.Dequeue();
				}
				_MessageQueue.Enqueue(new Tuple<string, JToken>(name, data));
			}
		}

		private class TrackEvent
		{
			public string Category;
			public string Action;
			public string Label;
			public int Value;
		}

	}


}
