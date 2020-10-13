﻿using UnityEngine;
using Dicom.Imaging;
using System.IO;
using Dicom.Media;
using Dicom;
using System.Linq;
using Dicom.Imaging.LUT;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine.XR.WSA.Input;
using Microsoft.MixedReality.Toolkit.Input;
//using HoloToolkit.Unity.InputModule.Utilities.Interactions;
//using HoloToolkit.Examples.InteractiveElements;
using System;
//using HoloToolkit.Unity.UX;
using HoloToolkit.Unity;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.UI;
using Unity.Mathematics;
using Assets.Scripts.HoloToolkitLegacy;

public class LoadDICOM : MonoBehaviour
{
	public Transform filebrowser;
	public GameObject quadPrefab;
	public GameObject annotationPrefab;
	public GameObject testQuad;
	public GameObject pin;
	public TextMesh status;
	public GameObject annotationsList;
	public GameObject annotationsListElementPrefab;
	private Dictionary<GameObject, DicomDirectoryRecord> directoryMap;
	public Dictionary<DicomDirectoryRecord, string> rootDirectoryMap;
	private GestureRecognizer recognizer;
	private Vector3 offset = Vector3.zero;
	private Dictionary<GameObject, bool> openedItems;
	private GameObject selectedObject = null;
	private static Dictionary<short, Color> huToColor = new Dictionary<short, Color>{
		{ -1000, Color.black }, // air
		{ -500, new Color(194/255f, 105/255f, 82/255f) }, // lung
		{ -80, new Color(194/255f, 166/255f, 115/255f) }, // fat
		{ 40, new Color(102/255f, 0, 0) }, // soft tissue
		{ 80, new Color(153/255f, 0, 0) }, // soft tissue
		{ 400, Color.white } // bone
	};
	private AnnotationCollection annotations = new AnnotationCollection();
	private string annotationPath;
	public string[] meshMarkers;

	public string GetDicomTag(DicomDirectoryRecord record, DicomTag tag)
	{
		return string.Join(",", record.Get<string[]>(tag, new string[] { "" }));
	}

	public static void PrintTagsForRecord(dynamic record)
	{
#if UNITY_EDITOR
		foreach (var field in typeof(DicomTag).GetFields())
		{
			try
			{
				Debug.Log(field.Name + ":" + string.Join(",", record.Get<string[]>((DicomTag)field.GetValue(null))));
			}
			catch (System.Exception e)
			{
			}
		}
#endif
	}

	static short[] ConvertByteArray(byte[] bytes)
	{
		var size = bytes.Length / sizeof(short);
		var shorts = new short[size];
		for (var index = 0; index < size; index++)
		{
			shorts[index] = System.BitConverter.ToInt16(bytes, index * sizeof(short));
		}
		return shorts;
	}

	static Color HounsfieldUnitsToColor(float intensity)
	{
		if (huToColor.First().Key > intensity)
		{
			return huToColor.First().Value;
		}
		else if (huToColor.Last().Key < intensity)
		{
			return huToColor.Last().Value;
		}
		for (short i = 0; i < huToColor.Count - 1; i++)
		{
			var low = huToColor.Keys.ElementAt(i);
			var high = huToColor.Keys.ElementAt(i + 1);
			if (intensity == low) return huToColor[low];
			if (intensity == high) return huToColor[high];
			if (intensity > low && intensity < high)
			{
				var t = (intensity - low) / (high - low);
				return Color.Lerp(huToColor[low], huToColor[high], t);
			}
		}
		Debug.LogError("unable to place " + intensity);
		return Color.black;
	}

	public Texture2D DicomToTex2D(DicomImage image)
	{
		var pixels = image.PixelData;
		var bytes = pixels.GetFrame(0).Data;
		var shorts = ConvertByteArray(bytes);
		var rescaleIntercept = image.Dataset.Get<float>(DicomTag.RescaleIntercept, -1024f);
		var rescaleSlope = image.Dataset.Get<float>(DicomTag.RescaleSlope, 1f);
		/*
		Debug.Log(pixels.Height + "," + pixels.Width);
		Debug.Log(shorts.Length);
		Debug.Log(shorts.Min() + "-" + shorts.Average(x => (int)x) + "-" + shorts.Max());
		Debug.Log(image.WindowCenter + "," + image.WindowWidth + "," + image.Scale);
		Debug.Log(rescale + "," + rescaleSlope);
		*/
		var tex = new Texture2D(pixels.Width, pixels.Height);
		var colors = new Color[shorts.Length];
		for (int i = 0; i < shorts.Length; i++)
		{
			float intensity = shorts[i];
			intensity = intensity * rescaleSlope + rescaleIntercept;
			var color = HounsfieldUnitsToColor(intensity);
			colors[i] = color;
		}
		tex.SetPixels(colors);
		tex.Apply();

		return tex;
	}

