﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class RayTracingMaster : MonoBehaviour
{
	public struct Sphere
	{
		public Vector3 position;
		public float radius;
		public Vector3 albedo;
		public Vector3 specular;
		public float smoothness;
		public Vector3 emission;
	};

	public int SphereSeed;
	public Light DirectionalLight;
	public Vector2 SphereRadius = new Vector2(1.0f, 15.0f);
	public uint SpheresMax = 40;
	public float SpherePlacementRadius = 100.0f;
	public ComputeShader RayTracingShader;
	public Texture SkyboxTexture;

	private RenderTexture _target;
	private RenderTexture _converged;
	private Camera _camera;
	private uint _currentSample = 0;
	private Material _addMaterial;
	private ComputeBuffer _sphereBuffer;

	public float maxSphereHeight = 2;
	public float minSphereHeight = 1;
	//===============================
	struct MeshObject
	{
		public Matrix4x4 localToWorldMatrix;
		public int indices_offset;
		public int indices_count;
	}
	private static List<MeshObject> _meshObjects = new List<MeshObject>();
	private static List<Vector3> _vertices = new List<Vector3>();
	private static List<int> _indices = new List<int>();
	private ComputeBuffer _meshObjectBuffer;
	private ComputeBuffer _vertexBuffer;
	private ComputeBuffer _indexBuffer;

	//===================================
	private static bool _meshObjectsNeedRebuilding = false;
	private static List<RayTracingObject> _rayTracingObjects = new List<RayTracingObject>();
	public static void RegisterObject(RayTracingObject obj)
	{
		_rayTracingObjects.Add(obj);
		_meshObjectsNeedRebuilding = true;
	}

	public static void UnregisterObject(RayTracingObject obj)
	{
		_rayTracingObjects.Remove(obj);
		_meshObjectsNeedRebuilding = true;
	}

	private void Update()
	{
		if (transform.hasChanged)
		{
			_currentSample = 0;
			transform.hasChanged = false;
		}
	}

	private void SetUpScene()
	{
		Random.InitState(SphereSeed);

		List<Sphere> spheres = new List<Sphere>();

		//float alpha = 8;
		float thetaMin = 0;
		float thetaMax = 10;
		float thetaStep = (thetaMax - thetaMin) / (SpheresMax - 1);
		// Add a number of random spheres
		for (int i = 0; i < SpheresMax; i++)
		{
			float currentTheta = i * thetaStep;
			Sphere sphere = new Sphere();
			// Radius and radius
			Vector2 randomPos = Random.insideUnitCircle * SpherePlacementRadius;
			sphere.radius = SphereRadius.x + Random.value * (SphereRadius.y - SphereRadius.x);
			sphere.position = new Vector3(randomPos.x, sphere.radius, randomPos.y);
			// Reject spheres that are intersecting others
			foreach (Sphere other in spheres)
			{
				float minDist = sphere.radius + other.radius;
				if (Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
					goto SkipSphere;
			}
			// Albedo and specular color
			Color color = Random.ColorHSV();
			float chance = Random.value;
			//if (chance < 0.2f)
			{
				bool metal = chance < 1.0f;
				sphere.albedo = metal ? Vector3.zero : new Vector3(color.r, color.g, color.b);
				sphere.specular = metal ? new Vector3(color.r, color.g, color.b) : new Vector3(0.04f, 0.04f, 0.04f);
				sphere.smoothness = 1.0f;// Random.value;
				sphere.emission = new Vector3(0.0f, 0.0f, 0.0f);
			}
			//else
			//{
			//	sphere.albedo = Vector3.one;
			//	sphere.specular = Vector3.zero;
			//	sphere.smoothness = Random.value;
			//	Color emission = Random.ColorHSV(0, 1, 0, 1, 3, 8);
			//	sphere.emission = new Vector3(emission.r, emission.g, emission.b);
			//}
			// Add the sphere to the list
			spheres.Add(sphere);
			SkipSphere:
			continue;
		}

		// Assign to compute buffer
		CreateComputeBuffer(ref _sphereBuffer, spheres, 56);
		//_sphereBuffer = new ComputeBuffer(spheres.Count, 56);
		//_sphereBuffer.SetData(spheres);
	}

	private void OnEnable()
	{
		_currentSample = 0;
		SetUpScene();
	}
	private void OnDisable()
	{
		if (_sphereBuffer != null)
			_sphereBuffer.Release();
		if (_vertexBuffer != null)
			_vertexBuffer.Release();
		if (_indexBuffer != null)
			_indexBuffer.Release();
		if (_meshObjectBuffer != null)
			_meshObjectBuffer.Release();
	}

	private void Awake()
	{
		_camera = GetComponent<Camera>();
	}

	private void SetShaderParameters()
	{
		RayTracingShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);
		RayTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
		RayTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
		RayTracingShader.SetVector("_PixelOffset", new Vector2(Random.value, Random.value));
		
		Vector3 l = DirectionalLight.transform.forward;
		RayTracingShader.SetVector("_DirectionalLight", new Vector4(l.x, l.y, l.z, DirectionalLight.intensity));
		RayTracingShader.SetFloat("_Seed", Random.value);

		SetComputeBuffer("_Spheres", _sphereBuffer);
		SetComputeBuffer("_MeshObjects", _meshObjectBuffer);
		SetComputeBuffer("_Vertices", _vertexBuffer);
		SetComputeBuffer("_Indices", _indexBuffer);
	}

	private void OnRenderImage(RenderTexture source, RenderTexture destination)
	{
		Awake();
		SetShaderParameters();
		RebuildMeshObjectBuffers();
		Render(destination);
	}

	private void Render(RenderTexture destination)
	{
		// Make sure we have a current render target
		InitRenderTexture();
		// Set the target and dispatch the compute shader
		RayTracingShader.SetTexture(0, "Result", _target);
		int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
		int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
		RayTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);
		// Blit the result texture to the screen
		if (_addMaterial == null)
			_addMaterial = new Material(Shader.Find("Hidden/AddShader"));
		_addMaterial.SetFloat("_Sample", _currentSample);
		Graphics.Blit(_target, _converged, _addMaterial);
		Graphics.Blit(_converged, destination);
		_currentSample++;
	}

	private void InitRenderTexture()
	{
		if (_target == null || _target.width != Screen.width || _target.height != Screen.height)
		{
			// Release render texture if we already have one
			if (_target != null)
				_target.Release();
			// Get a render target for Ray Tracing
			_currentSample = 0;
			_target = new RenderTexture(Screen.width, Screen.height, 0,
				RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
			_target.enableRandomWrite = true;
			_target.Create();
		}

		if (_converged == null || _converged.width != Screen.width || _converged.height != Screen.height)
		{
			// Release render texture if we already have one
			if (_converged != null)
				_converged.Release();
			// Get a render target for Ray Tracing
			_currentSample = 0;
			_converged = new RenderTexture(Screen.width, Screen.height, 0,
				RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
			_converged.enableRandomWrite = true;
			_converged.Create();
		}
	}

	private void RebuildMeshObjectBuffers()
	{
		if (!_meshObjectsNeedRebuilding)
		{
			return;
		}
		_meshObjectsNeedRebuilding = false;
		_currentSample = 0;
		// Clear all lists
		_meshObjects.Clear();
		_vertices.Clear();
		_indices.Clear();
		// Loop over all objects and gather their data
		foreach (RayTracingObject obj in _rayTracingObjects)
		{
			Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;
			// Add vertex data
			int firstVertex = _vertices.Count;
			_vertices.AddRange(mesh.vertices);
			// Add index data - if the vertex buffer wasn't empty before, the
			// indices need to be offset
			int firstIndex = _indices.Count;
			var indices = mesh.GetIndices(0);
			_indices.AddRange(indices.Select(index => index + firstVertex));
			// Add the object itself
			_meshObjects.Add(new MeshObject()
			{
				localToWorldMatrix = obj.transform.localToWorldMatrix,
				indices_offset = firstIndex,
				indices_count = indices.Length
			});
		}
		CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, 72);
		CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
		CreateComputeBuffer(ref _indexBuffer, _indices, 4);
	}

	private void SetComputeBuffer(string name, ComputeBuffer buffer)
	{
		if (buffer != null)
		{
			RayTracingShader.SetBuffer(0, name, buffer);
		}
	}

	private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride)
	where T: struct
	{
		// Do we already have a compute buffer?
		if (buffer != null)
		{
			// If no data or buffer doesn't match the given criteria, release it
			if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride)
			{
				buffer.Release();
				buffer = null;
			}
		}
		if (data.Count != 0)
		{
			// If the buffer has been released or wasn't there to
			// begin with, create it
			if (buffer == null)
			{
				buffer = new ComputeBuffer(data.Count, stride);
			}
			// Set data on the buffer
			buffer.SetData(data);
		}
	}
}

