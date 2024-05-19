using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    struct EncoderStateManager
    {
        private readonly MTLDevice _device;
        private readonly Pipeline _pipeline;

        private EncoderState _currentState = new();
        private EncoderState _backState = new();

        public readonly MTLBuffer IndexBuffer => _currentState.IndexBuffer;
        public readonly MTLIndexType IndexType => _currentState.IndexType;
        public readonly ulong IndexBufferOffset => _currentState.IndexBufferOffset;
        public readonly PrimitiveTopology Topology => _currentState.Topology;

        public EncoderStateManager(MTLDevice device, Pipeline pipeline)
        {
            _device = device;
            _pipeline = pipeline;
        }

        public void SwapStates()
        {
            (_currentState, _backState) = (_backState, _currentState);

            if (_pipeline.CurrentEncoderType == EncoderType.Render)
            {
                _pipeline.EndCurrentPass();
            }
        }

        public MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            // Initialise Pass & State

            var renderPassDescriptor = new MTLRenderPassDescriptor();
            var renderPipelineDescriptor = new MTLRenderPipelineDescriptor();

            for (int i = 0; i < EncoderState.MaxColorAttachments; i++)
            {
                if (_currentState.RenderTargets[i] != IntPtr.Zero)
                {
                    var passAttachment = renderPassDescriptor.ColorAttachments.Object((ulong)i);
                    passAttachment.Texture = _currentState.RenderTargets[i];
                    passAttachment.LoadAction = MTLLoadAction.Load;
                }
            }

            var depthAttachment = renderPassDescriptor.DepthAttachment;
            var stencilAttachment = renderPassDescriptor.StencilAttachment;

            if (_currentState.DepthStencil != IntPtr.Zero)
            {
                switch (_currentState.DepthStencil.PixelFormat)
                {
                    // Depth Only Attachment
                    case MTLPixelFormat.Depth16Unorm:
                    case MTLPixelFormat.Depth32Float:
                        depthAttachment.Texture = _currentState.DepthStencil;
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        break;

                    // Stencil Only Attachment
                    case MTLPixelFormat.Stencil8:
                        stencilAttachment.Texture = _currentState.DepthStencil;
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        break;

                    // Combined Attachment
                    case MTLPixelFormat.Depth24UnormStencil8:
                    case MTLPixelFormat.Depth32FloatStencil8:
                        depthAttachment.Texture = _currentState.DepthStencil;
                        depthAttachment.LoadAction = MTLLoadAction.Load;

                        var unpackedFormat = FormatTable.PackedStencilToXFormat(_currentState.DepthStencil.PixelFormat);
                        var stencilView = _currentState.DepthStencil.NewTextureView(unpackedFormat);
                        stencilAttachment.Texture = stencilView;
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        break;
                    default:
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Unsupported Depth/Stencil Format: {_currentState.DepthStencil.PixelFormat}!");
                        break;
                }
            }

            // Initialise Encoder

            var renderCommandEncoder = _pipeline.CommandBuffer.RenderCommandEncoder(renderPassDescriptor);

            // TODO: set dirty flags all to true

            return renderCommandEncoder;
        }

        public void RebindState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            // TODO: only rebind the dirty state
            SetPipelineState(renderCommandEncoder);
            SetDepthStencilState(renderCommandEncoder, _currentState.DepthStencilState);
            SetDepthClamp(renderCommandEncoder, _currentState.DepthClipMode);
            SetScissors(renderCommandEncoder, _currentState.Scissors);
            SetViewports(renderCommandEncoder, _currentState.Viewports);
            SetVertexBuffers(renderCommandEncoder, _currentState.VertexBuffers);
            SetBuffers(renderCommandEncoder, _currentState.UniformBuffers, true);
            SetBuffers(renderCommandEncoder, _currentState.StorageBuffers, true);
            SetCullMode(renderCommandEncoder, _currentState.CullMode);
            SetFrontFace(renderCommandEncoder, _currentState.Winding);
            SetTextureAndSampler(renderCommandEncoder, ShaderStage.Vertex, _currentState.VertexTextures, _currentState.VertexSamplers);
            SetTextureAndSampler(renderCommandEncoder, ShaderStage.Fragment, _currentState.FragmentTextures, _currentState.FragmentSamplers);

            _currentState.Dirty = new();
        }

        private void SetPipelineState(MTLRenderCommandEncoder renderCommandEncoder) {
            var renderPipelineDescriptor = new MTLRenderPipelineDescriptor();

            for (int i = 0; i < EncoderState.MaxColorAttachments; i++)
            {
                if (_currentState.RenderTargets[i] != IntPtr.Zero)
                {
                    var pipelineAttachment = renderPipelineDescriptor.ColorAttachments.Object((ulong)i);
                    pipelineAttachment.PixelFormat = _currentState.RenderTargets[i].PixelFormat;
                    pipelineAttachment.SourceAlphaBlendFactor = MTLBlendFactor.SourceAlpha;
                    pipelineAttachment.DestinationAlphaBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
                    pipelineAttachment.SourceRGBBlendFactor = MTLBlendFactor.SourceAlpha;
                    pipelineAttachment.DestinationRGBBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;

                    if (_currentState.BlendDescriptors.TryGetValue(i, out BlendDescriptor blendDescriptor))
                    {
                        pipelineAttachment.SetBlendingEnabled(blendDescriptor.Enable);
                        pipelineAttachment.AlphaBlendOperation = blendDescriptor.AlphaOp.Convert();
                        pipelineAttachment.RgbBlendOperation = blendDescriptor.ColorOp.Convert();
                        pipelineAttachment.SourceAlphaBlendFactor = blendDescriptor.AlphaSrcFactor.Convert();
                        pipelineAttachment.DestinationAlphaBlendFactor = blendDescriptor.AlphaDstFactor.Convert();
                        pipelineAttachment.SourceRGBBlendFactor = blendDescriptor.ColorSrcFactor.Convert();
                        pipelineAttachment.DestinationRGBBlendFactor = blendDescriptor.ColorDstFactor.Convert();
                    }
                }
            }

            if (_currentState.DepthStencil != IntPtr.Zero)
            {
                switch (_currentState.DepthStencil.PixelFormat)
                {
                    // Depth Only Attachment
                    case MTLPixelFormat.Depth16Unorm:
                    case MTLPixelFormat.Depth32Float:
                        renderPipelineDescriptor.DepthAttachmentPixelFormat = _currentState.DepthStencil.PixelFormat;
                        break;

                    // Stencil Only Attachment
                    case MTLPixelFormat.Stencil8:
                        renderPipelineDescriptor.StencilAttachmentPixelFormat = _currentState.DepthStencil.PixelFormat;
                        break;

                    // Combined Attachment
                    case MTLPixelFormat.Depth24UnormStencil8:
                    case MTLPixelFormat.Depth32FloatStencil8:
                        renderPipelineDescriptor.DepthAttachmentPixelFormat = _currentState.DepthStencil.PixelFormat;
                        renderPipelineDescriptor.StencilAttachmentPixelFormat = _currentState.DepthStencil.PixelFormat;
                        break;
                    default:
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Unsupported Depth/Stencil Format: {_currentState.DepthStencil.PixelFormat}!");
                        break;
                }
            }

            renderPipelineDescriptor.VertexDescriptor = BuildVertexDescriptor(_currentState.VertexBuffers, _currentState.VertexAttribs);

            if (_currentState.VertexFunction != null)
            {
                renderPipelineDescriptor.VertexFunction = _currentState.VertexFunction.Value;
            }
            else
            {
                return;
            }

            if (_currentState.FragmentFunction != null)
            {
                renderPipelineDescriptor.FragmentFunction = _currentState.FragmentFunction.Value;
            }

            var error = new NSError(IntPtr.Zero);
            var pipelineState = _device.NewRenderPipelineState(renderPipelineDescriptor, ref error);
            if (error != IntPtr.Zero)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Failed to create Render Pipeline State: {StringHelper.String(error.LocalizedDescription)}");
            }

            renderCommandEncoder.SetRenderPipelineState(pipelineState);

            renderCommandEncoder.SetBlendColor(
                _currentState.BlendColor.Red,
                _currentState.BlendColor.Green,
                _currentState.BlendColor.Blue,
                _currentState.BlendColor.Alpha);
        }

        public void UpdateIndexBuffer(BufferRange buffer, IndexType type)
        {
            if (buffer.Handle != BufferHandle.Null)
            {
                _currentState.IndexType = type.Convert();
                _currentState.IndexBufferOffset = (ulong)buffer.Offset;
                var handle = buffer.Handle;
                _currentState.IndexBuffer = new(Unsafe.As<BufferHandle, IntPtr>(ref handle));
            }
        }

        public void UpdatePrimitiveTopology(PrimitiveTopology topology)
        {
            _currentState.Topology = topology;
        }

        public void UpdateProgram(IProgram program)
        {
            Program prg = (Program)program;

            if (prg.VertexFunction == IntPtr.Zero)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, "Invalid Vertex Function!");
                return;
            }

            _currentState.VertexFunction = prg.VertexFunction;
            _currentState.FragmentFunction = prg.FragmentFunction;
        }

        public void UpdateRenderTargets(ITexture[] colors, ITexture depthStencil)
        {
            _currentState.RenderTargets = new MTLTexture[EncoderState.MaxColorAttachments];

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is not Texture tex)
                {
                    continue;
                }

                _currentState.RenderTargets[i] = tex.MTLTexture;
            }

            if (depthStencil is Texture depthTexture)
            {
                _currentState.DepthStencil = depthTexture.MTLTexture;
            }

            // Requires recreating pipeline
            if (_pipeline.CurrentEncoderType == EncoderType.Render)
            {
                _pipeline.EndCurrentPass();
            }
        }

        public void UpdateVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            _currentState.VertexAttribs = vertexAttribs.ToArray();
        }

        public void UpdateBlendDescriptors(int index, BlendDescriptor blend)
        {
            _currentState.BlendDescriptors[index] = blend;
            _currentState.BlendColor = blend.BlendConstant;
        }

        // Inlineable
        public void UpdateStencilState(StencilTestDescriptor stencilTest)
        {
            _currentState.BackFaceStencil = new MTLStencilDescriptor
            {
                StencilFailureOperation = stencilTest.BackSFail.Convert(),
                DepthFailureOperation = stencilTest.BackDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.BackDpPass.Convert(),
                StencilCompareFunction = stencilTest.BackFunc.Convert(),
                ReadMask = (uint)stencilTest.BackFuncMask,
                WriteMask = (uint)stencilTest.BackMask
            };

            _currentState.FrontFaceStencil = new MTLStencilDescriptor
            {
                StencilFailureOperation = stencilTest.FrontSFail.Convert(),
                DepthFailureOperation = stencilTest.FrontDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.FrontDpPass.Convert(),
                StencilCompareFunction = stencilTest.FrontFunc.Convert(),
                ReadMask = (uint)stencilTest.FrontFuncMask,
                WriteMask = (uint)stencilTest.FrontMask
            };

            _currentState.StencilTestEnabled = stencilTest.TestEnable;

            var descriptor = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = _currentState.DepthCompareFunction,
                DepthWriteEnabled = _currentState.DepthWriteEnabled
            };

            if (_currentState.StencilTestEnabled)
            {
                descriptor.BackFaceStencil = _currentState.BackFaceStencil;
                descriptor.FrontFaceStencil = _currentState.FrontFaceStencil;
            }

            _currentState.DepthStencilState = _device.NewDepthStencilState(descriptor);
        }

        // Inlineable
        public void UpdateDepthState(DepthTestDescriptor depthTest)
        {
            _currentState.DepthCompareFunction = depthTest.TestEnable ? depthTest.Func.Convert() : MTLCompareFunction.Always;
            _currentState.DepthWriteEnabled = depthTest.WriteEnable;

            var descriptor = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = _currentState.DepthCompareFunction,
                DepthWriteEnabled = _currentState.DepthWriteEnabled
            };

            if (_currentState.StencilTestEnabled)
            {
                descriptor.BackFaceStencil = _currentState.BackFaceStencil;
                descriptor.FrontFaceStencil = _currentState.FrontFaceStencil;
            }

            _currentState.DepthStencilState = _device.NewDepthStencilState(descriptor);
        }

        // Inlineable
        public void UpdateDepthClamp(bool clamp)
        {
            _currentState.DepthClipMode = clamp ? MTLDepthClipMode.Clamp : MTLDepthClipMode.Clip;
        }

        // Inlineable
        public void UpdateScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            int maxScissors = Math.Min(regions.Length, _currentState.Viewports.Length);

            if (maxScissors == 0)
            {
                return;
            }

            _currentState.Scissors = new MTLScissorRect[maxScissors];

            for (int i = 0; i < maxScissors; i++)
            {
                var region = regions[i];

                _currentState.Scissors[i] = new MTLScissorRect
                {
                    height = Math.Clamp((ulong)region.Height, 0, (ulong)_currentState.Viewports[i].height),
                    width = Math.Clamp((ulong)region.Width, 0, (ulong)_currentState.Viewports[i].width),
                    x = (ulong)region.X,
                    y = (ulong)region.Y
                };
            }
        }

        // Inlineable
        public void UpdateViewports(ReadOnlySpan<Viewport> viewports)
        {
            static float Clamp(float value)
            {
                return Math.Clamp(value, 0f, 1f);
            }

            _currentState.Viewports = new MTLViewport[viewports.Length];

            for (int i = 0; i < viewports.Length; i++)
            {
                var viewport = viewports[i];
                _currentState.Viewports[i] = new MTLViewport
                {
                    originX = viewport.Region.X,
                    originY = viewport.Region.Y,
                    width = viewport.Region.Width,
                    height = viewport.Region.Height,
                    znear = Clamp(viewport.DepthNear),
                    zfar = Clamp(viewport.DepthFar)
                };
            }
        }

        public void UpdateVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _currentState.VertexBuffers = vertexBuffers.ToArray();
        }

        // Inlineable
        public void UpdateUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _currentState.UniformBuffers = [];

            foreach (BufferAssignment buffer in buffers)
            {
                if (buffer.Range.Size != 0)
                {
                    _currentState.UniformBuffers.Add(new BufferInfo
                    {
                        Handle = buffer.Range.Handle.ToIntPtr(),
                        Offset = buffer.Range.Offset,
                        Index = buffer.Binding
                    });
                }
            }
        }

        // Inlineable
        public void UpdateStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _currentState.StorageBuffers = [];

            foreach (BufferAssignment buffer in buffers)
            {
                if (buffer.Range.Size != 0)
                {
                    // TODO: DONT offset the binding by 15
                    _currentState.StorageBuffers.Add(new BufferInfo
                    {
                        Handle = buffer.Range.Handle.ToIntPtr(),
                        Offset = buffer.Range.Offset,
                        Index = buffer.Binding + 15
                    });
                }
            }
        }

        // Inlineable
        public void UpdateCullMode(bool enable, Face face)
        {
            _currentState.CullMode = enable ? face.Convert() : MTLCullMode.None;
        }

        // Inlineable
        public void UpdateFrontFace(FrontFace frontFace)
        {
            _currentState.Winding = frontFace.Convert();
        }

        // Inlineable
        public readonly void UpdateTextureAndSampler(ShaderStage stage, ulong binding, MTLTexture texture, MTLSamplerState sampler)
        {
            switch (stage)
            {
                case ShaderStage.Fragment:
                    _currentState.FragmentTextures[binding] = texture;
                    _currentState.FragmentSamplers[binding] = sampler;
                    break;
                case ShaderStage.Vertex:
                    _currentState.VertexTextures[binding] = texture;
                    _currentState.VertexSamplers[binding] = sampler;
                    break;
            }
        }

        private static void SetDepthStencilState(MTLRenderCommandEncoder renderCommandEncoder, MTLDepthStencilState? depthStencilState)
        {
            if (depthStencilState != null)
            {
                renderCommandEncoder.SetDepthStencilState(depthStencilState.Value);
            }
        }

        private static void SetDepthClamp(MTLRenderCommandEncoder renderCommandEncoder, MTLDepthClipMode depthClipMode)
        {
            renderCommandEncoder.SetDepthClipMode(depthClipMode);
        }

        private unsafe static void SetScissors(MTLRenderCommandEncoder renderCommandEncoder, MTLScissorRect[] scissors)
        {
            if (scissors.Length > 0)
            {
                fixed (MTLScissorRect* pMtlScissors = scissors)
                {
                    renderCommandEncoder.SetScissorRects((IntPtr)pMtlScissors, (ulong)scissors.Length);
                }
            }
        }

        private unsafe static void SetViewports(MTLRenderCommandEncoder renderCommandEncoder, MTLViewport[] viewports)
        {
            if (viewports.Length > 0)
            {
                fixed (MTLViewport* pMtlViewports = viewports)
                {
                    renderCommandEncoder.SetViewports((IntPtr)pMtlViewports, (ulong)viewports.Length);
                }
            }
        }

        private static MTLVertexDescriptor BuildVertexDescriptor(VertexBufferDescriptor[] bufferDescriptors, VertexAttribDescriptor[] attribDescriptors)
        {
            var vertexDescriptor = new MTLVertexDescriptor();
            uint indexMask = 0;

            // TODO: Handle 'zero' buffers
            for (int i = 0; i < attribDescriptors.Length; i++)
            {
                var attrib = vertexDescriptor.Attributes.Object((ulong)i);
                // TODO: Format should not be hardcoded
                attrib.Format = MTLVertexFormat.Float4;
                indexMask |= 1u << attribDescriptors[i].BufferIndex;
                attrib.BufferIndex = (ulong)attribDescriptors[i].BufferIndex;
                attrib.Offset = (ulong)attribDescriptors[i].Offset;
            }

            for (int i = 0; i < bufferDescriptors.Length; i++)
            {
                var layout = vertexDescriptor.Layouts.Object((ulong)i);
                layout.Stride = (indexMask & (1u << i)) != 0 ? (ulong)bufferDescriptors[i].Stride : 0;
            }

            return vertexDescriptor;
        }

        private static void SetVertexBuffers(MTLRenderCommandEncoder renderCommandEncoder, VertexBufferDescriptor[] bufferDescriptors)
        {
            var buffers = new List<BufferInfo>();


            for (int i = 0; i < bufferDescriptors.Length; i++)
            {
                buffers.Add(new BufferInfo
                {
                    Handle = bufferDescriptors[i].Buffer.Handle.ToIntPtr(),
                    Offset = bufferDescriptors[i].Buffer.Offset,
                    Index = i
                });
            }

            SetBuffers(renderCommandEncoder, buffers);
        }

        private static void SetBuffers(MTLRenderCommandEncoder renderCommandEncoder, List<BufferInfo> buffers, bool fragment = false)
        {
            foreach (var buffer in buffers)
            {
                renderCommandEncoder.SetVertexBuffer(new MTLBuffer(buffer.Handle), (ulong)buffer.Offset, (ulong)buffer.Index);

                if (fragment)
                {
                    renderCommandEncoder.SetFragmentBuffer(new MTLBuffer(buffer.Handle), (ulong)buffer.Offset, (ulong)buffer.Index);
                }
            }
        }

        private static void SetCullMode(MTLRenderCommandEncoder renderCommandEncoder, MTLCullMode cullMode)
        {
            renderCommandEncoder.SetCullMode(cullMode);
        }

        private static void SetFrontFace(MTLRenderCommandEncoder renderCommandEncoder, MTLWinding winding)
        {
            renderCommandEncoder.SetFrontFacingWinding(winding);
        }

        private static void SetTextureAndSampler(MTLRenderCommandEncoder renderCommandEncoder, ShaderStage stage, Dictionary<ulong, MTLTexture> textures, Dictionary<ulong, MTLSamplerState> samplers)
        {
            foreach (var texture in textures)
            {
                switch (stage)
                {
                    case ShaderStage.Vertex:
                        renderCommandEncoder.SetVertexTexture(texture.Value, texture.Key);
                        break;
                    case ShaderStage.Fragment:
                        renderCommandEncoder.SetFragmentTexture(texture.Value, texture.Key);
                        break;
                }
            }

            foreach (var sampler in samplers)
            {
                switch (stage)
                {
                    case ShaderStage.Vertex:
                        renderCommandEncoder.SetVertexSamplerState(sampler.Value, sampler.Key);
                        break;
                    case ShaderStage.Fragment:
                        renderCommandEncoder.SetFragmentSamplerState(sampler.Value, sampler.Key);
                        break;
                }
            }
        }
    }
}
