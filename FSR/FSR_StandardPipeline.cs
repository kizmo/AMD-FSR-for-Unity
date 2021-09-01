﻿// AMD FSR For Unity Standard render pipeline

//Copyright<2021> < Abigail Hocking (aka Ninlilizi) >
//Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//documentation files (the "Software"), to deal in the Software without restriction, including without limitation
//the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
//to permit persons to whom the Software is furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
//TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;


namespace NKLI
{
    [ExecuteInEditMode]
    public class FSR_StandardPipeline : MonoBehaviour
    {
        // Cache local camera
        private Camera attached_camera;
        private Camera render_camera;
        private GameObject render_camera_gameObject;

        // Shader
        public ComputeShader compute_FSR;

        // Render textures
        private RenderTexture RT_FSR_RenderTarget;
        private RenderTexture RT_Output;

        // Cached camera flags
        private int cached_culling_mask;
        private CameraClearFlags cached_clear_flags;

        // Render scale
        [Range(0.25f, 1)] public float render_scale = 0.75f;
        private float render_scale_cached;

        public enum upsample_modes
        {
            FSR,
            Bilinear
        }

        public upsample_modes upsample_mode;

        public bool sharpening;
        [Range(0, 2)] public float sharpness = 1;


        // Start is called before the first frame update
        private void OnEnable()
        {
            // Load voxel insertion shader
            compute_FSR = Resources.Load("NKLI_FSR/FSR") as ComputeShader;
            if (compute_FSR == null) throw new Exception("[FSR] failed to load compute shader 'NKLI_FSR/FSR'");

            // Cache this
            attached_camera = GetComponent<Camera>();

            // Create textures
            CreateRenderTexture();

            // Create render camera
            render_camera_gameObject = new GameObject("FSR_Render_Camera");
            render_camera_gameObject.transform.parent = transform;
            render_camera_gameObject.transform.localPosition = Vector3.zero;
            render_camera_gameObject.transform.localRotation = Quaternion.identity;
            render_camera_gameObject.hideFlags = HideFlags.HideAndDontSave;
            render_camera = render_camera_gameObject.AddComponent<Camera>();
            render_camera.gameObject.SetActive(true);
        }


        private void OnDisable()
        {
            // Destroy render camera
            DestroyImmediate(render_camera_gameObject);

            // Dispose render target
            if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
            if (RT_Output != null) RT_Output.Release();
        }


        /// <summary>
        /// Creates render textures
        /// </summary>
        private void CreateRenderTexture()
        {

            if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
            float target_width = attached_camera.scaledPixelWidth * render_scale;
            float target_height = attached_camera.scaledPixelHeight * render_scale;
            RT_FSR_RenderTarget = new RenderTexture((int)target_width, (int)target_height, 24, attached_camera.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);

            if (RT_Output != null) RT_Output.Release();
            RT_Output = new RenderTexture(attached_camera.pixelWidth, attached_camera.pixelHeight, 24, attached_camera.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);
            if (XRSettings.isDeviceActive) RT_Output.vrUsage = XRSettings.eyeTextureDesc.vrUsage;

            RT_Output.enableRandomWrite = true;
            RT_Output.useMipMap = false;
            RT_Output.Create();
        }


        private void Update()
        {
            // If the render scale has changed we must recreate our textures
            if (render_scale != render_scale_cached)
            {
                render_scale_cached = render_scale;
                CreateRenderTexture();
            }
        }


        private void OnPreCull()
        {
            // Clone camera properties
            render_camera.CopyFrom(attached_camera);

            // Set render target
            render_camera.targetTexture = RT_FSR_RenderTarget;

            // Cache flags
            cached_culling_mask = attached_camera.cullingMask;
            cached_clear_flags = attached_camera.clearFlags;

            // Clear flags
            attached_camera.cullingMask = 0;
            attached_camera.clearFlags = CameraClearFlags.Nothing;
        }


        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // Set parameters to shader
            compute_FSR.SetInt("input_viewport_width", RT_FSR_RenderTarget.width);
            compute_FSR.SetInt("input_viewport_height", RT_FSR_RenderTarget.height);
            compute_FSR.SetInt("input_image_width", RT_FSR_RenderTarget.width);
            compute_FSR.SetInt("input_image_height", RT_FSR_RenderTarget.height);

            compute_FSR.SetInt("output_image_width", RT_Output.width);
            compute_FSR.SetInt("output_image_height", RT_Output.height);

            compute_FSR.SetInt("upsample_mode", (int)upsample_mode);

            // Calculate thread counts
            int dispatchX = (RT_Output.width + (16 - 1)) / 16;
            int dispatchY = (RT_Output.height + (16 - 1)) / 16;

            if (sharpening && upsample_mode == upsample_modes.FSR)
            {
                // Create intermediary render texture
                RenderTextureDescriptor intermdiaryDesc = new RenderTextureDescriptor
                {
                    width = RT_Output.width,
                    height = RT_Output.height,
                    depthBufferBits = 24,
                    volumeDepth = 1,
                    msaaSamples = 1,
                    dimension = TextureDimension.Tex2D,
                    graphicsFormat = RT_Output.graphicsFormat,
                    enableRandomWrite = true,
                    useMipMap = false
                };
                if (XRSettings.isDeviceActive) intermdiaryDesc.vrUsage = XRSettings.eyeTextureDesc.vrUsage;
                RenderTexture intermediary = RenderTexture.GetTemporary(intermdiaryDesc);
                intermediary.Create();

                // Upscale
                compute_FSR.SetInt("upscale_or_sharpen", 1);
                compute_FSR.SetTexture(0, "InputTexture", RT_FSR_RenderTarget);
                compute_FSR.SetTexture(0, "OutputTexture", intermediary);
                compute_FSR.Dispatch(0, dispatchX, dispatchY, 1);

                // Sharpen
                compute_FSR.SetInt("upscale_or_sharpen", 0);
                compute_FSR.SetFloat("sharpness", 2 - sharpness);
                compute_FSR.SetTexture(0, "InputTexture", intermediary);
                compute_FSR.SetTexture(0, "OutputTexture", RT_Output);
                compute_FSR.Dispatch(0, dispatchX, dispatchY, 1);

                // Dispose
                intermediary.Release();
            }
            else
            {
                compute_FSR.SetInt("upscale_or_sharpen", 1);
                compute_FSR.SetTexture(0, "InputTexture", RT_FSR_RenderTarget);
                compute_FSR.SetTexture(0, "OutputTexture", RT_Output);
                compute_FSR.Dispatch(0, dispatchX, dispatchY, 1);
            }

            Graphics.Blit(RT_Output, dest);

            // Restore camera flags
            attached_camera.clearFlags = cached_clear_flags;
            attached_camera.cullingMask = cached_culling_mask;
        }
    }
}