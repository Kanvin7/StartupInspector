using System.Runtime.CompilerServices;

// 单元测试需要访问 CommandLine 等 internal 辅助类型。
[assembly: InternalsVisibleTo("StartupInspector.Core.Tests")]
