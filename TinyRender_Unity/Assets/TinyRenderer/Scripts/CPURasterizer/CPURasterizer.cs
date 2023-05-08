using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Rendering.HableCurve;

namespace TinyRenderer
{
    public class CPURasterizer : IRasterizer
    {
        int _width, _height;
        RenderingConfig _config;

        Matrix4x4 _modelMatrix;
        Matrix4x4 _viewMatrix;
        Matrix4x4 _projectionMatrix;

        Color[] frameBuffer;
        float[] depthBuffer;
        Color[] tempBuffer;

        Color[] samplers_color_MSAA;
        bool[] samplers_mask_MSAA;
        float[] samplers_depth_MSAA;

        public Texture2D texture;
        ShaderUniforms uniforms;

        // Stats
        int _trianglesAll, _trianglesRendered;
        int _verticesAll;

        //优化GC
        Vector4[] _tmpVector4s = new Vector4[3];
        Vector3[] _tmpVector3s = new Vector3[3];

        public string Name => "CPU";

        public Texture ColorTexture => texture;

        public OnRasterizerStatUpdate onRasterizerStatUpdate;

        public float Aspect => (float)_width / _height;

        public CPURasterizer(int width, int height, RenderingConfig renderConfig)
        {
            _width = width;
            _height = height;
            _config = renderConfig;

            frameBuffer = new Color[width * height];
            depthBuffer = new float[width * height];
            tempBuffer = new Color[width * height];

            texture = new Texture2D(width, height);
            texture.filterMode = FilterMode.Point;

            // MSAA Buffer
            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }

        }

        void AllocateMSAABuffers()
        {
            int msaaLevel = (int)_config.MSAA;
            int bufSize = _width * _height * msaaLevel * msaaLevel;
            if (samplers_color_MSAA == null || samplers_color_MSAA.Length != bufSize)
            {
                samplers_color_MSAA = new Color[bufSize];
                samplers_mask_MSAA = new bool[bufSize];
                samplers_depth_MSAA = new float[bufSize];
            }
        }

        public void Clear(BufferMask mask)
        {
            ProfilerManager.BeginSample("CPURasterizer.Clear");
            // MSAA
            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }

