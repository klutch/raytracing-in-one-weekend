﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using Debug = UnityEngine.Debug;
using Random = Unity.Mathematics.Random;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#else
using Title = UnityEngine.HeaderAttribute;
#endif

#if UNITY_SOA
using Unity.Collections.Experimental;
#endif

namespace RaytracerInOneWeekend
{
	partial class Raytracer : MonoBehaviour
	{
		[Title("References")]
		[SerializeField] UnityEngine.Camera targetCamera = null;

		[Title("Settings")]
		[SerializeField] [Range(0.01f, 2)] float resolutionScaling = 0.5f;
		[SerializeField] [Range(1, 2000)] uint samplesPerPixel = 2000;
		[SerializeField] [Range(1, 100)] uint samplesPerBatch = 10;
		[SerializeField] [Range(1, 100)] uint traceDepth = 35;
		[SerializeField] bool previewAfterBatch = true;
		[SerializeField] bool stopWhenCompleted = true;

		[Title("Camera")]
		[SerializeField] float cameraAperture = 0.1f;

		[Title("World")]
		[SerializeField] bool randomScene = true;
#if ODIN_INSPECTOR
		[ShowIf(nameof(randomScene))]
#endif
		[SerializeField] uint sceneSeed = 45573880;
#if ODIN_INSPECTOR
		[HideIf(nameof(randomScene))]
#endif
		[SerializeField] SphereData[] spheres = null;

		CommandBuffer commandBuffer;
		NativeArray<float4> accumulationInputBuffer, accumulationOutputBuffer;
		NativeArray<half4> frontBuffer;
		NativeArray<uint> rayCountBuffer;

#if BUFFERED_MATERIALS || UNITY_SOA
		NativeArray<Material> materialBuffer;
#endif

#if MANUAL_SOA
		SoaSpheres sphereBuffer;
		internal SoaSpheres World => sphereBuffer;
#elif MANUAL_AOSOA
		AosoaSpheres sphereBuffer;
		internal AosoaSpheres World => sphereBuffer;
#elif UNITY_SOA
		NativeArrayFullSOA<Sphere> sphereBuffer;
		internal NativeArrayFullSOA<Sphere> World => sphereBuffer;
#else
		NativeArray<Primitive> primitiveBuffer;
		NativeArray<Sphere> sphereBuffer;
		internal NativeArray<Primitive> World => primitiveBuffer;
#endif

		JobHandle? accumulateJobHandle;
		JobHandle? combineJobHandle;

		bool commandBufferHooked;
		bool worldNeedsRebuild;
		float lastFieldOfView;
		bool initialized;
		float focusDistance;
		bool traceAborted;
		bool ignoreBatchTimings;

		readonly Stopwatch batchTimer = new Stopwatch();
		readonly Stopwatch traceTimer = new Stopwatch();
		readonly List<float> mraysPerSecResults = new List<float>();

		readonly List<SphereData> activeSpheres = new List<SphereData>();
		readonly List<MaterialData> activeMaterials = new List<MaterialData>();

		uint2 bufferSize;

		bool TraceActive => accumulateJobHandle.HasValue || combineJobHandle.HasValue;

		void Awake()
		{
			commandBuffer = new CommandBuffer { name = "Raytracer" };
			frontBufferTexture = new Texture2D(0, 0, TextureFormat.RGBAHalf, false)
			{
				hideFlags = HideFlags.HideAndDontSave
			};
			ignoreBatchTimings = true;
		}

		void Start()
		{
			RebuildWorld();
			EnsureBuffersBuilt();
			CleanCamera();

			ScheduleAccumulate(true);
		}

		void OnDestroy()
		{
			// if there is a running job, let it know it needs to cancel and wait for completion
			accumulateJobHandle?.Complete();
			combineJobHandle?.Complete();

#if MANUAL_SOA || MANUAL_AOSOA || UNITY_SOA
			sphereBuffer.Dispose();
#else
			if (primitiveBuffer.IsCreated) primitiveBuffer.Dispose();
			if (sphereBuffer.IsCreated) sphereBuffer.Dispose();
#endif
#if BUFFERED_MATERIALS || UNITY_SOA
			if (materialBuffer.IsCreated) materialBuffer.Dispose();
#endif
			if (accumulationInputBuffer.IsCreated) accumulationInputBuffer.Dispose();
			if (accumulationOutputBuffer.IsCreated) accumulationOutputBuffer.Dispose();
			if (rayCountBuffer.IsCreated) rayCountBuffer.Dispose();
		}

