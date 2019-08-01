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

		[SerializeField] Vector2 textureScale = Vector2.one;

		[ShowIf(nameof(Type), MaterialType.Metal)]
		[Range(0, 1)] [SerializeField] float fuzz = 0;

		[ShowIf(nameof(Type), MaterialType.Dielectric)]
		[Range(1, 2.65f)] [SerializeField] float refractiveIndex = 1;

#if UNITY_EDITOR
		[ValueDropdown(nameof(GetMaterialAssets))]
#endif
		[SerializeField] TextureData albedo = null;
#if UNITY_EDITOR
		[ShowInInspector]
		[InlineEditor(DrawHeader = false)]
		[ShowIf(nameof(albedo))]
		TextureData AlbedoTexture
		{
			get => albedo;
			set => albedo = value;
		}
#endif

		public MaterialType Type => type;
		public float Fuzz => fuzz;
		public float RefractiveIndex => refractiveIndex;
		public Vector2 TextureScale => textureScale;

		public TextureData Albedo
		{
			get => albedo;
			set => albedo = value;
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
		public bool Dirty => dirty || (albedo && albedo.Dirty);

		public void ClearDirty()
		{
			dirty = false;
			if (albedo) albedo.ClearDirty();
		}

		void OnValidate()
		{
			dirty = true;
		}

		IEnumerable<ValueDropdownItem<TextureData>> GetMaterialAssets => AssetDatabase.FindAssets("t:TextureData")
			.Select(AssetDatabase.GUIDToAssetPath)
			.Select(AssetDatabase.LoadAssetAtPath<TextureData>)
			.Select(asset => new ValueDropdownItem<TextureData>(asset.name, asset))
			.Concat(new[] { new ValueDropdownItem<TextureData>("Null", null) })
			.OrderBy(x => x.Value != null).ThenBy(x => x.Text);
#endif
	}
}