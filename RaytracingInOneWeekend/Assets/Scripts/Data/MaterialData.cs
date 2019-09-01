using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#else
using OdinMock;
#endif

namespace RaytracerInOneWeekend
{
	[CreateAssetMenu]
	class MaterialData : ScriptableObject
	{
		[SerializeField] MaterialType type = MaterialType.None;

		[ShowIf(nameof(TextureCanScale))]
		[SerializeField] Vector2 textureScale = Vector2.one;

		[ShowIf(nameof(Type), MaterialType.Metal)]
		[Range(0, 10)] [SerializeField] float fuzz = 0;

		[ShowIf(nameof(Type), MaterialType.Dielectric)]
		[Range(1, 2.65f)] [SerializeField] float refractiveIndex = 1;

		[ShowIf(nameof(Type), MaterialType.Isotropic)]
		[Range(0, 1)] [SerializeField] float density = 1;

#if UNITY_EDITOR
		[AssetList]
		[ShowIf(nameof(AlbedoSupported))]
#endif
		[SerializeField] TextureData albedo = null;
#if UNITY_EDITOR
		[ShowInInspector]
		[InlineEditor(DrawHeader = false, ObjectFieldMode = InlineEditorObjectFieldModes.Hidden)]
		[ShowIf(nameof(albedo))]
		[BoxGroup]
		TextureData AlbedoTexture
		{
			get => albedo;
			set => albedo = value;
		}
		bool AlbedoSupported => type == MaterialType.Lambertian || type == MaterialType.Metal ||
		                        type == MaterialType.Isotropic;
#endif

#if UNITY_EDITOR
		[AssetList]
		[ShowIf(nameof(type), MaterialType.DiffuseLight)]
#endif
		[SerializeField] TextureData emission = null;
#if UNITY_EDITOR
		[ShowInInspector]
		[InlineEditor(DrawHeader = false, ObjectFieldMode = InlineEditorObjectFieldModes.Hidden)]
		[ShowIf(nameof(emission))]
		[BoxGroup]
		TextureData EmissiveTexture
		{
			get => emission;
			set => emission = value;
		}
#endif

		public MaterialType Type => type;
		public float Fuzz => fuzz;
		public float RefractiveIndex => refractiveIndex;
		public Vector2 TextureScale => textureScale;
		public float Density => density;

		public TextureData Albedo
		{
			get => albedo;
			set => albedo = value;
		}

		public TextureData Emission
		{
			get => emission;
			set => emission = value;
		}

		public static MaterialData Lambertian(TextureData albedoTexture, float2 textureScale)
		{
			var data = CreateInstance<MaterialData>();
			data.hideFlags = HideFlags.HideAndDontSave;
			data.type = MaterialType.Lambertian;
			data.albedo = albedoTexture;
			data.textureScale = textureScale;
			return data;
		}

		public static MaterialData Metal(TextureData albedoTexture, float2 textureScale, float fuzz = 0)
		{
			var data = CreateInstance<MaterialData>();
			data.hideFlags = HideFlags.HideAndDontSave;
			data.type = MaterialType.Metal;
			data.albedo = albedoTexture;
			data.textureScale = textureScale;
			data.fuzz = fuzz;
			return data;
		}

		public static MaterialData Dielectric(float refractiveIndex)
		{
			var data = CreateInstance<MaterialData>();
			data.hideFlags = HideFlags.HideAndDontSave;
			data.type = MaterialType.Dielectric;
			data.refractiveIndex = refractiveIndex;
			return data;
		}

#if UNITY_EDITOR
		bool dirty;
		public bool Dirty => dirty || (albedo && albedo.Dirty) || (emission && emission.Dirty);

		public void ClearDirty()
		{
			dirty = false;
			if (albedo) albedo.ClearDirty();
			if (emission) emission.ClearDirty();
		}

		void OnValidate()
		{
			dirty = true;
		}

		bool TextureCanScale => (albedo && albedo.Type != TextureType.Constant) ||
								(emission && emission.Type != TextureType.Constant);
#endif
	}
}