	static DicomDirectoryRecord GetSeries(DicomDirectoryRecord record)
	{
		if (record.DirectoryRecordType == "IMAGE")
		{
			return record;
		}
		while (record.DirectoryRecordType != "SERIES")
		{
			record = record.LowerLevelDirectoryRecord;
		}
		return record;
	}

	DicomDirectoryRecord GetSeriesById(string id)
	{
		foreach (var record in rootDirectoryMap.Keys)
		{
			if (record.DirectoryRecordType == "PATIENT")
			{
				foreach (var study in record.LowerLevelDirectoryRecordCollection)
				{
					foreach (var series in study.LowerLevelDirectoryRecordCollection)
					{
						var seriesId = series.Get<string>(DicomTag.SeriesInstanceUID);
						if (seriesId == id)
						{
							return series;
						}
					}
				}
			}
		}
		return null;
	}

	DicomDirectoryRecord GetStudyForSeries(string id)
	{
		foreach (var record in rootDirectoryMap.Keys)
		{
			if (record.DirectoryRecordType == "PATIENT")
			{
				foreach (var study in record.LowerLevelDirectoryRecordCollection)
				{
					foreach (var series in study.LowerLevelDirectoryRecordCollection)
					{
						var seriesId = series.Get<string>(DicomTag.SeriesInstanceUID);
						if (seriesId == id)
						{
							return study;
						}
					}
				}
			}
		}
		return null;
	}

	public Texture2D GetTexture2DForRecord(DicomDirectoryRecord record, int frame = -1)
	{
		var tex = new Texture2D(1, 1);
		try
		{
			var img = GetImageForRecord(record, frame);
			//var s = Time.realtimeSinceStartup;
			tex = DicomToTex2D(img);
			//Debug.Log("DicomToText2D took " + (Time.realtimeSinceStartup - s));
		}
		catch
		{
		}
		return tex;
	}

	public DicomImage GetImageForRecord(DicomDirectoryRecord record, int frame = -1)
	{
		var directory = rootDirectoryMap[record];
		var series = GetSeries(record);
		var absoluteFilename = "";
		var instanceNumbers = series.LowerLevelDirectoryRecordCollection.Select(x => x.Get<int>(DicomTag.InstanceNumber)).OrderBy(x => x).ToArray();
		if (frame == 0)
		{
			frame = instanceNumbers[0];
		}
		if (frame == -1) // get thumbnail - get midpoint image
		{
			frame = instanceNumbers[instanceNumbers.Length / 2];
		}
		if (frame == -2) // get last
		{
			frame = instanceNumbers[instanceNumbers.Length - 1];
		}
		try
		{
			var imageRecord = series.LowerLevelDirectoryRecordCollection.First(x => x.Get<int>(DicomTag.InstanceNumber) == frame);
			var filename = Path.Combine(imageRecord.Get<string[]>(DicomTag.ReferencedFileID));
			absoluteFilename = Path.Combine(directory, filename);
			//Debug.Log("load image " + absoluteFilename);
			var img = new DicomImage(absoluteFilename);
			return img;
		}
		catch (InvalidOperationException)
		{
			Debug.LogError("series does not contain an image of InstanceNumber=" + frame + "- valid instance numbers = " + string.Join(",", instanceNumbers));
			return null;
		}
		catch (System.Exception e)
		{
			Debug.LogError("Failed to load " + absoluteFilename + ":" + e);
			return null;
		}
	}

	string Unzip(string prefix, bool overwrite = false)
	{
		var root = Application.persistentDataPath;
		var zip = Path.Combine(root, prefix + ".zip");
		var path = Path.Combine(root, prefix);
		if (!File.Exists(zip) && !Directory.Exists(path))
		{
			status.text = "ERROR: No zip file found!";
		}
		if (File.Exists(zip) && (overwrite || !Directory.Exists(path)))
		{
			Debug.Log("unzipping..");
			status.text = "unzipping...";
			System.IO.Compression.ZipFile.ExtractToDirectory(zip, path);
			Debug.Log("unzip done!");
		}
		return path;
	}

