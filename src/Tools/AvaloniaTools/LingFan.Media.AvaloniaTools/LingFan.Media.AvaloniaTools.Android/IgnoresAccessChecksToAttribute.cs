// R2 前置探针专用（2026-09-01，验证后随探针一并撤销）。
// Roslyn（dotnet/roslyn PR #22719）识别该名字的程序集级 attribute：允许当前程序集访问
// 所标注目标程序集的 internal 成员。BCL 未内置，须在使用方程序集自带定义。
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

        public string AssemblyName { get; }
    }
}
