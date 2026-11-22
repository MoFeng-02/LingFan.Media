using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.AppleAudioUnit;

/// <summary>
/// AudioToolbox 原生互操作分部：结构体布局与 AudioUnit v2 C API P/Invoke（macOS/iOS 同路径）。
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe partial class AudioUnitEngine
{
    private const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // AudioToolbox 结构体（Sequential，匹配 CoreAudioTypes.h / AUComponent.h 布局）

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioComponentDescription
    {
        public uint ComponentType;
        public uint ComponentSubType;
        public uint ComponentManufacturer;
        public uint ComponentFlags;
        public uint ComponentFlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AURenderCallbackStruct
    {
        public IntPtr InputProc;
        public IntPtr InputProcRefCon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer0; // 变长数组首元素（后续元素经指针步进访问）
    }

    // AudioToolbox P/Invoke（AudioComponent + AudioUnit v2 API，macOS/iOS 同路径）

    [LibraryImport(AudioToolboxLibrary)]
    private static partial IntPtr AudioComponentFindNext(IntPtr inComponent, ref AudioComponentDescription inDesc);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioComponentInstanceNew(IntPtr inComponent, out IntPtr outInstance);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioComponentInstanceDispose(IntPtr inInstance);

    [LibraryImport(AudioToolboxLibrary, EntryPoint = "AudioUnitSetProperty")]
    private static partial int AudioUnitSetPropertyFormat(IntPtr inUnit, uint inId, uint inScope, uint inElement,
        ref AudioStreamBasicDescription inData, uint inDataSize);

    [LibraryImport(AudioToolboxLibrary, EntryPoint = "AudioUnitSetProperty")]
    private static partial int AudioUnitSetPropertyCallback(IntPtr inUnit, uint inId, uint inScope, uint inElement,
        ref AURenderCallbackStruct inData, uint inDataSize);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioUnitInitialize(IntPtr inUnit);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioUnitUninitialize(IntPtr inUnit);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioOutputUnitStart(IntPtr inUnit);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioOutputUnitStop(IntPtr inUnit);
}