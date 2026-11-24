using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using ProbeShared;

namespace VulkanHeadfulPlaybackProbe;

// Program 的重播验收段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>重播验收：委托 ProbeShared.ReplayVerdict（判据唯一实现），本探针仅传差异化阈值。</summary>
    private static Task<bool> RunReplayAsync(
        IMediaPlayer player, CountingVideoRendererFactory? countingFactory,
        bool doVideo, bool visible, bool verbose, int presentCount)
        => ReplayVerdict.RunAsync(player, countingFactory, doVideo, visible, verbose, presentCount);
}
