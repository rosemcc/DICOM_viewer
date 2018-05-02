﻿using UnityEngine;
using Dicom.Imaging;
using System.IO;
using Dicom.Media;
using Dicom;
using System.Linq;
using Dicom.Imaging.LUT;
using System.Collections.Generic;
using UnityEngine.UI;

public class LoadDICOM : MonoBehaviour {

	public GameObject quadPrefab;

	short[] ConvertByteArray(byte[] bytes)
	{
		var size = bytes.Length / sizeof(short);
		var shorts = new short[size];
		for (var index = 0; index < size; index++)
		{
			shorts[index] = System.BitConverter.ToInt16(bytes, index * sizeof(short));
		}
		return shorts;
	}

	Texture2D DicomToTex2D(DicomImage image)
	{
		var pixels = image.PixelData;
		var bytes = pixels.GetFrame(0).Data;
		var shorts = ConvertByteArray(bytes);
		var rescale = image.Dataset.Get<float>(DicomTag.RescaleIntercept, -1024f);
		/*
		Debug.Log(pixels.Height + "," + pixels.Width);
		Debug.Log(shorts.Length);
		Debug.Log(shorts.Min() + "-" + shorts.Average(x => (int)x) + "-" + shorts.Max());
		Debug.Log(image.WindowCenter + "," + image.WindowWidth + "," + image.Scale);
		Debug.Log(rescale + "," + rescaleSlope);
		*/
		var tex = new Texture2D(pixels.Width, pixels.Height);
		var colors = new UnityEngine.Color32[shorts.Length];
		for (int i = 0; i < shorts.Length; i++)
		{
			double intensity = shorts[i];
			intensity += rescale;
			// Threshold based on WindowCenter and WindowWidth
			intensity -= image.WindowCenter - image.WindowWidth;
			// Remap to 0-255 range and clamp
			intensity = intensity / (image.WindowCenter + image.WindowWidth) * 255;
			intensity = Mathf.Clamp((int)intensity, 0, 255);
			var color = image.GrayscaleColorMap[(int)intensity];
			//Debug.Log("intensity at " + x + "," + y + "=" + intensity);
			colors[i] = new UnityEngine.Color32(color.R, color.G, color.B, color.A);
		}
		tex.SetPixels32(colors);
		tex.Apply();

		return tex;
	}

	// Use this for initialization
	void Start () {
		var dict = new DicomDictionary();
		dict.Load(Application.dataPath + "/StreamingAssets/Dictionaries/DICOM Dictionary.xml", DicomDictionaryFormat.XML);
		DicomDictionary.Default = dict;
		#if !UNITY_EDITOR && UNITY_METRO
		var root = Windows.Storage.ApplicationData.Current.RoamingFolder.Path;
		#else
		var root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
		#endif
		var path = Path.Combine(root, "DICOM");
		var offset = 0;
		foreach (var directory in Directory.GetDirectories(path))
		{
			var directoryName = Path.GetFileName(directory);
			Debug.Log("--DIRECTORY--" + directoryName);
			var dd = DicomDirectory.Open(directory + "/DICOMDIR");
			var firstSeries = dd.RootDirectoryRecordCollection.First().LowerLevelDirectoryRecordCollection.First().LowerLevelDirectoryRecordCollection.First();
			var imageRecord = firstSeries.LowerLevelDirectoryRecordCollection.OrderBy(x => x.Get<string>(DicomTag.ReferencedFileID, 3)).First();
			var filename = Path.Combine(imageRecord.Get<string[]>(DicomTag.ReferencedFileID));
			var absoluteFilename = Path.Combine(directory, filename);
			var img = new DicomImage(absoluteFilename);
			var startTime = Time.realtimeSinceStartup;
			var tex = DicomToTex2D(img);
			Debug.Log("imgtotex2D took " + System.Math.Round(Time.realtimeSinceStartup - startTime, 2) + "s");
			var quad = Instantiate(quadPrefab, transform);
			quad.GetComponent<Renderer>().material.mainTexture = tex;
			quad.transform.Translate(offset, 0, 0);
			quad.transform.Find("Canvas").Find("title").GetComponent<Text>().text = "Directory: " + directoryName;
			quad.name = directoryName;
			offset += 1;
		}
	}
	
	// Update is called once per frame
	void Update () {
		
	}
}
