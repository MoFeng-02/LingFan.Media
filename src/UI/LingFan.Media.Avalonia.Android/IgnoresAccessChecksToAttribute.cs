// 程序集级声明必须置于文件所有其他元素之前（CS1730；using 子句与外部别名声明除外）。
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Avalonia.Vulkan")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Avalonia.Base")]

// Roslyn（dotnet/roslyn PR #22719）识别该名字的程序集级 attribute：允许当前程序集访问
// 所标注目标程序集的 internal 成员。BCL 未内置，须在使用方程序集自带定义。
// Avalonia.Vulkan：VulkanOptions.CustomSharedDevice 注入通道；Avalonia.Base：平台图形选项内部成员。
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

        public string AssemblyName { get; }
    }
}