		void Update()
		{
#if UNITY_EDITOR
			// watch for material data changes (won't catch those from OnValidate)
			if (!randomScene && spheres.Any(x => x.Material.Dirty))
			{
				foreach (SphereData sphere in spheres) sphere.Material.Dirty = false;
				worldNeedsRebuild = true;
			}
#endif
			uint2 currentSize = uint2(
				(uint) ceil(targetCamera.pixelWidth * resolutionScaling),
				(uint) ceil(targetCamera.pixelHeight * resolutionScaling));

			bool buffersNeedRebuild = any(currentSize != bufferSize);
			bool cameraDirty = targetCamera.transform.hasChanged ||
							   !Mathf.Approximately(lastFieldOfView, targetCamera.fieldOfView);
			bool traceNeedsReset = buffersNeedRebuild || worldNeedsRebuild || cameraDirty;

			void RebuildDirtyComponents()
			{
				if (buffersNeedRebuild) EnsureBuffersBuilt();
				if (worldNeedsRebuild) RebuildWorld();
				if (cameraDirty) CleanCamera();
			}

			void CompleteAccumulate()
			{
				TimeSpan elapsedTime = batchTimer.Elapsed;

				accumulateJobHandle.Value.Complete();
				accumulateJobHandle = null;

				uint rayCount = rayCountBuffer.Sum();

				accumulatedSamples += samplesPerBatch;
				lastBatchDuration = (float) elapsedTime.TotalMilliseconds;
				millionRaysPerSecond = rayCount / (float) elapsedTime.TotalSeconds / 1000000;
				if (!ignoreBatchTimings) mraysPerSecResults.Add(millionRaysPerSecond);
				avgMRaysPerSecond = mraysPerSecResults.Count == 0 ? 0 : mraysPerSecResults.Average();
				ignoreBatchTimings = false;
			}

			if (!TraceActive && traceNeedsReset)
			{
				RebuildDirtyComponents();
				ScheduleAccumulate(true);
			}

			if (combineJobHandle.HasValue && combineJobHandle.Value.IsCompleted)
			{
				if (accumulateJobHandle.HasValue && accumulateJobHandle.Value.IsCompleted)
					CompleteAccumulate();

				combineJobHandle.Value.Complete();
				combineJobHandle = null;

				bool traceCompleted = false;
				if (accumulatedSamples >= samplesPerPixel)
				{
					traceCompleted = true;
					lastTraceDuration = (float) traceTimer.Elapsed.TotalMilliseconds;
				}

				SwapBuffers();
				ForceUpdateInspector();
				RebuildDirtyComponents();

				if ((!(traceCompleted && stopWhenCompleted) || traceNeedsReset) && !traceAborted)
					ScheduleAccumulate(traceCompleted | traceNeedsReset);

				traceAborted = false;
			}

			// only when preview is disabled
			if (!combineJobHandle.HasValue && accumulateJobHandle.HasValue && accumulateJobHandle.Value.IsCompleted)
			{
				CompleteAccumulate();
				ForceUpdateInspector();
				RebuildDirtyComponents();

				if (!traceAborted)
					ScheduleAccumulate(false);

				traceAborted = false;
			}
		}

		void CleanCamera()
		{
			lastFieldOfView = targetCamera.fieldOfView;
			targetCamera.transform.hasChanged = false;
		}

		void SwapBuffers()
		{
			frontBufferTexture.Apply(false);

			if (!commandBufferHooked)
			{
				targetCamera.AddCommandBuffer(CameraEvent.AfterEverything, commandBuffer);
				commandBufferHooked = true;
			}
		}

		void ScheduleAccumulate(bool firstBatch)
		{
			Transform cameraTransform = targetCamera.transform;
			Vector3 origin = cameraTransform.localPosition;
			Vector3 lookAt = origin + cameraTransform.forward;

			if (World.Hit(new Ray(origin, cameraTransform.forward), 0, float.PositiveInfinity,
				out HitRecord hitRec))
			{
				focusDistance = hitRec.Distance;
			}

			var raytracingCamera = new Camera(origin, lookAt, cameraTransform.up, targetCamera.fieldOfView,
				(float) bufferSize.x / bufferSize.y, cameraAperture, focusDistance);

			var totalBufferSize = (int) (bufferSize.x * bufferSize.y);

			if (rayCountBuffer.IsCreated) rayCountBuffer.Dispose();
			rayCountBuffer = new NativeArray<uint>(totalBufferSize, Allocator.Persistent);

			if (firstBatch)
			{
				if (accumulationInputBuffer.IsCreated) accumulationInputBuffer.Dispose();
				accumulationInputBuffer = new NativeArray<float4>(totalBufferSize, Allocator.Persistent);

				mraysPerSecResults.Clear();
				accumulatedSamples = 0;
				ForceUpdateInspector();
			}
			else
				ExchangeBuffers(ref accumulationInputBuffer, ref accumulationOutputBuffer);

			var accumulateJob = new AccumulateJob
			{
				Size = bufferSize,
				Camera = raytracingCamera,
				InputSamples = accumulationInputBuffer,
				Seed = (uint) Time.frameCount + 1,
				SampleCount = min(samplesPerPixel, samplesPerBatch),
				TraceDepth = traceDepth,
				World = World,
				OutputSamples = accumulationOutputBuffer,
				OutputRayCount = rayCountBuffer,
#if BUFFERED_MATERIALS || UNITY_SOA
				Material = materialBuffer
#endif
			};

			accumulateJobHandle = accumulateJob.Schedule(totalBufferSize, 1);

			if (accumulatedSamples + samplesPerBatch >= samplesPerPixel || previewAfterBatch)
			{
				var combineJob = new CombineJob { Input = accumulationOutputBuffer, Output = frontBuffer };
				combineJobHandle = combineJob.Schedule(totalBufferSize, 128, accumulateJobHandle.Value);
			}

			batchTimer.Restart();
			if (firstBatch) traceTimer.Restart();
			JobHandle.ScheduleBatchedJobs();
		}