	void UpdateAnnotationsList()
	{
		foreach (Transform child in annotationsList.transform)
		{
			Destroy(child.gameObject);
		}
		var sorted = annotations.annotations.OrderBy(x => x.modified).Take(10);
		var offset = 0;
		foreach (var a in sorted)
		{
			var listElement = Instantiate(annotationsListElementPrefab, annotationsList.transform);
			listElement.transform.localPosition = new Vector3(0, offset, 0);
			var record = GetSeriesById(a.series);
			var desc = GetDicomTag(record, DicomTag.SeriesDescription);
			listElement.GetComponent<Text>().text = a.modified + ":" + desc;
			offset -= 200;
		}
	}

	// Use this for initialization
	void Start()
	{
		Unzip("Genomics", false);
		Debug.Log("Loading DICOM DICT");
		var dict = new DicomDictionary();
		dict.Load(Application.dataPath + "/StreamingAssets/Dictionaries/DICOM Dictionary.xml", DicomDictionaryFormat.XML);
		DicomDictionary.Default = dict;
		annotationPath = Path.Combine(Application.persistentDataPath, "annotations.json");
		if (File.Exists(annotationPath))
		{
			annotations = JsonUtility.FromJson<AnnotationCollection>(File.ReadAllText(annotationPath));
			Debug.Log("Loaded " + annotations.annotations.Count + " annotations");
		}
		var meshMarkerPath = Path.Combine(Application.persistentDataPath, "MeshMarkers");
		if (Directory.Exists(meshMarkerPath))
		{
			meshMarkers = Directory.GetFiles(meshMarkerPath, "*.*", SearchOption.AllDirectories);
		}
		var path = Unzip("DICOM");
		Unzip("Volumes", false);
		var offset = 0;
		directoryMap = new Dictionary<GameObject, DicomDirectoryRecord>();
		rootDirectoryMap = new Dictionary<DicomDirectoryRecord, string>();
		openedItems = new Dictionary<GameObject, bool>();
		var directories = Directory.GetDirectories(path);
		if (directories.Length == 0)
		{
			status.text = "ERROR: No directories found!";
			return;
		}
		status.text = "Loading...";
		foreach (var directory in Directory.GetDirectories(path))
		{
			var directoryName = Path.GetFileName(directory);
			Debug.Log("--DIRECTORY--" + directoryName);
			var dd = DicomDirectory.Open(Path.Combine(directory, "DICOMDIR"));
			rootDirectoryMap[dd.RootDirectoryRecord] = directory;
			var tex = GetTexture2DForRecord(dd.RootDirectoryRecord);
			var quad = Instantiate(quadPrefab, filebrowser);
			quad.GetComponent<Renderer>().material.mainTexture = tex;
			quad.transform.localPosition += new Vector3(offset, 0, 0);
			quad.transform.Find("Canvas").Find("title").GetComponent<Text>().text = "Directory: " + directoryName;
			quad.name = directory;
			directoryMap[quad] = dd.RootDirectoryRecord;
			openedItems[quad] = false;
			quad.tag = "directory";
			offset += 1;
		}
		recognizer = new GestureRecognizer();
		recognizer.TappedEvent += Recognizer_TappedEvent;
		recognizer.StartCapturingGestures();
		status.text = "";
		UpdateAnnotationsList();

		testQuad.SetActive(false);
#if UNITY_EDITOR
		testQuad.SetActive(true);
		testQuad.transform.position = new Vector3(0, 0, 2);
		var series = GetSeriesById("1.3.12.2.1107.5.1.4.50714.30000016083120205201500011155");
		var seriesHandler = testQuad.GetComponent<OpenSeriesHandler>();
		seriesHandler.record = series;

		var modality = GetDicomTag(series, DicomTag.Modality);
		var seriesDesc = GetDicomTag(series, DicomTag.SeriesDescription);
		testQuad.name = "Series: " + modality + "\n" + seriesDesc;
		Debug.Log(seriesDesc);
		directoryMap[testQuad] = series;
		rootDirectoryMap[series] = rootDirectoryMap[directoryMap.ElementAt(1).Value];

		//testQuad.GetComponent<TwoHandManipulatable>().enabled = true;
		//testQuad.transform.Find("3D_toggle").gameObject.SetActive(true);
		//testQuad.transform.Find("3D_toggle").GetComponent<InteractiveToggle>().SetSelection(true);
		seriesHandler.ButtonPush("3D");
		var slider = testQuad.transform.Find("zstack slider");
		slider.gameObject.SetActive(true);
		//var sliderComponent = slider.GetComponent<SliderGestureControl>();
		//var n_images = series.LowerLevelDirectoryRecordCollection.Count();
		//sliderComponent.SetSpan(0, n_images);
		//sliderComponent.SetSliderValue(n_images / 2f);
		testQuad.GetComponent<Renderer>().material.mainTexture = GetTexture2DForRecord(series);

		var seriesId = series.Get<string>(DicomTag.SeriesInstanceUID, "no series id");
		foreach (var a in annotations.annotations)
		{
			if (a.series == seriesId)
			{
				var annotation = Instantiate(annotationPrefab, testQuad.transform);
				annotation.transform.localPosition = DeserializeVector(a.position, Vector3.zero);
				annotation.transform.localRotation = Quaternion.Euler(DeserializeVector(a.rotation, Vector3.zero));
				annotation.transform.localScale = DeserializeVector(a.scale, Vector3.one);
			}
		}
		//WarmVolumeCache();
#endif
	}

