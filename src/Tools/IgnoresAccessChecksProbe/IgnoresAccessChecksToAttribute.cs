// Roslyn（dotnet/roslyn PR #22719，作者即 Avalonia 作者 kekekeks）识别该名字的程序集级 attribute：
// 允许当前程序集访问标注目标程序集的 internal 成员。BCL 未内置，须在使用方程序集自带定义。
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

        public string AssemblyName { get; }
    }
}
