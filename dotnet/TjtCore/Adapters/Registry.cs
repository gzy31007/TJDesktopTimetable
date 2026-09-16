using System.Runtime.CompilerServices;

namespace Tjt.Core.Adapters;

/// <summary>
/// 适配器注册表（TS 侧 <c>adapters/registry.ts</c> 的移植）——新增学校 = 实现
/// <see cref="ISchoolAdapter"/> + 在这里登记。
///
/// 注册表的职责只有三件：按顺序列出、按 id 取、按 <c>detect</c> 打分挑最优。
/// 打分失败（适配器内部抛异常）必须被吞掉记 0 分：一个坏掉的适配器不能让整条导入管线崩掉。
/// </summary>
public static class AdapterRegistry
{
    /// <summary>
    /// 内置适配器（按优先级：具体学校优先于通用格式）。
    ///
    /// 顺序即优先级：<c>detect</c> 打分相同时先登记的胜出，因此同济（0.98/0.9）永远压过
    /// 通用 JSON（0.9/0.6/0.4）。同济与交大都是 0.98，但特征字段互斥
    /// （同济 <c>selectedCourses</c>/<c>timeTableList</c>/<c>weekState</c>，
    /// 交大 <c>errno</c> + <c>jxbId</c>/<c>duration</c>），不会互相抢。
    /// </summary>
    public static readonly IReadOnlyList<ISchoolAdapter> BuiltinAdapters =
    [
        TongjiStudentAdapter.Instance,
        SjtuStudentAdapter.Instance,
        PreviewHtmlAdapter.Instance,
        GenericJsonAdapter.Instance,
    ];

    /// <summary>内置注册表实例（导入管线默认用它）。</summary>
    public static IAdapterRegistry Default { get; } = Create(BuiltinAdapters);

    /// <summary>
    /// 把内置注册表装配给导入管线（<see cref="ImportPipeline.DefaultRegistryFactory"/>）。
    ///
    /// 方向是"注册表 → 管线"而不是反过来：管线不引用具体适配器，适配器层也不知道管线怎么用，
    /// 只有这里做一次装配。
    /// </summary>
    static AdapterRegistry()
    {
        ImportPipeline.DefaultRegistryFactory = () => Default;
    }

    /// <summary>用给定适配器列表造一个注册表（测试与"只启用某几个适配器"的场景用）。</summary>
    public static IAdapterRegistry Create(IEnumerable<ISchoolAdapter> adapters) => new Registry(adapters);

    /// <summary>同上，方便直接传若干个适配器实例。</summary>
    public static IAdapterRegistry Create(params ISchoolAdapter[] adapters) => new Registry(adapters);

    private sealed class Registry : IAdapterRegistry
    {
        private readonly List<ISchoolAdapter> _items;

        public Registry(IEnumerable<ISchoolAdapter> adapters) => _items = [.. adapters];

        public IReadOnlyList<ISchoolAdapter> List() => _items;

        public ISchoolAdapter? Get(string id) => _items.FirstOrDefault(adapter => adapter.Id == id);

        public (ISchoolAdapter Adapter, double Score)? Best(ImportInput input)
        {
            (ISchoolAdapter Adapter, double Score)? winner = null;
            foreach (var adapter in _items)
            {
                double score;
                try
                {
                    score = adapter.Detect(input);
                }
                catch
                {
                    // 与 TS 的 `catch { score = 0 }` 一致：探测失败 = 不匹配，不向上抛。
                    score = 0;
                }

                // 严格大于：同分保留先登记的（顺序即优先级）。
                if (score > 0 && (winner is null || score > winner.Value.Score)) winner = (adapter, score);
            }

            return winner;
        }
    }
}

/// <summary>
/// 程序集加载即装配默认注册表。
///
/// 静态构造函数是**惰性**的：<c>ImportPipeline.ImportTimetable(input)</c> 可能在任何代码访问
/// <see cref="AdapterRegistry"/> 之前被调用（测试与 UI 都会直接走管线），那时
/// <c>DefaultRegistryFactory</c> 还是 <c>null</c>，管线会抛 <c>adapter.noregistry</c>。
/// 模块初始化器在程序集加载时运行，保证"不带 registry 也能导入"。
/// </summary>
internal static class AdapterRegistryBootstrap
{
    // CA2255 提醒"库代码不要用模块初始化器"（`dotnet_diagnostic` 默认把它提为错误）。
    // 这里的用例恰恰是它存在的理由：默认注册表必须在**任何**代码调用导入管线之前就装配好，
    // 而惰性静态构造做不到这一点（见上面的类注释）。这是库内唯一的副作用，且只赋值一次。
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() => ImportPipeline.DefaultRegistryFactory = () => AdapterRegistry.Default;
#pragma warning restore CA2255
}