	void WarmVolumeCache()
	{
		var startTime = Time.realtimeSinceStartup;
		foreach (var directory in directoryMap)
		{
			foreach (var study in directory.Value.LowerLevelDirectoryRecordCollection)
			{
				Debug.Log("warming cache for " + study.Get<string>(DicomTag.StudyDescription, ""));
				foreach (var series in study.LowerLevelDirectoryRecordCollection)
				{
					var id = series.Get<string>(DicomTag.SeriesInstanceUID);
					try
					{
						var path = Path.Combine(Application.persistentDataPath, "Volumes", id);
						if (!File.Exists(path))
						{
							rootDirectoryMap[series] = rootDirectoryMap[directory.Value];
							var seriesHandler = testQuad.GetComponent<OpenSeriesHandler>();
							seriesHandler.record = series;
							Int3 size;
							var vol = seriesHandler.DICOMSeriesToVolume(series, out size);
							File.WriteAllBytes(path, vol);
							Debug.Log((Time.realtimeSinceStartup - startTime) + ": wrote " + path);
						}
					}
					catch
					{
						Debug.LogError("unable to create volume for " + id);
					}
				}
			}
		}
	}

	private void Recognizer_TappedEvent(InteractionSourceKind source, int tapCount, Ray headRay)
	{
		RaycastHit hit;
		if (Physics.Raycast(headRay, out hit))
		{
			ClickObject(hit.collider.gameObject);
		}
	}

	void Close(GameObject go)
	{
		foreach (Transform child in go.transform)
		{
			if (child.name != "Canvas")
			{
				Destroy(child.gameObject);
			}
		}
		openedItems[go] = false;
	}

	Vector3 DeserializeVector(string v, Vector3 ifError)
	{
		if (v.Length == 0)
		{
			return ifError;
		}
		v = v.Trim("()".ToCharArray()).Replace(" ", "");
		var bits = v.Split(',');
		return new Vector3(float.Parse(bits[0]), float.Parse(bits[1]), float.Parse(bits[2]));
	}

