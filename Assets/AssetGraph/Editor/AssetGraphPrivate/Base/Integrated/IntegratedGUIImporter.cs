using UnityEngine;
using UnityEditor;

using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;

namespace AssetGraph {
	public class IntegratedGUIImporter : INodeBase {
		public void Setup (string nodeId, string labelToNext, Dictionary<string, List<InternalAssetData>> groupedSources, List<string> alreadyCached, Action<string, string, Dictionary<string, List<InternalAssetData>>, List<string>> Output) {
			var samplingDirectoryPath = FileController.PathCombine(AssetGraphSettings.IMPORTER_SAMPLING_PLACE, nodeId);
			var outputDict = new Dictionary<string, List<InternalAssetData>>();

			var first = true;

			foreach (var groupKey in groupedSources.Keys) {
				var inputSources = groupedSources[groupKey];
				
				var assumedImportedAssetDatas = new List<InternalAssetData>();
				
				// caution if file is exists already.
				if (Directory.Exists(samplingDirectoryPath)) {
					var filesInSampling = FileController.FilePathsInFolder(samplingDirectoryPath);
					switch (filesInSampling.Count) {
						case 0: {
							Debug.LogWarning("sampling start. 仮のimportが走るんで、なにかするならここ。");
							break;
						}
						case 1: {
							first = false;
							break;
						}
						default: {
							first = false;
							break;
						}
					}
				}

				foreach (var inputSource in inputSources) {
					var assumedImportedBasePath = inputSource.absoluteSourcePath.Replace(inputSource.sourceBasePath, AssetGraphSettings.IMPORTER_CACHE_PLACE);
					var assumedImportedPath = FileController.PathCombine(assumedImportedBasePath, nodeId, groupKey);

					var assumedType = AssumeTypeFromExtension();

					var newData = InternalAssetData.InternalAssetDataByImporter(
						inputSource.traceId,
						inputSource.absoluteSourcePath,
						inputSource.sourceBasePath,
						inputSource.fileNameAndExtension,
						inputSource.pathUnderSourceBase,
						assumedImportedPath,
						null,
						assumedType
					);
					assumedImportedAssetDatas.Add(newData);

					if (first) {
						Debug.LogWarning("このへんで、これからこのファイルのサンプリングimportするんですよこれ時間かかりますよ、って書きたい");
						if (!Directory.Exists(samplingDirectoryPath)) Directory.CreateDirectory(samplingDirectoryPath);

						var absoluteFilePath = inputSource.absoluteSourcePath;
						var targetFilePath = FileController.PathCombine(samplingDirectoryPath, inputSource.fileNameAndExtension);

						FileController.CopyFileFromGlobalToLocal(absoluteFilePath, targetFilePath);
						first = false;
						Debug.Log("succeeded to sampling:" + targetFilePath);
						AssetDatabase.Refresh(ImportAssetOptions.ImportRecursive);
					}
				}

				outputDict[groupKey] = assumedImportedAssetDatas;
			}

			Output(nodeId, labelToNext, outputDict, new List<string>());
		}
		
		public void Run (string nodeId, string labelToNext, Dictionary<string, List<InternalAssetData>> groupedSources, List<string> alreadyCached, Action<string, string, Dictionary<string, List<InternalAssetData>>, List<string>> Output) {
			var usedCache = new List<string>();
			
			var samplingDirectoryPath = FileController.PathCombine(AssetGraphSettings.IMPORTER_SAMPLING_PLACE, nodeId);
			var outputDict = new Dictionary<string, List<InternalAssetData>>();

			var nodeDirectoryPath = FileController.PathCombine(AssetGraphSettings.IMPORTER_CACHE_PLACE, nodeId);
			
			foreach (var groupKey in groupedSources.Keys) {
				var inputSources = groupedSources[groupKey];
				
				var groupDirectoryPath = FileController.PathCombine(nodeDirectoryPath, groupKey);
				var localFilePathsBeforeImport = FileController.FilePathsInFolder(groupDirectoryPath);
				usedCache.AddRange(localFilePathsBeforeImport);

				// caution if file is exists already.
				var sampleAssetPath = string.Empty;
				if (Directory.Exists(samplingDirectoryPath)) {
					var filesInSampling = FileController.FilePathsInFolderOnly1Level(samplingDirectoryPath);
					switch (filesInSampling.Count) {
						case 0: {
							Debug.LogWarning("no samples found in samplingDirectoryPath:" + samplingDirectoryPath + ", please reload first.");
							return;
						}
						case 1: {
							Debug.Log("using sample:" + filesInSampling[0]);
							sampleAssetPath = filesInSampling[0];
							break;
						}
						default: {
							Debug.LogWarning("too many samples in samplingDirectoryPath:" + samplingDirectoryPath);
							return;
						}
					}
				} else {
					Debug.LogWarning("no samples found in samplingDirectoryPath:" + samplingDirectoryPath + ", applying default importer settings. If you want to set Importer seting, please Reload and set import setting by Importer.");
				}

				var samplingAssetImporter = AssetImporter.GetAtPath(sampleAssetPath);
				
				/*
					copy all sources from outside to inside of Unity.
				*/
				InternalSamplingImportAdopter.Attach(samplingAssetImporter);
				foreach (var inputSource in inputSources) {
					var absoluteFilePath = inputSource.absoluteSourcePath;
					var pathUnderSourceBase = inputSource.pathUnderSourceBase;

					var targetFilePath = FileController.PathCombine(groupDirectoryPath, pathUnderSourceBase);

					// skip if cached.
					if (GraphStackController.IsCached(alreadyCached, targetFilePath)) continue;

					try {
						/*
							copy files into local.
						*/
						FileController.CopyFileFromGlobalToLocal(absoluteFilePath, targetFilePath);
					} catch (Exception e) {
						Debug.LogError("IntegratedGUIImporter:" + this + " error:" + e);
						return;
					}
				}
				AssetDatabase.Refresh(ImportAssetOptions.ImportRecursive);
				InternalSamplingImportAdopter.Detach();


				// get files, which are imported or cached assets.
				var localFilePathsAfterImport = FileController.FilePathsInFolder(groupDirectoryPath);

				// modify to local path.
				var localFilePathsWithoutnodeDirectoryPath = localFilePathsAfterImport.Select(path => InternalAssetData.GetPathWithoutBasePath(path, groupDirectoryPath)).ToList();
				
				
				var outputSources = new List<InternalAssetData>();
				/*
					treat all assets inside node.
				*/
				foreach (var newAssetPath in localFilePathsWithoutnodeDirectoryPath) {
					var basePathWithNewAssetPath = InternalAssetData.GetPathWithBasePath(newAssetPath, groupDirectoryPath);
					var newInternalAssetData = InternalAssetData.InternalAssetDataGeneratedByImporterOrPrefabricator(
						basePathWithNewAssetPath,
						AssetDatabase.AssetPathToGUID(basePathWithNewAssetPath),
						AssetGraphInternalFunctions.GetAssetType(basePathWithNewAssetPath)
					);
					outputSources.Add(newInternalAssetData);
				}

				outputDict[groupKey] = outputSources;
			}

			Output(nodeId, labelToNext, outputDict, usedCache);
		}
		
		public Type AssumeTypeFromExtension () {
			return typeof(UnityEngine.Object);
		}
	}
}