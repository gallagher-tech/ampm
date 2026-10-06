using UnityEngine;
using System.Collections;
using AmpmLib;

public class AmpmCommunicator : MonoBehaviour {

	// Use this for initialization
	void OnEnable () {
		// AMPM now needs an explicit Initialize. AmpmCommunicator is superseded by AMPMManager and will be removed.
		AMPM.Initialize(new AmpmSettings());
		// The blocking AMPM.GetConfig was removed; config loading now lives in AMPMManager.
		StartHeartBeat();

    }

	void StartHeartBeat ()
	{
		StopAllCoroutines ();
		Debug.Log("Starting App heartbeat");
		StartCoroutine ("HeartNow");
	}

	private IEnumerator HeartNow(){
		while (true) {
			AMPM.Heart ();
			yield return new WaitForSeconds ((1/60));
		}
	}
}
