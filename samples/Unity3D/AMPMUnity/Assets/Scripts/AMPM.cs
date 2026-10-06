using UnityEngine;
using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
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
	/// </summary>
	public static class AMPM
	{
		public delegate void ConfigLoadHandler();
		public static event ConfigLoadHandler OnConfigLoaded;

        private static bool _Connected = false;
		private static JObject _Config = null;

		public static event EventHandler<JObject> ConfigLoaded; //<JObject>
		public static event EventHandler<Tuple<string, JToken>> OnAmpmMessage;

        // The OSC server to receive OSC messages.
		private static OscServer _OscReceive;

		// The OSC client to send OSC messages to the local node.js server.
		private static OscClient _OscSend;

		// The destination for OSC messages to the local node.js server.
		private static IPAddress ipAddress;

        private static Queue<Tuple<string, object>> _MessageQueue = new Queue<Tuple<string, object>>();
			

		static AMPM()
		{
			// Create a OSC Reciever to receive UDP messages
			_OscReceive = new OscServer(3003);

			// Handle incoming OSC messages. An empty address receives every message.
			_OscReceive.MessageDispatcher.AddCallback(string.Empty, Server_MessageReceived);

			ipAddress = GetLocalIPAddress();
            _OscSend = new OscClient(ipAddress.ToString(), 3002); // Creating a client to send messages on

			// Close the client and server when the app quits (or play mode stops in the editor).
			Application.quitting += CloseOsc;
		}

		private static void CloseOsc()
		{
			if (_OscSend != null)
			{
				_OscSend.Dispose();
				_OscSend = null;
			}

			if (_OscReceive != null)
			{
				_OscReceive.Dispose();
				_OscReceive = null;
			}
		}

        public static IPAddress GetLocalIPAddress()
        {
            IPHostEntry host;
            try
            {
                 host = Dns.GetHostEntry("127.0.0.1");
            }
            catch(System.Net.Sockets.SocketException ex)
            {
                return new IPAddress(new byte[]{ 127,0,0,1 });
            }
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }
            throw new Exception("Local IP Address Not Found!");
        }
        public static void GetConfig(string url = "http://localhost:8888/config") {
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
			UdpEvent("log", new { level = eventLevel.ToString(), message = message });
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
			name = "/" + name;
			if (_OscSend == null)
			{
				Debug.LogError("Can't send OSC messages to AMPM. Client doesn't exist.");
				return;
			}

			if (data == null)
			{
				_OscSend.Send(name, "");
			}
			else
			{
				string d = JsonConvert.SerializeObject(data);
				_OscSend.Send(name, d);
			}
		}

		private static void Server_MessageReceived(string address, OscDataHandle oscData)
		{
			if (OnAmpmMessage == null)
			{
				return;
			}

			string name = address.Replace("/", string.Empty);
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

					OnAmpmMessage(null, new Tuple<string, JToken>(name, data));
		}

		[Serializable]
		private class TrackEvent
		{
			public string Category;
			public string Action;
			public string Label;
			public int Value;
		}
			
	}


}
