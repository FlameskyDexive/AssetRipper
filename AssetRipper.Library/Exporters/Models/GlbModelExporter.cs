﻿using AssetRipper.Core.Classes.Misc;
using AssetRipper.Core.Interfaces;
using AssetRipper.Core.IO;
using AssetRipper.Core.Logging;
using AssetRipper.Core.Math.Transformations;
using AssetRipper.Core.Parser.Files.SerializedFiles;
using AssetRipper.Core.Project;
using AssetRipper.Core.Project.Collections;
using AssetRipper.Core.Project.Exporters;
using AssetRipper.Core.SourceGenExtensions;
using AssetRipper.Library.Exporters.Meshes;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_18;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Material_;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetRipper.Library.Exporters.Models
{
	public class GlbModelExporter : BinaryAssetExporter
	{
		public override bool IsHandle(IUnityObjectBase asset)
		{
			return SceneExportHelpers.IsSceneCompatible(asset);
		}

		public override IExportCollection CreateCollection(VirtualSerializedFile virtualFile, IUnityObjectBase asset)
		{
			if (asset.SerializedFile.Collection.IsScene(asset.SerializedFile))
			{
				return new GlbSceneModelExportCollection(this, asset.SerializedFile);
			}
			else if (PrefabExportCollection.IsValidAsset(asset))
			{
				return new GlbPrefabModelExportCollection(this, GetRoot(asset));
			}
			else
			{
				return new FailExportCollection(this, asset);
			}
		}

		private static IGameObject GetRoot(IUnityObjectBase asset)
		{
			return asset switch
			{
				IGameObject gameObject => gameObject.GetRoot(),
				IComponent component => component.GetRoot(),
				_ => throw new InvalidOperationException()
			};
		}

		public override bool Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path)
		{
			return ExportModel(assets, path, false); //Called by the prefab exporter
		}

		public bool ExportModel(IEnumerable<IUnityObjectBase> assets, string path, bool isScene)
		{
			byte[] data = ExportBinary(assets, isScene);
			if (data.Length != 0)
			{
				File.WriteAllBytes(path, data);
				return true;
			}
			return false;
		}

		private static byte[] ExportBinary(IEnumerable<IUnityObjectBase> assets, bool isScene)
		{
			SceneBuilder sceneBuilder = new();
			BuildParameters parameters = new BuildParameters(isScene);

			HashSet<IUnityObjectBase> exportedAssets = new();

			foreach (IUnityObjectBase asset in assets)
			{
				if (!exportedAssets.Contains(asset) && asset is IGameObject or IComponent)
				{
					IGameObject root = GetRoot(asset);

					AddGameObjectToScene(sceneBuilder, parameters, null, Transformation.Identity, Transformation.Identity, root.GetTransform());

					foreach (IEditorExtension exportedAsset in root.FetchHierarchy())
					{
						exportedAssets.Add(exportedAsset);
					}
				}
			}

			SharpGLTF.Schema2.WriteSettings writeSettings = new();

			return sceneBuilder.ToGltf2().WriteGLB(writeSettings).ToArray();
		}

		private static void AddGameObjectToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder? parentNode, Transformation parentGlobalTransform, Transformation parentGlobalInverseTransform, ITransform transform)
		{
			IGameObject gameObject = transform.GetGameObject();
			Transformation localTransform = transform.ToTransformation();
			Transformation localInverseTransform = transform.ToInverseTransformation();
			Transformation globalTransform = localTransform * parentGlobalTransform;
			Transformation globalInverseTransform = parentGlobalInverseTransform * localInverseTransform;

			NodeBuilder node = parentNode is null ? new NodeBuilder(gameObject.NameString) : parentNode.CreateNode(gameObject.NameString);
			if (parentNode is not null || parameters.IsScene)
			{
				node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
					transform.LocalScale_C4.CastToStruct(),
					transform.LocalRotation_C4.CastToStruct(),
					transform.LocalPosition_C4.CastToStruct());
			}
			sceneBuilder.AddNode(node);

			if (gameObject.TryFindComponent(out IMeshFilter? meshFilter)
				&& meshFilter.TryGetMesh(out IMesh? mesh)
				&& mesh.IsSet()
				&& parameters.TryGetOrMakeMeshData(mesh, out MeshData meshData))
			{
				if (gameObject.TryFindComponent(out IMeshRenderer? meshRenderer))
				{
					if (ReferencesDynamicMesh(meshRenderer))
					{
						
						AddDynamicMeshToScene(sceneBuilder, parameters, node, meshData, new MaterialList(meshRenderer));
					}
					else
					{
						int[] subsetIndices = GetSubsetIndices(meshRenderer);
						AddStaticMeshToScene(sceneBuilder, parameters, node, meshData, subsetIndices, new MaterialList(meshRenderer), globalTransform, globalInverseTransform);
					}
				}
				else if (gameObject.TryFindComponent(out ISkinnedMeshRenderer? skinnedMeshRenderer))
				{
					if (ReferencesDynamicMesh(skinnedMeshRenderer))
					{
						AddDynamicMeshToScene(sceneBuilder, parameters, node, meshData, new MaterialList(skinnedMeshRenderer));
					}
					else
					{
						int[] subsetIndices = GetSubsetIndices(skinnedMeshRenderer);
						AddStaticMeshToScene(sceneBuilder, parameters, node, meshData, subsetIndices, new MaterialList(skinnedMeshRenderer), globalTransform, globalInverseTransform);
					}
				}
				else if (gameObject.TryFindComponent(out IRenderer? renderer))
				{
					Logger.Warning(LogCategory.Export, $"Renderer type {renderer.GetType()} not supported in {nameof(GlbModelExporter)}");
				}
			}

			foreach (ITransform childTransform in transform.GetChildren())
			{
				AddGameObjectToScene(sceneBuilder, parameters, node, localTransform * parentGlobalTransform, parentGlobalInverseTransform * localInverseTransform, childTransform);
			}
		}

		private static void AddDynamicMeshToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder node, MeshData meshData, MaterialList materialList)
		{
			AccessListBase<ISubMesh> subMeshes = meshData.Mesh.SubMeshes_C43;
			(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subMeshes.Count);
			for (int i = 0; i < subMeshes.Count; i++)
			{
				MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
				subMeshArray[i] = (subMeshes[i], materialBuilder);
			}
			ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray, 0, subMeshes.Count);
			IMeshBuilder<MaterialBuilder> subMeshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, meshData, Transformation.Identity, Transformation.Identity);
			sceneBuilder.AddRigidMesh(subMeshBuilder, node);
			ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);
		}

		private static void AddStaticMeshToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder node, MeshData meshData, int[] subsetIndices, MaterialList materialList, Transformation globalTransform, Transformation globalInverseTransform)
		{
			(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subsetIndices.Length);
			AccessListBase<ISubMesh> subMeshes = meshData.Mesh.SubMeshes_C43;
			for (int i = 0; i < subsetIndices.Length; i++)
			{
				ISubMesh subMesh = subMeshes[subsetIndices[i]];
				MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
				subMeshArray[i] = (subMesh, materialBuilder);
			}
			ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray, 0, subsetIndices.Length);
			IMeshBuilder<MaterialBuilder> subMeshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, meshData, globalInverseTransform, globalTransform);
			sceneBuilder.AddRigidMesh(subMeshBuilder, node);
			ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);
		}

		private static bool ReferencesDynamicMesh(IMeshRenderer renderer)
		{
			return (renderer.Has_StaticBatchInfo_C23() && renderer.StaticBatchInfo_C23.SubMeshCount == 0)
				|| (renderer.Has_SubsetIndices_C23() && renderer.SubsetIndices_C23.Length == 0);
		}

		private static bool ReferencesDynamicMesh(ISkinnedMeshRenderer renderer)
		{
			return (renderer.Has_StaticBatchInfo_C137() && renderer.StaticBatchInfo_C137.SubMeshCount == 0)
				|| (renderer.Has_SubsetIndices_C137() && renderer.SubsetIndices_C137.Length == 0);
		}

		private static int[] GetSubsetIndices(IMeshRenderer renderer)
		{
			AccessListBase<IPPtr_Material_> materials = renderer.Materials_C23;
			if (renderer.Has_SubsetIndices_C23())
			{
				return renderer.SubsetIndices_C23.Select(i => (int)i).ToArray();
			}
			else if (renderer.Has_StaticBatchInfo_C23())
			{
				return Enumerable.Range(renderer.StaticBatchInfo_C23.FirstSubMesh, renderer.StaticBatchInfo_C23.SubMeshCount).ToArray();
			}
			else
			{
				return Array.Empty<int>();
			}
		}

		private static int[] GetSubsetIndices(ISkinnedMeshRenderer renderer)
		{
			if (renderer.Has_SubsetIndices_C137())
			{
				return renderer.SubsetIndices_C137.Select(i => (int)i).ToArray();
			}
			else if (renderer.Has_StaticBatchInfo_C137())
			{
				return Enumerable.Range(renderer.StaticBatchInfo_C137.FirstSubMesh, renderer.StaticBatchInfo_C137.SubMeshCount).ToArray();
			}
			else
			{
				return Array.Empty<int>();
			}
		}

		private readonly record struct BuildParameters(
			MaterialBuilder DefaultMaterial,
			Dictionary<IMaterial, MaterialBuilder> MaterialCache,
			Dictionary<IMesh, MeshData> MeshCache,
			Dictionary<ITexture2D, MemoryImage> TextureCache,
			bool IsScene)
		{
			public BuildParameters(bool isScene) : this(new MaterialBuilder("DefaultMaterial"), new(), new(), new(), isScene) { }
			public bool TryGetOrMakeMeshData(IMesh mesh, out MeshData meshData)
			{
				if (MeshCache.TryGetValue(mesh, out meshData))
				{
					return true;
				}
				else if (MeshData.TryMakeFromMesh(mesh, out meshData))
				{
					MeshCache.Add(mesh, meshData);
					return true;
				}
				return false;
			}
			public MaterialBuilder GetOrMakeMaterial(IMaterial? material)
			{
				if (material is null)
				{
					return DefaultMaterial;
				}
				if (!MaterialCache.TryGetValue(material, out MaterialBuilder? materialBuilder))
				{
					materialBuilder = MakeMaterialBuilder(material);
					MaterialCache.Add(material, materialBuilder);
				}
				return materialBuilder;
			}

			private static MaterialBuilder MakeMaterialBuilder(IMaterial material)
			{
				MaterialBuilder materialBuilder = new MaterialBuilder(material.NameString);
				//materialBuilder.WithDiffuse()//For _MainTex
				//materialBuilder.WithNormal() //For _Normal
				return materialBuilder;
			}

			private static MemoryImage MakeMemoryImage(ITexture2D texture)
			{
				byte[] data = Array.Empty<byte>();//Convert texture to png
				return new MemoryImage(data);
			}
		}

		private readonly struct MaterialList
		{
			private readonly AccessListBase<IPPtr_Material_> materials;
			private readonly ISerializedFile file;

			private MaterialList(AccessListBase<IPPtr_Material_> materials, ISerializedFile file)
			{
				this.materials = materials;
				this.file = file;
			}

			public MaterialList(IMeshRenderer renderer) : this(renderer.Materials_C23, renderer.SerializedFile) { }

			public MaterialList(ISkinnedMeshRenderer renderer) : this(renderer.Materials_C137, renderer.SerializedFile) { }

			public int Count => materials.Count;

			public IMaterial? this[int index]
			{
				get
				{
					if (index >= materials.Count)
					{
						return null;
					}
					return materials[index].FindAsset(file);
				}
			}
		}
	}
}
