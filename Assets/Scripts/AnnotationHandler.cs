using Microsoft.MixedReality.Toolkit.Input;
using UnityEngine;

public class AnnotationHandler : MonoBehaviour, IMixedRealityInputHandler
{
	private OpenSeriesHandler openSeries;
	public Annotation annotation;

	void Start()
	{
		openSeries = transform.parent.GetComponent<OpenSeriesHandler>();
	}

	public void OnInputDown(InputEventData eventData)
	{
	}

	public void OnInputUp(InputEventData eventData)
	{
		Debug.Log("annotation moved!");
		annotation.SetFromTransform(transform);
		if (openSeries.is3D)
		{
			annotation.frame = null;
		}
		else
		{
			annotation.frame = openSeries.frame;
		}
		openSeries.loadDicomInstance.SaveAnnotations();
	}
}