		void EnsureBuffersBuilt()
		{
			int width = (int) ceil(targetCamera.pixelWidth * resolutionScaling);
			int height = (int) ceil(targetCamera.pixelHeight * resolutionScaling);

			if (frontBufferTexture.width != width || frontBufferTexture.height != height)
			{
				if (commandBufferHooked)
				{
					targetCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, commandBuffer);
					commandBufferHooked = false;
				}

				frontBufferTexture.Resize(width, height);
				frontBufferTexture.filterMode = resolutionScaling > 1 ? FilterMode.Bilinear : FilterMode.Point;
				frontBuffer = frontBufferTexture.GetRawTextureData<half4>();

				commandBuffer.Clear();
				commandBuffer.Blit(frontBufferTexture, new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));

				Debug.Log($"Rebuilt front buffer (now {width} x {height})");
			}

			if (accumulationOutputBuffer.Length != width * height)
			{
				if (accumulationOutputBuffer.IsCreated) accumulationOutputBuffer.Dispose();
				accumulationOutputBuffer = new NativeArray<float4>(width * height,
					Allocator.Persistent,
					NativeArrayOptions.UninitializedMemory);

				Debug.Log($"Rebuilt accumulation output buffer (now {width} x {height})");
			}

			bufferSize = uint2((uint) width, (uint) height);
		}

		void RebuildWorld()
		{
			if (randomScene)
				BuildRandomScene();
			else
			{
				activeSpheres.Clear();
				foreach (SphereData sphere in spheres)
					if (sphere.Enabled)
						activeSpheres.Add(sphere);
			}

			activeMaterials.Clear();
			foreach (SphereData sphere in activeSpheres)
				if (!activeMaterials.Contains(sphere.Material))
					activeMaterials.Add(sphere.Material);

#if BUFFERED_MATERIALS || UNITY_SOA
			int materialCount = activeMaterials.Count;
			if (materialBuffer.Length != materialCount)
			{
				if (materialBuffer.IsCreated) materialBuffer.Dispose();
				materialBuffer = new NativeArray<Material>(materialCount, Allocator.Persistent);
			}

			for (var i = 0; i < activeMaterials.Count; i++)
			{
				MaterialData material = activeMaterials[i];
				materialBuffer[i] = new Material(material.Type, material.Albedo.ToFloat3(),
					material.Fuzz, material.RefractiveIndex);
			}
#endif

#if MANUAL_SOA
			int sphereCount = activeSpheres.Count;
			if (sphereBuffer.Count != sphereCount)
			{
				sphereBuffer.Dispose();
				sphereBuffer = new SoaSpheres(sphereCount);
			}

			for (var i = 0; i < activeSpheres.Count; i++)
			{
				SphereData sphereData = activeSpheres[i];
				sphereBuffer.SetElement(i, sphereData.Center, sphereData.Radius);

				MaterialData material = sphereData.Material;
#if BUFFERED_MATERIALS
				sphereBuffer.MaterialIndex[i] = activeMaterials.IndexOf(material);
#else
				sphereBuffer.Material[i] =
					new Material(material.Type, material.Albedo.ToFloat3(), material.Fuzz, material.RefractiveIndex);
#endif
			}

#elif MANUAL_AOSOA
			int sphereCount = activeSpheres.Count;
			if (sphereBuffer.Length != sphereCount)
			{
				sphereBuffer.Dispose();
				sphereBuffer = new AosoaSpheres(sphereCount);
			}

			for (int i = 0; i < activeSpheres.Count; i++)
			{
				SphereData sphereData = activeSpheres[i];
				sphereBuffer.SetElement(i, sphereData.Center, sphereData.Radius);

				MaterialData material = sphereData.Material;
#if BUFFERED_MATERIALS
				sphereBuffer.MaterialIndex[i] = activeMaterials.IndexOf(material);
#else
				sphereBuffer.Material[i] =
					new Material(material.Type, material.Albedo.ToFloat3(), material.Fuzz, material.RefractiveIndex);
#endif
			}

#elif UNITY_SOA
			int sphereCount = activeSpheres.Count;
			if (sphereBuffer.Length != sphereCount)
			{
				if (sphereBuffer.Length > 0) sphereBuffer.Dispose();
				sphereBuffer = new NativeArrayFullSOA<Sphere>(sphereCount, Allocator.Persistent);
			}

			for (int i = 0; i < activeSpheres.Count; i++)
			{
				SphereData sphereData = activeSpheres[i];
				MaterialData material = sphereData.Material;

				sphereBuffer[i] = new Sphere(sphereData.Center, sphereData.Radius, activeMaterials.IndexOf(material));
			}

#else
			int primitiveCount = activeSpheres.Count;

			// other typed active primitives would be collected here

			// rebuild primitive buffer
			if (!primitiveBuffer.IsCreated || primitiveBuffer.Length != primitiveCount)
			{
				if (primitiveBuffer.IsCreated) primitiveBuffer.Dispose();
				primitiveBuffer = new NativeArray<Primitive>(primitiveCount, Allocator.Persistent);
			}

			// rebuild individual typed primitive buffers
			if (!sphereBuffer.IsCreated || sphereBuffer.Length != activeSpheres.Count)
			{
				if (sphereBuffer.IsCreated) sphereBuffer.Dispose();
				sphereBuffer = new NativeArray<Sphere>(activeSpheres.Count, Allocator.Persistent);
			}

			// collect primitives
			int primitiveIndex = 0;
			for (var i = 0; i < activeSpheres.Count; i++)
			{
				var sphereData = activeSpheres[i];
				var material = sphereData.Material;
				sphereBuffer[i] = new Sphere(sphereData.Center, sphereData.Radius,
#if BUFFERED_MATERIALS
					activeMaterials.IndexOf(material));
#else
					new Material(material.Type, material.Albedo.ToFloat3(), material.Fuzz, material.RefractiveIndex));
#endif
				var sphereSlice = new NativeSlice<Sphere>(sphereBuffer, i, 1);
				primitiveBuffer[primitiveIndex++] = new Primitive(sphereSlice);
			}
