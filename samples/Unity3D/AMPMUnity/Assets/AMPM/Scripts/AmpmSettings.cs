using System;
using UnityEngine;

namespace AmpmLib
{
	/// <summary>
	/// How the app reaches AMPM. The defaults match AMPM's defaults; only change them if the
	/// install's ampm.json changes the matching "network" values.
	/// </summary>
	[Serializable]
	public class AmpmSettings
	{
		[Tooltip("Address of the machine running AMPM. Almost always this machine (127.0.0.1).")]
		public string host = "127.0.0.1";

		[Tooltip("Port the app sends heartbeats, logs and events to. Matches oscFromAppPort in ampm.json (default 3002).")]
		public int sendPort = 3002;

		[Tooltip("Listen for messages sent by AMPM. The AMPM server doesn't send any today, so this is off by default.")]
		public bool listenForMessages = false;

		[Tooltip("Port the app listens on when Listen For Messages is on. Matches oscToAppPort in ampm.json (default 3003).")]
		public int listenPort = 3003;

		[Tooltip("Port of AMPM's web server, used to download the config. Matches socketToConsolePort in ampm.json (default 8888).")]
		public int configPort = 8888;

		public AmpmSettings Clone()
		{
			return (AmpmSettings)MemberwiseClone();
		}
	}
}