/*
 * 
 * 
 * private void SetUpScene()
	{
		Random.InitState(SphereSeed);

		List<Sphere> spheres = new List<Sphere>();

		float alpha = 8;
		float thetaMin = 0;
		float thetaMax = 10;
		float thetaStep = (thetaMax - thetaMin) / (SpheresMax - 1);
		// Add a number of random spheres
		for (int i = 0; i < SpheresMax; i++)
		{
			float currentTheta = i * thetaStep;
			Sphere sphere = new Sphere();
			// Radius and radius
			//sphere.radius = SphereRadius.x + Random.value * (SphereRadius.y - SphereRadius.x);
			sphere.radius = SphereRadius.x + ((float)(i) / (float)(SpheresMax - 1)) * SphereRadius.y;
			Vector2 randomPos = Random.insideUnitCircle * SpherePlacementRadius;
			float x = alpha * Mathf.Sqrt(currentTheta) * Mathf.Cos(currentTheta);
			float y = alpha * Mathf.Sqrt(currentTheta) * Mathf.Sin(currentTheta);
			//sphere.position = new Vector3(randomPos.x, sphere.radius + Random.value * (maxSphereHeight - minSphereHeight), randomPos.y);
			sphere.position = new Vector3(x, 2.5f * SphereRadius.y + 3 * SphereRadius.y* (1.0f - ((float)(i) / (float)(SpheresMax - 1))) + Random.value * (maxSphereHeight - minSphereHeight), y);
			// Reject spheres that are intersecting others
			//foreach (Sphere other in spheres)
			//{
				//float minDist = sphere.radius + other.radius;
				//if (Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
				//	goto SkipSphere;
			//}
			// Albedo and specular color
			Color color = Random.ColorHSV();
			//float chance = Random.value;
			//if (chance < 0.8f)
			if((i % 3) != 0)
			{
				//bool metal = chance < 0.4f;
				bool metal = (i % 3) != 1;
sphere.albedo = metal? Vector3.zero : new Vector3(color.r, color.g, color.b);
sphere.specular = metal? new Vector3(color.r, color.g, color.b) : new Vector3(0.04f, 0.04f, 0.04f);
sphere.smoothness = Random.value;
				sphere.emission = new Vector3(0.0f, 0.0f, 0.0f);
			}
			else
			{
				sphere.albedo = Vector3.one;
				sphere.specular = Vector3.one;
				sphere.smoothness = Random.value;
				Color emission = Random.ColorHSV(0, 1, 0, 1, 3.0f, 8.0f);
sphere.emission = new Vector3(emission.r, emission.g, emission.b);
			}
			// Add the sphere to the list
			spheres.Add(sphere);
			SkipSphere:
			continue;
		}

		float bigSphereRadius = 12;
float bigSphereDisp = 40;
		for (int i = 0; i< 4; i++)
		{
			float x = bigSphereDisp * ((i & 1) == 0 ? -1.0f : 1.0f);
float y = bigSphereDisp * ((i & 2) == 0 ? -1.0f : 1.0f);
Sphere sphere = new Sphere();
sphere.radius = bigSphereRadius;
			sphere.position = new Vector3(x, sphere.radius, y);
sphere.albedo = Vector3.one;
			sphere.specular = Vector3.one;
			sphere.smoothness = Random.value;
			Color emission = Random.ColorHSV(0, 1, 0, 1, 3.0f, 8.0f);
sphere.emission = new Vector3(emission.r, emission.g, emission.b);
spheres.Add(sphere);
		}
			// Assign to compute buffer
			_sphereBuffer = new ComputeBuffer(spheres.Count, 56);
_sphereBuffer.SetData(spheres);
	}
 * */