            // Color Buffer
            if ((mask & BufferMask.Color) == BufferMask.Color)
            {
                TinyRenderUtils.FillArray<Color>(frameBuffer, _config.ClearColor);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    TinyRenderUtils.FillArray(samplers_color_MSAA, _config.ClearColor);
                    TinyRenderUtils.FillArray(samplers_mask_MSAA, false);
                }
            }

            // Depth Buffer
            if ((mask & BufferMask.Depth) == BufferMask.Depth)
            {
                TinyRenderUtils.FillArray<float>(depthBuffer, 0f);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    TinyRenderUtils.FillArray(samplers_depth_MSAA, 0f);
                }
            }

            _trianglesAll = _trianglesRendered = 0;
            _verticesAll = 0;

            ProfilerManager.EndSample();
        }

        public void SetupUniforms(Camera camera, Light mainLight)
        {
            ShaderContext.Config = _config;

            var camPos = camera.transform.position;
            uniforms.WorldSpaceCameraPos = camPos;

            var lightDir = -mainLight.transform.forward;
            uniforms.WorldSpaceLightDir = lightDir;
            uniforms.LightColor = mainLight.color * mainLight.intensity;
            uniforms.AmbientColor = _config.AmbientColor;

            //TransformTool.SetupViewProjectionMatrix(camera, Aspect, out _viewMatrix, out _projectionMatrix);
            _viewMatrix = camera.worldToCameraMatrix;
            _projectionMatrix = camera.projectionMatrix;
        }

        public void DrawObject(RenderingObject obj)
        {
            ProfilerManager.BeginSample("CPURasterizer.DrawObject");

            var mesh = obj.mesh;
            _modelMatrix = obj.GetModelMatrix();
            var mvp = _projectionMatrix * _viewMatrix * _modelMatrix;

            if (_config.FrustumCulling && TinyRenderUtils.FrustumCulling(mesh.bounds, mvp))
            {
                ProfilerManager.EndSample();
                return;
            }

            var normalMatrix = _modelMatrix.inverse.transpose;
            _verticesAll += mesh.vertexCount;
            _trianglesAll += obj.cpuData.MeshTriangles.Length / 3;

            // -------------- Vertex Shader --------------
            var vsOutput = obj.cpuData.vsOutputBuffer;
            ProfilerManager.BeginSample("CPURasterizer.VertexShader CPU");
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var vert = obj.cpuData.MeshVertices[i];
                var objVert = new Vector4(vert.x, vert.y, vert.z, 1);
                vsOutput[i].clipPos = mvp * objVert;
                vsOutput[i].worldPos = _modelMatrix * objVert;
                vsOutput[i].objectNormal = obj.cpuData.MeshNormals[i];
                vsOutput[i].worldNormal = normalMatrix * obj.cpuData.MeshNormals[i];
            }
            ProfilerManager.EndSample();

            // -------------- Primitive Assembly --------------
            ProfilerManager.BeginSample("CPURasterizer.PrimitiveAssembly");
            var indices = obj.cpuData.MeshTriangles;
            for (int i = 0; i < indices.Length; i += 3)
            {
                // -------- Primitive Assembly --------
                int idx0 = indices[i];
                int idx1 = indices[i + 1];
                int idx2 = indices[i + 2];

                var v = _tmpVector4s;

                v[0] = vsOutput[idx0].clipPos;
                v[1] = vsOutput[idx1].clipPos;
                v[2] = vsOutput[idx2].clipPos;

                // -------- Clipping --------
                if (Clipped(_tmpVector4s))
                {
                    continue;
                }

                // ------- Perspective division --------
                // clip space to NDC
                for (int k = 0; k < 3; k++)
                {
                    v[k].x /= v[k].w;
                    v[k].y /= v[k].w;
                    v[k].z /= v[k].w;
                }

                // ------- backface culling --------
                if (_config.BackfaceCulling && !obj.DoubleSideRendering)
                {
                    Vector3 v0 = new Vector3(v[0].x, v[0].y, v[0].z);
                    Vector3 v1 = new Vector3(v[1].x, v[1].y, v[1].z);
                    Vector3 v2 = new Vector3(v[2].x, v[2].y, v[2].z);
                    Vector3 e01 = v1 - v0;
                    Vector3 e02 = v2 - v0;
                    Vector3 cross = Vector3.Cross(e01, e02);
                    if (cross.z > 0)
                    {
                        continue;
                    }
                }

                ++_trianglesRendered;

                // ------- Viewport Transform ----------
                // NDC to screen space
                for (int k = 0; k < 3; k++)
                {
                    var vec = v[k];
                    vec.x = 0.5f * (_width - 1) * (vec.x + 1.0f);
                    vec.y = 0.5f * (_height - 1) * (vec.y + 1.0f);

                    //在硬件渲染中，NDC的z值经过硬件的透视除法之后就直接写入到depth buffer了，如果要调整需要在投影矩阵中调整
                    //由于我们是软件渲染，所以可以在这里调整z值。                    

                    // 原注释，留着参考
                    //GAMES101约定的NDC是右手坐标系，z值范围是[-1,1]，但n为1，f为-1，因此值越大越靠近n。                    
                    //为了可视化Depth buffer，将最终的z值从[-1,1]映射到[0,1]的范围，因此最终n为1, f为0。离n越近，深度值越大。                    
                    //由于远处的z值为0，因此clear时深度要清除为0，然后深度测试时，使用GREATER测试。
                    //(当然我们也可以在这儿反转z值，然后clear时使用float.MaxValue清除，并且深度测试时使用LESS_EQUAL测试)
                    //注意：这儿的z值调整并不是必要的，只是为了可视化时便于映射为颜色值。其实也可以在可视化的地方调整。
                    //但是这么调整后，正好和Unity在DirectX平台的Reverse z一样，让near plane附近的z值的浮点数精度提高。
                    //vec.z = vec.z * 0.5f + 0.5f;
                    // 原注释结束

                    // 我们采用Unity的左手坐标系，做一个reverse-z即可
                    vec.z = 1 - vec.z;

                    v[k] = vec;
                }

                // TODO, 下面这坨有点累赘
                Triangle t = new Triangle();
                t.Vertex0.Position = v[0];
                t.Vertex1.Position = v[1];
                t.Vertex2.Position = v[2];

                //set obj normal
                t.Vertex0.Normal = vsOutput[idx0].objectNormal;
                t.Vertex1.Normal = vsOutput[idx1].objectNormal;
                t.Vertex2.Normal = vsOutput[idx2].objectNormal;

                if (obj.cpuData.MeshUVs.Length > 0)
                {
                    t.Vertex0.Texcoord = obj.cpuData.MeshUVs[idx0];
                    t.Vertex1.Texcoord = obj.cpuData.MeshUVs[idx1];
                    t.Vertex2.Texcoord = obj.cpuData.MeshUVs[idx2];
                }

                //设置顶点色,使用config中的颜色数组循环设置                
                if (_config.VertexColors != null && _config.VertexColors.Length > 0)
                {
                    int vertexColorCount = _config.VertexColors.Length;

                    t.Vertex0.Color = _config.VertexColors[idx0 % vertexColorCount];
                    t.Vertex1.Color = _config.VertexColors[idx1 % vertexColorCount];
                    t.Vertex2.Color = _config.VertexColors[idx2 % vertexColorCount];
                }
                else
                {
                    t.Vertex0.Color = Color.white;
                    t.Vertex1.Color = Color.white;
                    t.Vertex2.Color = Color.white;
                }

                //set world space pos & normal
                t.Vertex0.WorldPos = vsOutput[idx0].worldPos;
                t.Vertex1.WorldPos = vsOutput[idx1].worldPos;
                t.Vertex2.WorldPos = vsOutput[idx2].worldPos;
                t.Vertex0.WorldNormal = vsOutput[idx0].worldNormal;
                t.Vertex1.WorldNormal = vsOutput[idx1].worldNormal;
                t.Vertex2.WorldNormal = vsOutput[idx2].worldNormal;

                /// ---------- Rasterization -----------
                if (_config.WireframeMode)
                {
                    RasterizeWireframe(t);
                }
                else
                {
                    RasterizeTriangle(t, obj);
                }
            }

            ProfilerManager.EndSample();

            // Resolve AA
            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                int MSAALevel = (int)_config.MSAA;
                int SamplersPerPixel = MSAALevel * MSAALevel;

                for (int y = 0; y < _height; ++y)
                {
                    for (int x = 0; x < _width; ++x)
                    {
                        int index = GetIndex(x, y);
                        Color color = Color.clear;
                        float a = 0.0f;
                        for (int si = 0; si < MSAALevel; ++si)
                        {
                            for (int sj = 0; sj < MSAALevel; ++sj)
                            {
                                int xi = x * MSAALevel + si;
                                int yi = y * MSAALevel + sj;
                                int indexSamper = yi * _width * MSAALevel + xi;
                                if (samplers_mask_MSAA[indexSamper])
                                {
                                    color += samplers_color_MSAA[indexSamper];
                                    a += 1.0f;
                                }
                            }
                        }
                        if (a > 0.0f)
                        {
                            frameBuffer[index] = color / SamplersPerPixel;
                        }
                    }
                }
            }
            ProfilerManager.EndSample();
        }

        public void UpdateFrame()
        {
            ProfilerManager.BeginSample("CPURasterizer.UpdateFrame");
            switch (_config.DisplayBuffer)
            {
                case DisplayBufferType.Color:
                    texture.SetPixels(frameBuffer);
                    break;
                case DisplayBufferType.DepthRed:
                case DisplayBufferType.DepthGray:
                    for (int i = 0; i < depthBuffer.Length; i++)
                    {
                        // depth_buf中的值范围是[0,1]，且最近处为1，最远处为0。因此可视化后背景是黑色
                        float d = depthBuffer[i];
                        if (_config.DisplayBuffer == DisplayBufferType.DepthRed)
                        {
                            tempBuffer[i] = new Color(d, 0, 0);
                        }
                        else
                        {
                            tempBuffer[i] = new Color(d, d, d);
                        }
                    }
                    texture.SetPixels(tempBuffer);
                    break;
                default:
                    break;
            }
            texture.Apply();
            if (onRasterizerStatUpdate != null)
            {
                onRasterizerStatUpdate(_verticesAll, _trianglesAll, _trianglesRendered);
            }

            ProfilerManager.EndSample();
        }

        public void Release()
        {
            frameBuffer = null;
            depthBuffer = null;
            tempBuffer = null;
        }
       
        #region Wireframe mode
        //Breshham算法画线,颜色使用线性插值（非透视校正）
        private void DrawLine(Vector3 begin, Vector3 end, Color colorBegin, Color colorEnd)
        {
            int x1 = Mathf.FloorToInt(begin.x);
            int y1 = Mathf.FloorToInt(begin.y);
            int x2 = Mathf.FloorToInt(end.x);
            int y2 = Mathf.FloorToInt(end.y);

            int x, y, dx, dy, dx1, dy1, px, py, xe, ye, i;

            dx = x2 - x1;
            dy = y2 - y1;
            dx1 = Math.Abs(dx);
            dy1 = Math.Abs(dy);
            px = 2 * dy1 - dx1;
            py = 2 * dx1 - dy1;

            Color c1 = colorBegin;
            Color c2 = colorEnd;

            if (dy1 <= dx1)
            {
                if (dx >= 0)
                {
                    x = x1;
                    y = y1;
                    xe = x2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    xe = x1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);
                SetPixel(point, c1);
                for (i = 0; x < xe; i++)
                {
                    x++;
                    if (px < 0)
                    {
                        px += 2 * dy1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            y++;
                        }
                        else
                        {
                            y--;
                        }
                        px += 2 * (dy1 - dx1);
                    }

                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(xe - x) / dx1;
                    Color line_color = Color.Lerp(c1, c2, t);
                    SetPixel(pt, line_color);
                }
            }
            else
            {
                if (dy >= 0)
                {
                    x = x1;
                    y = y1;
                    ye = y2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    ye = y1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);
                SetPixel(point, c1);

                for (i = 0; y < ye; i++)
                {
                    y++;
                    if (py <= 0)
                    {
                        py += 2 * dx1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            x++;
                        }
                        else
                        {
                            x--;
                        }
                        py += 2 * (dx1 - dy1);
                    }
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(ye - y) / dy1;
                    Color line_color = Color.Lerp(c1, c2, t);
                    SetPixel(pt, line_color);
                }
            }
        }

        private void RasterizeWireframe(Triangle t)
        {
            ProfilerManager.BeginSample("CPURasterizer.RasterizeWireframe");
            DrawLine(t.Vertex0.Position, t.Vertex1.Position, t.Vertex0.Color, t.Vertex1.Color);
            DrawLine(t.Vertex1.Position, t.Vertex2.Position, t.Vertex1.Color, t.Vertex2.Color);
            DrawLine(t.Vertex2.Position, t.Vertex0.Position, t.Vertex2.Color, t.Vertex0.Color);
            ProfilerManager.EndSample();
        }

        #endregion

        #region Triangle Mode
        //Screen space  rasterization
        void RasterizeTriangle(Triangle t, RenderingObject ro)
        {
            ProfilerManager.BeginSample("CPURasterizer.RasterizeTriangle");
            var v = _tmpVector4s;
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;

            //Find out the bounding box of current triangle.
            float minX = v[0].x;
            float maxX = minX;
            float minY = v[0].y;
            float maxY = minY;

            for (int i = 1; i < 3; ++i)
            {
                float x = v[i].x;
                if (x < minX)
                {
                    minX = x;
                }
                else if (x > maxX)
                {
                    maxX = x;
                }
                float y = v[i].y;
                if (y < minY)
                {
                    minY = y;
                }
                else if (y > maxY)
                {
                    maxY = y;
                }
            }

            int minPX = Mathf.FloorToInt(minX);
            minPX = minPX < 0 ? 0 : minPX;
            int maxPX = Mathf.CeilToInt(maxX);
            maxPX = maxPX > _width ? _width : maxPX;
            int minPY = Mathf.FloorToInt(minY);
            minPY = minPY < 0 ? 0 : minPY;
            int maxPY = Mathf.CeilToInt(maxY);
            maxPY = maxPY > _height ? _height : maxPY;

            if (_config.MSAA == MSAALevel.Disabled)
            {
                // 遍历当前三角形包围中的所有像素，判断当前像素是否在三角形中
                // 对于在三角形中的像素，使用重心坐标插值得到深度值，并使用z buffer进行深度测试和写入
                for (int y = minPY; y < maxPY; ++y)
                {
                    for (int x = minPX; x < maxPX; ++x)
                    {
                        //if(IsInsideTriangle(x, y, t)) //-->检测是否在三角形内比使用重心坐标检测要慢，因此先计算重心坐标，再检查3个坐标是否有小于0
                        {
                            //计算重心坐标
                            var c = ComputeBarycentric2D(x, y, t);
                            float alpha = c.x;
                            float beta = c.y;
                            float gamma = c.z;
                            if (alpha < 0 || beta < 0 || gamma < 0)
                            {
                                continue;
                            }
                            //透视校正插值，z为透视校正插值后的view space z值
                            float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                            //zp为透视校正插值后的screen space z值
                            float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;

                            //深度测试(这儿的z值越大（reverse z）越靠近near plane，因此小值通过测试）
                            int index = GetIndex(x, y);
                            if (zp >= depthBuffer[index])
                            {
                                depthBuffer[index] = zp;

                                //透视校正插值
                                ProfilerManager.BeginSample("CPURasterizer.AttributeInterpolation");
                                Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                                Vector2 uv_p = (alpha * t.Vertex0.Texcoord / v[0].w + beta * t.Vertex1.Texcoord / v[1].w + gamma * t.Vertex2.Texcoord / v[2].w) * z;
                                Vector3 normal_p = (alpha * t.Vertex0.Normal / v[0].w + beta * t.Vertex1.Normal / v[1].w + gamma * t.Vertex2.Normal / v[2].w) * z;
                                Vector3 worldPos_p = (alpha * t.Vertex0.WorldPos / v[0].w + beta * t.Vertex1.WorldPos / v[1].w + gamma * t.Vertex2.WorldPos / v[2].w) * z;
                                Vector3 worldNormal_p = (alpha * t.Vertex0.WorldNormal / v[0].w + beta * t.Vertex1.WorldNormal / v[1].w + gamma * t.Vertex2.WorldNormal / v[2].w) * z;
                                ProfilerManager.EndSample();

                                FragmentShaderInputData input = new FragmentShaderInputData();
                                input.Color = color_p;
                                input.UV = uv_p;
                                input.TextureData = ro.texture.GetPixelData<TRColor24>(0);
                                input.TextureWidth = ro.texture.width;
                                input.TextureHeight = ro.texture.height;
                                input.UseBilinear = _config.BilinearSample;
                                input.LocalNormal = normal_p;
                                input.WorldPos = worldPos_p;
                                input.WorldNormal = worldNormal_p;

                                ProfilerManager.BeginSample("CPURasterizer.FragmentShader");
                                switch (_config.FragmentShaderType)
                                {
                                    case ShaderType.BlinnPhong:
                                        frameBuffer[index] = ShaderContext.FSBlinnPhong(input, uniforms);
                                        break;
                                    case ShaderType.NormalVisual:
                                        frameBuffer[index] = ShaderContext.FSNormalVisual(input);
                                        break;
                                    case ShaderType.VertexColor:
                                        frameBuffer[index] = ShaderContext.FSVertexColor(input);
                                        break;
                                }

                                ProfilerManager.EndSample();
                            }
                        }
                    }
                }
            }
            else
            {
                // 后注：这个msaa的做法完全不对。就留在这儿不删掉了
                // 正确的做法应该是判断有几个子像素在三角形内，然后对正常frag shader的结果乘以相应的比例

                int MSAALevel = (int)_config.MSAA;
                float sampler_dis = 1.0f / MSAALevel;
                float sampler_dis_half = sampler_dis * 0.5f;

                for (int y = minPY; y < maxPY; ++y)
                {
                    for (int x = minPX; x < maxPX; ++x)
                    {
                        //检查每个子像素是否在三角形内，如果在进行重心坐标插值和深度测试
                        for (int si = 0; si < MSAALevel; ++si)
                        {
                            for (int sj = 0; sj < MSAALevel; ++sj)
                            {
                                float offsetx = sampler_dis_half + si * sampler_dis;
                                float offsety = sampler_dis_half + sj * sampler_dis;
                                //if (IsInsideTriangle(x, y, t, offsetx, offsety))
                                {
                                    //计算重心坐标
                                    var c = ComputeBarycentric2D(x + offsetx, y + offsety, t);
                                    float alpha = c.x;
                                    float beta = c.y;
                                    float gamma = c.z;
                                    if (alpha < 0 || beta < 0 || gamma < 0)
                                    {
                                        continue;
                                    }
                                    //透视校正插值，z为透视校正插值后的view space z值
                                    float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                                    //zp为透视校正插值后的screen space z值
                                    float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;

                                    //深度测试(注意我们这儿的z值越大越靠近near plane，因此大值通过测试）                                    
                                    int xi = x * MSAALevel + si;
                                    int yi = y * MSAALevel + sj;
                                    int index = yi * _width * MSAALevel + xi;
                                    if (zp > samplers_depth_MSAA[index])
                                    {
                                        samplers_depth_MSAA[index] = zp;
                                        samplers_mask_MSAA[index] = true;

                                        //透视校正插值
                                        Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                                        samplers_color_MSAA[index] = color_p;
                                    }

                                }
                            }
                        }

                    }
                }
            }

            ProfilerManager.EndSample();
        }

        #endregion

        #region Base Method

        //三角形Clipping操作，对于部分在clipping volume中的图元，
        //硬件实现时一般只对部分顶点z值在near,far之间的图元进行clipping操作，
        //而部分顶点x,y值在x,y裁剪平面之间的图元则不进行裁剪，只是通过一个比viewport更大一些的guard-band区域进行整体剔除（相当于放大x,y的测试范围）
        //这样x,y裁剪平面之间的图元最终在frame buffer上进行Scissor测试。
        //此处的实现简化为只整体的视锥剔除，不做任何clipping操作。对于x,y裁剪没问题，虽然没扩大region,也可以最后在frame buffer上裁剪掉。
        //对于z的裁剪由于没有处理，会看到整个三角形消失导致的边缘不齐整

        //直接使用Clip space下的视锥剔除算法   
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Clipped(Vector4[] v)
        {
            //分别检查视锥体的六个面，如果三角形所有三个顶点都在某个面之外，则该三角形在视锥外，剔除  
            //由于NDC中总是满足-1<=Zndc<=1, 而当 w < 0 时，-w >= Zclip = Zndc*w >= w。
            //所以此时clip space的坐标范围是[w,-w], 为了比较时更明确，将w取正      
            var v0 = v[0];
            var w0 = v0.w >= 0 ? v0.w : -v0.w;
            var v1 = v[1];
            var w1 = v1.w >= 0 ? v1.w : -v1.w;
            var v2 = v[2];
            var w2 = v2.w >= 0 ? v2.w : -v2.w;

            //left
            if (v0.x < -w0 && v1.x < -w1 && v2.x < -w2)
            {
                return true;
            }
            //right
            if (v0.x > w0 && v1.x > w1 && v2.x > w2)
            {
                return true;
            }
            //bottom
            if (v0.y < -w0 && v1.y < -w1 && v2.y < -w2)
            {
                return true;
            }
            //top
            if (v0.y > w0 && v1.y > w1 && v2.y > w2)
            {
                return true;
            }
            //near
            if (v0.z < -w0 && v1.z < -w1 && v2.z < -w2)
            {
                return true;
            }
            //far
            if (v0.z > w0 && v1.z > w1 && v2.z > w2)
            {
                return true;
            }
            return false;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPixel(Vector3 point, Color color)
        {
            if (point.x < 0 || point.x >= _width || point.y < 0 || point.y >= _height)
            {
                return;
            }

            int idx = (int)point.y * _width + (int)point.x;
            frameBuffer[idx] = color;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Vector3 ComputeBarycentric2D(float x, float y, Triangle t)
        {
            ProfilerManager.BeginSample("CPURasterizer.ComputeBarycentric2D");
            var v = _tmpVector4s;
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;

            float c1 = (x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * y + v[1].x * v[2].y - v[2].x * v[1].y) / (v[0].x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * v[0].y + v[1].x * v[2].y - v[2].x * v[1].y);
            float c2 = (x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * y + v[2].x * v[0].y - v[0].x * v[2].y) / (v[1].x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * v[1].y + v[2].x * v[0].y - v[0].x * v[2].y);
            float c3 = (x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * y + v[0].x * v[1].y - v[1].x * v[0].y) / (v[2].x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * v[2].y + v[0].x * v[1].y - v[1].x * v[0].y);

            ProfilerManager.EndSample();
            return new Vector3(c1, c2, c3);
        }

        /// <summary>
        /// get pixel index
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(int x, int y)
        {
            return y * _width + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsInsideTriangle(int x, int y, Triangle t, float offsetX = 0.5f, float offsetY = 0.5f)
        {
            ProfilerManager.BeginSample("CPURasterizer.IsInsideTriangle");
            var v = _tmpVector3s;
            v[0] = new Vector3(t.Vertex0.Position.x, t.Vertex0.Position.y, t.Vertex0.Position.z);
            v[1] = new Vector3(t.Vertex1.Position.x, t.Vertex1.Position.y, t.Vertex1.Position.z);
            v[2] = new Vector3(t.Vertex2.Position.x, t.Vertex2.Position.y, t.Vertex2.Position.z);

            //当前像素中心位置p
            Vector3 p = new Vector3(x + offsetX, y + offsetY, 0);

            Vector3 v0p = p - v[0]; v0p[2] = 0;
            Vector3 v01 = v[1] - v[0]; v01[2] = 0;
            Vector3 cross0p = Vector3.Cross(v0p, v01);

            Vector3 v1p = p - v[1]; v1p[2] = 0;
            Vector3 v12 = v[2] - v[1]; v12[2] = 0;
            Vector3 cross1p = Vector3.Cross(v1p, v12);

            if (cross0p.z * cross1p.z > 0)
            {
                Vector3 v2p = p - v[2]; v2p[2] = 0;
                Vector3 v20 = v[0] - v[2]; v20[2] = 0;
                Vector3 cross2p = Vector3.Cross(v2p, v20);
                if (cross2p.z * cross1p.z > 0)
                {
                    ProfilerManager.EndSample();
                    return true;
                }
            }

            ProfilerManager.EndSample();
            return false;
        }
        #endregion
    }
}
