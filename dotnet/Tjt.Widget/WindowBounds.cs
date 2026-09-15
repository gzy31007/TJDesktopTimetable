using System.Text.Json.Serialization;

namespace Tjt.Widget;

/// <summary>
/// 挂件窗口的位置与尺寸（DIP），持久化到 <c>settings.json</c>。
///
/// <para>为什么用 DIP 而不是物理像素：显示器缩放可能在两次运行之间变化
/// （例如插拔外接屏），存物理像素会让挂件在新缩放下一半跑到屏幕外。DIP 换算由外壳负责。</para>
/// </summary>
/// <param name="X">左上角 X。</param>
/// <param name="Y">左上角 Y。</param>
/// <param name="Width">宽。</param>
/// <param name="Height">高。</param>
public sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// 宽高是否是一个能用的尺寸（防住 settings.json 被写坏）。
    ///
    /// <c>JsonIgnore</c>：这是从 Width/Height 派生的判定，不该出现在配置文件里
    /// （实测它会被当成可写属性序列化出去，读起来像是"用户可以改"的字段）。
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => Width >= 320 && Height >= 240;
}
