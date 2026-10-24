namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧对象池接口。供解码器等组件复用帧实例，降低分配与 GC 压力。
/// </summary>
/// <remarks>
/// <para><b>线程</b>：<see cref="Rent"/> 与 <see cref="Return"/> 必须可从任意线程调用——租借与归还可能
/// 分别发生在解码线程、管线线程与 UI 线程，实现须自带线程安全（如并发栈），不得要求调用方加锁。</para>
/// <para><b>所有权</b>：<see cref="Rent"/> 之后帧归调用方，<see cref="Return"/> 之后归池。
/// 调用方不得在归还后继续访问该帧（池会重置其内容，持有 GPU/缓冲资源的帧会被释放）。</para>
/// <para><b>生命周期</b>：本接口自身不承担池的释放——池是否实现 <see cref="IDisposable"/>、
/// 释放后两个方法如何表现，由实现声明。约定下限：释放后 <see cref="Return"/> 必须<b>直接释放</b>
/// 传入的帧而非入池；<see cref="Rent"/> 允许返回新实例（不要求抛异常），使迟到的归还退化为释放而不是泄漏。</para>
/// </remarks>
/// <typeparam name="T">帧类型。</typeparam>
public interface IFramePool<T> where T : class
{
    /// <summary>租用一个帧实例。</summary>
    T Rent();

    /// <summary>归还帧实例到池中。</summary>
    void Return(T frame);
}
