﻿using HoloToolkit.Unity.InputModule;
using HoloToolkit.Unity.InputModule.Utilities.Interactions;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace HoloToolkit.Unity.SharingWithUNET
{
	[RequireComponent(typeof(TwoHandManipulatable))]
	public class SyncLocalTransformUNET : NetworkBehaviour
	{
		private Vector3 lastPos;
		private Quaternion lastRot;
		private TwoHandManipulatable twoHandManipulatable;

		IEnumerator SyncTransform()
		{
			while (true)
			{
				if (twoHandManipulatable.currentState != ManipulationMode.None && (transform.localPosition != lastPos || transform.localRotation != lastRot))
				{
					PlayerController.Instance.SendSharedTransform(gameObject, transform.localPosition, transform.localRotation);
					lastPos = transform.localPosition;
					lastRot = transform.localRotation;
				}
				yield return new WaitForSeconds(1/30f);
			}
		}

		[ClientRpc(channel = Channels.DefaultUnreliable)]
		public void RpcSetLocalTransform(Vector3 position, Quaternion rotation)
		{
			if (twoHandManipulatable.currentState == ManipulationMode.None)
			{
				transform.localPosition = position;
				transform.localRotation = rotation;
			}
		}

		public void ResetTransform(Vector3 newPos, Quaternion newRot) {
			PlayerController.Instance.SendSharedTransform(gameObject, newPos, newRot);
			lastPos = newPos;
			lastRot = newRot;
		}

		public void SetSavedPosition(Vector3 newPos, Quaternion newRot) {
			PlayerController.Instance.SendSharedSavedPosition(gameObject, transform.localPosition, transform.localRotation);
		}


		[ClientRpc(channel = Channels.DefaultUnreliable)]
		public void RpcSetSavedTransform(Vector3 position, Quaternion rotation)
		{
			GetComponent<Position>().SetSavedTransform(position, rotation);
		}

		public void Start()
		{
			lastPos = transform.localPosition;
			lastRot = transform.localRotation;
			twoHandManipulatable = GetComponent<TwoHandManipulatable>();
			StartCoroutine(SyncTransform());
		}
	}
}