	void ClickObject(GameObject go)
	{
		if (go.tag == "opened_series")
		{
			selectedObject = go;
			var annotation = Instantiate(annotationPrefab, go.transform);
			annotation.transform.position = go.transform.position;
			var ah = annotation.GetComponent<AnnotationHandler>();
			var seriesId = directoryMap[go].Get<string>(DicomTag.SeriesInstanceUID, "no series id");
			ah.annotation = new Annotation(seriesId);
			ah.annotation.SetFromTransform(annotation.transform);
			annotations.Add(ah.annotation);
			return;
		}
		if (go == selectedObject) // object is already selected
		{
			return;
		}
		else
		{
			selectedObject = null;
		}
		if (!directoryMap.ContainsKey(go))
		{
			return;
		}
		if (openedItems.ContainsKey(go) && openedItems[go]) // clicking on an open directory or study - close it
		{
			Close(go);
			return;
		}
		foreach (var otherGo in GameObject.FindGameObjectsWithTag(go.tag)) // opening a directory or study - close all other directories or studies
		{
			if (openedItems[otherGo])
			{
				Close(otherGo);
			}
		}
		openedItems[go] = true; // this is now open
		var record = directoryMap[go];
		if (record.DirectoryRecordType == "SERIES") // opening a series - bring it out of the tree
		{
			var clone = Instantiate(go, transform);
			clone.transform.localScale = go.transform.lossyScale;
			clone.transform.position = go.transform.position;
			clone.transform.rotation = go.transform.rotation;
			clone.transform.Translate(0, 0, -.5f, Space.Self);
			selectedObject = clone;
			directoryMap[clone] = record;
			clone.tag = "opened_series";
			//clone.GetComponent<TwoHandManipulatable>().enabled = true;
			clone.transform.Find("3D_toggle").gameObject.SetActive(true);
			var slider = clone.transform.Find("zstack slider");
			slider.gameObject.SetActive(true);
			//var sliderComponent = slider.GetComponent<SliderGestureControl>();
			var n_images = record.LowerLevelDirectoryRecordCollection.Count();
			//sliderComponent.SetSpan(0, n_images);
			//sliderComponent.SetSliderValue(n_images / 2f);
			var openSeriesHandler = clone.GetComponent<OpenSeriesHandler>();
			openSeriesHandler.record = record;
			openSeriesHandler.loadDicomInstance = this;
			var seriesId = record.Get<string>(DicomTag.SeriesInstanceUID, "no series id");
			foreach (var a in annotations.annotations)
			{
				if (a.series == seriesId)
				{
					var annotation = Instantiate(annotationPrefab, clone.transform);
					annotation.transform.localPosition = DeserializeVector(a.position, Vector3.zero);
					annotation.transform.localRotation = Quaternion.Euler(DeserializeVector(a.rotation, Vector3.zero));
					annotation.transform.localScale = DeserializeVector(a.scale, Vector3.one);
				}
			}
			return;
		}
		var rootDirectory = rootDirectoryMap[record];
		var offset = 0;
		status.text = "Loading...";
		foreach (var subRecord in record.LowerLevelDirectoryRecordCollection)
		{
			rootDirectoryMap[subRecord] = rootDirectory;
			var desc = "";
			var quad = Instantiate(quadPrefab, go.transform);
			if (subRecord.DirectoryRecordType == "STUDY")
			{
				var studyDate = GetDicomTag(subRecord, DicomTag.StudyDate);
				var studyDesc = GetDicomTag(subRecord, DicomTag.StudyDescription);
				var studyComments = GetDicomTag(subRecord, DicomTag.StudyCommentsRETIRED);
				desc = "Study: " + studyDate + "\n" + studyDesc + "\n" + studyComments;
				quad.tag = "study";
			}
			else if (subRecord.DirectoryRecordType == "SERIES")
			{
				var modality = GetDicomTag(subRecord, DicomTag.Modality);
				var seriesDesc = GetDicomTag(subRecord, DicomTag.SeriesDescription);
				desc = "Series: " + modality + "\n" + seriesDesc;
				quad.tag = "series";
			}
			else if (subRecord.DirectoryRecordType == "IMAGE")
			{
				desc = "Image: " + subRecord.Get<string>(DicomTag.InstanceNumber);
				quad.tag = "image";
			}
			var tex = GetTexture2DForRecord(subRecord);
			quad.GetComponent<Renderer>().material.mainTexture = tex;
			quad.transform.localPosition += new Vector3(offset, -2, 0);
			quad.transform.Find("Canvas").Find("title").GetComponent<Text>().text = desc;
			quad.name = desc.Replace("\n", ":");
			directoryMap[quad] = subRecord;
			openedItems[quad] = false;
			offset += 1;
		}
		status.text = "";
	}

	// Update is called once per frame
	void Update()
	{
#if UNITY_EDITOR
		var t = transform;
		if (selectedObject)
		{
			t = selectedObject.transform;
		}
		if (Input.GetMouseButtonDown(0))
		{
			float distance_to_screen = Camera.main.WorldToScreenPoint(t.position).z;
			offset = t.position - Camera.main.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, distance_to_screen));
		}
		if (Input.GetMouseButton(0))
		{
			float distance_to_screen = Camera.main.WorldToScreenPoint(t.position).z;
			t.position = Camera.main.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, distance_to_screen)) + offset;
		}
		if (Input.GetMouseButtonUp(0))
		{
			var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
			RaycastHit hit;
			if (Physics.Raycast(ray, out hit))
			{
				ClickObject(hit.collider.gameObject);
			}
		}
#endif
	}

	private void OnApplicationFocus(bool focus)
	{
		if (!focus)
		{
#if !UNITY_EDITOR
			transform.localScale = Vector3.zero;
			pin.SetActive(true);
#endif
		}
	}

	public void SaveAnnotations()
	{
		var json = JsonUtility.ToJson(annotations, true);
		File.WriteAllText(annotationPath, json);
		UpdateAnnotationsList();
	}
}