#endif

			worldNeedsRebuild = false;

			Debug.Log($"Rebuilt world ({activeSpheres.Count} spheres, {activeMaterials.Count} materials)");
		}

		void BuildRandomScene()
		{
			activeSpheres.Clear();
			activeSpheres.Add(new SphereData(new Vector3(0, -1000, 0), 1000, MaterialData.Lambertian(0.5f)));

			var rng = new Random(sceneSeed);

			for (int a = -11; a < 11; a++)
			{
				for (int b = -11; b < 11; b++)
				{
					float materialProb = rng.NextFloat();
					float3 center = float3(a + 0.9f * rng.NextFloat(), 0.2f, b + 0.9f * rng.NextFloat());

					if (distance(center, float3(4, 0.2f, 0)) <= 0.9)
						continue;

					if (materialProb < 0.8)
						activeSpheres.Add(new SphereData(center, 0.2f, MaterialData.Lambertian(rng.NextFloat3() * rng.NextFloat3())));
					else if (materialProb < 0.95)
						activeSpheres.Add(new SphereData(center, 0.2f,
							MaterialData.Metal(rng.NextFloat3(0.5f, 1), rng.NextFloat(0, 0.5f))));
					else
						activeSpheres.Add(new SphereData(center, 0.2f, MaterialData.Dielectric(1.5f)));
				}
			}

			activeSpheres.Add(new SphereData(float3(0, 1, 0), 1, MaterialData.Dielectric(1.5f)));
			activeSpheres.Add(new SphereData(float3(-4, 1, 0), 1, MaterialData.Lambertian(float3(0.4f, 0.2f, 0.1f))));
			activeSpheres.Add(new SphereData(float3(4, 1, 0), 1, MaterialData.Metal(float3(0.7f, 0.6f, 0.5f))));
		}

		static void ExchangeBuffers(ref NativeArray<float4> lhs, ref NativeArray<float4> rhs)
		{
			var temp = lhs;
			lhs = rhs;
			rhs = temp;
		}
	}
}