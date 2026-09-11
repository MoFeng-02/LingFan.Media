using System.Runtime.InteropServices;

namespace LingFan.Media.Interop;

/// <summary>
/// COM vtable 槽位读取器（仓级唯一实现，AOT 兼容零反射）。
/// </summary>
/// <remarks>
/// <para><b>归属</b>：基础设施层——GPUShare.D3D11、Backends.MediaFoundation（MfVTable）、
/// Outputs（WASAPI/OpenSLES）、Renderers.D3D11（DComp）等所有手写 COM 互操作共用本实现，
/// 禁止在任何工程复刻"读 vtable 槽位"逻辑（历史教训：同名辅助类多处复制且槽位约定不同，易错）。</para>
/// <para><b>两种槽位约定（调用方必须明确选择）</b>：</para>
/// <para>- <b>相对槽位</b>（<see cref="Get{T}"/> / <see cref="GetMethodPointer"/>）：
/// <c>slotIndex</c> 0 = IUnknown 三槽之后第一个接口方法，绝对槽位 = 3 + slotIndex。
/// 适用于标准 COM 接口（WASAPI/MMDevice、Media Foundation、DirectComposition）。</para>
/// <para>- <b>绝对槽位</b>（<see cref="GetAbsolute{T}"/> / <see cref="ReadSlot"/>）：
/// 直接以 vtable 0 基下标取槽（0=QueryInterface、1=AddRef、2=Release）。
/// 适用于以绝对槽位表述的底座（D3D11/DXGI）或无 IUnknown 前缀的接口（OpenSL ES 各 Itf）。</para>
/// <para><b>AOT</b>：<see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}"/> 为运行时封送 API，
/// 无反射遍历，NativeAOT 兼容；委托须标注 <c>[UnmanagedFunctionPointer(Winapi)]</c>。</para>
/// </remarks>
public static class ComVTable
{
    /// <summary>读取指定<b>绝对</b>槽位的原始方法指针（不构造委托）：comPtr → vtable → vtable[slot]。</summary>
    public static IntPtr ReadSlot(IntPtr comPtr, int absoluteSlot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        return Marshal.ReadIntPtr(vtable, absoluteSlot * IntPtr.Size);
    }

    /// <summary>读取指定<b>绝对</b>槽位并构造强类型委托（0=QueryInterface、1=AddRef、2=Release；无 IUnknown 前缀的接口亦按 0 基绝对槽）。</summary>
    public static TDelegate GetAbsolute<TDelegate>(IntPtr comPtr, int absoluteSlot) where TDelegate : Delegate
    {
        IntPtr fp = ReadSlot(comPtr, absoluteSlot);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(fp);
    }

    /// <summary>读取<b>相对</b>槽位（slotIndex 0 = IUnknown 之后第一个接口方法）的原始方法指针。</summary>
    public static IntPtr GetMethodPointer(IntPtr comPtr, int slotIndex)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        return Marshal.ReadIntPtr(vtable, (3 + slotIndex) * IntPtr.Size);
    }

    /// <summary>读取<b>相对</b>槽位并构造强类型委托（标准 COM 接口默认约定：绝对槽位 = 3 + slotIndex）。</summary>
    public static TDelegate Get<TDelegate>(IntPtr comPtr, int slotIndex) where TDelegate : Delegate
    {
        IntPtr methodPtr = GetMethodPointer(comPtr, slotIndex);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }
}
